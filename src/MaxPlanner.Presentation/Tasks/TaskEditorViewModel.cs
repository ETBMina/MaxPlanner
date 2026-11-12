using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Fields;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tasks;

/// <summary>A priority offered by the editors' Priority box.</summary>
public sealed record PriorityOption(PriorityId Id, string Name, PriorityLevel Level = PriorityLevel.Mid) : IPriorityChoice
{
    string IPriorityChoice.DisplayName => Name;

    PriorityLevel? IPriorityChoice.Level => Level;

    /// <summary>The configured priorities in rank order, each with the glyph level of its rank (design §6.1).</summary>
    public static IReadOnlyList<PriorityOption> From(WorkflowSnapshot workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var levels = PriorityLevels.Of(workflow.Priorities);
        return [.. workflow.Priorities.OrderBy(p => p.Order).Select(p => new PriorityOption(p.Id, p.Name.Display, levels.GetValueOrDefault(p.Id, PriorityLevel.Mid)))];
    }

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
public sealed partial class TaskEditorViewModel : EditorViewModelBase<EditorFollowUp>
{
    private static readonly string[] StateProperties =
    [
        nameof(IsLinked), nameof(LinkedCr), nameof(IsTitleEditable), nameof(ShowInheritedTitle), nameof(InheritedTitle),
        nameof(IsServiceRequired), nameof(ServiceHeader), nameof(ServiceHelper), nameof(HasServiceHelper),
        nameof(IsUnlinkedWithoutPending), nameof(HasPendingLink), nameof(PendingLinkWarning), nameof(CanLinkToCr), nameof(TaskNumberText),
        nameof(SquadText), nameof(OwnerText), nameof(HasOwner), nameof(LinkedCrNumber), nameof(OpenCrName),
        nameof(PendingCrNumber), nameof(CrNumberToCopy), nameof(CanCopyCrNumber), nameof(CopyCrNumberName),
        nameof(TaskNumberToCopy), nameof(CanCopyTaskNumber), nameof(CopyTaskNumberName),
        nameof(CanEditFields), nameof(CanSave), nameof(CanArchive), nameof(CanRestore), nameof(CanAddSubtaskCard), nameof(CanPopOut),
        nameof(IsFormStep), nameof(IsLinkPickStep), nameof(IsLinkConfirmStep), nameof(IsConflictReviewStep), nameof(IsReloadConfirmStep),
        nameof(Caption), nameof(HasValidationSummary), nameof(HasSaveError), nameof(HasCrChangedMessage), nameof(IsReadOnly),
    ];

    private readonly NewNameRegistry _newPeople = new();
    private readonly NewNameRegistry _newTags = new();
    private readonly ObservableCollection<EditorChecklistItemViewModel> _checklist = [];
    private readonly ObservableCollection<EditorSubtaskCardViewModel> _subtaskCards = [];
    private readonly ObservableCollection<ConflictRowViewModel> _conflictRows = [];
    private readonly TaskId _taskId;
    private readonly WorkflowSnapshot _workflow;
    private TaskReadModel? _base;
    private DraftValues _baseValues;
    private IReadOnlyDictionary<FieldId, CustomValue> _customValues = CustomValue.NoValues;
    private bool _startCleared;
    private bool _endCleared;
    private readonly ChangeRequestId? _presetLinkId;

    /// <param name="taskId">The task to edit, or null for a new task.</param>
    /// <param name="initialStatus">For a new task: the Board bucket's status (B-02.1); null uses the default.</param>
    /// <param name="linkTo">
    /// For a new task created from a CR (B-03.5): the saved CR, preselected as the pending permanent link so the
    /// task and its link are created together by Save (B-02.1 "CR creation preselects (and links) the CR").
    /// </param>
    public TaskEditorViewModel(WorkspaceContext context, TaskId? taskId, TaskStatusId? initialStatus, ChangeRequestId? linkTo = null)
        : base(context, isNew: taskId is null, "Record_Task")
    {
        _workflow = context.Store.CurrentWorkflow
            ?? throw new InvalidOperationException("The task editor needs the loaded workflow; editing is blocked until the store is ready.");
        _taskId = taskId ?? new TaskId(Guid.NewGuid());
        StatusOptions = [.. _workflow.TaskStatuses.OrderBy(s => s.Order).Select(s => new StatusOption(s.Id, s.Name.Display))];
        PriorityOptions = PriorityOption.From(_workflow);
        // ADR 0024, design §5.0: Designer and Developer are single-person pickers with suggestions and add-new on Save.
        Designer = new TokenPickerViewModel(context.Strings, People, _newPeople) { IsSingleSelection = true, IsPeople = true };
        Developer = new TokenPickerViewModel(context.Strings, People, _newPeople) { IsSingleSelection = true, IsPeople = true };
        Tags = new TokenPickerViewModel(context.Strings, () => _workflow.Tags.Select(t => (t.Id.Value, t.Name.Display)), _newTags) { IsTags = true };
        Designer.Changed += OnDraftChanged;
        Developer.Changed += OnDraftChanged;
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
            IsLoading = true;
            SelectedStatus = StatusOptions.FirstOrDefault(o => o.Id == status);
            SelectedPriority = PriorityOptions.FirstOrDefault(o => o.Id == _workflow.Defaults.DefaultPriorityId);

            // B-11: a new task starts with each custom field's default; it is the base, so it is not a change.
            CustomFields.LoadDefaults();
            IsLoading = false;
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
        ObserveCommits();
    }

