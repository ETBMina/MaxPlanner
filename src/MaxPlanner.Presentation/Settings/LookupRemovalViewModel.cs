using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Settings;

/// <summary>
/// Lookup removal with replacement (design §5.9, B-04, B-11, AC-08, AC-31): shows the usage counts, offers a
/// replacement (or clearing, where allowed), and runs the removal as one command with the confirmed counts.
/// If the usage changed before commit, the counts refresh and a new confirmation is needed.
/// </summary>
public sealed partial class LookupRemovalViewModel : ObservableObject, IDialogViewModel
{
    private static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(5);

    private readonly WorkspaceContext _context;
    private readonly SettingsUseCases _useCases;
    private readonly LookupKind _kind;
    private readonly Guid _id;
    private readonly string _name;
    private OperationId? _retryOperation;

    public LookupRemovalViewModel(WorkspaceContext context, SettingsUseCases useCases, WorkflowSnapshot workflow, LookupKind kind, Guid id, string name)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _useCases = useCases ?? throw new ArgumentNullException(nameof(useCases));
        ArgumentNullException.ThrowIfNull(workflow);
        _kind = kind;
        _id = id;
        _name = name;
        ReplacementChoices = [.. WorkflowLookups.Entries(workflow, kind).Where(e => e.Id != id).Select(e => new SettingsChoice(e.Id, e.Name.Display))];
        SelectedReplacementIndex = ReplacementChoices.Count > 0 ? 0 : -1;
    }

    public event EventHandler? CloseRequested;

    /// <summary>"Remove status 'Review'?" (design §5.9).</summary>
    public string DialogTitle => _context.Strings.Format("LookupRemove_Title_" + _kind, _name);

    public IReadOnlyList<SettingsChoice> ReplacementChoices { get; }

    /// <summary>"In use by:" lines: tasks, Subtask cards, CRs, CR owner, reviews, defaults.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> UsageLines { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove), nameof(ShowReplacement), nameof(ShowClearOption), nameof(PrimaryButtonText), nameof(IsUnused))]
    public partial LookupUsageCounts? Usage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial int SelectedReplacementIndex { get; set; }

    /// <summary>"Clear from records" instead of a replacement; offered only where B-04 allows clearing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove), nameof(PrimaryButtonText))]
    public partial bool ClearInstead { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>"The usage changed. Review the new counts." after a refused removal.</summary>
    [ObservableProperty]
    public partial string? UsageChangedMessage { get; private set; }

    public bool IsUnused => Usage is { IsUnused: true };

    /// <summary>A replacement is offered whenever the value is used; for statuses and priorities it is required.</summary>
    public bool ShowReplacement => Usage is { IsUnused: false } && ReplacementChoices.Count > 0;

    /// <summary>
    /// B-04/design §11 Q4: Designers, Developers, review Developers, tags, and squads may be cleared; CR Owner
    /// usage, the default CR Owner, statuses, and priorities can only be replaced.
    /// </summary>
    public bool ShowClearOption => Usage is { IsUnused: false } usage
        && _kind is LookupKind.Person or LookupKind.Tag or LookupKind.Squad
        && !(_kind == LookupKind.Person && (usage.CrOwners > 0 || usage.Defaults != WorkspaceDefaultRoles.None));

    /// <summary>Design §5.9 note for statuses: "Dates are kept. This is not a status move."</summary>
    public bool ShowStatusNote => _kind is LookupKind.TaskStatus or LookupKind.CrStatus;

    public string PrimaryButtonText => _context.Strings.GetString(IsUnused || ClearInstead ? "LookupRemove_RemoveButton" : "LookupRemove_RemoveAndReplaceButton");

    public string CloseButtonText => _context.Strings.GetString("Common_Cancel");

    public bool CanRemove => !IsLoading && !IsSaving && Usage is not null
        && (IsUnused || (ShowClearOption && ClearInstead) || (SelectedReplacementIndex >= 0 && SelectedReplacementIndex < ReplacementChoices.Count));

    /// <summary>Reads the current counts from the database (design §5.9 "show counts before applying").</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        var result = await _useCases.PreviewLookupRemoval.ExecuteAsync(_kind, _id, CancellationToken.None).ConfigureAwait(true);
        IsLoading = false;
        if (!result.IsSuccess)
        {
            Error = CommandErrorText.Describe(result.Error!, "Record_Settings", _context.Strings);
            return;
        }

        Usage = result.Value;
        UsageLines = Describe(result.Value);
    }

    /// <summary>Primary: removes the value with the confirmed counts. The dialog stays open on failure.</summary>
    public async Task<bool> RemoveAsync()
    {
        if (!CanRemove || Usage is not { } confirmed)
        {
            return false;
        }

        if (_context.IsEditingBlocked)
        {
            Error = _context.Strings.GetString("Error_EditingBlocked");
            return false;
        }

        Error = null;
        UsageChangedMessage = null;
        Guid? replacement = IsUnused || ClearInstead ? null : ReplacementChoices[SelectedReplacementIndex].Id;
        var operation = _retryOperation ?? OperationId.New();
        IsSaving = true;
        _context.SaveStatus.Begin();
        var result = await _useCases.RemoveLookup.ExecuteAsync(new RemoveLookupCommand(operation, _kind, _id, replacement, confirmed), CancellationToken.None).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            var failure = CommandFailure.From(result.Error!, operation, "Record_Settings", _context.Strings);
            _context.SaveStatus.Report(failure);
            _retryOperation = failure.RequiresSameOperationOnRetry ? operation : null;
            IsSaving = false;
            if (result.Error is ValidationFailedError { Errors: [{ Code: DomainErrorCodes.LookupUsageChanged }, ..] })
            {
                UsageChangedMessage = _context.Strings.GetString("LookupRemove_UsageChanged");
                await LoadAsync().ConfigureAwait(true);
            }
            else
            {
                Error = failure.Message;
            }

            _context.Announce(failure.Message, isFailure: true);
            return false;
        }

        _context.SaveStatus.Complete();
        _retryOperation = null;
        await _context.WhenDisplayedAsync(result.Value.CommitSequence, DisplayTimeout).ConfigureAwait(true);
        IsSaving = false;
        _context.Announce(_context.Strings.Format("LookupRemove_Done", _name));
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private List<string> Describe(LookupUsageCounts usage)
    {
        var strings = _context.Strings;
        var lines = new List<string>();
        void Add(int count, string key)
        {
            if (count > 0)
            {
                lines.Add(strings.Format(key, count));
            }
        }

        Add(usage.Tasks, "LookupRemove_UsageTasks");
        Add(usage.SubtaskCards, "LookupRemove_UsageSubtaskCards");
        Add(usage.ChangeRequests - usage.CrOwners, "LookupRemove_UsageCrs");
        Add(usage.CrOwners, "LookupRemove_UsageCrOwners");
        Add(usage.ReviewItems, "LookupRemove_UsageReviews");
        foreach (var role in Enum.GetValues<WorkspaceDefaultRoles>().Where(r => r != WorkspaceDefaultRoles.None && usage.Defaults.HasFlag(r)))
        {
            lines.Add(strings.GetString("LookupRemove_Default_" + role));
        }

        if (lines.Count == 0)
        {
            lines.Add(strings.GetString("LookupRemove_Unused"));
        }

        return lines;
    }
}
