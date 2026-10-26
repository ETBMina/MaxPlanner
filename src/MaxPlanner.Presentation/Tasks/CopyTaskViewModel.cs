using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tasks;

public enum CopyStep
{
    Options,
    LinkPick,
    LinkConfirm,
    Review,
}

/// <summary>
/// The Copy task dialog (B-06, AC-09, design §5.5): options, an optional permanent link, then a read-only
/// review of the resulting draft before Create copy. Cancel creates nothing. Use on the UI thread.
/// </summary>
public sealed partial class CopyTaskViewModel : ObservableObject, IDialogViewModel, IBusyDialog
{
    private readonly WorkspaceContext _context;
    private readonly TaskReadModel _source;
    private readonly TaskId _newId = new(Guid.NewGuid());
    private readonly StatusOption? _sourceStatus;
    private readonly StatusOption? _defaultStatus;
    private OperationId? _retryOperation;

    public CopyTaskViewModel(WorkspaceContext context, TaskReadModel source)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        var workflow = context.Store.CurrentWorkflow ?? throw new InvalidOperationException("The workflow is not loaded.");
        StatusOptions = [.. workflow.TaskStatuses.OrderBy(s => s.Order).Select(s => new StatusOption(s.Id, s.Name.Display))];
        PriorityOptions = [.. workflow.Priorities.OrderBy(p => p.Order).Select(p => new PriorityOption(p.Id, p.Name.Display))];
        _sourceStatus = StatusOptions.FirstOrDefault(o => o.Id == source.Status.Id);
        _defaultStatus = StatusOptions.FirstOrDefault(o => o.Id == workflow.Defaults.DefaultTaskStatusId);
        CrPicker = new CrPickerViewModel(context.Store, context.Strings);

