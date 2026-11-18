using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Options;
using MaxPlanner.Presentation.Common.Services;
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

    /// <summary>More › Create pull request… (design §5.11): uses the parent's CR and task numbers.</summary>
    CreatePullRequest,
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
public sealed partial class SubtaskCardEditorViewModel : ConflictEditorViewModelBase<SubtaskEditorFollowUp>, IInlineStepEditor
{
    private const string SubtitleKey = "Subtitle";
    private const string StatusKey = "Status";
    private const string StartKey = "StartDate";
    private const string EndKey = "EndDate";
    private const string NotesKey = "Notes";

    private static readonly string[] StateProperties =
    [
        nameof(CreatedText), nameof(Caption), nameof(ParentTitle), nameof(ParentService), nameof(CrNumberText), nameof(TaskNumberText), nameof(SquadText),
        nameof(OwnerText), nameof(HasOwner), nameof(IsReadOnly), nameof(IsParentArchived), nameof(CanEditFields), nameof(CanSave), nameof(CanDelete), nameof(IsMoreMenuEnabled),
        nameof(IsFormStep), nameof(IsConflictReviewStep), nameof(IsReloadConfirmStep), nameof(HasValidationSummary), nameof(HasSaveError),
        nameof(CanOpenParent),
    ];

    private readonly WorkflowSnapshot _workflow;
    private readonly SubtaskCardId _cardId;
    private readonly TaskId _parentId;
    private SubtaskCardReadModel? _base;
    private DraftSnapshot _baseValues;
    private bool _startCleared;
    private bool _endCleared;