    /// <summary>The edited task, or null for a new one (until it is created).</summary>
    public TaskId? ExistingTaskId => IsNew ? null : _taskId;

    /// <summary>The task created by Save of a new draft.</summary>
    public TaskId? CreatedTaskId { get; private set; }

    /// <summary>The Subtask card to open for <see cref="EditorFollowUp.OpenSubtaskCard"/>.</summary>
    public SubtaskCardId? FollowUpCardId { get; private set; }

    public IReadOnlyList<StatusOption> StatusOptions { get; }

    public IReadOnlyList<PriorityOption> PriorityOptions { get; }

    /// <summary>The single Designer (ADR 0024).</summary>
    public TokenPickerViewModel Designer { get; }

    /// <summary>The single Developer (ADR 0024).</summary>
    public TokenPickerViewModel Developer { get; }

    public TokenPickerViewModel Tags { get; }

    /// <summary>The active task custom fields (B-11), typed per kind (design §5.0).</summary>
    public CustomFieldsEditorViewModel CustomFields { get; }

    public ReadOnlyObservableCollection<EditorChecklistItemViewModel> ChecklistItems { get; }

    public ReadOnlyObservableCollection<EditorSubtaskCardViewModel> SubtaskCards { get; }

    public ReadOnlyObservableCollection<ConflictRowViewModel> ConflictRows { get; }

    public CrPickerViewModel CrPicker { get; }

    /// <summary>The committed state of the task now (null for a new task or after a deletion elsewhere).</summary>
    public TaskReadModel? Latest { get; private set; }

    public string DialogTitle => Context.Strings.GetString(IsNew ? "TaskEditor_TitleNew" : "TaskEditor_Title");

    public string PrimaryButtonText => Context.Strings.GetString(IsNew ? "TaskEditor_CreateButton" : "TaskEditor_SaveButton");

    /// <summary>"Created Oct 6, 2026" (design §5.0 title row).</summary>
    public string Caption => _base is { } b ? Context.Strings.Format("TaskEditor_CreatedCaption", b.CreatedAtUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)) : string.Empty;

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
    public partial string? DesignerError { get; private set; }

    [ObservableProperty]
    public partial string? DeveloperError { get; private set; }

    [ObservableProperty]
    public partial string? TagsError { get; private set; }

    [ObservableProperty]
    public partial string? DateError { get; private set; }

    [ObservableProperty]
    public partial string? NotesError { get; private set; }

    [ObservableProperty]
    public partial string? ChecklistError { get; private set; }

    [ObservableProperty]
    public partial string? SubtaskCardError { get; private set; }

    /// <summary>T-13: the pending link's CR changed before Save; the preview was refreshed.</summary>
    [ObservableProperty]
    public partial string? CrChangedMessage { get; private set; }

