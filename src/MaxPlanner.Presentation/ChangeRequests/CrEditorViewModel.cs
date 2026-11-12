using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Lookups;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Fields;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.ChangeRequests;

/// <summary>A CR status offered by the CR editor's Status box.</summary>
public sealed record CrStatusOption(CrStatusId Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A squad in the CR editor's Squad box; a null ID is "General (no squad)" (D15).</summary>
public sealed record SquadOption(SquadId? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One row of the CR editor's Linked tasks list (design §5.3 group 8).</summary>
public sealed record LinkedTaskRowViewModel(TaskId Id, string Title, string Service, bool HasService, string StatusName, string ChecklistText, bool IsArchived, string AccessibleName);

/// <summary>The inline steps of the CR editor (design §5.0: nested decisions never open a second dialog).</summary>
public enum CrEditorStep
{
    Form,
    SaveAndCreateTask,
    DeleteConfirm,
    ConflictReview,
    ReloadConfirm,
}

/// <summary>What the CR editor asked for after it closes (design §5.0: dialogs never stack).</summary>
public enum CrEditorFollowUp
{
    None,
    CreateTask,
    OpenTask,
    OpenOtherCr,

    /// <summary>More › Move to backlog (B-08.11): the command runs after the editor closed.</summary>
    MoveToBacklog,
}

/// <summary>
/// The CR editor (B-03, B-05, B-11, T-13, design §5.3, §5.7). It edits a draft of the CR and commits it
/// through one use case with a revision check. Use on the UI thread.
/// </summary>
/// <remarks>
/// <para>The CR owns the Title, Task number, Squad, and Owner that its linked tasks and their Subtask cards
/// show (T-13). A save publishes the CR, and the projection store recomposes every dependent record, so the
/// Board, Grid, and open editors show the new values after the commit (AC-04).</para>
/// <para>Create task never uses unsaved values: a new or dirty CR is saved first, after an inline step
/// (T-13). Delete is the cascade of B-05: the confirmation lists the counts, and the exact set of linked
/// task IDs it showed is sent; if the set changed meanwhile, the deletion is refused and the step shows the
/// new counts for a new confirmation (AC-30).</para>
/// </remarks>
public sealed partial class CrEditorViewModel : EditorViewModelBase<CrEditorFollowUp>
{
    private const string NumberKey = "Number";
    private const string TaskNumberKey = "TaskNumber";
    private const string TitleKey = "Title";
    private const string OwnerKey = "Owner";
    private const string SquadKey = "Squad";
    private const string StatusKey = "Status";
    private const string PriorityKey = "Priority";
    private const string StartKey = "PlannedStart";
    private const string EndKey = "PlannedEnd";
    private const string EmailKey = "EmailSubject";
    private const string UrlKey = "SolutionDesignUrl";
    private const string DescriptionKey = "Description";
    private const string CustomFieldsKey = "CustomFields";

    private static readonly string[] StateProperties =
    [
        nameof(Caption), nameof(IsReadOnly), nameof(CanEditFields), nameof(CanSave), nameof(CanDelete), nameof(CanCreateTask),
        nameof(IsFormStep), nameof(IsSaveAndCreateStep), nameof(IsDeleteStep), nameof(IsConflictReviewStep), nameof(IsReloadConfirmStep),
        nameof(HasValidationSummary), nameof(HasSaveError), nameof(IsOverdue), nameof(OverdueMessage), nameof(LinkedTasksHeader),
        nameof(HasLinkedTasks), nameof(TaskNumberHelper), nameof(SaveAndCreatePrompt), nameof(ShowMakeDefaultOwner), nameof(MakeDefaultOwnerText),
        nameof(CanOpenUrl), nameof(PlannedCaption), nameof(HasDuplicate), nameof(OpenDuplicateText), nameof(DisplayNumber),
        nameof(CanMoveToBacklog),
    ];

    private readonly WorkflowSnapshot _workflow;
    private readonly NewNameRegistry _newPeople = new();
    private readonly ObservableCollection<LinkedTaskRowViewModel> _linkedTasks = [];
    private readonly ObservableCollection<ConflictRowViewModel> _conflictRows = [];
    private readonly ChangeRequestId _id;
    private ChangeRequestReadModel? _base;
    private DraftSnapshot _baseValues;
    private IReadOnlyDictionary<FieldId, CustomValue> _customValues = CustomValue.NoValues;
    private IReadOnlyCollection<TaskId> _deleteScope = [];

    /// <param name="changeRequestId">The CR to edit, or null for a new CR.</param>
    /// <param name="plannedDay">
    /// Calendar day flyout "New CR on this date": a new CR starts as a one-day range on that day with the
    /// scheduling status, like a scheduled backlog CR (B-08.5). Ignored for an existing CR.
    /// </param>
    public CrEditorViewModel(WorkspaceContext context, ChangeRequestId? changeRequestId, DateOnly? plannedDay = null)
        : base(context, isNew: changeRequestId is null, "Record_ChangeRequest")
    {
        _workflow = context.Store.CurrentWorkflow
            ?? throw new InvalidOperationException("The CR editor needs the loaded workflow; editing is blocked until the store is ready.");
        _id = changeRequestId ?? new ChangeRequestId(Guid.NewGuid());
        StatusOptions = [.. _workflow.CrStatuses.OrderBy(s => s.Order).Select(s => new CrStatusOption(s.Id, s.Name.Display))];
        PriorityOptions = PriorityOption.From(_workflow);
        SquadOptions =
        [
            new SquadOption(null, context.Strings.GetString("CrEditor_SquadGeneral")),
            .. _workflow.Squads.OrderBy(s => s.Order).Select(s => new SquadOption(s.Id, s.Name.Display)),
        ];

        // B-11/design §5.0: the Owner is a single-person picker with the same add-new rule (created only on Save).
        Owner = new TokenPickerViewModel(context.Strings, () => _workflow.People.Select(p => (p.Id.Value, p.Name.Display)), _newPeople) { IsSingleSelection = true, IsPeople = true };
        Owner.Changed += OnOwnerChanged;
        CustomFields = new CustomFieldsEditorViewModel(FieldTarget.ChangeRequest, _workflow, context.Strings);
        CustomFields.Changed += OnCustomFieldsChanged;
        LinkedTasks = new ReadOnlyObservableCollection<LinkedTaskRowViewModel>(_linkedTasks);
        ConflictRows = new ReadOnlyObservableCollection<ConflictRowViewModel>(_conflictRows);

        if (IsNew)
        {
            IsLoading = true;
            SelectedStatus = StatusOptions.FirstOrDefault(o => o.Id == _workflow.Defaults.DefaultCrStatusId);
            SelectedPriority = PriorityOptions.FirstOrDefault(o => o.Id == _workflow.Defaults.DefaultPriorityId);
            SelectedSquad = SquadOptions[0];
            if (plannedDay is { } day)
            {
                PlannedStart = DraftDates.ToPicker(day);
                PlannedEnd = DraftDates.ToPicker(day);
                SelectedStatus = StatusOptions.FirstOrDefault(o => o.Id == _workflow.Defaults.CalendarSchedulingStatusId) ?? SelectedStatus;
            }

            // B-03.1: a new CR prefills the default CR Owner when one is set.
            if (_workflow.Defaults.DefaultCrOwnerId is { } owner && _workflow.FindPerson(owner) is { } person)
            {
                Owner.Load([(person.Id.Value, person.Name.Display, false)]);
            }

            // B-11: a new CR starts with each custom field's default; it is the base, so it is not a change.
            CustomFields.LoadDefaults();
            IsLoading = false;
            _baseValues = Capture();
        }
        else
        {
            var model = context.Store.ChangeRequests.Lookup(_id);
            Latest = model.HasValue ? model.Value : null;
            _baseValues = Capture();
            if (Latest is not null)
            {
                Load(Latest);
            }
            else
            {
                IsDeletedElsewhere = true;
                SaveError = context.Strings.GetString("CrEditor_DeletedElsewhere");
            }
        }

        RefreshLinkedTasks();
        ObserveCommits();
    }

    /// <summary>The edited CR, or the CR this editor created; null while a new CR is unsaved.</summary>
    public ChangeRequestId? SavedId => !IsNew || CreatedId is not null ? _id : null;

    public ChangeRequestId? CreatedId { get; private set; }

    /// <summary>The task to open for <see cref="CrEditorFollowUp.OpenTask"/>.</summary>
    public TaskId? FollowUpTaskId { get; private set; }

    /// <summary>The CR that already uses the typed number, for <see cref="CrEditorFollowUp.OpenOtherCr"/> (B-03.2).</summary>
    [ObservableProperty]
    public partial ChangeRequestId? DuplicateId { get; private set; }

    public IReadOnlyList<CrStatusOption> StatusOptions { get; }

    public IReadOnlyList<PriorityOption> PriorityOptions { get; }

    public IReadOnlyList<SquadOption> SquadOptions { get; }

    public TokenPickerViewModel Owner { get; }

    /// <summary>The active CR custom fields (B-11), typed per kind (design §5.0).</summary>
    public CustomFieldsEditorViewModel CustomFields { get; }

    public ReadOnlyObservableCollection<LinkedTaskRowViewModel> LinkedTasks { get; }

    public ReadOnlyObservableCollection<ConflictRowViewModel> ConflictRows { get; }

    /// <summary>The committed CR now (null for a new CR or after a deletion elsewhere).</summary>
    public ChangeRequestReadModel? Latest { get; private set; }

    public string DialogTitle => Context.Strings.GetString(IsNew ? "CrEditor_TitleNew" : "CrEditor_Title");

    public string PrimaryButtonText => Context.Strings.GetString(IsNew ? "CrEditor_CreateButton" : "TaskEditor_SaveButton");

    /// <summary>The number as saved, or as typed for a new CR, for step texts ("Save CR-1042 and create a task?").</summary>
    public string DisplayNumber => Latest?.Number ?? (Number.Trim().Length > 0 ? Number.Trim() : Context.Strings.GetString("CrEditor_ThisCr"));

    public string Caption => _base is { } b ? Context.Strings.Format("TaskEditor_CreatedCaption", b.CreatedAtUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)) : string.Empty;

    [ObservableProperty]
    public partial string Number { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TaskNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SquadOption? SelectedSquad { get; set; }

    [ObservableProperty]
    public partial CrStatusOption? SelectedStatus { get; set; }

    [ObservableProperty]
    public partial PriorityOption? SelectedPriority { get; set; }

    /// <summary>Inclusive planned start, as a local calendar day (B-04 CR planned dates).</summary>
    [ObservableProperty]
    public partial DateTimeOffset? PlannedStart { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? PlannedEnd { get; set; }

    [ObservableProperty]
    public partial string EmailSubject { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SolutionDesignUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    /// <summary>B-11 first-CR prompt: shown while no default CR Owner exists; checked by default (design §5.3).</summary>
    [ObservableProperty]
    public partial bool MakeDefaultOwner { get; set; } = true;

    [ObservableProperty]
    public partial string? NumberError { get; private set; }

    [ObservableProperty]
    public partial string? TaskNumberError { get; private set; }

    [ObservableProperty]
    public partial string? TitleError { get; private set; }

    [ObservableProperty]
    public partial string? OwnerError { get; private set; }

    [ObservableProperty]
    public partial string? SquadError { get; private set; }

    [ObservableProperty]
    public partial string? StatusError { get; private set; }

    [ObservableProperty]
    public partial string? PriorityError { get; private set; }

    [ObservableProperty]
    public partial string? PlannedError { get; private set; }

    [ObservableProperty]
    public partial string? EmailSubjectError { get; private set; }

    [ObservableProperty]
    public partial string? UrlError { get; private set; }

    [ObservableProperty]
    public partial string? DescriptionError { get; private set; }

    /// <summary>"The linked tasks changed. Review the new counts." (design §5.7 scope changed).</summary>
    [ObservableProperty]
    public partial string? DeleteScopeMessage { get; private set; }

    [ObservableProperty]
    public partial string? DeleteError { get; private set; }

    [ObservableProperty]
    public partial CrEditorStep CurrentStep { get; private set; }

    /// <summary>"Delete CR-1042 and its tasks?" (design §5.7).</summary>
    [ObservableProperty]
    public partial string DeleteHeading { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DeleteTasksLine { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DeleteChecklistLine { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DeleteSubtaskCardsLine { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DeleteButtonText { get; private set; } = string.Empty;

    public bool IsReadOnly => IsDeletedElsewhere;

    public bool CanEditFields => !IsReadOnly && !IsSaving;

    public override bool CanSave => !IsSaving && !IsReadOnly && CurrentStep == CrEditorStep.Form;

    public bool CanDelete => !IsNew && !IsReadOnly && !IsSaving;

    /// <summary>More › Move to backlog: offered while the CR has dates or a non-backlog status (B-08.11).</summary>
    public bool CanMoveToBacklog => CanDelete && Latest is { } cr && (cr.PlannedStart is not null || !cr.IsBacklog);

    public bool CanCreateTask => !IsReadOnly && !IsSaving && CurrentStep == CrEditorStep.Form;

    public bool IsFormStep => CurrentStep == CrEditorStep.Form;

    public bool IsSaveAndCreateStep => CurrentStep == CrEditorStep.SaveAndCreateTask;

    public bool IsDeleteStep => CurrentStep == CrEditorStep.DeleteConfirm;

    public bool IsConflictReviewStep => CurrentStep == CrEditorStep.ConflictReview;

    public bool IsReloadConfirmStep => CurrentStep == CrEditorStep.ReloadConfirm;

    public bool HasDeleteScopeMessage => DeleteScopeMessage is not null;

    public bool HasDeleteError => DeleteError is not null;

    public bool HasDuplicate => DuplicateId is not null;

    public string OpenDuplicateText => DuplicateId is { } id && Context.Store.ChangeRequests.Lookup(id) is { HasValue: true } other
        ? Context.Strings.Format("CrEditor_OpenDuplicate", other.Value.Number)
        : string.Empty;

    /// <summary>B-15 predicate, the same one the Dashboard uses (design §5.3 Overdue InfoBar).</summary>
    public bool IsOverdue => Latest is { IsOverdue: true };

    public string OverdueMessage => Latest is { IsOverdue: true, PlannedEnd: { } end } cr
        ? Context.Strings.Format(cr.DaysOverdue == 1 ? "CrEditor_OverdueOne" : "CrEditor_Overdue", cr.DaysOverdue, end.ToString("D", CultureInfo.CurrentCulture))
        : string.Empty;

    /// <summary>"Unscheduled" when both planned dates are empty (design §5.3 group 5).</summary>
    public string PlannedCaption => PlannedStart is null && PlannedEnd is null ? Context.Strings.GetString("CrEditor_Unscheduled") : string.Empty;

    public string LinkedTasksHeader => Context.Strings.Format("CrEditor_LinkedTasksHeader", _linkedTasks.Count);

    public bool HasLinkedTasks => _linkedTasks.Count > 0;

    /// <summary>"Shared by all 3 linked tasks." (design §5.3 group 1).</summary>
    public string TaskNumberHelper => Context.Strings.Format(_linkedTasks.Count == 1 ? "CrEditor_TaskNumberHelperOne" : "CrEditor_TaskNumberHelper", _linkedTasks.Count);

    public string SaveAndCreatePrompt => Context.Strings.Format("CrEditor_SaveAndCreatePrompt", DisplayNumber);

    /// <summary>B-11: while no default CR Owner exists, offer to make the chosen owner the default.</summary>
    public bool ShowMakeDefaultOwner => CurrentDefaultOwner is null && Owner.Tokens.Count > 0 && !IsReadOnly;

    public string MakeDefaultOwnerText => Owner.Tokens.FirstOrDefault() is { } owner ? Context.Strings.Format("CrEditor_MakeDefaultOwner", owner.Name) : string.Empty;

    /// <summary>B-02.11: only an http/https link can be opened, and only by the user.</summary>
    public bool CanOpenUrl => HttpUrl.CreateOptional(SolutionDesignUrl) is { IsSuccess: true, Value: not null };

    protected override bool NeedsClosePrompt => IsDirty && !IsReadOnly;

    /// <summary>A clean CR still saves when it would make the chosen owner the default (B-11).</summary>
    protected override bool HasChangesToSave => IsDirty || OffersDefaultOwner;

    private PersonId? CurrentDefaultOwner => Context.Store.CurrentWorkflow?.Defaults.DefaultCrOwnerId;

    private bool OffersDefaultOwner => ShowMakeDefaultOwner && MakeDefaultOwner;

    /// <summary>
    /// "+ Create task" (B-03.5, T-13): a saved, clean CR closes the editor and the task editor opens with the
    /// link; a new or dirty CR first shows the inline "Save and create" step.
    /// </summary>
    public void BeginCreateTask()
    {
        if (!CanCreateTask)
        {
            return;
        }

        Owner.CommitText();
        if (IsNew || IsDirty)
        {
            CurrentStep = CrEditorStep.SaveAndCreateTask;
            RaiseState();
            return;
        }

        FollowUp = CrEditorFollowUp.CreateTask;
        Approve();
    }

    /// <summary>"Save and create": saves the CR, then the task editor opens over the saved CR (never unsaved values, T-13).</summary>
    public async Task<bool> SaveAndCreateTaskAsync()
    {
        Owner.CommitText();
        CurrentStep = CrEditorStep.Form;
        RaiseState();
        if (!await SaveDraftAsync().ConfigureAwait(true))
        {
            return false;
        }

        FollowUp = CrEditorFollowUp.CreateTask;
        Approve();
        return true;
    }

    /// <summary>A linked task row: the editor closes (after the dirty prompt) and the task editor opens.</summary>
    public void OpenLinkedTask(LinkedTaskRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        FollowUpTaskId = row.Id;
        CloseForFollowUp(CrEditorFollowUp.OpenTask);
    }

    /// <summary>"Open CR-1042" beside a duplicate-number error (B-03.2).</summary>
    public void OpenDuplicate()
    {
        if (DuplicateId is not null)
        {
            CloseForFollowUp(CrEditorFollowUp.OpenOtherCr);
        }
    }

    /// <summary>Opens the solution design link through the safe launcher (B-02.11); nothing else is launched.</summary>
    public async Task<bool> OpenUrlAsync()
    {
        var url = HttpUrl.CreateOptional(SolutionDesignUrl);
        return url is { IsSuccess: true, Value: { } link } && await Context.UrlLauncher.OpenAsync(link.Value).ConfigureAwait(true);
    }

    /// <summary>
    /// More › Move to backlog (B-08.11): the editor closes (after the dirty prompt, so a draft is never saved
    /// implicitly) and the explicit command clears the dates and sets the backlog status.
    /// </summary>
    public void RequestMoveToBacklog()
    {
        if (CanMoveToBacklog)
        {
            CloseForFollowUp(CrEditorFollowUp.MoveToBacklog);
        }
    }

    /// <summary>More › Delete… (design §5.7): the cascade step with the counts of what goes with the CR.</summary>
    public void BeginDelete()
    {
        if (!CanDelete)
        {
            return;
        }

        DeleteScopeMessage = null;
        DeleteError = null;
        ComputeDeleteScope();
        CurrentStep = CrEditorStep.DeleteConfirm;
        RaiseState();
    }

    /// <summary>
    /// "Delete CR and n tasks": sends the exact task set the step showed. When the linked tasks changed, the
    /// step stays with the new counts and a warning, and the user confirms again (B-05, AC-30).
    /// </summary>
    public async Task<bool> ConfirmDeleteAsync()
    {
        if (CurrentStep != CrEditorStep.DeleteConfirm || IsSaving || Latest is not { } cr)
        {
            return false;
        }

        if (Context.IsEditingBlocked)
        {
            DeleteError = Context.Strings.GetString("Error_EditingBlocked");
            RaiseDeleteState();
            return false;
        }

        IsSaving = true;
        DeleteError = null;
        var operation = OperationId.New();
        Context.SaveStatus.Begin();

        // The confirmation is about what is deleted, so the CR's current revision is sent: a content edit
        // elsewhere does not change what the user agreed to, while a scope change does and is refused.
        var result = await Context.UseCases.DeleteChangeRequest.ExecuteAsync(
            new DeleteChangeRequestCommand(operation, cr.Id, cr.Revision, _deleteScope), CancellationToken.None).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            var failure = CommandFailure.From(result.Error!, operation, "Record_ChangeRequest", Context.Strings);
            Context.SaveStatus.Report(failure);
            IsSaving = false;
            if (result.Error is ValidationFailedError { Errors: [{ Code: DomainErrorCodes.CascadeScopeChanged }, ..] })
            {
                ComputeDeleteScope();
                DeleteScopeMessage = Context.Strings.GetString("CascadeDelete_ScopeChanged");
                Context.Announce(DeleteScopeMessage, isFailure: true);
            }
            else
            {
                DeleteError = failure.Message;
                Context.Announce(failure.Message, isFailure: true);
            }

            RaiseDeleteState();
            return false;
        }

        Context.SaveStatus.Complete();
        await WhenDisplayedAsync(result.Value.CommitSequence).ConfigureAwait(true);
        IsSaving = false;
        Context.Announce(Context.Strings.Format("CrEditor_Deleted", cr.Number, result.Value.Value.TaskCount));
        Approve();
        return true;
    }

    /// <summary>Back (Alt+Left) from an inline step.</summary>
    public void StepBack()
    {
        CurrentStep = CrEditorStep.Form;
        DeleteScopeMessage = null;
        DeleteError = null;
        RaiseState();
        RaiseDeleteState();
    }

    public void ClearPlannedStart() => PlannedStart = null;

    public void ClearPlannedEnd() => PlannedEnd = null;

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
            var (mineText, savedText) = key == CustomFieldsKey
                ? (CustomFields.DisplayText(None), SavedCustomFieldsText(latest))
                : (Describe(mine, key), Describe(saved, key));
            _conflictRows.Add(new ConflictRowViewModel(key, Context.Strings.GetString("Field_" + key), mineText, savedText));
        }

        CurrentStep = CrEditorStep.ConflictReview;
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
        var mineOwner = Owner.Tokens.Select(t => (t.Id, t.Name, t.IsNew)).ToList();
        _ = CustomFields.TryBuild(latest.CustomValues, out var mineCustom);
        Load(latest);
        IsLoading = true;
        foreach (var key in keep)
        {
            Restore(key, mine, mineOwner);
        }

        if (keep.Contains(CustomFieldsKey))
        {
            CustomFields.Load(mineCustom);
        }

        IsLoading = false;
        EndConflict();
        _conflictRows.Clear();
        CurrentStep = CrEditorStep.Form;
        RecomputeDirty();
        RaiseState();
    }

    public void BeginReload()
    {
        CurrentStep = CrEditorStep.ReloadConfirm;
        RaiseState();
    }

    public void ConfirmReload()
    {
        if (Latest is { } latest)
        {
            Load(latest);
        }

        EndConflict();
        CurrentStep = CrEditorStep.Form;
        RaiseState();
    }

    partial void OnNumberChanged(string value)
    {
        DuplicateId = null;
        RecomputeDirty();
    }

    partial void OnTaskNumberChanged(string value) => RecomputeDirty();

    partial void OnTitleChanged(string value) => RecomputeDirty();

    partial void OnSelectedSquadChanged(SquadOption? value) => RecomputeDirty();

    partial void OnSelectedStatusChanged(CrStatusOption? value) => RecomputeDirty();

    partial void OnSelectedPriorityChanged(PriorityOption? value) => RecomputeDirty();

    partial void OnPlannedStartChanged(DateTimeOffset? value)
    {
        RecomputeDirty();
        OnPropertyChanged(nameof(PlannedCaption));
    }

    partial void OnPlannedEndChanged(DateTimeOffset? value)
    {
        RecomputeDirty();
        OnPropertyChanged(nameof(PlannedCaption));
    }

    partial void OnEmailSubjectChanged(string value) => RecomputeDirty();

    partial void OnSolutionDesignUrlChanged(string value)
    {
        RecomputeDirty();
        OnPropertyChanged(nameof(CanOpenUrl));
    }

    partial void OnDescriptionChanged(string value) => RecomputeDirty();

    partial void OnDuplicateIdChanged(ChangeRequestId? value)
    {
        OnPropertyChanged(nameof(HasDuplicate));
        OnPropertyChanged(nameof(OpenDuplicateText));
    }

    partial void OnDeleteScopeMessageChanged(string? value) => OnPropertyChanged(nameof(HasDeleteScopeMessage));

    partial void OnDeleteErrorChanged(string? value) => OnPropertyChanged(nameof(HasDeleteError));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Owner.Changed -= OnOwnerChanged;
            CustomFields.Changed -= OnCustomFieldsChanged;
        }
    }

    protected override void OnSavingChanged()
    {
        Owner.IsReadOnly = IsSaving || IsReadOnly;
        CustomFields.IsReadOnly = IsSaving || IsReadOnly;
        RaiseState();
    }

    // Text typed in the Owner box counts even when Save comes from Ctrl+Enter, before the box loses focus.
    protected override void CommitPendingInput() => Owner.CommitText();

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

    protected override void OnSaved()
    {
        Context.Announce(Context.Strings.GetString("SaveStatus_Saved"));
        Context.ReportChangeRequestSaved(_id);
    }

    protected override async Task<(AppError? Error, long Sequence)> ExecuteSaveAsync(OperationId operation)
    {
        var makeDefaultOwner = OffersDefaultOwner;
        var customValues = _customValues;
        var useCases = Context.UseCases;
        var ownerToken = Owner.Tokens.FirstOrDefault();
        var newOwner = ownerToken is { IsNew: true } typed ? new NewPerson(new PersonId(typed.Id), typed.Name) : null;
        var draft = new ChangeRequestDraft(
            Number,
            Title,
            ownerToken is null ? null : new PersonId(ownerToken.Id),
            SelectedSquad?.Id,
            SelectedPriority?.Id,
            DraftDates.FromPicker(PlannedStart),
            DraftDates.FromPicker(PlannedEnd),
            EmailSubject,
            SolutionDesignUrl,
            Description,
            TaskNumber,
            customValues);

        if (IsNew && CreatedId is null)
        {
            var created = await useCases.CreateChangeRequest.ExecuteAsync(
                new CreateChangeRequestCommand(operation, _id, draft, SelectedStatus?.Id, newOwner, makeDefaultOwner), CancellationToken.None).ConfigureAwait(true);
            if (!created.IsSuccess)
            {
                return (created.Error, 0);
            }

            CreatedId = created.Value.Value;
            return (null, created.Value.CommitSequence);
        }

        // A status is always selected: the box offers only configured statuses and starts on the CR's own.
        var statusId = SelectedStatus?.Id ?? _base!.Status.Id;
        var updated = await useCases.UpdateChangeRequest.ExecuteAsync(
            new UpdateChangeRequestCommand(operation, _id, _base!.Revision, draft, statusId, newOwner, makeDefaultOwner), CancellationToken.None).ConfigureAwait(true);
        return updated.IsSuccess ? (null, updated.Value.CommitSequence) : (updated.Error, 0);
    }

    protected override bool ShowEditorFailure(CommandFailure failure)
    {
        if (failure.Error is not DuplicateCrNumberError duplicate)
        {
            return false;
        }

        // B-03.2: the error names the existing CR and offers to open it.
        NumberError = Context.Strings.Format("CrEditor_DuplicateNumber", duplicate.Number);
        DuplicateId = duplicate.ExistingId;
        ShowValidationSummary(1, NumberKey);
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
                NumberKey => Set(() => NumberError = text, NumberKey),
                TaskNumberKey => Set(() => TaskNumberError = text, TaskNumberKey),
                TitleKey => Set(() => TitleError = text, TitleKey),
                OwnerKey => Set(() => OwnerError = text, OwnerKey),
                SquadKey => Set(() => SquadError = text, SquadKey),
                StatusKey => Set(() => StatusError = text, StatusKey),
                PriorityKey => Set(() => PriorityError = text, PriorityKey),
                StartKey or EndKey => Set(() => PlannedError = text, EndKey),
                EmailKey => Set(() => EmailSubjectError = text, EmailKey),
                UrlKey => Set(() => UrlError = text, UrlKey),
                DescriptionKey => Set(() => DescriptionError = text, DescriptionKey),
                _ => Set(() => SaveError = text, "Summary"),
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

        static string Set(Action set, string key)
        {
            set();
            return key;
        }
    }

    protected override void ClearFieldErrors()
    {
        NumberError = TaskNumberError = TitleError = OwnerError = SquadError = StatusError = PriorityError = null;
        PlannedError = EmailSubjectError = UrlError = DescriptionError = null;
        CustomFields.ClearErrors();
        DuplicateId = null;
    }

    /// <summary>
    /// B-05: every linked task (archived included) with its checklist items and Subtask cards. Counted from the
    /// committed projection; the use case checks the same task set again at commit time.
    /// </summary>
    private void ComputeDeleteScope()
    {
        var tasks = Context.Store.Tasks.Items.Where(t => t.Cr.ChangeRequestId == _id).ToList();
        _deleteScope = [.. tasks.Select(t => t.Id)];
        var archived = tasks.Count(t => t.IsArchived);
        var strings = Context.Strings;
        DeleteHeading = strings.Format("CascadeDelete_Heading", DisplayNumber);
        DeleteTasksLine = strings.Format("CascadeDelete_Tasks", tasks.Count, tasks.Count - archived, archived);
        DeleteChecklistLine = strings.Format("CascadeDelete_ChecklistItems", tasks.Sum(t => t.ChecklistTotal));
        DeleteSubtaskCardsLine = strings.Format("CascadeDelete_SubtaskCards", tasks.Sum(t => t.SubtaskCardCount));
        DeleteButtonText = strings.Format(tasks.Count == 1 ? "CascadeDelete_ButtonOne" : "CascadeDelete_Button", tasks.Count);
    }

    private void Load(ChangeRequestReadModel model)
    {
        IsLoading = true;
        _base = model;
        Latest = model;
        Number = model.Number;
        TaskNumber = model.TaskNumber ?? string.Empty;
        Title = model.Title;
        Owner.Load([(model.Owner.Id.Value, model.Owner.Name, false)]);
        SelectedSquad = SquadOptions.FirstOrDefault(o => o.Id == model.SquadId) ?? SquadOptions[0];
        SelectedStatus = StatusOptions.FirstOrDefault(o => o.Id == model.Status.Id);
        SelectedPriority = PriorityOptions.FirstOrDefault(o => o.Id == model.Priority.Id);
        PlannedStart = DraftDates.ToPicker(model.PlannedStart);
        PlannedEnd = DraftDates.ToPicker(model.PlannedEnd);
        EmailSubject = model.EmailSubject ?? string.Empty;
        SolutionDesignUrl = model.SolutionDesignUrl ?? string.Empty;
        Description = model.Description ?? string.Empty;
        CustomFields.Load(model.CustomValues);
        DuplicateId = null;
        _baseValues = ValuesOf(model);
        IsLoading = false;
        RecomputeDirty();
        RaiseState();
    }

    private void Restore(string key, DraftSnapshot mine, List<(Guid Id, string Name, bool IsNew)> mineOwner)
    {
        switch (key)
        {
            case NumberKey:
                Number = (string)mine[key]!;
                break;
            case TaskNumberKey:
                TaskNumber = (string)mine[key]!;
                break;
            case TitleKey:
                Title = (string)mine[key]!;
                break;
            case OwnerKey:
                Owner.Load(mineOwner);
                break;
            case SquadKey:
                SelectedSquad = SquadOptions.FirstOrDefault(o => Equals(o.Id, mine[key])) ?? SquadOptions[0];
                break;
            case StatusKey:
                SelectedStatus = StatusOptions.FirstOrDefault(o => Equals(o.Id, mine[key]));
                break;
            case PriorityKey:
                SelectedPriority = PriorityOptions.FirstOrDefault(o => Equals(o.Id, mine[key]));
                break;
            case StartKey:
                PlannedStart = DraftDates.ToPicker((DateOnly?)mine[key]);
                break;
            case EndKey:
                PlannedEnd = DraftDates.ToPicker((DateOnly?)mine[key]);
                break;
            case EmailKey:
                EmailSubject = (string)mine[key]!;
                break;
            case UrlKey:
                SolutionDesignUrl = (string)mine[key]!;
                break;
            case DescriptionKey:
                Description = (string)mine[key]!;
                break;
            default:
                break;
        }
    }

    private DraftSnapshot Capture() => new(
    [
        (NumberKey, Number.Trim()),
        (TaskNumberKey, TaskNumber.Trim()),
        (TitleKey, Title.Trim()),
        (OwnerKey, Owner.Tokens.FirstOrDefault()?.Id),
        (SquadKey, SelectedSquad?.Id),
        (StatusKey, SelectedStatus?.Id),
        (PriorityKey, SelectedPriority?.Id),
        (StartKey, DraftDates.FromPicker(PlannedStart)),
        (EndKey, DraftDates.FromPicker(PlannedEnd)),
        (EmailKey, EmailSubject.Trim()),
        (UrlKey, SolutionDesignUrl.Trim()),
        (DescriptionKey, LineBreaks.Normalize(Description)),
        (CustomFieldsKey, CustomFields.Signature),
    ]);

    private DraftSnapshot ValuesOf(ChangeRequestReadModel model) => new(
    [
        (NumberKey, model.Number),
        (TaskNumberKey, model.TaskNumber ?? string.Empty),
        (TitleKey, model.Title),
        (OwnerKey, (Guid?)model.Owner.Id.Value),
        (SquadKey, model.SquadId),
        (StatusKey, (CrStatusId?)model.Status.Id),
        (PriorityKey, (PriorityId?)model.Priority.Id),
        (StartKey, model.PlannedStart),
        (EndKey, model.PlannedEnd),
        (EmailKey, model.EmailSubject ?? string.Empty),
        (UrlKey, model.SolutionDesignUrl ?? string.Empty),
        (DescriptionKey, model.Description ?? string.Empty),
        (CustomFieldsKey, CustomFields.SignatureOf(model.CustomValues)),
    ]);

    private string Describe(DraftSnapshot values, string key)
    {
        var none = Context.Strings.GetString("Common_None");
        return values[key] switch
        {
            null when key == SquadKey => Context.Strings.GetString("Squad_General"),
            null => none,
            string { Length: 0 } => none,
            string text => text,
            Guid person => _workflow.FindPerson(new PersonId(person))?.Name.Display
                ?? _newPeople.Used([person]).Select(p => p.Name).FirstOrDefault() ?? none,
            SquadId squad => _workflow.FindSquad(squad)?.Name.Display ?? none,
            CrStatusId status => _workflow.FindCrStatus(status)?.Name.Display ?? none,
            PriorityId priority => _workflow.FindPriority(priority)?.Name.Display ?? none,
            DateOnly date => date.ToString("d", CultureInfo.CurrentCulture),
            var other => other.ToString() ?? none,
        };
    }

    private string None => Context.Strings.GetString("Common_None");

    private string SavedCustomFieldsText(ChangeRequestReadModel latest)
    {
        var saved = new CustomFieldsEditorViewModel(FieldTarget.ChangeRequest, _workflow, Context.Strings);
        saved.Load(latest.CustomValues);
        return saved.DisplayText(None);
    }

    private void OnCustomFieldsChanged(object? sender, EventArgs e) => RecomputeDirty();

    private void OnOwnerChanged(object? sender, EventArgs e)
    {
        OwnerError = null;
        RecomputeDirty();
        OnPropertyChanged(nameof(ShowMakeDefaultOwner));
        OnPropertyChanged(nameof(MakeDefaultOwnerText));
    }

    protected override bool ComputeIsDirty() => !Capture().SameAs(_baseValues);

    // The step texts name the number as typed, so they follow every draft change.
    protected override void RecomputeDirty()
    {
        base.RecomputeDirty();
        if (!IsLoading)
        {
            OnPropertyChanged(nameof(DisplayNumber));
            OnPropertyChanged(nameof(SaveAndCreatePrompt));
        }
    }

    protected override void OnCommitted()
    {
        RefreshLinkedTasks();
        if (IsNew && CreatedId is null)
        {
            RaiseState();
            return;
        }

        var model = Context.Store.ChangeRequests.Lookup(_id);
        Latest = model.HasValue ? model.Value : null;
        if (Latest is null)
        {
            // Deleted elsewhere (or by this editor, which is closing): the draft stays visible, read-only.
            IsDeletedElsewhere = true;
            SaveError ??= Context.Strings.GetString("CrEditor_DeletedElsewhere");
        }
        else if (!IsDirty && !IsSaving && !IsConflict && Latest.Revision != _base?.Revision)
        {
            // A draft without changes follows the committed CR; a dirty one is never replaced (B-02.9).
            Load(Latest);
        }

        RaiseState();
    }

    private void RefreshLinkedTasks()
    {
        var strings = Context.Strings;
        var rows = IsNew && CreatedId is null ? [] : Context.Store.Tasks.Items
            .Where(t => t.Cr.ChangeRequestId == _id)
            .OrderBy(t => t.IsArchived)
            .ThenBy(t => t.StatusOrder)
            .ThenBy(t => t.Rank)
            .ThenBy(t => t.Id.Value)
            .Select(t =>
            {
                var service = t.Service ?? string.Empty;
                var checklist = t.ChecklistTotal > 0
                    ? strings.Format("Board_ChecklistProgress", t.ChecklistDone, t.ChecklistTotal)
                    : strings.GetString("Board_NoChecklistItems");
                var name = strings.Format(t.IsArchived ? "CrEditor_LinkedTaskNameArchived" : "CrEditor_LinkedTaskName",
                    service.Length > 0 ? service : strings.GetString("Board_NoService"), t.Status.Name, checklist);
                return new LinkedTaskRowViewModel(t.Id, t.EffectiveTitle, service, service.Length > 0, t.Status.Name, checklist, t.IsArchived, name);
            })
            .ToList();
        Common.Projections.KeyedCollectionSync.Sync(_linkedTasks, rows, r => r.Id, r => r.Id, r => r, (_, _) => { });
        for (var i = 0; i < rows.Count; i++)
        {
            if (_linkedTasks[i] != rows[i])
            {
                _linkedTasks[i] = rows[i];
            }
        }

        OnPropertyChanged(nameof(LinkedTasksHeader));
        OnPropertyChanged(nameof(HasLinkedTasks));
        OnPropertyChanged(nameof(TaskNumberHelper));
    }

    private void RaiseDeleteState()
    {
        OnPropertyChanged(nameof(HasDeleteScopeMessage));
        OnPropertyChanged(nameof(HasDeleteError));
    }

    protected override void RaiseState()
    {
        foreach (var name in StateProperties)
        {
            OnPropertyChanged(name);
        }
    }
}