    private SubtaskCardEditorViewModel(WorkspaceContext context, SubtaskCardId? cardId, TaskId parentId, TaskStatusId? initialStatus)
        : base(context, isNew: cardId is null, "Record_SubtaskCard")
    {
        _workflow = context.Store.CurrentWorkflow
            ?? throw new InvalidOperationException("The Subtask card editor needs the loaded workflow; editing is blocked until the store is ready.");
        _cardId = cardId ?? new SubtaskCardId(Guid.NewGuid());
        _parentId = parentId;
        StatusOptions = [.. _workflow.TaskStatuses.OrderBy(s => s.Order).Select(s => new StatusOption(s.Id, s.Name.Display))];
        Parent = Lookup(context.Store.Tasks.Lookup(parentId));

        if (IsNew)
        {
            var status = initialStatus is { } s && _workflow.FindTaskStatus(s) is not null ? s : _workflow.Defaults.DefaultTaskStatusId;
            IsLoading = true;
            SelectedStatus = StatusOptions.FirstOrDefault(o => o.Id == status);
            IsLoading = false;
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

        ObserveCommits();
    }

    /// <summary>The edited card, or null for a new one (until it is created).</summary>
    public SubtaskCardId? ExistingCardId => IsNew ? null : _cardId;

    /// <summary>Settings › Integrations › Azure DevOps is on: More shows Create pull request… (design §5.11).</summary>
    public bool IsPullRequestAvailable => Context.IsAzureDevOpsEnabled;

    /// <summary>The More button: enabled for a saved record while Delete… or Create pull request… can be used.</summary>
    public bool IsMoreMenuEnabled => EditorControlRules.IsMoreMenuEnabled(IsNew, CanDelete, IsPullRequestAvailable);

    public SubtaskCardId? CreatedCardId { get; private set; }

    public TaskId ParentTaskId => _parentId;

    public IReadOnlyList<StatusOption> StatusOptions { get; }

    /// <summary>The committed card now (null for a new card or after a deletion elsewhere).</summary>
    public SubtaskCardReadModel? Latest { get; private set; }

    /// <summary>The parent task as it is now (T-13: inherited values are always current).</summary>
    public TaskReadModel? Parent { get; private set; }

    public string DialogTitle => Context.Strings.GetString(IsNew ? "SubtaskEditor_TitleNew" : "SubtaskEditor_Title");

    public string PrimaryButtonText => Context.Strings.GetString(IsNew ? "SubtaskEditor_CreateButton" : "TaskEditor_SaveButton");

    /// <summary>The info box's Created fact, "Oct 6, 2026"; empty for a new card (the fact is hidden).</summary>
    public string CreatedText => _base is { } b ? DateText.RecordDate(b.CreatedAtUtc.ToLocalTime()) : string.Empty;

    /// <summary>"Subtask of Card limits API": the accessible name of the heading (the parent title) and its Open parent link.</summary>
    public string Caption => Context.Strings.Format("Board_SubtaskOf", ParentTitle);

    public string ParentTitle => Parent?.EffectiveTitle ?? Latest?.ParentTitle ?? string.Empty;

    // The info area shows "None" for an empty value (FactValue).
    public string ParentService => Parent?.Service ?? string.Empty;

    public string CrNumberText => Parent?.Cr.CrNumber ?? string.Empty;

    public string TaskNumberText => Parent?.Cr.TaskNumber ?? string.Empty;

    /// <summary>D15: "General" when the parent is unlinked or its CR has no squad.</summary>
    public string SquadText => Parent?.Cr.SquadName ?? Context.Strings.GetString("Squad_General");

    public string OwnerText => Parent?.Cr.OwnerName ?? string.Empty;

    /// <summary>Whether <see cref="OwnerText"/> names a person (it shows the avatar).</summary>
    public bool HasOwner => !string.IsNullOrEmpty(Parent?.Cr.OwnerName);

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
    public partial SubtaskEditorStep CurrentStep { get; private set; }

    /// <summary>B-16.6: the cards of an archived parent are read-only until it is restored.</summary>
    public bool IsParentArchived => Parent?.IsArchived ?? Latest?.IsParentArchived ?? false;

    public bool IsReadOnly => IsParentArchived || IsDeletedElsewhere;

    public bool CanEditFields => !IsReadOnly && !IsSaving;

    public override bool CanSave => !IsSaving && !IsReadOnly && CurrentStep == SubtaskEditorStep.Form;

    public bool CanDelete => !IsNew && !IsReadOnly && !IsSaving;

    public bool CanOpenParent => Parent is not null && !IsSaving;

    public bool IsFormStep => CurrentStep == SubtaskEditorStep.Form;

    public bool IsConflictReviewStep => CurrentStep == SubtaskEditorStep.ConflictReview;

    public bool IsReloadConfirmStep => CurrentStep == SubtaskEditorStep.ReloadConfirm;

    protected override bool NeedsClosePrompt => IsDirty && !IsReadOnly;

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
    /// <param name="status">Board creation passes the bucket's status; null uses the default task status.</param>
    public static SubtaskCardEditorViewModel ForNewCard(WorkspaceContext context, TaskId parentId, TaskStatusId? status = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SubtaskCardEditorViewModel(context, cardId: null, parentId, status);
    }

    /// <summary>More › Delete… or Open parent: closes this dialog first (after the dirty prompt), then the caller opens the next one.</summary>
    public void RequestFollowUp(SubtaskEditorFollowUp followUp)
    {
        if ((followUp == SubtaskEditorFollowUp.Delete && !CanDelete) || (followUp == SubtaskEditorFollowUp.CreatePullRequest && IsNew))
        {
            return;
        }

        CloseForFollowUp(followUp);
    }

    [RelayCommand]
    private void RequestDelete() => RequestFollowUp(SubtaskEditorFollowUp.Delete);

    [RelayCommand]
    private void RequestOpenParent() => RequestFollowUp(SubtaskEditorFollowUp.OpenParent);

    [RelayCommand]
    private void RequestCreatePullRequest() => RequestFollowUp(SubtaskEditorFollowUp.CreatePullRequest);

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

    /// <summary>Back (Alt+Left) from an inline step.</summary>
    [RelayCommand]
    public void StepBack()
    {
        CurrentStep = SubtaskEditorStep.Form;
        RaiseState();
    }

    partial void OnSubtitleChanged(string value) => RecomputeDirty();

    partial void OnSelectedStatusChanged(StatusOption? value) => RecomputeDirty();

    partial void OnStartDateChanged(DateTimeOffset? value) => RecomputeDirty();

    partial void OnEndDateChanged(DateTimeOffset? value) => RecomputeDirty();

    partial void OnNotesChanged(string value) => RecomputeDirty();

    protected override void OnSavingChanged() => RaiseState();

    protected override IReadOnlyList<ConflictRowViewModel>? BuildConflictRows()
    {
        if (Latest is not { } latest)
        {
            return null;
        }

        var mine = Capture();
        var saved = ValuesOf(latest);
        return
        [
            .. mine.Keys.Where(k => !mine.SameAs(saved, k))
                .Select(key => new ConflictRowViewModel(key, Context.Strings.GetString("Field_" + key), Describe(mine, key), Describe(saved, key))),
        ];
    }

    protected override bool ApplyReviewToDraft(IReadOnlySet<string> keep)
    {
        if (Latest is not { } latest)
        {
            return false;
        }

        var mine = Capture();
        Load(latest);
        IsLoading = true;
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

        IsLoading = false;
        return true;
    }

    protected override void ReloadLatest()
    {
        if (Latest is { } latest)
        {
            Load(latest);
        }
    }

    protected override void ShowConflictStep(ConflictStep conflictStep) => CurrentStep = conflictStep switch
    {
        ConflictStep.Review => SubtaskEditorStep.ConflictReview,
        ConflictStep.ReloadConfirm => SubtaskEditorStep.ReloadConfirm,
        _ => SubtaskEditorStep.Form,
    };

    protected override async Task<(AppError? Error, long Sequence)> ExecuteSaveAsync(OperationId operation)
    {
        var useCases = Context.UseCases;
        var start = DraftDates.Intent(DraftDates.FromPicker(StartDate), (DateOnly?)_baseValues[StartKey], _startCleared);
        var end = DraftDates.Intent(DraftDates.FromPicker(EndDate), (DateOnly?)_baseValues[EndKey], _endCleared);
        if (IsNew)
        {
            var created = await useCases.SubtaskCards.CreateSubtaskCard.ExecuteAsync(
                new CreateSubtaskCardCommand(operation, _parentId, _cardId, Subtitle, Notes, SelectedStatus?.Id, start, end), CancellationToken.None).ConfigureAwait(true);
            CreatedCardId = created.IsSuccess ? created.Value.Value : null;
            return created.IsSuccess ? (null, created.Value.CommitSequence) : (created.Error, 0);
        }

        var card = _base!;
        var updated = await useCases.SubtaskCards.UpdateSubtaskCard.ExecuteAsync(
            new UpdateSubtaskCardCommand(operation, card.Id, card.Revision, Subtitle, Notes, SelectedStatus?.Id ?? card.Status.Id, start, end),
            CancellationToken.None).ConfigureAwait(true);
        return updated.IsSuccess ? (null, updated.Value.CommitSequence) : (updated.Error, 0);
    }

    protected override void OnSaved() =>
        Context.Announce(IsNew ? Context.Strings.Format("SubtaskCard_Created", Subtitle.Trim()) : Context.Strings.GetString("SaveStatus_Saved"));

    protected override void ShowFieldErrors(IReadOnlyList<DomainError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        string? first = null;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var error in errors)
        {
            var text = CommandErrorText.Describe(error, Context.Strings);
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

        ShowValidationSummary(fields.Count, first);
    }

    protected override void ClearFieldErrors() => SubtitleError = StatusError = DateError = NotesError = null;

    protected override bool ComputeIsDirty() => !Capture().SameAs(_baseValues) || _startCleared || _endCleared;

    protected override void OnCommitted()
    {
        Parent = Lookup(Context.Store.Tasks.Lookup(_parentId));
        if (IsNew || CreatedCardId is not null)
        {
            if (Parent is null && CreatedCardId is null)
            {
                IsDeletedElsewhere = true;
                SaveError ??= Context.Strings.GetString("SubtaskEditor_ParentDeleted");
            }

            RaiseState();
            return;
        }

        Latest = Lookup(Context.Store.SubtaskCards.Lookup(_cardId));
        if (Latest is null)
        {
            IsDeletedElsewhere = true;
            SaveError ??= Context.Strings.GetString("SubtaskEditor_DeletedElsewhere");
        }
        else if (!IsDirty && !IsSaving && !IsConflict && Latest.Revision != _base?.Revision)
        {
            // A draft without changes follows the committed card; a dirty one is never replaced (B-02.9).
            Load(Latest);
        }

        RaiseState();
    }

    protected override void RaiseState()
    {
        foreach (var name in StateProperties)
        {
            OnPropertyChanged(name);
        }
    }

    private static T? Lookup<T>(DynamicData.Kernel.Optional<T> value)
        where T : class => value.HasValue ? value.Value : null;

    private void Load(SubtaskCardReadModel model)
    {
        IsLoading = true;
        _base = model;
        Latest = model;
        Subtitle = model.Subtitle;
        SelectedStatus = StatusOptions.FirstOrDefault(o => o.Id == model.Status.Id);
        StartDate = DraftDates.ToPicker(model.StartDate);
        EndDate = DraftDates.ToPicker(model.EndDate);
        Notes = model.Notes ?? string.Empty;
        _startCleared = _endCleared = false;
        _baseValues = ValuesOf(model);
        IsLoading = false;
        RecomputeDirty();
        RaiseState();
    }

    private DraftSnapshot Capture() => new(
    [
        (SubtitleKey, Subtitle.Trim()),
        (StatusKey, SelectedStatus?.Id),
        (StartKey, DraftDates.FromPicker(StartDate)),
        (EndKey, DraftDates.FromPicker(EndDate)),
        (NotesKey, LineBreaks.Normalize(Notes)),
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
        var none = Context.Strings.GetString("Common_None");
        return values[key] switch
        {
            null => none,
            string { Length: 0 } => none,
            string text => text,
            TaskStatusId status => _workflow.FindTaskStatus(status)?.Name.Display ?? none,
            DateOnly date => DateText.RecordDate(date),
            var other => other.ToString() ?? none,
        };
    }

    private string Helper(string key, IEnumerable<TaskStatusDefinition> statuses)
    {
        var names = statuses.OrderBy(s => s.Order).Select(s => s.Name.Display).ToList();
        return names.Count == 0 ? string.Empty : Context.Strings.Format(key, string.Join(", ", names));
    }
}