    [ObservableProperty]
    public partial EditorStep CurrentStep { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinkServiceLocked))]
    public partial bool IsArchived { get; private set; }

    /// <summary>The CR chosen in the link step, not yet saved (design §5.1 group 2 "Pending").</summary>
    [ObservableProperty]
    public partial CrChoice? PendingLink { get; private set; }

    public bool IsReadOnly => IsArchived || IsDeletedElsewhere;

    /// <summary>
    /// The Link step's Service box: an archived task that already has a Service keeps it (B-05); one without a
    /// Service gets it from this box, saved with the link (D10).
    /// </summary>
    public bool IsLinkServiceLocked => IsArchived && _base?.Service is not null;

    public bool IsLinked => _base?.Cr.ChangeRequestId is not null;

    /// <summary>The linked CR as it is now (T-13: inherited values are always current).</summary>
    public CrChoice? LinkedCr => (Latest ?? _base)?.Cr.ChangeRequestId is { } id ? CrPicker.Current(id) : null;

    public string LinkedCrNumber => LinkedCr?.Number ?? string.Empty;

    /// <summary>"Open CR-1042": the CR link's accessible name and tooltip (design §8.2).</summary>
    public string OpenCrName => LinkedCr is { } cr ? Context.Strings.Format("TaskEditor_OpenCrName", cr.Number) : string.Empty;

    public bool CanEditFields => !IsReadOnly && !IsSaving;

    public bool IsTitleEditable => !IsLinked && PendingLink is null && CanEditFields;

    public bool ShowInheritedTitle => !IsTitleEditable && (IsLinked || PendingLink is not null);

    public string InheritedTitle => PendingLink?.Title ?? LinkedCr?.Title ?? (Latest ?? _base)?.EffectiveTitle ?? string.Empty;

    /// <summary>D10: Service is required on linked tasks and while a link is pending.</summary>
    public bool IsServiceRequired => IsLinked || PendingLink is not null;

    public string ServiceHeader => Context.Strings.GetString(IsServiceRequired ? "TaskEditor_ServiceRequiredHeader" : "TaskEditor_ServiceHeader");

    /// <summary>"Tells this task apart from the other 2 tasks on CR-1042."</summary>
    public string ServiceHelper => (LinkedCr ?? PendingLink) is { } cr
        ? Context.Strings.Format("TaskEditor_ServiceHelper", Math.Max(0, cr.LinkedTaskCount - (IsLinked ? 1 : 0)), cr.Number)
        : string.Empty;

    public bool HasServiceHelper => ServiceHelper.Length > 0;

    public bool IsUnlinkedWithoutPending => !IsLinked && PendingLink is null;

    public bool HasPendingLink => PendingLink is not null;

    /// <summary>B-02.4: the permanence notice shown while a link is pending.</summary>
    public string PendingLinkWarning => PendingLink is { } cr ? Context.Strings.Format("TaskEditor_PendingLinkWarning", cr.Title, cr.Number) : string.Empty;

    /// <summary>B-03.6: only an unlinked task (active or archived) can be linked, and only once (AC-29).</summary>
    public bool CanLinkToCr => !IsLinked && !IsSaving && !IsDeletedElsewhere;

    public string TaskNumberText => (LinkedCr ?? PendingLink) is { } cr
        ? cr.TaskNumber ?? Context.Strings.GetString("TaskEditor_NoTaskNumber")
        : Context.Strings.GetString("TaskEditor_NoTaskNumber");

    /// <summary>D15: "General" when the task is unlinked or its CR has no squad.</summary>
    public string SquadText => (LinkedCr ?? PendingLink)?.SquadName ?? (Latest ?? _base)?.Cr.SquadName ?? Context.Strings.GetString("Squad_General");

    public string OwnerText => (LinkedCr ?? PendingLink)?.OwnerName ?? Context.Strings.GetString("TaskEditor_NoOwner");

    /// <summary>Whether <see cref="OwnerText"/> names a person (it shows the avatar), not the "No owner" placeholder.</summary>
    public bool HasOwner => !string.IsNullOrEmpty((LinkedCr ?? PendingLink)?.OwnerName);

    /// <summary>The CR chosen in the link step, shown in the facts strip until Save.</summary>
    public string PendingCrNumber => PendingLink?.Number ?? string.Empty;

    /// <summary>The plain CR number the facts strip's copy button copies ("RP-116"), not the Copy ID format; empty without a CR.</summary>
    public string CrNumberToCopy => (LinkedCr ?? PendingLink)?.Number ?? string.Empty;

    public bool CanCopyCrNumber => CrNumberToCopy.Length > 0;

    /// <summary>"Copy CR number RP-116": the copy button's accessible name and tooltip.</summary>
    public string CopyCrNumberName => CanCopyCrNumber ? Context.Strings.Format("TaskEditor_CopyCrNumberName", CrNumberToCopy) : string.Empty;

    /// <summary>The plain task number ("INT-7116"); empty when the CR has none or the task is unlinked.</summary>
    public string TaskNumberToCopy => (LinkedCr ?? PendingLink)?.TaskNumber ?? string.Empty;

    public bool CanCopyTaskNumber => TaskNumberToCopy.Length > 0;

    public string CopyTaskNumberName => CanCopyTaskNumber ? Context.Strings.Format("TaskEditor_CopyTaskNumberName", TaskNumberToCopy) : string.Empty;

    /// <summary>"Checklist (2 of 4 items done)", or "Checklist" while it is empty.</summary>
    public string ChecklistHeading => _checklist.Count == 0
        ? Context.Strings.GetString("TaskEditor_ChecklistHeading")
        : Context.Strings.Format("TaskEditor_ChecklistHeadingCount", _checklist.Count(i => i.IsCompleted), _checklist.Count);

    /// <summary>B-04 helper from the status flags, never from labels.</summary>
    public string StartHelper => Helper("TaskEditor_StartHelper", _workflow.TaskStatuses.Where(s => s.SetsStartDate));

    public string EndHelper => Helper("TaskEditor_EndHelper", _workflow.TaskStatuses.Where(s => s.IsCompleted));

    public override bool CanSave => !IsSaving && CurrentStep == EditorStep.Form && !IsDeletedElsewhere && (!IsArchived || PendingLink is not null);

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

    public bool HasCrChangedMessage => CrChangedMessage is not null;

    // A link the user chose is a change; the CR a "Create task" route preselected is not (B-03.5).
    protected override bool NeedsClosePrompt => IsDirty || (PendingLink is not null && PendingLink.Id != _presetLinkId);

    protected override bool HasChangesToSave => IsDirty || PendingLink is not null;

    /// <summary>
    /// More › Copy…, Delete…, Add Subtask card, Open CR, or a Subtask card row: closes this dialog first (after
    /// the dirty prompt), then the caller opens the next one.
    /// </summary>
    public void RequestFollowUp(EditorFollowUp followUp)
    {
        if (IsNew || (followUp == EditorFollowUp.OpenCr && !IsLinked) || (followUp == EditorFollowUp.AddSubtaskCard && !CanAddSubtaskCard))
        {
            return;
        }

        CloseForFollowUp(followUp);
    }

    /// <summary>A row of the Subtask cards list (design §5.1 group 8): this dialog closes and §5.2 opens.</summary>
    public void OpenSubtaskCard(EditorSubtaskCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        FollowUpCardId = card.Id;
        RequestFollowUp(EditorFollowUp.OpenSubtaskCard);
    }

    /// <summary>Link to CR… (design §5.6 step 1).</summary>
    [RelayCommand]
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
    [RelayCommand]
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
            ServiceError = Context.Strings.GetString("Error_ServiceRequiredWhenLinked");
            RequestFocus("Service");
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
    [RelayCommand]
    public void StepBack()
    {
        CurrentStep = CurrentStep == EditorStep.LinkConfirm ? EditorStep.LinkPick : EditorStep.Form;
        RaiseState();
    }

    /// <summary>Review changes (design §5.10): Field | Your draft | Saved now for each differing field.</summary>
    [RelayCommand]
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
            _conflictRows.Add(new ConflictRowViewModel(key, Context.Strings.GetString("Field_" + key), mineText, savedText));
        }

        CurrentStep = EditorStep.ConflictReview;
        RaiseState();
    }

    /// <summary>
    /// Apply to draft: the draft moves to the saved revision, keeping the user's value where Use mine is on
    /// and taking the saved value elsewhere. Nothing is saved until the user saves again.
    /// </summary>
    [RelayCommand]
    public void ApplyConflictReview()
    {
        if (Latest is not { } latest)
        {
            return;
        }

        var keep = _conflictRows.Where(r => r.UseMine).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var mine = Capture();
        var mineTokens = (Designer.Tokens.ToList(), Developer.Tokens.ToList(), Tags.Tokens.ToList());
        var mineChecklist = _checklist.Select(i => (i.Id, i.Text, i.IsCompleted, i.IsNew)).ToList();
        _ = CustomFields.TryBuild(latest.CustomValues, out var mineCustom);
        Load(latest);
        IsLoading = true;
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

        if (keep.Contains("Designer"))
        {
            Designer.Load(mineTokens.Item1.Select(t => (t.Id, t.Name, t.IsNew)));
        }

        if (keep.Contains("Developer"))
        {
            Developer.Load(mineTokens.Item2.Select(t => (t.Id, t.Name, t.IsNew)));
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

        IsLoading = false;
        EndConflict();
        _conflictRows.Clear();
        CurrentStep = EditorStep.Form;
        RecomputeDirty();
        RaiseState();
    }

    /// <summary>Reload asks first, because it discards the draft (design §5.10).</summary>
    [RelayCommand]
    public void BeginReload()
    {
        CurrentStep = EditorStep.ReloadConfirm;
        RaiseState();
    }

    [RelayCommand]
    public void ConfirmReload()
    {
        if (Latest is { } latest)
        {
            PendingLink = null;
            Load(latest);
        }

        EndConflict();
        CurrentStep = EditorStep.Form;
        RaiseState();
    }

    [RelayCommand]
    public void ClearStartDate()
    {
        StartDate = null;
        _startCleared = true;
        RecomputeDirty();
    }

    [RelayCommand]
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
            ChecklistError = Context.Strings.Format("Error_Required", Context.Strings.GetString("Field_Text"));
            return false;
        }

        ChecklistError = null;
        AddChecklistRow(new EditorChecklistItemViewModel(new ChecklistItemId(Guid.NewGuid()), NewChecklistText.Trim(), false, isNew: true));
        NewChecklistText = string.Empty;
        return true;
    }

    public void MoveChecklistItem(EditorChecklistItemViewModel item, int delta) => MoveChecklistItemTo(item, _checklist.IndexOf(item) + delta);

    /// <summary>Moves an item to a position in the draft (Move up/down, or a drag in the list); out-of-range targets do nothing.</summary>
    public bool MoveChecklistItemTo(EditorChecklistItemViewModel item, int index)
    {
        var current = _checklist.IndexOf(item);
        if (!CanEditFields || current < 0 || index < 0 || index >= _checklist.Count || index == current)
        {
            return false;
        }

        _checklist.Move(current, index);
        return true;
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
        return await Context.Tasks.PopOutAsync(task, item.Id).ConfigureAwait(true) is not null;
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
        Context.SaveStatus.Begin();
        AppResult<CommandOutcome<SubtaskCardId>> result;
        try
        {
            result = await Context.UseCases.CreateSubtaskCard.ExecuteAsync(
                new CreateSubtaskCardCommand(operation, _taskId, new SubtaskCardId(Guid.NewGuid()), NewSubtaskCardText), CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The indicator leaves its busy state; the box keeps its text with the unexpected-error text below it.
            Context.SaveStatus.Reject();
            SubtaskCardError = Context.Strings.GetString("Error_Unexpected");
            UiTasks.Report(ex, Context);
            return false;
        }

        if (!result.IsSuccess)
        {
            var failure = CommandFailure.From(result.Error!, operation, "Record_SubtaskCard", Context.Strings);
            Context.SaveStatus.Report(failure);
            SubtaskCardError = failure.Message;
            return false;
        }

        Context.SaveStatus.Complete();
        NewSubtaskCardText = string.Empty;
        await WhenDisplayedAsync(result.Value.CommitSequence).ConfigureAwait(true);
        return true;
    }

    public async Task<bool> ArchiveAsync() =>
        CanArchive && Latest is { } task && await RunLifecycleAsync(Context.Tasks.ArchiveAsync(task)).ConfigureAwait(true);

    public async Task<bool> RestoreAsync() =>
        CanRestore && Latest is { } task && await RunLifecycleAsync(Context.Tasks.RestoreAsync(task)).ConfigureAwait(true);

    /// <summary>The link step's Link button; <see cref="ConfirmLink"/> keeps its result for callers that need it.</summary>
    [RelayCommand]
    private void UseSelectedCr() => ConfirmLink();

    // The editor's buttons and menu items bind these commands. The async ones allow concurrent runs, so a running
    // command never disables its button; the methods keep their own guards (CanArchive, CanRestore, IsSaving…).
    [RelayCommand]
    private void RequestCopy() => RequestFollowUp(EditorFollowUp.Copy);

    [RelayCommand]
    private void RequestDelete() => RequestFollowUp(EditorFollowUp.Delete);

    [RelayCommand]
    private void RequestAddSubtaskCard() => RequestFollowUp(EditorFollowUp.AddSubtaskCard);

    [RelayCommand]
    private void RequestOpenCr() => RequestFollowUp(EditorFollowUp.OpenCr);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Archive() => UiTasks.RunGuardedAsync(ArchiveAsync);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Restore() => UiTasks.RunGuardedAsync(RestoreAsync);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task AddSubtaskCard() => UiTasks.RunGuardedAsync(AddSubtaskCardAsync);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task PopOut(EditorChecklistItemViewModel item) => UiTasks.RunGuardedAsync(() => PopOutAsync(item));

    internal static DateTimeOffset? ToPicker(DateOnly? date) => DraftDates.ToPicker(date);

    internal static DateOnly? FromPicker(DateTimeOffset? value) => DraftDates.FromPicker(value);

    partial void OnTitleChanged(string value) => RecomputeDirty();

    partial void OnServiceChanged(string value) => RecomputeDirty();

    partial void OnSelectedStatusChanged(StatusOption? value) => RecomputeDirty();

    partial void OnSelectedPriorityChanged(PriorityOption? value) => RecomputeDirty();

    partial void OnStartDateChanged(DateTimeOffset? value) => RecomputeDirty();

    partial void OnEndDateChanged(DateTimeOffset? value) => RecomputeDirty();

    partial void OnNotesChanged(string value) => RecomputeDirty();

    partial void OnIsArchivedChanged(bool value) => OnSavingChanged();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        Designer.Changed -= OnDraftChanged;
        Developer.Changed -= OnDraftChanged;
        Tags.Changed -= OnDraftChanged;
        CustomFields.Changed -= OnDraftChanged;
        _checklist.CollectionChanged -= OnChecklistChanged;
        foreach (var item in _checklist)
        {
            item.PropertyChanged -= OnChecklistItemChanged;
        }
    }

    protected override void OnDirtyChanged() => RaiseState();

    protected override void OnSavingChanged()
    {
        Designer.IsReadOnly = IsSaving || IsReadOnly;
        Developer.IsReadOnly = IsSaving || IsReadOnly;
        Tags.IsReadOnly = IsSaving || IsReadOnly;
        CustomFields.IsReadOnly = IsSaving || IsReadOnly;
    }

    // Text typed in a person box counts even when Save comes from Ctrl+Enter, before the box loses focus.
    protected override void CommitPendingInput()
    {
        Designer.CommitText();
        Developer.CommitText();
    }

    // B-11: the custom fields are checked in the editor first; their errors show beside their typed editors.
    protected override bool TryPrepareSave()
    {
        if (CustomFields.TryBuild(_base?.CustomValues ?? CustomValue.NoValues, out _customValues))
        {
            return true;
        }

        ShowValidationSummary(1, "Custom:" + CustomFields.FirstErrorKey);
        return false;
    }

    protected override void OnSaved() => Context.Announce(Context.Strings.GetString("SaveStatus_Saved"));

    private string None => Context.Strings.GetString("Common_None");

    private string SavedCustomFieldsText(TaskReadModel latest)
    {
        var saved = new CustomFieldsEditorViewModel(FieldTarget.Task, _workflow, Context.Strings);
        saved.Load(latest.CustomValues);
        return saved.DisplayText(None);
    }

    private IEnumerable<(Guid Id, string Name)> People() => _workflow.People.Select(p => (p.Id.Value, p.Name.Display));

    protected override async Task<(AppError? Error, long Sequence)> ExecuteSaveAsync(OperationId operation)
    {
        var customValues = _customValues;
        var useCases = Context.UseCases;
        var designerIds = Designer.Ids;
        var developerIds = Developer.Ids;
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
            designerIds.Count > 0 ? new PersonId(designerIds[0]) : null,
            developerIds.Count > 0 ? new PersonId(developerIds[0]) : null,
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

        // B-05: an archived task is read-only; the one permitted change is linking it (B-03.6), together with
        // the Service the Link step asked for when the task had none (D10, owner decision 2026-10-24).
        if (IsArchived)
        {
            var linkService = task.Service is null ? Service : null;
            var linked = await useCases.LinkTaskToChangeRequest.ExecuteAsync(
                new LinkTaskToChangeRequestCommand(operation, task.Id, task.Revision, link!.Id, link.ExpectedRevision, linkService), CancellationToken.None).ConfigureAwait(true);
            return linked.IsSuccess ? (null, linked.Value.CommitSequence) : (linked.Error, 0);
        }

        var updated = await useCases.UpdateTask.ExecuteAsync(
            new UpdateTaskCommand(operation, task.Id, task.Revision, content, SelectedStatus?.Id ?? task.Status.Id, SelectedPriority?.Id ?? task.Priority.Id,
                start, end, checklist, lookups, link),
            CancellationToken.None).ConfigureAwait(true);
        return updated.IsSuccess ? (null, updated.Value.CommitSequence) : (updated.Error, 0);
    }

    private static DateIntent Intent(DateOnly? draft, DateOnly? based, bool cleared) => DraftDates.Intent(draft, based, cleared);

    protected override string ConflictMessage => Context.Strings.GetString("TaskEditor_ConflictMessage");

    protected override bool ShowEditorFailure(CommandFailure failure)
    {
        if (failure.Error is not ConcurrencyConflictError { Entity: EntityKind.ChangeRequest } || PendingLink is not { } pending)
        {
            return false;
        }

        // T-13: the CR changed after the preview; refresh it and let the user review before saving.
        PendingLink = CrPicker.Current(pending.Id);
        CrChangedMessage = Context.Strings.Format("TaskEditor_CrChangedMessage", pending.Number);
        return true;
    }

    protected override void ShowFieldErrors(IReadOnlyList<DomainError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        string? first = null;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var error in errors.Where(e => e.Field?.StartsWith("Custom:", StringComparison.Ordinal) != true))
        {
            var text = CommandErrorText.Describe(error, Context.Strings);
            var key = error.Field switch
            {
                "Title" => SetError(() => TitleError = text, "Title"),
                "Service" => SetError(() => ServiceError = text, "Service"),
                "Status" => SetError(() => StatusError = text, "Status"),
                "Priority" => SetError(() => PriorityError = text, "Priority"),
                "Designer" => SetError(() => DesignerError = text, "Designer"),
                "Developer" => SetError(() => DeveloperError = text, "Developer"),
                "Tags" => SetError(() => TagsError = text, "Tags"),
                "StartDate" or "EndDate" => SetError(() => DateError = text, "EndDate"),
                "Notes" => SetError(() => NotesError = text, "Notes"),
                "ChecklistItems" => SetError(() => ChecklistError = text, "ChecklistItems"),
                _ => SetError(() => SaveError = text, "Summary"),
            };
            first ??= key;
            fields.Add(key);
        }

        // B-11: custom-field errors (field "Custom:{key}") show beside their typed editors.
        if (CustomFields.ShowErrors(errors, Context.Strings) is { } customKey)
        {
            first ??= "Custom:" + customKey;
            fields.UnionWith(CustomFields.Fields.Where(f => f.Error is not null).Select(f => "Custom:" + f.Key));
        }

        ShowValidationSummary(fields.Count, first);

        static string SetError(Action set, string key)
        {
            set();
            return key;
        }
    }

    protected override void ClearFieldErrors()
    {
        TitleError = ServiceError = StatusError = PriorityError = DesignerError = DeveloperError = TagsError = DateError = NotesError = ChecklistError = null;
        CustomFields.ClearErrors();
        CrChangedMessage = null;
    }

    private async Task<bool> RunLifecycleAsync(Task<bool> command)
    {
        IsSaving = true;
        RaiseState();
        try
        {
            return await command.ConfigureAwait(true);
        }
        finally
        {
            // A command that throws ends here too, so the editor never stays busy; the command's guard reports it.
            IsSaving = false;
            RaiseState();
        }
    }

    private void Load(TaskReadModel model)
    {
        IsLoading = true;
        _base = model;
        Latest = model;
        IsArchived = model.IsArchived;
        Title = model.IsTitleInherited ? string.Empty : model.EffectiveTitle;
        Service = model.Service ?? string.Empty;
        SelectedStatus = StatusOptions.FirstOrDefault(o => o.Id == model.Status.Id);
        SelectedPriority = PriorityOptions.FirstOrDefault(o => o.Id == model.Priority.Id);
        Designer.Load(Tokens(model.Designer));
        Developer.Load(Tokens(model.Developer));
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
        IsLoading = false;
        RecomputeDirty();
        RaiseState();
    }

    private static IEnumerable<(Guid Id, string Name, bool IsNew)> Tokens(LookupRef<PersonId>? person) =>
        person is { } p ? [(p.Id.Value, p.Name, false)] : [];

    private void AddChecklistRow(EditorChecklistItemViewModel item)
    {
        item.PropertyChanged += OnChecklistItemChanged;
        UpdateRowState(item);
        _checklist.Add(item);
    }

    // The rows' action buttons follow the editor's state (design §5.1 group 7): edits need an editable draft, and Pop
    // out runs immediately, so it is off for a dirty draft or an item that is not saved yet.
    private void UpdateRowState(EditorChecklistItemViewModel item)
    {
        var canPopOut = CanPopOut && !item.IsNew;
        var reason = !canPopOut && CanEditFields ? Context.Strings.GetString("TaskEditor_PopOutUnavailable") : string.Empty;
        item.SetState(CanEditFields, canPopOut, reason);
    }

    private DraftValues Capture() => new(
        Title.Trim(),
        Service.Trim(),
        SelectedStatus?.Id,
        SelectedPriority?.Id,
        Designer.Ids.Count > 0 ? Designer.Ids[0] : null,
        Developer.Ids.Count > 0 ? Developer.Ids[0] : null,
        Tags.Ids,
        FromPicker(StartDate),
        FromPicker(EndDate),
        LineBreaks.Normalize(Notes),
        [.. _checklist.Select(i => (i.Id, LineBreaks.Normalize(i.Text), i.IsCompleted))],
        CustomFields.Signature);

    private DraftValues ValuesOf(TaskReadModel model) => new(
        model.IsTitleInherited ? string.Empty : model.EffectiveTitle,
        model.Service ?? string.Empty,
        model.Status.Id,
        model.Priority.Id,
        model.Designer?.Id.Value,
        model.Developer?.Id.Value,
        [.. model.Tags.Select(t => t.Id.Value)],
        model.StartDate,
        model.EndDate,
        model.Notes ?? string.Empty,
        [.. model.ChecklistItems.Select(i => (i.Id, i.Text, i.IsCompleted))],
        CustomFields.SignatureOf(model.CustomValues));

    private string Describe(DraftValues values, string key)
    {
        var none = Context.Strings.GetString("Common_None");
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
            "Designer" => values.Designer is { } designer ? Person(designer) : none,
            "Developer" => values.Developer is { } developer ? Person(developer) : none,
            "Tags" => Join(values.Tags.Select(Tag)),
            "StartDate" => Date(values.Start),
            "EndDate" => Date(values.End),
            "Notes" => Text(values.Notes),
            _ => Context.Strings.Format("TaskEditor_ChecklistSummary", values.Checklist.Count, values.Checklist.Count(i => i.IsCompleted)),
        };
    }

    private void OnDraftChanged(object? sender, EventArgs e) => RecomputeDirty();

    private void OnChecklistChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RecomputeDirty();
        OnPropertyChanged(nameof(ChecklistHeading));
    }

    private void OnChecklistItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorChecklistItemViewModel.Text) or nameof(EditorChecklistItemViewModel.IsCompleted))
        {
            RecomputeDirty();
            OnPropertyChanged(nameof(ChecklistHeading));
        }
    }

    protected override bool ComputeIsDirty() => !Capture().SameAs(_baseValues) || _startCleared || _endCleared;

    protected override void OnCommitted()
    {
        RefreshSubtaskCards();
        if (IsNew || CreatedTaskId is not null)
        {
            RaiseState();
            return;
        }

        var model = Context.Store.Tasks.Lookup(_taskId);
        Latest = model.HasValue ? model.Value : null;
        if (Latest is null)
        {
            IsDeletedElsewhere = true;
            SaveError ??= Context.Strings.GetString("TaskEditor_DeletedElsewhere");
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
        var cards = IsNew ? [] : Context.Store.SubtaskCards.Items
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
        ({ } s, null) => Context.Strings.Format("Board_DatesStarted", s.ToString("d", CultureInfo.CurrentCulture)),
        (null, { } e) => Context.Strings.Format("Board_DatesEnded", e.ToString("d", CultureInfo.CurrentCulture)),
        ({ } s, { } e) => Context.Strings.Format("Board_DatesRange", s.ToString("d", CultureInfo.CurrentCulture), e.ToString("d", CultureInfo.CurrentCulture)),
    };

    private string Helper(string key, IEnumerable<Domain.Workflow.TaskStatusDefinition> statuses)
    {
        var names = statuses.OrderBy(s => s.Order).Select(s => s.Name.Display).ToList();
        return names.Count == 0 ? string.Empty : Context.Strings.Format(key, string.Join(", ", names));
    }

    protected override void RaiseState()
    {
        foreach (var name in StateProperties)
        {
            OnPropertyChanged(name);
        }

        foreach (var item in _checklist)
        {
            UpdateRowState(item);
        }
    }
}
