using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.SubtaskCards;

/// <summary>The inline steps of the Subtask card editor (design §5.0: nested decisions never open a second dialog).</summary>
public enum SubtaskEditorStep
{
    Form,
    ConflictReview,
    ReloadConfirm,
}

/// <summary>What the Subtask card editor asked for after it closes (design §5.0: dialogs never stack).</summary>
public enum SubtaskEditorFollowUp
{
    None,
    Delete,
    OpenParent,
}

/// <summary>
/// The Subtask card editor (B-16, design §5.2). It edits a draft of the card's own fields (Subtitle, Status,
/// dates, Notes) and commits it through one use case. The parent block (title, service, CR values) is
/// read-only and always shows the parent's current committed state (T-13). Use on the UI thread.
/// </summary>
/// <remarks>
/// <para>B-16.4: saving never changes the parent task; date automation applies to the card's own dates.</para>
/// <para>Like the task editor, a clean draft follows later commits and a dirty one is never replaced
/// silently: a save against a newer revision is a conflict, reviewed field by field or reloaded (B-02.9).</para>
/// <para>B-16.6: while the parent is archived the card is read-only.</para>
/// </remarks>
public sealed partial class SubtaskCardEditorViewModel : ObservableObject, IDialogViewModel, IDirtyCloseEditor, IDisposable
{
    private const string SubtitleKey = "Subtitle";
    private const string StatusKey = "Status";
    private const string StartKey = "StartDate";
    private const string EndKey = "EndDate";
    private const string NotesKey = "Notes";