        // B-06: the title is prefilled from the effective (possibly inherited) title; Service starts blank;
        // status is the configured default; priority is the source's if it still exists.
        Title = source.EffectiveTitle;
        SelectedStatus = _defaultStatus;
        SelectedPriority = PriorityOptions.FirstOrDefault(o => o.Id == source.Priority.Id)
            ?? PriorityOptions.FirstOrDefault(o => o.Id == workflow.Defaults.DefaultPriorityId);
    }

    public event EventHandler? CloseRequested;

    public IReadOnlyList<StatusOption> StatusOptions { get; }

    public IReadOnlyList<PriorityOption> PriorityOptions { get; }

    public CrPickerViewModel CrPicker { get; }

    public ObservableCollection<string> ReviewLines { get; } = [];

    /// <summary>"Copy of: Card limits API · CR-1042".</summary>
    public string SourceCaption => _source.Cr.CrNumber is { } number
        ? _context.Strings.Format("Copy_SourceCaptionLinked", _source.EffectiveTitle, number)
        : _context.Strings.Format("Copy_SourceCaption", _source.EffectiveTitle);

    public TaskId? CreatedTaskId { get; private set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Service { get; set; } = string.Empty;

    [ObservableProperty]
    public partial StatusOption? SelectedStatus { get; set; }

    [ObservableProperty]
    public partial PriorityOption? SelectedPriority { get; set; }

    /// <summary>B-06 "Keep current status" (off by default): the source status, or the default if it no longer exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseStatus))]
    public partial bool KeepCurrentStatus { get; set; }

    [ObservableProperty]
    public partial bool CopyPeople { get; set; }

    [ObservableProperty]
    public partial bool CopyDates { get; set; }

    [ObservableProperty]
    public partial bool CopyTags { get; set; }

    [ObservableProperty]
    public partial bool CopyNotes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreserveCompletion))]
    public partial bool CopyChecklistItems { get; set; }

    /// <summary>B-06 design default: copied items start unchecked unless this is on.</summary>
    [ObservableProperty]
    public partial bool PreserveCompletion { get; set; }

    [ObservableProperty]
    public partial bool CopyCustomValues { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingLink), nameof(IsTitleEditable), nameof(ServiceHeader), nameof(PendingLinkWarning), nameof(LinkStateText))]
    public partial CrChoice? PendingLink { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptionsStep), nameof(IsLinkPickStep), nameof(IsLinkConfirmStep), nameof(IsReviewStep), nameof(PrimaryButtonText), nameof(CanPrimary))]
    public partial CopyStep CurrentStep { get; private set; }

    [ObservableProperty]
    public partial string? TitleError { get; private set; }

    [ObservableProperty]
    public partial string? ServiceError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrimary))]
    public partial bool IsSaving { get; private set; }

    public bool HasError => Error is not null;

    public bool CanChooseStatus => !KeepCurrentStatus;

    public bool CanPreserveCompletion => CopyChecklistItems;

    public bool HasPendingLink => PendingLink is not null;

    /// <summary>A linked copy shows its CR's title, so its own title is not used (ADR 0005).</summary>
    public bool IsTitleEditable => PendingLink is null;

    public string ServiceHeader => _context.Strings.GetString(PendingLink is null ? "TaskEditor_ServiceHeader" : "TaskEditor_ServiceRequiredHeader");

    public string PendingLinkWarning => PendingLink is { } cr ? _context.Strings.Format("TaskEditor_PendingLinkWarning", cr.Title, cr.Number) : string.Empty;

    /// <summary>"New copy will be unlinked." until a CR is chosen (B-06).</summary>
    public string LinkStateText => PendingLink is { } cr ? _context.Strings.Format("Copy_WillLink", cr.Number) : _context.Strings.GetString("Copy_Unlinked");

    public bool IsOptionsStep => CurrentStep == CopyStep.Options;

    public bool IsLinkPickStep => CurrentStep == CopyStep.LinkPick;

    public bool IsLinkConfirmStep => CurrentStep == CopyStep.LinkConfirm;

    public bool IsReviewStep => CurrentStep == CopyStep.Review;

    public string PrimaryButtonText => _context.Strings.GetString(CurrentStep == CopyStep.Review ? "Copy_CreateButton" : "Copy_ReviewButton");

    public bool CanPrimary => !IsSaving && CurrentStep is CopyStep.Options or CopyStep.Review;

    /// <summary>The dialog's primary button: Options → Review → Create copy.</summary>
    public async Task<bool> PrimaryAsync()
    {
        switch (CurrentStep)
        {
            case CopyStep.Options:
                GoToReview();
                return false;
            case CopyStep.Review:
                return await CreateAsync().ConfigureAwait(true);
            default:
                return false;
        }
    }

    public void GoToReview()
    {
        TitleError = ServiceError = null;
        if (PendingLink is not null && string.IsNullOrWhiteSpace(Service))
        {
            ServiceError = _context.Strings.GetString("Error_ServiceRequiredWhenLinked");
            return;
        }

        if (PendingLink is null && string.IsNullOrWhiteSpace(Title))
        {
            TitleError = _context.Strings.Format("Error_Required", _context.Strings.GetString("Field_Title"));
            return;
        }

        ReviewLines.Clear();
        var strings = _context.Strings;
        var none = strings.GetString("Common_None");
        ReviewLines.Add(strings.Format("Copy_ReviewTitle", PendingLink?.Title ?? Title.Trim()));
        ReviewLines.Add(strings.Format("Copy_ReviewService", Service.Trim().Length == 0 ? none : Service.Trim()));
        ReviewLines.Add(strings.Format("Copy_ReviewStatus", ResolvedStatus?.Name ?? none));
        ReviewLines.Add(strings.Format("Copy_ReviewPriority", SelectedPriority?.Name ?? none));
        ReviewLines.Add(LinkStateText);
        var copied = new List<string>();
        AddIf(CopyPeople, "Copy_People");
        AddIf(CopyDates, "Copy_Dates");
        AddIf(CopyTags, "Copy_Tags");
        AddIf(CopyNotes, "Copy_Notes");
        AddIf(CopyChecklistItems, PreserveCompletion ? "Copy_ChecklistPreserved" : "Copy_Checklist");
        AddIf(CopyCustomValues, "Copy_CustomValues");
        ReviewLines.Add(strings.Format("Copy_ReviewCopied", copied.Count == 0 ? none : string.Join(", ", copied)));
        ReviewLines.Add(strings.GetString("Copy_NoSubtaskCards"));
        CurrentStep = CopyStep.Review;

        void AddIf(bool on, string key)
        {
            if (on)
            {
                copied.Add(strings.GetString(key));
            }
        }
    }

    public void BeginLink()
    {
        CrPicker.Refresh();
        CurrentStep = CopyStep.LinkPick;
    }

    public void LinkNext()
    {
        if (CurrentStep == CopyStep.LinkPick && CrPicker.Selected is not null)
        {
            CurrentStep = CopyStep.LinkConfirm;
        }
    }

    /// <summary>"Use this CR": the copy will be created linked, permanently (B-02.4); Service becomes required.</summary>
    public bool ConfirmLink()
    {
        if (CurrentStep != CopyStep.LinkConfirm || CrPicker.Selected is not { } chosen)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Service))
        {
            ServiceError = _context.Strings.GetString("Error_ServiceRequiredWhenLinked");
            return false;
        }

        ServiceError = null;
        PendingLink = chosen;
        CurrentStep = CopyStep.Options;
        return true;
    }

    public void StepBack() => CurrentStep = CurrentStep switch
    {
        CopyStep.LinkConfirm => CopyStep.LinkPick,
        _ => CopyStep.Options,
    };

    /// <summary>Create copy: one CopyTask command; on failure the dialog returns to the options with the errors.</summary>
    public async Task<bool> CreateAsync()
    {
        if (IsSaving)
        {
            return false;
        }

        if (_context.IsEditingBlocked)
        {
            Error = _context.Strings.GetString("Error_EditingBlocked");
            return false;
        }

        IsSaving = true;
        Error = null;
        var operation = _retryOperation ?? OperationId.New();
        var options = new CopyOptions
        {
            CopyStatus = KeepCurrentStatus,
            CopyPeople = CopyPeople,
            CopyDates = CopyDates,
            CopyTags = CopyTags,
            CopyNotes = CopyNotes,
            CopyChecklistItems = CopyChecklistItems,
            PreserveChecklistCompletion = CopyChecklistItems && PreserveCompletion,
            CopyCustomValues = CopyCustomValues,
        };
        var link = PendingLink is { } cr ? new CrLinkTarget(cr.Id, cr.Revision) : null;
        _context.SaveStatus.Begin();
        var result = await _context.UseCases.CopyTask.ExecuteAsync(
            new CopyTaskCommand(operation, _source.Id, _newId, PendingLink is null ? Title : null, Service, options,
                KeepCurrentStatus ? null : SelectedStatus?.Id, SelectedPriority?.Id, link),
            CancellationToken.None).ConfigureAwait(true);
        IsSaving = false;
        if (!result.IsSuccess)
        {
            var failure = CommandFailure.From(result.Error!, operation, "Record_Task", _context.Strings);
            _context.SaveStatus.Report(failure);
            _retryOperation = failure.RequiresSameOperationOnRetry ? operation : null;
            Error = failure.Message;
            if (failure.Error is ValidationFailedError validation)
            {
                foreach (var error in validation.Errors)
                {
                    var text = CommandErrorText.Describe(error, _context.Strings);
                    if (error.Field == "Title")
                    {
                        TitleError = text;
                    }
                    else if (error.Field == "Service" || error.Code == DomainErrorCodes.ServiceRequiredWhenLinked)
                    {
                        ServiceError = text;
                    }
                }

                CurrentStep = CopyStep.Options;
            }

            _context.Announce(failure.Message, isFailure: true);
            return false;
        }

        _context.SaveStatus.Complete();
        CreatedTaskId = result.Value.Value;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private StatusOption? ResolvedStatus => KeepCurrentStatus ? _sourceStatus ?? _defaultStatus : SelectedStatus;

    partial void OnKeepCurrentStatusChanged(bool value) => SelectedStatus = value ? _sourceStatus ?? _defaultStatus : _defaultStatus;
}
