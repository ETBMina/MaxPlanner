using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Lookups;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Fields;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tasks;

/// <summary>A priority offered by the editors' Priority box.</summary>
public sealed record PriorityOption(PriorityId Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The task editor (B-02, B-03, B-05, T-13, design §5.0–§5.1, §5.6, §5.8, §5.10). It edits a draft and never
/// changes shared state until Save commits the whole draft through one use case. Use on the UI thread.
/// </summary>
/// <remarks>
/// <para>The draft starts from the committed read model (the base). While the draft has no changes it follows
/// later commits, so for example a checklist toggle on the Checklists page shows here too (AC-06). Once the
/// user changed something the draft is never replaced silently: a save against a newer revision is a
/// conflict, reviewed field by field or reloaded after a confirmation (B-02.9, AC-07).</para>
/// <para>Inherited values (title, task number, squad, owner) are read-only projections of the linked CR and
/// always show its current committed state (T-13). A link chosen here is pending until Save and is permanent
/// once saved; there is no unlink or change (B-03.7, AC-29).</para>
/// </remarks>
public sealed partial class TaskEditorViewModel : ObservableObject, IDialogViewModel, IWindowCloseGuard, IDisposable
{
    private static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] StateProperties =
    [
        nameof(IsLinked), nameof(LinkedCr), nameof(IsTitleEditable), nameof(ShowInheritedTitle), nameof(InheritedTitle),
        nameof(InheritedTitleHelper), nameof(IsServiceRequired), nameof(ServiceHeader), nameof(ServiceHelper), nameof(HasServiceHelper),
        nameof(IsUnlinkedWithoutPending), nameof(HasPendingLink), nameof(PendingLinkWarning), nameof(CanLinkToCr), nameof(TaskNumberText),
        nameof(SquadText), nameof(OwnerText), nameof(CrHelperText), nameof(LinkedCrTitle), nameof(LinkedCrSecondary), nameof(LinkedCrNumber), nameof(OpenCrName),
        nameof(CanEditFields), nameof(CanSave), nameof(CanArchive), nameof(CanRestore), nameof(CanAddSubtaskCard), nameof(CanPopOut),
        nameof(IsFormStep), nameof(IsLinkPickStep), nameof(IsLinkConfirmStep), nameof(IsConflictReviewStep), nameof(IsReloadConfirmStep),
        nameof(Caption), nameof(HasValidationSummary), nameof(HasSaveError), nameof(HasCrChangedMessage), nameof(IsReadOnly),
    ];

    private readonly WorkspaceContext _context;
    private readonly NewNameRegistry _newPeople = new();
    private readonly NewNameRegistry _newTags = new();
    private readonly ObservableCollection<EditorChecklistItemViewModel> _checklist = [];
    private readonly ObservableCollection<EditorSubtaskCardViewModel> _subtaskCards = [];
    private readonly ObservableCollection<ConflictRowViewModel> _conflictRows = [];
    private readonly IDisposable _commits;
    private readonly TaskId _taskId;
    private readonly WorkflowSnapshot _workflow;
    private TaskReadModel? _base;
    private DraftValues _baseValues;
    private bool _startCleared;
    private bool _endCleared;
    private bool _isLoading;
    private bool _closeApproved;
    private OperationId? _retryOperation;
    private readonly ChangeRequestId? _presetLinkId;

    /// <param name="taskId">The task to edit, or null for a new task.</param>
    /// <param name="initialStatus">For a new task: the Board column's status (B-02.1); null uses the default.</param>
    /// <param name="linkTo">
    /// For a new task created from a CR (B-03.5): the saved CR, preselected as the pending permanent link so the
    /// task and its link are created together by Save (B-02.1 "CR creation preselects (and links) the CR").
    /// </param>
    public TaskEditorViewModel(WorkspaceContext context, TaskId? taskId, TaskStatusId? initialStatus, ChangeRequestId? linkTo = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _workflow = context.Store.CurrentWorkflow
            ?? throw new InvalidOperationException("The task editor needs the loaded workflow; editing is blocked until the store is ready.");
        IsNew = taskId is null;
        _taskId = taskId ?? new TaskId(Guid.NewGuid());
        StatusOptions = [.. _workflow.TaskStatuses.OrderBy(s => s.Order).Select(s => new StatusOption(s.Id, s.Name.Display))];
        PriorityOptions = [.. _workflow.Priorities.OrderBy(p => p.Order).Select(p => new PriorityOption(p.Id, p.Name.Display))];
        Designers = new TokenPickerViewModel(context.Strings, People, _newPeople);
        Developers = new TokenPickerViewModel(context.Strings, People, _newPeople);
        Tags = new TokenPickerViewModel(context.Strings, () => _workflow.Tags.Select(t => (t.Id.Value, t.Name.Display)), _newTags);
        Designers.Changed += OnDraftChanged;
        Developers.Changed += OnDraftChanged;
        Tags.Changed += OnDraftChanged;
        CustomFields = new CustomFieldsEditorViewModel(FieldTarget.Task, _workflow, context.Strings);
        CustomFields.Changed += OnDraftChanged;
        ChecklistItems = new ReadOnlyObservableCollection<EditorChecklistItemViewModel>(_checklist);
        SubtaskCards = new ReadOnlyObservableCollection<EditorSubtaskCardViewModel>(_subtaskCards);
        ConflictRows = new ReadOnlyObservableCollection<ConflictRowViewModel>(_conflictRows);
        _checklist.CollectionChanged += OnChecklistChanged;
        CrPicker = new CrPickerViewModel(context.Store, context.Strings);

        if (IsNew)
        {
            var status = initialStatus is { } s && _workflow.FindTaskStatus(s) is not null ? s : _workflow.Defaults.DefaultTaskStatusId;
            _isLoading = true;
            SelectedStatus = StatusOptions.FirstOrDefault(o => o.Id == status);
            SelectedPriority = PriorityOptions.FirstOrDefault(o => o.Id == _workflow.Defaults.DefaultPriorityId);

            // B-11: a new task starts with each custom field's default; it is the base, so it is not a change.
            CustomFields.LoadDefaults();
            _isLoading = false;
            _baseValues = Capture();

            // T-13: the preview comes from the committed CR, and Save checks the revision it was taken from.
            PendingLink = linkTo is { } crId ? CrPicker.Current(crId) : null;
            _presetLinkId = PendingLink?.Id;
        }
        else
        {
            var model = context.Store.Tasks.Lookup(_taskId);
            Latest = model.HasValue ? model.Value : null;
            _baseValues = Capture();
            if (Latest is not null)
            {
                Load(Latest);
            }
            else
            {
                IsDeletedElsewhere = true;
                SaveError = context.Strings.GetString("TaskEditor_DeletedElsewhere");
            }
        }

        RefreshSubtaskCards();
        _commits = context.Store.Committed.ObserveOn(context.UiScheduler).Subscribe(_ => OnCommitted());
    }

    public event EventHandler? CloseRequested;

    /// <summary>Asks the view to focus a field: a field key such as <c>Title</c>, or <c>DirtyClose</c> (design §5.0, §5.8).</summary>
    public event EventHandler<string>? FocusRequested;

    public bool IsNew { get; }

    /// <summary>The edited task, or null for a new one (until it is created).</summary>
    public TaskId? ExistingTaskId => IsNew ? null : _taskId;

    /// <summary>The task created by Save of a new draft.</summary>
    public TaskId? CreatedTaskId { get; private set; }

    public EditorFollowUp FollowUp { get; private set; }

    /// <summary>The Subtask card to open for <see cref="EditorFollowUp.OpenSubtaskCard"/>.</summary>
    public SubtaskCardId? FollowUpCardId { get; private set; }

    public IReadOnlyList<StatusOption> StatusOptions { get; }

    public IReadOnlyList<PriorityOption> PriorityOptions { get; }

    public TokenPickerViewModel Designers { get; }

    public TokenPickerViewModel Developers { get; }

    public TokenPickerViewModel Tags { get; }

    /// <summary>The active task custom fields (B-11), typed per kind (design §5.0).</summary>
    public CustomFieldsEditorViewModel CustomFields { get; }

    public ReadOnlyObservableCollection<EditorChecklistItemViewModel> ChecklistItems { get; }

    public ReadOnlyObservableCollection<EditorSubtaskCardViewModel> SubtaskCards { get; }

    public ReadOnlyObservableCollection<ConflictRowViewModel> ConflictRows { get; }

    public CrPickerViewModel CrPicker { get; }

    /// <summary>The committed state of the task now (null for a new task or after a deletion elsewhere).</summary>
    public TaskReadModel? Latest { get; private set; }

    public string DialogTitle => _context.Strings.GetString(IsNew ? "TaskEditor_TitleNew" : "TaskEditor_Title");

    public string PrimaryButtonText => _context.Strings.GetString(IsNew ? "TaskEditor_CreateButton" : "TaskEditor_SaveButton");

    public string CloseButtonText => _context.Strings.GetString("Common_Cancel");

    /// <summary>"Created Oct 6, 2026" (design §5.0 title row).</summary>
    public string Caption => _base is { } b ? _context.Strings.Format("TaskEditor_CreatedCaption", b.CreatedAtUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)) : string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Service { get; set; } = string.Empty;

    [ObservableProperty]
    public partial StatusOption? SelectedStatus { get; set; }

    [ObservableProperty]
    public partial PriorityOption? SelectedPriority { get; set; }

    /// <summary>Bound to a <c>CalendarDatePicker</c>; the local calendar day, never converted through UTC (B-04).</summary>
    [ObservableProperty]
    public partial DateTimeOffset? StartDate { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? EndDate { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewChecklistText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewSubtaskCardText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? TitleError { get; private set; }

    [ObservableProperty]
    public partial string? ServiceError { get; private set; }

    [ObservableProperty]
    public partial string? StatusError { get; private set; }

    [ObservableProperty]
    public partial string? PriorityError { get; private set; }

    [ObservableProperty]
    public partial string? PeopleError { get; private set; }

    [ObservableProperty]
    public partial string? DateError { get; private set; }

    [ObservableProperty]
    public partial string? NotesError { get; private set; }

    [ObservableProperty]
    public partial string? ChecklistError { get; private set; }

    [ObservableProperty]
    public partial string? SubtaskCardError { get; private set; }

    /// <summary>"Fix 2 fields to save" (design §5.0); shown in the Error InfoBar at the top.</summary>
    [ObservableProperty]
    public partial string? ValidationSummary { get; private set; }

    /// <summary>A non-field failure (storage, conflict text, deleted elsewhere); the draft is kept (AC-07).</summary>
    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    /// <summary>T-13: the pending link's CR changed before Save; the preview was refreshed.</summary>
    [ObservableProperty]
    public partial string? CrChangedMessage { get; private set; }

    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    public partial EditorStep CurrentStep { get; private set; }

    [ObservableProperty]
    public partial bool IsDirtyClosePromptOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsConflict { get; private set; }

    [ObservableProperty]
    public partial bool IsArchived { get; private set; }

    [ObservableProperty]
    public partial bool IsDeletedElsewhere { get; private set; }

    /// <summary>The CR chosen in the link step, not yet saved (design §5.1 group 2 "Pending").</summary>
    [ObservableProperty]
    public partial CrChoice? PendingLink { get; private set; }

    public bool IsReadOnly => IsArchived || IsDeletedElsewhere;

    public bool IsLinked => _base?.Cr.ChangeRequestId is not null;

    /// <summary>The linked CR as it is now (T-13: inherited values are always current).</summary>
    public CrChoice? LinkedCr => (Latest ?? _base)?.Cr.ChangeRequestId is { } id ? CrPicker.Current(id) : null;

    public string LinkedCrNumber => LinkedCr?.Number ?? string.Empty;

    /// <summary>"Open CR-1042": the CR link's accessible name and tooltip (design §8.2).</summary>
    public string OpenCrName => LinkedCr is { } cr ? _context.Strings.Format("TaskEditor_OpenCrName", cr.Number) : string.Empty;

    public string LinkedCrTitle => LinkedCr?.Title ?? string.Empty;

    public string LinkedCrSecondary => LinkedCr?.Secondary ?? string.Empty;

    public bool CanEditFields => !IsReadOnly && !IsSaving;

    public bool IsTitleEditable => !IsLinked && PendingLink is null && CanEditFields;

    public bool ShowInheritedTitle => !IsTitleEditable && (IsLinked || PendingLink is not null);

    public string InheritedTitle => PendingLink?.Title ?? LinkedCr?.Title ?? (Latest ?? _base)?.EffectiveTitle ?? string.Empty;

    /// <summary>"Inherited from CR-1042. Edit it on the CR." or, while pending, what the title will become.</summary>
    public string InheritedTitleHelper => PendingLink is { } pending
        ? _context.Strings.Format("TaskEditor_PendingTitleHelper", pending.Number)
        : LinkedCr is { } cr ? _context.Strings.Format("TaskEditor_InheritedTitleHelper", cr.Number) : string.Empty;

    /// <summary>D10: Service is required on linked tasks and while a link is pending.</summary>
    public bool IsServiceRequired => IsLinked || PendingLink is not null;

    public string ServiceHeader => _context.Strings.GetString(IsServiceRequired ? "TaskEditor_ServiceRequiredHeader" : "TaskEditor_ServiceHeader");

    /// <summary>"Tells this task apart from the other 2 tasks on CR-1042."</summary>
    public string ServiceHelper => (LinkedCr ?? PendingLink) is { } cr
        ? _context.Strings.Format("TaskEditor_ServiceHelper", Math.Max(0, cr.LinkedTaskCount - (IsLinked ? 1 : 0)), cr.Number)
        : string.Empty;

    public bool HasServiceHelper => ServiceHelper.Length > 0;

    public bool IsUnlinkedWithoutPending => !IsLinked && PendingLink is null;

    public bool HasPendingLink => PendingLink is not null;

    /// <summary>B-02.4: the permanence notice shown while a link is pending.</summary>
    public string PendingLinkWarning => PendingLink is { } cr ? _context.Strings.Format("TaskEditor_PendingLinkWarning", cr.Title, cr.Number) : string.Empty;

    /// <summary>B-03.6: only an unlinked task (active or archived) can be linked, and only once (AC-29).</summary>
    public bool CanLinkToCr => !IsLinked && !IsSaving && !IsDeletedElsewhere;

    public string TaskNumberText => (LinkedCr ?? PendingLink) is { } cr
        ? cr.TaskNumber ?? _context.Strings.GetString("TaskEditor_NoTaskNumber")
        : _context.Strings.GetString("TaskEditor_NoTaskNumber");

    /// <summary>D15: "General" when the task is unlinked or its CR has no squad.</summary>
    public string SquadText => (LinkedCr ?? PendingLink)?.SquadName ?? (Latest ?? _base)?.Cr.SquadName ?? _context.Strings.GetString("Squad_General");

    public string OwnerText => (LinkedCr ?? PendingLink)?.OwnerName ?? _context.Strings.GetString("TaskEditor_NoOwner");

    public string CrHelperText => (LinkedCr ?? PendingLink) is { } cr ? _context.Strings.Format("TaskEditor_FromCrHelper", cr.Number) : string.Empty;

    /// <summary>B-04 helper from the status flags, never from labels.</summary>
    public string StartHelper => Helper("TaskEditor_StartHelper", _workflow.TaskStatuses.Where(s => s.SetsStartDate));

    public string EndHelper => Helper("TaskEditor_EndHelper", _workflow.TaskStatuses.Where(s => s.IsCompleted));

    public bool CanSave => !IsSaving && CurrentStep == EditorStep.Form && !IsDeletedElsewhere && (!IsArchived || PendingLink is not null);

    public bool CanArchive => !IsNew && !IsReadOnly && !IsDirty && PendingLink is null && !IsSaving;

    public bool CanRestore => !IsNew && IsArchived && !IsDeletedElsewhere && !IsSaving;

    /// <summary>Subtask cards are created immediately, so the task must exist and be active (design §5.1 group 8).</summary>
    public bool CanAddSubtaskCard => !IsNew && !IsReadOnly && !IsSaving;

    /// <summary>Pop out runs immediately, so it is off while the draft has unsaved changes (design §5.1 group 7).</summary>
    public bool CanPopOut => !IsNew && !IsReadOnly && !IsDirty && !IsSaving && PendingLink is null;

    public bool IsFormStep => CurrentStep == EditorStep.Form;

    public bool IsLinkPickStep => CurrentStep == EditorStep.LinkPick;

    public bool IsLinkConfirmStep => CurrentStep == EditorStep.LinkConfirm;

    public bool IsConflictReviewStep => CurrentStep == EditorStep.ConflictReview;

    public bool IsReloadConfirmStep => CurrentStep == EditorStep.ReloadConfirm;

    public bool HasValidationSummary => ValidationSummary is not null;

    public bool HasSaveError => SaveError is not null;

    public bool HasCrChangedMessage => CrChangedMessage is not null;

    /// <summary>
    /// Saves the whole draft as one command (B-02.8). Repeated calls while saving are ignored. On success
    /// the editor closes once the result is displayed; on failure the draft, its errors, and the dialog stay.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        if (!CanSave)
        {
            return false;
        }

        if (!IsNew && !IsDirty && PendingLink is null)
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
        if (!CustomFields.TryBuild(_base?.CustomValues ?? CustomValue.NoValues, out var customValues))
        {
            ValidationSummary = _context.Strings.Format("TaskEditor_FixField", 1);
            FocusRequested?.Invoke(this, "Custom:" + CustomFields.FirstErrorKey);
            RaiseState();
            return false;
        }

        var operation = _retryOperation ?? OperationId.New();
        IsSaving = true;
        IsDirtyClosePromptOpen = false;
        RaiseState();
        _context.SaveStatus.Begin();
        var (error, sequence) = await ExecuteSaveAsync(operation, customValues).ConfigureAwait(true);
        if (error is not null)
        {
            var failure = CommandFailure.From(error, operation, "Record_Task", _context.Strings);
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
        _context.Announce(_context.Strings.GetString("SaveStatus_Saved"));
        Approve();
        return true;
    }

    /// <summary>
    /// The dialog is closing by Esc, Cancel, or the close button. A dirty draft (or a pending link) cancels
    /// the close and shows the inline Save / Discard / Continue editing prompt (design §5.8).
    /// </summary>
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

        // A link the user chose is a change; the CR a "Create task" route preselected is not (B-03.5).
        if (IsDirty || (PendingLink is not null && PendingLink.Id != _presetLinkId))
        {
            IsDirtyClosePromptOpen = true;
            FocusRequested?.Invoke(this, "DirtyClose");
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

    /// <summary>Discard: closes without saving; nothing is created, including new people and tags (B-02.6).</summary>
    public void DiscardChanges()
    {
        IsDirtyClosePromptOpen = false;
        Approve();
    }

    /// <summary>Continue editing (also Esc on the prompt): the draft stays and any pending follow-up is dropped.</summary>
    public void ContinueEditing()
    {
        IsDirtyClosePromptOpen = false;
        IsWindowClosePending = false;
        FollowUp = EditorFollowUp.None;
    }

    /// <summary>
    /// More › Copy…, Delete…, Add Subtask card, Open CR, or a Subtask card row: closes this dialog first (after
    /// the dirty prompt), then the caller opens the next one.
    /// </summary>
    public void RequestFollowUp(EditorFollowUp followUp)
    {
        if (IsNew || IsSaving || (followUp == EditorFollowUp.OpenCr && !IsLinked) || (followUp == EditorFollowUp.AddSubtaskCard && !CanAddSubtaskCard))
        {
            return;
        }

        FollowUp = followUp;
        if (TryClose())
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A row of the Subtask cards list (design §5.1 group 8): this dialog closes and §5.2 opens.</summary>
    public void OpenSubtaskCard(EditorSubtaskCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        FollowUpCardId = card.Id;
        RequestFollowUp(EditorFollowUp.OpenSubtaskCard);
    }

    /// <summary>Link to CR… (design §5.6 step 1).</summary>
    public void BeginLink()
    {
        if (!CanLinkToCr)
        {
            return;
        }

        CrPicker.Refresh();
        CrPicker.Selected = PendingLink is { } pending ? CrPicker.Results.FirstOrDefault(r => r.Id == pending.Id) : null;
        CurrentStep = EditorStep.LinkPick;
        RaiseState();
    }

    /// <summary>Next: the permanence confirmation (design §5.6 step 2).</summary>
    public void LinkNext()
    {
        if (CurrentStep == EditorStep.LinkPick && CrPicker.Selected is not null)
        {
            CurrentStep = EditorStep.LinkConfirm;
            RaiseState();
        }
    }

    /// <summary>
    /// "Use this CR": the link becomes pending and is created permanently by Save. A Service is required
    /// first (D10), shown in the confirmation step when the draft has none.
    /// </summary>
    public bool ConfirmLink()
    {
        if (CurrentStep != EditorStep.LinkConfirm || CrPicker.Selected is not { } chosen)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Service))
        {
            ServiceError = _context.Strings.GetString("Error_ServiceRequiredWhenLinked");
            FocusRequested?.Invoke(this, "Service");
            return false;
        }

        ServiceError = null;
        CrChangedMessage = null;
        PendingLink = chosen;
        CurrentStep = EditorStep.Form;
        RecomputeDirty();
        RaiseState();
        return true;
    }

    /// <summary>Back (Alt+Left) from an inline step.</summary>
    public void StepBack()
    {
        CurrentStep = CurrentStep == EditorStep.LinkConfirm ? EditorStep.LinkPick : EditorStep.Form;
        RaiseState();
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
        foreach (var key in DraftValues.Keys.Where(k => !mine.SameAs(saved, k)))
        {
            var (mineText, savedText) = key == "CustomFields" ? (CustomFields.DisplayText(None), SavedCustomFieldsText(latest)) : (Describe(mine, key), Describe(saved, key));
            _conflictRows.Add(new ConflictRowViewModel(key, _context.Strings.GetString("Field_" + key), mineText, savedText));
        }

        CurrentStep = EditorStep.ConflictReview;
        RaiseState();
    }

    /// <summary>
    /// Apply to draft: the draft moves to the saved revision, keeping the user's value where Use mine is on
    /// and taking the saved value elsewhere. Nothing is saved until the user saves again.
    /// </summary>
    public void ApplyConflictReview()
    {
        if (Latest is not { } latest)
        {
            return;
        }

        var keep = _conflictRows.Where(r => r.UseMine).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var mine = Capture();
        var mineTokens = (Designers.Tokens.ToList(), Developers.Tokens.ToList(), Tags.Tokens.ToList());
        var mineChecklist = _checklist.Select(i => (i.Id, i.Text, i.IsCompleted, i.IsNew)).ToList();
        _ = CustomFields.TryBuild(latest.CustomValues, out var mineCustom);
        Load(latest);
        _isLoading = true;
        if (keep.Contains("Title"))
        {
            Title = mine.Title;
        }

        if (keep.Contains("Service"))
        {
            Service = mine.Service;
        }

        if (keep.Contains("Status"))
        {
            SelectedStatus = StatusOptions.FirstOrDefault(o => o.Id == mine.Status);
        }

        if (keep.Contains("Priority"))
        {
            SelectedPriority = PriorityOptions.FirstOrDefault(o => o.Id == mine.Priority);
        }

        if (keep.Contains("Designers"))
        {
            Designers.Load(mineTokens.Item1.Select(t => (t.Id, t.Name, t.IsNew)));
        }

        if (keep.Contains("Developers"))
        {
            Developers.Load(mineTokens.Item2.Select(t => (t.Id, t.Name, t.IsNew)));
        }

        if (keep.Contains("Tags"))
        {
            Tags.Load(mineTokens.Item3.Select(t => (t.Id, t.Name, t.IsNew)));
        }

        if (keep.Contains("StartDate"))
        {
            StartDate = ToPicker(mine.Start);
        }

        if (keep.Contains("EndDate"))
        {
            EndDate = ToPicker(mine.End);
        }

        if (keep.Contains("Notes"))
        {
            Notes = mine.Notes;
        }

        if (keep.Contains("CustomFields"))
        {
            CustomFields.Load(mineCustom);
        }

        if (keep.Contains("ChecklistItems"))
        {
            _checklist.Clear();
            foreach (var (id, text, isCompleted, isNew) in mineChecklist)
            {
                AddChecklistRow(new EditorChecklistItemViewModel(id, text, isCompleted, isNew));
            }
        }

        _isLoading = false;
        IsConflict = false;
        SaveError = null;
        _conflictRows.Clear();
        CurrentStep = EditorStep.Form;
        RecomputeDirty();
        RaiseState();
    }

    /// <summary>Reload asks first, because it discards the draft (design §5.10).</summary>
    public void BeginReload()
    {
        CurrentStep = EditorStep.ReloadConfirm;
        RaiseState();
    }

    public void ConfirmReload()
    {
        if (Latest is { } latest)
        {
            PendingLink = null;
            Load(latest);
        }

        IsConflict = false;
        SaveError = null;
        CurrentStep = EditorStep.Form;
        RaiseState();
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

    /// <summary>Adds the typed item to the draft; blank text is an inline error, never an item (B-02.7).</summary>
    public bool AddChecklistItem()
    {
        if (!CanEditFields)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(NewChecklistText))
        {
            ChecklistError = _context.Strings.Format("Error_Required", _context.Strings.GetString("Field_Text"));
            return false;
        }

        ChecklistError = null;
        AddChecklistRow(new EditorChecklistItemViewModel(new ChecklistItemId(Guid.NewGuid()), NewChecklistText.Trim(), false, isNew: true));
        NewChecklistText = string.Empty;
        return true;
    }

    public void MoveChecklistItem(EditorChecklistItemViewModel item, int delta)
    {
        var index = _checklist.IndexOf(item);
        var target = index + delta;
        if (CanEditFields && index >= 0 && target >= 0 && target < _checklist.Count)
        {
            _checklist.Move(index, target);
        }
    }

    /// <summary>Delete is an explicit command on the item (B-05); the item goes when the draft is saved.</summary>
    public void RemoveChecklistItem(EditorChecklistItemViewModel item)
    {
        if (CanEditFields && _checklist.Remove(item))
        {
            item.PropertyChanged -= OnChecklistItemChanged;
        }
    }

    /// <summary>Pop out to card (B-16.3) runs immediately, against the saved task.</summary>
    public async Task<bool> PopOutAsync(EditorChecklistItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!CanPopOut || item.IsNew || _base is not { } task)
        {
            return false;
        }

        // The same command as the Checklists page: one transaction, then "Moved to a Subtask card in …".
        return await _context.Tasks.PopOutAsync(task, item.Id).ConfigureAwait(true) is not null;
    }

    /// <summary>Add Subtask card (design §5.1 group 8): creates the card immediately in the default status.</summary>
    public async Task<bool> AddSubtaskCardAsync()
    {
        if (!CanAddSubtaskCard)
        {
            return false;
        }

        SubtaskCardError = null;
        var operation = OperationId.New();
        _context.SaveStatus.Begin();
        var result = await _context.UseCases.CreateSubtaskCard.ExecuteAsync(
            new CreateSubtaskCardCommand(operation, _taskId, new SubtaskCardId(Guid.NewGuid()), NewSubtaskCardText), CancellationToken.None).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            var failure = CommandFailure.From(result.Error!, operation, "Record_SubtaskCard", _context.Strings);
            _context.SaveStatus.Report(failure);
            SubtaskCardError = failure.Message;
            return false;
        }

        _context.SaveStatus.Complete();
        NewSubtaskCardText = string.Empty;
        await _context.WhenDisplayedAsync(result.Value.CommitSequence, DisplayTimeout).ConfigureAwait(true);
        return true;
    }

    public async Task<bool> ArchiveAsync() =>
        CanArchive && Latest is { } task && await RunLifecycleAsync(_context.Tasks.ArchiveAsync(task)).ConfigureAwait(true);

    public async Task<bool> RestoreAsync() =>
        CanRestore && Latest is { } task && await RunLifecycleAsync(_context.Tasks.RestoreAsync(task)).ConfigureAwait(true);

    public void Dispose()
    {
        _commits.Dispose();
        Designers.Changed -= OnDraftChanged;
        Developers.Changed -= OnDraftChanged;
        Tags.Changed -= OnDraftChanged;
        CustomFields.Changed -= OnDraftChanged;
        _checklist.CollectionChanged -= OnChecklistChanged;
        foreach (var item in _checklist)
        {
            item.PropertyChanged -= OnChecklistItemChanged;
        }
    }

    internal static DateTimeOffset? ToPicker(DateOnly? date) => DraftDates.ToPicker(date);

    internal static DateOnly? FromPicker(DateTimeOffset? value) => DraftDates.FromPicker(value);

    partial void OnTitleChanged(string value) => RecomputeDirty();

    partial void OnServiceChanged(string value) => RecomputeDirty();

    partial void OnSelectedStatusChanged(StatusOption? value) => RecomputeDirty();

    partial void OnSelectedPriorityChanged(PriorityOption? value) => RecomputeDirty();

    partial void OnStartDateChanged(DateTimeOffset? value) => RecomputeDirty();

    partial void OnEndDateChanged(DateTimeOffset? value) => RecomputeDirty();

    partial void OnNotesChanged(string value) => RecomputeDirty();

    partial void OnIsDirtyChanged(bool value) => RaiseState();

    partial void OnIsSavingChanged(bool value)
    {
        Designers.IsReadOnly = value || IsReadOnly;
        Developers.IsReadOnly = value || IsReadOnly;
        Tags.IsReadOnly = value || IsReadOnly;
        CustomFields.IsReadOnly = value || IsReadOnly;
    }

    partial void OnIsArchivedChanged(bool value) => OnIsSavingChanged(IsSaving);

    private string None => _context.Strings.GetString("Common_None");

    private string SavedCustomFieldsText(TaskReadModel latest)
    {
        var saved = new CustomFieldsEditorViewModel(FieldTarget.Task, _workflow, _context.Strings);
        saved.Load(latest.CustomValues);
        return saved.DisplayText(None);
    }

    private IEnumerable<(Guid Id, string Name)> People() => _workflow.People.Select(p => (p.Id.Value, p.Name.Display));

    private async Task<(AppError? Error, long Sequence)> ExecuteSaveAsync(OperationId operation, IReadOnlyDictionary<FieldId, CustomValue> customValues)
    {
        var useCases = _context.UseCases;
        var designerIds = Designers.Ids;
        var developerIds = Developers.Ids;
        var tagIds = Tags.Ids;
        var lookups = new NewLookups(
            [.. _newPeople.Used(designerIds.Concat(developerIds)).Select(p => new NewPerson(new PersonId(p.Id), p.Name))],
            [.. _newTags.Used(tagIds).Select(t => new NewTag(new TagId(t.Id), t.Name))]);
        var link = PendingLink is { } pending ? new CrLinkTarget(pending.Id, pending.Revision) : null;

        // The title belongs to the CR once linked, so it is not sent; a new task created with its link has no
        // own title to preserve, while an existing task's own title moves to preserved metadata (ADR 0005).
        var title = IsLinked || (IsNew && link is not null) ? null : Title;
        var content = new TaskContentDraft(
            title,
            Service,
            [.. designerIds.Select(id => new PersonId(id))],
            [.. developerIds.Select(id => new PersonId(id))],
            [.. tagIds.Select(id => new TagId(id))],
            Notes,
            customValues);
        var checklist = _checklist.Select(i => new ChecklistItemDraft(i.Id, i.Text, i.IsCompleted)).ToList();
        var start = Intent(FromPicker(StartDate), _baseValues.Start, _startCleared);
        var end = Intent(FromPicker(EndDate), _baseValues.End, _endCleared);

        if (IsNew)
        {
            var created = await useCases.CreateTask.ExecuteAsync(
                new CreateTaskCommand(operation, _taskId, content, SelectedStatus?.Id, SelectedPriority?.Id, start, end, link, checklist, lookups),
                CancellationToken.None).ConfigureAwait(true);
            CreatedTaskId = created.IsSuccess ? created.Value.Value : null;
            return created.IsSuccess ? (null, created.Value.CommitSequence) : (created.Error, 0);
        }

        var task = _base!;

        // B-05: an archived task is read-only; the one permitted change is linking it (B-03.6).
        if (IsArchived)
        {
            var linked = await useCases.LinkTaskToChangeRequest.ExecuteAsync(
                new LinkTaskToChangeRequestCommand(operation, task.Id, task.Revision, link!.Id, link.ExpectedRevision), CancellationToken.None).ConfigureAwait(true);
            return linked.IsSuccess ? (null, linked.Value.CommitSequence) : (linked.Error, 0);
        }

        var updated = await useCases.UpdateTask.ExecuteAsync(
            new UpdateTaskCommand(operation, task.Id, task.Revision, content, SelectedStatus?.Id ?? task.Status.Id, SelectedPriority?.Id ?? task.Priority.Id,
                start, end, checklist, lookups, link),
            CancellationToken.None).ConfigureAwait(true);
        return updated.IsSuccess ? (null, updated.Value.CommitSequence) : (updated.Error, 0);
    }

    private static DateIntent Intent(DateOnly? draft, DateOnly? based, bool cleared) => DraftDates.Intent(draft, based, cleared);

    private void ShowFailure(CommandFailure failure)
    {
        switch (failure.Error)
        {
            case ValidationFailedError validation:
                ShowFieldErrors(validation.Errors);
                break;
            case ConcurrencyConflictError { Entity: EntityKind.ChangeRequest } when PendingLink is { } pending:
                // T-13: the CR changed after the preview; refresh it and let the user review before saving.
                PendingLink = CrPicker.Current(pending.Id);
                CrChangedMessage = _context.Strings.Format("TaskEditor_CrChangedMessage", pending.Number);
                break;
            case ConcurrencyConflictError:
                IsConflict = true;
                SaveError = _context.Strings.GetString("TaskEditor_ConflictMessage");
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
        foreach (var error in errors.Where(e => e.Field?.StartsWith("Custom:", StringComparison.Ordinal) != true))
        {
            var text = CommandErrorText.Describe(error, _context.Strings);
            var key = error.Field switch
            {
                "Title" => SetError(() => TitleError = text, "Title"),
                "Service" => SetError(() => ServiceError = text, "Service"),
                "Status" => SetError(() => StatusError = text, "Status"),
                "Priority" => SetError(() => PriorityError = text, "Priority"),
                "Designers" or "Developers" or "People" or "Tags" => SetError(() => PeopleError = text, "Designers"),
                "StartDate" or "EndDate" => SetError(() => DateError = text, "EndDate"),
                "Notes" => SetError(() => NotesError = text, "Notes"),
                "ChecklistItems" => SetError(() => ChecklistError = text, "ChecklistItems"),
                _ => SetError(() => SaveError = text, "Summary"),
            };
            first ??= key;
            fields.Add(key);
        }

        // B-11: custom-field errors (field "Custom:{key}") show beside their typed editors.
        if (CustomFields.ShowErrors(errors, _context.Strings) is { } customKey)
        {
            first ??= "Custom:" + customKey;
            fields.UnionWith(CustomFields.Fields.Where(f => f.Error is not null).Select(f => "Custom:" + f.Key));
        }

        ValidationSummary = _context.Strings.Format(fields.Count == 1 ? "TaskEditor_FixField" : "TaskEditor_FixFields", fields.Count);
        if (first is not null)
        {
            FocusRequested?.Invoke(this, first);
        }

        static string SetError(Action set, string key)
        {
            set();
            return key;
        }
    }

    private void ClearErrors()
    {
        TitleError = ServiceError = StatusError = PriorityError = PeopleError = DateError = NotesError = ChecklistError = null;
        CustomFields.ClearErrors();
        ValidationSummary = null;
        SaveError = null;
        CrChangedMessage = null;
    }

    private async Task<bool> RunLifecycleAsync(Task<bool> command)
    {
        IsSaving = true;
        RaiseState();
        var succeeded = await command.ConfigureAwait(true);
        IsSaving = false;
        RaiseState();
        return succeeded;
    }

    private void Approve()
    {
        _closeApproved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Load(TaskReadModel model)
    {
        _isLoading = true;
        _base = model;
        Latest = model;
        IsArchived = model.IsArchived;
        Title = model.IsTitleInherited ? string.Empty : model.EffectiveTitle;
        Service = model.Service ?? string.Empty;
        SelectedStatus = StatusOptions.FirstOrDefault(o => o.Id == model.Status.Id);
        SelectedPriority = PriorityOptions.FirstOrDefault(o => o.Id == model.Priority.Id);
        Designers.Load(model.Designers.Select(p => (p.Id.Value, p.Name, false)));
        Developers.Load(model.Developers.Select(p => (p.Id.Value, p.Name, false)));
        Tags.Load(model.Tags.Select(t => (t.Id.Value, t.Name, false)));
        StartDate = ToPicker(model.StartDate);
        EndDate = ToPicker(model.EndDate);
        Notes = model.Notes ?? string.Empty;
        CustomFields.Load(model.CustomValues);
        foreach (var item in _checklist)
        {
            item.PropertyChanged -= OnChecklistItemChanged;
        }

        _checklist.Clear();
        foreach (var item in model.ChecklistItems)
        {
            AddChecklistRow(new EditorChecklistItemViewModel(item.Id, item.Text, item.IsCompleted, isNew: false));
        }

        _startCleared = _endCleared = false;
        _baseValues = ValuesOf(model);
        _isLoading = false;
        RecomputeDirty();
        RaiseState();
    }

    private void AddChecklistRow(EditorChecklistItemViewModel item)
    {
        item.PropertyChanged += OnChecklistItemChanged;
        _checklist.Add(item);
    }

    private DraftValues Capture() => new(
        Title.Trim(),
        Service.Trim(),
        SelectedStatus?.Id,
        SelectedPriority?.Id,
        Designers.Ids,
        Developers.Ids,
        Tags.Ids,
        FromPicker(StartDate),
        FromPicker(EndDate),
        Notes,
        [.. _checklist.Select(i => (i.Id, i.Text, i.IsCompleted))],
        CustomFields.Signature);

    private DraftValues ValuesOf(TaskReadModel model) => new(
        model.IsTitleInherited ? string.Empty : model.EffectiveTitle,
        model.Service ?? string.Empty,
        model.Status.Id,
        model.Priority.Id,
        [.. model.Designers.Select(p => p.Id.Value)],
        [.. model.Developers.Select(p => p.Id.Value)],
        [.. model.Tags.Select(t => t.Id.Value)],
        model.StartDate,
        model.EndDate,
        model.Notes ?? string.Empty,
        [.. model.ChecklistItems.Select(i => (i.Id, i.Text, i.IsCompleted))],
        CustomFields.SignatureOf(model.CustomValues));

    private string Describe(DraftValues values, string key)
    {
        var none = _context.Strings.GetString("Common_None");
        string Join(IEnumerable<string> names) => names.DefaultIfEmpty(none).Aggregate((a, b) => a + ", " + b);
        string Person(Guid id) => _workflow.FindPerson(new PersonId(id))?.Name.Display ?? _newPeople.Used([id]).Select(p => p.Name).FirstOrDefault() ?? none;
        string Tag(Guid id) => _workflow.FindTag(new TagId(id))?.Name.Display ?? _newTags.Used([id]).Select(t => t.Name).FirstOrDefault() ?? none;
        string Date(DateOnly? date) => date?.ToString("d", CultureInfo.CurrentCulture) ?? none;
        string Text(string text) => text.Length == 0 ? none : text;
        return key switch
        {
            "Title" => Text(values.Title),
            "Service" => Text(values.Service),
            "Status" => values.Status is { } s ? _workflow.FindTaskStatus(s)?.Name.Display ?? none : none,
            "Priority" => values.Priority is { } p ? _workflow.FindPriority(p)?.Name.Display ?? none : none,
            "Designers" => Join(values.Designers.Select(Person)),
            "Developers" => Join(values.Developers.Select(Person)),
            "Tags" => Join(values.Tags.Select(Tag)),
            "StartDate" => Date(values.Start),
            "EndDate" => Date(values.End),
            "Notes" => Text(values.Notes),
            _ => _context.Strings.Format("TaskEditor_ChecklistSummary", values.Checklist.Count, values.Checklist.Count(i => i.IsCompleted)),
        };
    }

    private void OnDraftChanged(object? sender, EventArgs e) => RecomputeDirty();

    private void OnChecklistChanged(object? sender, NotifyCollectionChangedEventArgs e) => RecomputeDirty();

    private void OnChecklistItemChanged(object? sender, PropertyChangedEventArgs e) => RecomputeDirty();

    private void RecomputeDirty()
    {
        if (!_isLoading)
        {
            IsDirty = !Capture().SameAs(_baseValues) || _startCleared || _endCleared;
        }
    }

    private void OnCommitted()
    {
        RefreshSubtaskCards();
        if (IsNew || CreatedTaskId is not null)
        {
            RaiseState();
            return;
        }

        var model = _context.Store.Tasks.Lookup(_taskId);
        Latest = model.HasValue ? model.Value : null;
        if (Latest is null)
        {
            IsDeletedElsewhere = true;
            SaveError ??= _context.Strings.GetString("TaskEditor_DeletedElsewhere");
        }
        else if (!IsDirty && !IsSaving && !IsConflict && PendingLink is null && Latest.Revision != _base?.Revision)
        {
            // A draft without changes follows the committed task (AC-06); a dirty one is never replaced (B-02.9).
            Load(Latest);
        }

        RaiseState();
    }

    private void RefreshSubtaskCards()
    {
        var cards = IsNew ? [] : _context.Store.SubtaskCards.Items
            .Where(c => c.ParentTaskId == _taskId)
            .OrderBy(c => c.StatusOrder)
            .ThenBy(c => c.Rank)
            .ThenBy(c => c.Id.Value)
            .Select(c => new EditorSubtaskCardViewModel(c.Id, c.Subtitle, c.Status.Name, DatesText(c.StartDate, c.EndDate)))
            .ToList();
        KeyedCollectionSync.Sync(_subtaskCards, cards, c => c.Id, c => c.Id, c => c, (_, _) => { });
        for (var i = 0; i < cards.Count; i++)
        {
            if (_subtaskCards[i] != cards[i])
            {
                _subtaskCards[i] = cards[i];
            }
        }
    }

    private string DatesText(DateOnly? start, DateOnly? end) => (start, end) switch
    {
        (null, null) => string.Empty,
        ({ } s, null) => _context.Strings.Format("Board_DatesStarted", s.ToString("d", CultureInfo.CurrentCulture)),
        (null, { } e) => _context.Strings.Format("Board_DatesEnded", e.ToString("d", CultureInfo.CurrentCulture)),
        ({ } s, { } e) => _context.Strings.Format("Board_DatesRange", s.ToString("d", CultureInfo.CurrentCulture), e.ToString("d", CultureInfo.CurrentCulture)),
    };

    private string Helper(string key, IEnumerable<Domain.Workflow.TaskStatusDefinition> statuses)
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