    private static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] StateProperties =
    [
        nameof(Caption), nameof(ParentTitle), nameof(ParentService), nameof(CrNumberText), nameof(TaskNumberText), nameof(SquadText),
        nameof(OwnerText), nameof(IsReadOnly), nameof(IsParentArchived), nameof(CanEditFields), nameof(CanSave), nameof(CanDelete),
        nameof(IsFormStep), nameof(IsConflictReviewStep), nameof(IsReloadConfirmStep), nameof(HasValidationSummary), nameof(HasSaveError),
        nameof(CanOpenParent),
    ];

    private readonly WorkspaceContext _context;
    private readonly WorkflowSnapshot _workflow;
    private readonly ObservableCollection<ConflictRowViewModel> _conflictRows = [];
    private readonly IDisposable _commits;
    private readonly SubtaskCardId _cardId;
    private readonly TaskId _parentId;
    private SubtaskCardReadModel? _base;
    private DraftSnapshot _baseValues;
    private bool _startCleared;
    private bool _endCleared;
    private bool _isLoading;
    private bool _closeApproved;
    private OperationId? _retryOperation;

    private SubtaskCardEditorViewModel(WorkspaceContext context, SubtaskCardId? cardId, TaskId parentId, TaskStatusId? initialStatus)
    {
        _context = context;
        _workflow = context.Store.CurrentWorkflow
            ?? throw new InvalidOperationException("The Subtask card editor needs the loaded workflow; editing is blocked until the store is ready.");
        IsNew = cardId is null;
        _cardId = cardId ?? new SubtaskCardId(Guid.NewGuid());
        _parentId = parentId;
        StatusOptions = [.. _workflow.TaskStatuses.OrderBy(s => s.Order).Select(s => new StatusOption(s.Id, s.Name.Display))];
        ConflictRows = new ReadOnlyObservableCollection<ConflictRowViewModel>(_conflictRows);
        Parent = Lookup(context.Store.Tasks.Lookup(parentId));

        if (IsNew)
        {
            var status = initialStatus is { } s && _workflow.FindTaskStatus(s) is not null ? s : _workflow.Defaults.DefaultTaskStatusId;
            _isLoading = true;
            SelectedStatus = StatusOptions.FirstOrDefault(o => o.Id == status);
            _isLoading = false;
            _baseValues = Capture();
            if (Parent is null)
            {
                IsDeletedElsewhere = true;
                SaveError = context.Strings.GetString("SubtaskEditor_ParentDeleted");
            }
        }
        else
        {
            Latest = Lookup(context.Store.SubtaskCards.Lookup(_cardId));
            _baseValues = Capture();
            if (Latest is not null)
            {
                Load(Latest);
            }
            else
            {
                IsDeletedElsewhere = true;
                SaveError = context.Strings.GetString("SubtaskEditor_DeletedElsewhere");
            }
        }

        _commits = context.Store.Committed.ObserveOn(context.UiScheduler).Subscribe(_ => OnCommitted());
    }

    public event EventHandler? CloseRequested;

    /// <summary>Asks the view to focus a field key such as <c>Subtitle</c> (design §5.0).</summary>
    public event EventHandler<string>? FocusRequested;

    public bool IsNew { get; }

    /// <summary>The edited card, or null for a new one (until it is created).</summary>
    public SubtaskCardId? ExistingCardId => IsNew ? null : _cardId;

    public SubtaskCardId? CreatedCardId { get; private set; }

    public TaskId ParentTaskId => _parentId;

    public SubtaskEditorFollowUp FollowUp { get; private set; }

    public IReadOnlyList<StatusOption> StatusOptions { get; }

    public ReadOnlyObservableCollection<ConflictRowViewModel> ConflictRows { get; }

    /// <summary>The committed card now (null for a new card or after a deletion elsewhere).</summary>
    public SubtaskCardReadModel? Latest { get; private set; }

    /// <summary>The parent task as it is now (T-13: inherited values are always current).</summary>
    public TaskReadModel? Parent { get; private set; }

    public string DialogTitle => _context.Strings.GetString(IsNew ? "SubtaskEditor_TitleNew" : "SubtaskEditor_Title");

    public string PrimaryButtonText => _context.Strings.GetString(IsNew ? "SubtaskEditor_CreateButton" : "TaskEditor_SaveButton");

    public string CloseButtonText => _context.Strings.GetString("Common_Cancel");

    /// <summary>"Subtask of Card limits API" (design §5.2 header).</summary>
    public string Caption => _context.Strings.Format("Board_SubtaskOf", ParentTitle);

    public string ParentTitle => Parent?.EffectiveTitle ?? Latest?.ParentTitle ?? string.Empty;

    public string ParentService => Parent?.Service ?? _context.Strings.GetString("Common_None");

    public string CrNumberText => Parent?.Cr.CrNumber ?? _context.Strings.GetString("Grid_NoCr");

    public string TaskNumberText => Parent?.Cr.TaskNumber ?? _context.Strings.GetString("TaskEditor_NoTaskNumber");

    /// <summary>D15: "General" when the parent is unlinked or its CR has no squad.</summary>
    public string SquadText => Parent?.Cr.SquadName ?? _context.Strings.GetString("Squad_General");

    public string OwnerText => Parent?.Cr.OwnerName ?? _context.Strings.GetString("TaskEditor_NoOwner");

    /// <summary>B-04 helpers come from the status flags, never from labels.</summary>
    public string StartHelper => Helper("TaskEditor_StartHelper", _workflow.TaskStatuses.Where(s => s.SetsStartDate));

    public string EndHelper => Helper("TaskEditor_EndHelper", _workflow.TaskStatuses.Where(s => s.IsCompleted));

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial StatusOption? SelectedStatus { get; set; }

    /// <summary>Bound to a <c>CalendarDatePicker</c>; the local calendar day (B-04).</summary>
    [ObservableProperty]
    public partial DateTimeOffset? StartDate { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? EndDate { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SubtitleError { get; private set; }

    [ObservableProperty]
    public partial string? StatusError { get; private set; }

    [ObservableProperty]
    public partial string? DateError { get; private set; }

    [ObservableProperty]
    public partial string? NotesError { get; private set; }

    [ObservableProperty]
    public partial string? ValidationSummary { get; private set; }

    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    public partial SubtaskEditorStep CurrentStep { get; private set; }

    [ObservableProperty]
    public partial bool IsDirtyClosePromptOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsConflict { get; private set; }

    [ObservableProperty]
    public partial bool IsDeletedElsewhere { get; private set; }

    /// <summary>B-16.6: the cards of an archived parent are read-only until it is restored.</summary>
    public bool IsParentArchived => Parent?.IsArchived ?? Latest?.IsParentArchived ?? false;

    public bool IsReadOnly => IsParentArchived || IsDeletedElsewhere;

    public bool CanEditFields => !IsReadOnly && !IsSaving;

    public bool CanSave => !IsSaving && !IsReadOnly && CurrentStep == SubtaskEditorStep.Form;

    public bool CanDelete => !IsNew && !IsReadOnly && !IsSaving;

    public bool CanOpenParent => Parent is not null && !IsSaving;

    public bool IsFormStep => CurrentStep == SubtaskEditorStep.Form;

    public bool IsConflictReviewStep => CurrentStep == SubtaskEditorStep.ConflictReview;

    public bool IsReloadConfirmStep => CurrentStep == SubtaskEditorStep.ReloadConfirm;

    public bool HasValidationSummary => ValidationSummary is not null;

    public bool HasSaveError => SaveError is not null;

    /// <summary>Edits an existing Subtask card (Board/Grid Open, task editor list).</summary>
    public static SubtaskCardEditorViewModel ForCard(WorkspaceContext context, SubtaskCardId cardId)
    {
        ArgumentNullException.ThrowIfNull(context);
        var card = context.Store.SubtaskCards.Lookup(cardId);

        // A card deleted meanwhile still opens, read-only, with the "deleted elsewhere" message.
        var parentId = card.HasValue ? card.Value.ParentTaskId : default;
        return new SubtaskCardEditorViewModel(context, cardId, parentId, initialStatus: null);
    }

    /// <summary>A new Subtask card under <paramref name="parentId"/> (B-16.3 "Add Subtask card").</summary>
    /// <param name="status">Board creation passes the column's status; null uses the default task status.</param>
    public static SubtaskCardEditorViewModel ForNewCard(WorkspaceContext context, TaskId parentId, TaskStatusId? status = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SubtaskCardEditorViewModel(context, cardId: null, parentId, status);
    }

    /// <summary>
    /// Saves the whole draft as one command (B-02.8). Repeated calls while saving are ignored. On success the
    /// editor closes once the result is displayed; on failure the draft, its errors, and the dialog stay.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        if (!CanSave)
        {
            return false;
        }

        if (!IsNew && !IsDirty)
        {
            Approve();
            return true;
        }

        if (_context.IsEditingBlocked)
        {
            SaveError = _context.Strings.GetString("Error_EditingBlocked");
            RaiseState();
            return false;
        }

        ClearErrors();
        var operation = _retryOperation ?? OperationId.New();
        IsSaving = true;
        IsDirtyClosePromptOpen = false;
        RaiseState();
        _context.SaveStatus.Begin();
        var (error, sequence) = await ExecuteSaveAsync(operation).ConfigureAwait(true);
        if (error is not null)
        {
            var failure = CommandFailure.From(error, operation, "Record_SubtaskCard", _context.Strings);
            _context.SaveStatus.Report(failure);

            // T-05: only an unknown outcome is retried under the same operation; anything else is a new intent.
            _retryOperation = failure.RequiresSameOperationOnRetry ? operation : null;
            IsSaving = false;
            ShowFailure(failure);
            RaiseState();
            return false;
        }

        _context.SaveStatus.Complete();
        _retryOperation = null;
        await _context.WhenDisplayedAsync(sequence, DisplayTimeout).ConfigureAwait(true);
        IsSaving = false;
        _context.Announce(IsNew ? _context.Strings.Format("SubtaskCard_Created", Subtitle.Trim()) : _context.Strings.GetString("SaveStatus_Saved"));
        Approve();
        return true;
    }

    /// <summary>Esc, Cancel, an outside click or a follow-up: a dirty draft asks for the Save / Discard / Continue editing confirmation (design §5.8).</summary>
    /// <returns>True when the dialog may close.</returns>
    public bool TryClose()
    {
        if (_closeApproved)
        {
            return true;
        }

        if (IsSaving)
        {
            return false;
        }

        if (IsDirty && !IsReadOnly)
        {
            IsDirtyClosePromptOpen = true;
            return false;
        }

        _closeApproved = true;
        return true;
    }

    public async Task<bool> SaveFromPromptAsync()
    {
        IsDirtyClosePromptOpen = false;
        var saved = await SaveAsync().ConfigureAwait(true);
        if (!saved)
        {
            // A failed save keeps the editor open; the window close the prompt answered is abandoned.
            IsWindowClosePending = false;
        }

        return saved;
    }

    /// <inheritdoc/>
    public bool IsWindowClosePending { get; private set; }

    /// <inheritdoc/>
    public bool TryCloseForWindow()
    {
        if (TryClose())
        {
            return true;
        }

        IsWindowClosePending = IsDirtyClosePromptOpen;
        return false;
    }

    public void DiscardChanges()
    {
        IsDirtyClosePromptOpen = false;
        Approve();
    }

    public void ContinueEditing()
    {
        IsDirtyClosePromptOpen = false;
        IsWindowClosePending = false;
        FollowUp = SubtaskEditorFollowUp.None;
    }

    /// <summary>More › Delete… or Open parent: closes this dialog first (after the dirty prompt), then the caller opens the next one.</summary>
    public void RequestFollowUp(SubtaskEditorFollowUp followUp)
    {
        if (IsSaving || (followUp == SubtaskEditorFollowUp.Delete && !CanDelete))
        {
            return;
        }

        FollowUp = followUp;
        if (TryClose())
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ClearStartDate()
    {
        StartDate = null;
        _startCleared = true;
        RecomputeDirty();
    }

    public void ClearEndDate()
    {
        EndDate = null;
        _endCleared = true;
        RecomputeDirty();
    }

    /// <summary>Review changes (design §5.10): Field | Your draft | Saved now for each differing field.</summary>
    public void ReviewConflict()
    {
        if (!IsConflict || Latest is not { } latest)
        {
            return;
        }

        var mine = Capture();
        var saved = ValuesOf(latest);
        _conflictRows.Clear();
        foreach (var key in mine.Keys.Where(k => !mine.SameAs(saved, k)))
        {
            _conflictRows.Add(new ConflictRowViewModel(key, _context.Strings.GetString("Field_" + key), Describe(mine, key), Describe(saved, key)));
        }

        CurrentStep = SubtaskEditorStep.ConflictReview;
        RaiseState();
    }

    /// <summary>Apply to draft: the draft moves to the saved revision, keeping the user's value where Use mine is on.</summary>
    public void ApplyConflictReview()
    {
        if (Latest is not { } latest)
        {
            return;
        }

        var keep = _conflictRows.Where(r => r.UseMine).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var mine = Capture();
        Load(latest);
        _isLoading = true;
        if (keep.Contains(SubtitleKey))
        {
            Subtitle = (string)mine[SubtitleKey]!;
        }

        if (keep.Contains(StatusKey))
        {
            SelectedStatus = StatusOptions.FirstOrDefault(o => Equals(o.Id, mine[StatusKey]));
        }

        if (keep.Contains(StartKey))
        {
            StartDate = DraftDates.ToPicker((DateOnly?)mine[StartKey]);
        }

        if (keep.Contains(EndKey))
        {
            EndDate = DraftDates.ToPicker((DateOnly?)mine[EndKey]);
        }

        if (keep.Contains(NotesKey))
        {
            Notes = (string)mine[NotesKey]!;
        }

        _isLoading = false;
        IsConflict = false;
        SaveError = null;
        _conflictRows.Clear();
        CurrentStep = SubtaskEditorStep.Form;
        RecomputeDirty();
        RaiseState();
    }

    public void BeginReload()
    {
        CurrentStep = SubtaskEditorStep.ReloadConfirm;
        RaiseState();
    }

    public void ConfirmReload()
    {
        if (Latest is { } latest)
        {
            Load(latest);
        }

        IsConflict = false;
        SaveError = null;
        CurrentStep = SubtaskEditorStep.Form;
        RaiseState();
    }

    public void StepBack()
    {
        CurrentStep = SubtaskEditorStep.Form;
        RaiseState();
    }

    public void Dispose() => _commits.Dispose();

    partial void OnSubtitleChanged(string value) => RecomputeDirty();

    partial void OnSelectedStatusChanged(StatusOption? value) => RecomputeDirty();

    partial void OnStartDateChanged(DateTimeOffset? value) => RecomputeDirty();

    partial void OnEndDateChanged(DateTimeOffset? value) => RecomputeDirty();

    partial void OnNotesChanged(string value) => RecomputeDirty();

    partial void OnIsSavingChanged(bool value) => RaiseState();

    private static T? Lookup<T>(DynamicData.Kernel.Optional<T> value)
        where T : class => value.HasValue ? value.Value : null;

    private async Task<(AppError? Error, long Sequence)> ExecuteSaveAsync(OperationId operation)
    {
        var useCases = _context.UseCases;
        var start = DraftDates.Intent(DraftDates.FromPicker(StartDate), (DateOnly?)_baseValues[StartKey], _startCleared);
        var end = DraftDates.Intent(DraftDates.FromPicker(EndDate), (DateOnly?)_baseValues[EndKey], _endCleared);
        if (IsNew)
        {
            var created = await useCases.CreateSubtaskCard.ExecuteAsync(
                new CreateSubtaskCardCommand(operation, _parentId, _cardId, Subtitle, Notes, SelectedStatus?.Id, start, end), CancellationToken.None).ConfigureAwait(true);
            CreatedCardId = created.IsSuccess ? created.Value.Value : null;
            return created.IsSuccess ? (null, created.Value.CommitSequence) : (created.Error, 0);
        }

        var card = _base!;
        var updated = await useCases.UpdateSubtaskCard.ExecuteAsync(
            new UpdateSubtaskCardCommand(operation, card.Id, card.Revision, Subtitle, Notes, SelectedStatus?.Id ?? card.Status.Id, start, end),
            CancellationToken.None).ConfigureAwait(true);
        return updated.IsSuccess ? (null, updated.Value.CommitSequence) : (updated.Error, 0);
    }

    private void ShowFailure(CommandFailure failure)
    {
        switch (failure.Error)
        {
            case ValidationFailedError validation:
                ShowFieldErrors(validation.Errors);
                break;
            case ConcurrencyConflictError:
                IsConflict = true;
                SaveError = _context.Strings.Format("Editor_ConflictMessage", _context.Strings.GetString("Record_SubtaskCard"));
                break;
            default:
                SaveError = failure.Message;
                break;
        }

        _context.Announce(failure.Message, isFailure: true);
    }

    private void ShowFieldErrors(IReadOnlyList<DomainError> errors)
    {
        string? first = null;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var error in errors)
        {
            var text = CommandErrorText.Describe(error, _context.Strings);
            string key;
            switch (error.Field)
            {
                case SubtitleKey:
                    SubtitleError = text;
                    key = SubtitleKey;
                    break;
                case StatusKey:
                    StatusError = text;
                    key = StatusKey;
                    break;
                case StartKey or EndKey:
                    DateError = text;
                    key = EndKey;
                    break;
                case NotesKey:
                    NotesError = text;
                    key = NotesKey;
                    break;
                default:
                    SaveError = text;
                    key = "Summary";
                    break;
            }

            first ??= key;
            fields.Add(key);
        }

        ValidationSummary = _context.Strings.Format(fields.Count == 1 ? "TaskEditor_FixField" : "TaskEditor_FixFields", fields.Count);
        if (first is not null)
        {
            FocusRequested?.Invoke(this, first);
        }
    }

    private void ClearErrors()
    {
        SubtitleError = StatusError = DateError = NotesError = null;
        ValidationSummary = null;
        SaveError = null;
    }

    private void Approve()
    {
        _closeApproved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Load(SubtaskCardReadModel model)
    {
        _isLoading = true;
        _base = model;
        Latest = model;
        Subtitle = model.Subtitle;
        SelectedStatus = StatusOptions.FirstOrDefault(o => o.Id == model.Status.Id);
        StartDate = DraftDates.ToPicker(model.StartDate);
        EndDate = DraftDates.ToPicker(model.EndDate);
        Notes = model.Notes ?? string.Empty;
        _startCleared = _endCleared = false;
        _baseValues = ValuesOf(model);
        _isLoading = false;
        RecomputeDirty();
        RaiseState();
    }

    private DraftSnapshot Capture() => new(
    [
        (SubtitleKey, Subtitle.Trim()),
        (StatusKey, SelectedStatus?.Id),
        (StartKey, DraftDates.FromPicker(StartDate)),
        (EndKey, DraftDates.FromPicker(EndDate)),
        (NotesKey, Notes),
    ]);

    private static DraftSnapshot ValuesOf(SubtaskCardReadModel model) => new(
    [
        (SubtitleKey, model.Subtitle),
        (StatusKey, (TaskStatusId?)model.Status.Id),
        (StartKey, model.StartDate),
        (EndKey, model.EndDate),
        (NotesKey, model.Notes ?? string.Empty),
    ]);

    private string Describe(DraftSnapshot values, string key)
    {
        var none = _context.Strings.GetString("Common_None");
        return values[key] switch
        {
            null => none,
            string { Length: 0 } => none,
            string text => text,
            TaskStatusId status => _workflow.FindTaskStatus(status)?.Name.Display ?? none,
            DateOnly date => date.ToString("d", CultureInfo.CurrentCulture),
            var other => other.ToString() ?? none,
        };
    }

    private void RecomputeDirty()
    {
        if (!_isLoading)
        {
            IsDirty = !Capture().SameAs(_baseValues) || _startCleared || _endCleared;
        }
    }

    private void OnCommitted()
    {
        Parent = Lookup(_context.Store.Tasks.Lookup(_parentId));
        if (IsNew || CreatedCardId is not null)
        {
            if (Parent is null && CreatedCardId is null)
            {
                IsDeletedElsewhere = true;
                SaveError ??= _context.Strings.GetString("SubtaskEditor_ParentDeleted");
            }

            RaiseState();
            return;
        }

        Latest = Lookup(_context.Store.SubtaskCards.Lookup(_cardId));
        if (Latest is null)
        {
            IsDeletedElsewhere = true;
            SaveError ??= _context.Strings.GetString("SubtaskEditor_DeletedElsewhere");
        }
        else if (!IsDirty && !IsSaving && !IsConflict && Latest.Revision != _base?.Revision)
        {
            // A draft without changes follows the committed card; a dirty one is never replaced (B-02.9).
            Load(Latest);
        }

        RaiseState();
    }

    private string Helper(string key, IEnumerable<TaskStatusDefinition> statuses)
    {
        var names = statuses.OrderBy(s => s.Order).Select(s => s.Name.Display).ToList();
        return names.Count == 0 ? string.Empty : _context.Strings.Format(key, string.Join(", ", names));
    }

    private void RaiseState()
    {
        foreach (var name in StateProperties)
        {
            OnPropertyChanged(name);
        }
    }
}
