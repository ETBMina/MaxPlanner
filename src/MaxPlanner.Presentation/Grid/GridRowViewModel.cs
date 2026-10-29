using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Fields;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Tasks;

namespace MaxPlanner.Presentation.Grid;

/// <summary>A task status offered by the Grid's Status editor.</summary>
public sealed record StatusOption(TaskStatusId Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// One Grid row: a task or a Subtask card (B-07). Display values always show the committed read model; an open
/// cell editor works on a typed draft that is replaced only by the user or by an explicit Reload (B-02.9).
/// </summary>
/// <remarks>
/// Use on the UI thread. While a cell editor is open the row's sort position is held (<see cref="SortModel"/>
/// stops following <see cref="ProjectionItemViewModel{TModel, TKey}.Model"/>) so the row the user is
/// typing in never jumps away; the page re-sorts once editing ends (design §7.7).
/// </remarks>
public sealed partial class GridRowViewModel : ProjectionItemViewModel<WorkItemRecord, Guid>
{
    private readonly ILocalizedStrings _strings;
    private readonly Func<WorkflowSnapshot?> _workflow;
    private TaskCompletionSource? _revisionWaiter;
    private long _awaitedRevision;

    /// <param name="workflow">The current workflow, for custom field definitions and lookup names.</param>
    public GridRowViewModel(WorkItemRecord model, ILocalizedStrings strings, Func<WorkflowSnapshot?>? workflow = null)
        : base(model)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _workflow = workflow ?? (() => null);
        SortModel = model;
    }

    /// <summary>Raised when any displayed value may have changed (the row presenter refreshes its cells).</summary>
    public event EventHandler? DisplayChanged;

    public override Guid Key => Model.Id;

    /// <summary>The model the sort order uses; frozen while the row is being edited.</summary>
    public WorkItemRecord SortModel { get; private set; }

    public bool IsTask => Model.Kind == BoardCardKind.Task;

    public bool IsSubtaskCard => !IsTask;

    /// <summary>B-05: archived tasks and the cards of archived parents are read-only (except Link to CR…).</summary>
    public bool IsArchived => !Model.IsActive;

    public string KindText => _strings.GetString(IsTask ? "Grid_KindTask" : "Grid_KindSubtaskCard");

    /// <summary>Segoe Fluent Icons: Page (E7C3) for a task, BulletedList (E8FD) for a Subtask card.</summary>
    public string KindGlyph => IsTask ? "" : "";

    public string CrNumberText => Model.Cr.CrNumber ?? string.Empty;

    public string TitleText => Model.Task?.EffectiveTitle ?? Model.Card!.ParentTitle;

    /// <summary>"More actions for Card limits API" (design §8.2).</summary>
    public string RowMenuName => _strings.Format("Grid_RowMenuName", Model.Task?.EffectiveTitle ?? Model.Card!.Subtitle);

    /// <summary>Linked tasks inherit the CR title (B-02.3); a Subtask card shows its parent's title (B-16.2).</summary>
    public bool IsTitleLocked => Model.Task is not { IsTitleInherited: false };

    /// <summary>The lock glyph's tooltip and accessible text, e.g. "Inherited from CR-1042".</summary>
    public string TitleLockText => Model.Task is { IsTitleInherited: true }
        ? _strings.Format("Grid_TitleInherited", CrNumberText)
        : IsSubtaskCard ? _strings.GetString("Grid_TitleFromParent") : string.Empty;

    public string SubtitleText => Model.Card?.Subtitle ?? string.Empty;

    public string ServiceText => (IsTask ? Model.Task!.Service : Model.Card!.ParentService) ?? string.Empty;

    public string StatusText => Model.Status.Name;

    public string PriorityText => Model.Task?.Priority.Name ?? string.Empty;

    public string StartDateText => FormatDate(Model.Task is { } task ? task.StartDate : Model.Card!.StartDate);

    public string EndDateText => FormatDate(Model.Task is { } task ? task.EndDate : Model.Card!.EndDate);

    /// <summary>The row's accessible name (design §8.2), e.g. "Task Card freeze, CR-1042, In Progress".</summary>
    public string AccessibleName => _strings.Format(
        IsTask ? "Grid_RowNameTask" : "Grid_RowNameSubtaskCard",
        IsTask ? TitleText : SubtitleText,
        string.IsNullOrEmpty(CrNumberText) ? _strings.GetString("Grid_NoCr") : CrNumberText,
        StatusText,
        TitleText) + (IsArchived ? _strings.GetString("Grid_RowArchivedSuffix") : string.Empty);

    /// <summary>The column whose editor is open, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsEditingTitle), nameof(IsEditingSubtitle), nameof(IsEditingService), nameof(IsEditingStatus), nameof(ConflictSavedText))]
    public partial string? EditingColumn { get; private set; }

    /// <summary>The text editor's draft (Title, Subtitle, Service, Notes); bound two-way.</summary>
    [ObservableProperty]
    public partial string DraftText { get; set; } = string.Empty;

    /// <summary>The Status editor's draft.</summary>
    [ObservableProperty]
    public partial StatusOption? DraftStatus { get; set; }

    /// <summary>The Priority editor's draft.</summary>
    [ObservableProperty]
    public partial PriorityOption? DraftPriority { get; set; }

    /// <summary>The date editor's draft: the local calendar day of a <c>CalendarDatePicker</c> (B-04).</summary>
    [ObservableProperty]
    public partial DateTimeOffset? DraftDate { get; set; }

    /// <summary>The Designers, Developers, or Tags picker of the open edit; null otherwise.</summary>
    public TokenPickerViewModel? DraftTokens { get; private set; }

    /// <summary>New names typed into <see cref="DraftTokens"/>; created only by a successful commit (B-02.6).</summary>
    internal NewNameRegistry? DraftNewNames { get; private set; }

    /// <summary>The custom field editor of the open edit; null otherwise.</summary>
    public CustomFieldEditorViewModel? DraftCustom { get; private set; }

    /// <summary>True while the cell's command runs; the editor stays open with a progress ring (ADR 0004 row 3).</summary>
    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>The draft was refused because the record changed after the edit began (design §5.10, single-cell form).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictSavedText))]
    public partial bool HasConflict { get; private set; }

    public bool HasError => ErrorMessage is not null;

    public bool IsEditing => EditingColumn is not null;

    public bool IsEditingTitle => EditingColumn == GridColumns.Title;

    public bool IsEditingSubtitle => EditingColumn == GridColumns.Subtitle;

    public bool IsEditingService => EditingColumn == GridColumns.Service;

    public bool IsEditingStatus => EditingColumn == GridColumns.Status;

    /// <summary>The date column was explicitly cleared in this edit (B-04: automation must respect it).</summary>
    internal bool DraftDateCleared { get; private set; }

    /// <summary>"Saved now" in the conflict popup: the committed value, which follows later store updates.</summary>
    public string? ConflictSavedText => HasConflict && EditingColumn is { } column ? DisplayValue(column) : null;

    /// <summary>The model the open edit started from; its revision is the expected revision of the commit.</summary>
    internal WorkItemRecord? BaseModel { get; private set; }

    /// <summary>The draft the last failed attempt sent; leaving the cell does not resend the same refused draft.</summary>
    internal string? FailedDraft { get; private set; }

    /// <summary>T-05: the operation to reuse when the last attempt's outcome was unknown, for the same draft.</summary>
    internal (OperationId Operation, string Draft)? UnknownOutcome { get; private set; }

    /// <summary>
    /// B-07 editability (design §4.3 table): inherited and derived columns never edit; a task edits its own fields
    /// (Title only while unlinked); a Subtask card edits Subtitle, Status, dates, and Notes; archived rows are read-only.
    /// </summary>
    public bool CanEdit(string column) => Model.IsActive && column switch
    {
        GridColumns.Title => Model.Task is { IsTitleInherited: false },
        GridColumns.Subtitle => IsSubtaskCard,
        GridColumns.Service or GridColumns.Priority or GridColumns.Designers or GridColumns.Developers or GridColumns.Tags => IsTask,
        GridColumns.Status or GridColumns.StartDate or GridColumns.EndDate or GridColumns.Notes => true,
        _ when GridColumns.IsCustom(column) => IsTask && FieldOf(column) is { IsActive: true },
        _ => false,
    };

    public string DisplayValue(string column) => column switch
    {
        GridColumns.Kind => KindText,
        GridColumns.CrNumber => CrNumberText,
        GridColumns.TaskNumber => Model.Cr.TaskNumber ?? string.Empty,
        GridColumns.Squad => Model.Cr.SquadName,
        GridColumns.Owner => Model.Cr.OwnerName ?? string.Empty,
        GridColumns.Title => TitleText,
        GridColumns.Subtitle => SubtitleText,
        GridColumns.Service => ServiceText,
        GridColumns.Status => StatusText,
        GridColumns.Priority => PriorityText,
        GridColumns.Designers => Names(Model.Task?.Designers),
        GridColumns.Developers => Names(Model.Task?.Developers),
        GridColumns.Tags => Names(Model.Task?.Tags),
        GridColumns.StartDate => StartDateText,
        GridColumns.EndDate => EndDateText,
        GridColumns.Notes => FirstLine(Model.Task is { } task ? task.Notes : Model.Card!.Notes),
        GridColumns.Archived => IsArchived ? _strings.GetString("Grid_ArchivedChip") : string.Empty,
        _ when GridColumns.IsCustom(column) => CustomDisplay(column),
        _ => string.Empty,
    };

    /// <summary>
    /// The people a cell names, for its avatars: the CR Owner, Designers, Developers, or a custom People field. Empty
    /// for other columns. The order matches <see cref="DisplayValue"/>.
    /// </summary>
    public IReadOnlyList<string> PeopleNames(string column) => column switch
    {
        GridColumns.Owner => Model.Cr.OwnerName is { Length: > 0 } owner ? [owner] : [],
        GridColumns.Designers => [.. (Model.Task?.Designers ?? []).Select(p => p.Name)],
        GridColumns.Developers => [.. (Model.Task?.Developers ?? []).Select(p => p.Name)],
        _ when GridColumns.IsCustom(column) && Model.Task is { } task && FieldOf(column) is { Kind: FieldKind.People } field
            && task.CustomValues.GetValueOrDefault(field.Id) is PeopleCustomValue people
            => [.. people.PersonIds.Select(id => _workflow()?.FindPerson(id)?.Name.Display ?? string.Empty).Where(n => n.Length > 0)],
        _ => [],
    };

    /// <summary>The full text of a cell for its tooltip (notes are shown by first line).</summary>
    public string FullValue(string column) => column == GridColumns.Notes
        ? (Model.Task is { } task ? task.Notes : Model.Card!.Notes) ?? string.Empty
        : DisplayValue(column);

    /// <summary>The draft as text, for comparisons and the cell's UIA value while editing.</summary>
    public string DraftValue => EditingColumn switch
    {
        GridColumns.Status => DraftStatus?.Name ?? string.Empty,
        GridColumns.Priority => DraftPriority?.Name ?? string.Empty,
        GridColumns.StartDate or GridColumns.EndDate => FormatDate(DraftDates.FromPicker(DraftDate)),
        GridColumns.Designers or GridColumns.Developers or GridColumns.Tags => string.Join(", ", DraftTokens?.Tokens.Select(t => t.Name) ?? []),
        { } column when GridColumns.IsCustom(column) => DraftCustom?.DisplayText ?? string.Empty,
        _ => DraftText,
    };

    internal void BeginEdit(string column, string? initialText, IEnumerable<StatusOption> statusOptions, IEnumerable<PriorityOption> priorityOptions)
    {
        EditingColumn = column;
        BaseModel = Model;
        UnknownOutcome = null;
        FailedDraft = null;
        ErrorMessage = null;
        HasConflict = false;
        DraftDateCleared = false;
        DraftText = initialText ?? (column == GridColumns.Notes ? FullValue(column) : DisplayValue(column));
        DraftStatus = statusOptions.FirstOrDefault(o => o.Id == Model.Status.Id);
        DraftPriority = Model.Task is { } task ? priorityOptions.FirstOrDefault(o => o.Id == task.Priority.Id) : null;
        DraftDate = DraftDates.ToPicker(DateOf(Model, column));
        DraftTokens = null;
        DraftNewNames = null;
        DraftCustom = null;
        var workflow = _workflow();
        if (column is GridColumns.Designers or GridColumns.Developers or GridColumns.Tags && workflow is not null && Model.Task is { } edited)
        {
            DraftNewNames = new NewNameRegistry();
            DraftTokens = column == GridColumns.Tags
                ? new TokenPickerViewModel(_strings, () => workflow.Tags.Select(t => (t.Id.Value, t.Name.Display)), DraftNewNames)
                : new TokenPickerViewModel(_strings, () => workflow.People.Select(p => (p.Id.Value, p.Name.Display)), DraftNewNames) { IsPeople = true };
            var refs = column switch
            {
                GridColumns.Designers => edited.Designers.Select(p => (p.Id.Value, p.Name, false)),
                GridColumns.Developers => edited.Developers.Select(p => (p.Id.Value, p.Name, false)),
                _ => edited.Tags.Select(t => (t.Id.Value, t.Name, false)),
            };
            DraftTokens.Load(refs);
        }
        else if (GridColumns.IsCustom(column) && workflow is not null && FieldOf(column) is { } field && Model.Task is { } owner)
        {
            DraftCustom = new CustomFieldEditorViewModel(field, workflow, _strings);
            DraftCustom.Load(owner.CustomValues.GetValueOrDefault(field.Id), workflow);
            if (initialText is not null && DraftCustom.IsText)
            {
                DraftCustom.Text = initialText;
            }
        }

        OnPropertyChanged(nameof(DraftTokens));
        OnPropertyChanged(nameof(DraftCustom));
    }

    /// <summary>The date editor's Clear (design §5.0): an explicit clear that B-04 automation respects.</summary>
    internal void ClearDraftDate()
    {
        DraftDate = null;
        DraftDateCleared = true;
    }

    /// <summary>True when committing would change the record.</summary>
    internal bool IsDraftChanged()
    {
        var baseModel = BaseModel!;
        return EditingColumn switch
        {
            GridColumns.Status => DraftStatus is { } status && status.Id != baseModel.Status.Id,
            GridColumns.Priority => DraftPriority is { } priority && baseModel.Task is { } task && priority.Id != task.Priority.Id,
            GridColumns.StartDate or GridColumns.EndDate => DraftDates.FromPicker(DraftDate) != DateOf(baseModel, EditingColumn) || (DraftDateCleared && DraftDate is null),
            GridColumns.Designers => !TokensEqual(baseModel.Task?.Designers.Select(p => p.Id.Value)),
            GridColumns.Developers => !TokensEqual(baseModel.Task?.Developers.Select(p => p.Id.Value)),
            GridColumns.Tags => !TokensEqual(baseModel.Task?.Tags.Select(t => t.Id.Value)),
            GridColumns.Notes => !string.Equals(DraftText, (baseModel.Task is { } t ? t.Notes : baseModel.Card!.Notes) ?? string.Empty, StringComparison.Ordinal),
            { } column when GridColumns.IsCustom(column) => DraftCustom is { } custom && baseModel.Task is { } owner
                && custom.Signature != CustomFieldEditorViewModel.SignatureOf(custom.Field, owner.CustomValues.GetValueOrDefault(custom.Field.Id)),
            { } column => !string.Equals(DraftText, DisplayValueOf(baseModel, column), StringComparison.Ordinal),
            null => false,
        };
    }

    /// <summary>The operation for the next attempt: reused only after an unknown outcome for the same draft (T-05).</summary>
    internal OperationId NextOperation() =>
        UnknownOutcome is { } unknown && string.Equals(unknown.Draft, DraftValue, StringComparison.Ordinal) ? unknown.Operation : OperationId.New();

    internal void BeginSave()
    {
        IsSaving = true;
        ErrorMessage = null;
        HasConflict = false;
    }

    internal void FailSave(CommandFailure failure)
    {
        IsSaving = false;
        ErrorMessage = failure.Message;
        HasConflict = failure.IsConflict;
        FailedDraft = DraftValue;
        UnknownOutcome = failure.RequiresSameOperationOnRetry ? (failure.OperationId, DraftValue) : null;
    }

    /// <summary>A draft that cannot be converted (for example an invalid URL); shown like a refused commit.</summary>
    internal void FailDraft(string message)
    {
        ErrorMessage = message;
        FailedDraft = DraftValue;
    }

    /// <summary>
    /// Waits until the committed change has reached this row, so closing the editor never shows the
    /// pre-edit value for a moment (displays show committed state only, T-07). Completes on the UI thread.
    /// </summary>
    internal Task WaitForRevisionAsync(long revision)
    {
        if (Model.Revision >= revision)
        {
            return Task.CompletedTask;
        }

        _awaitedRevision = revision;
        _revisionWaiter = new TaskCompletionSource();
        return _revisionWaiter.Task;
    }

    /// <summary>"Use mine": keeps the draft and retries it against the record as it is now (design §5.10).</summary>
    internal void Rebase()
    {
        BaseModel = Model;
        HasConflict = false;
        ErrorMessage = null;
        FailedDraft = null;
    }

    /// <summary>Closes the editor and drops the draft. Returns true when the held position must be re-sorted.</summary>
    internal bool EndEdit()
    {
        EditingColumn = null;
        BaseModel = null;
        UnknownOutcome = null;
        FailedDraft = null;
        IsSaving = false;
        ErrorMessage = null;
        HasConflict = false;
        DraftTokens = null;
        DraftNewNames = null;
        DraftCustom = null;
        DraftDateCleared = false;
        CompleteRevisionWait();
        if (ReferenceEquals(SortModel, Model))
        {
            return false;
        }

        SortModel = Model;
        return true;
    }

    /// <summary>Shows an error without a command (for example editing while the store reloads).</summary>
    internal void ShowError(string message) => ErrorMessage = message;

    /// <summary>The workflow changed (labels, custom fields); every displayed value may differ.</summary>
    internal void RaiseDisplayChanged() => DisplayChanged?.Invoke(this, EventArgs.Empty);

    internal FieldDefinition? FieldOf(string column) =>
        Guid.TryParseExact(column.AsSpan(GridColumns.CustomPrefix.Length), "N", out var id) ? _workflow()?.Fields.FirstOrDefault(f => f.Id.Value == id) : null;

    protected override void OnModelUpdated(WorkItemRecord previous)
    {
        if (!IsEditing)
        {
            SortModel = Model;
        }

        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(IsSubtaskCard));
        OnPropertyChanged(nameof(IsArchived));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(RowMenuName));
        OnPropertyChanged(nameof(KindGlyph));
        OnPropertyChanged(nameof(CrNumberText));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(IsTitleLocked));
        OnPropertyChanged(nameof(TitleLockText));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(ServiceText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PriorityText));
        OnPropertyChanged(nameof(StartDateText));
        OnPropertyChanged(nameof(EndDateText));
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(ConflictSavedText));
        DisplayChanged?.Invoke(this, EventArgs.Empty);
        if (_revisionWaiter is not null && Model.Revision >= _awaitedRevision)
        {
            CompleteRevisionWait();
        }
    }

    private static DateOnly? DateOf(WorkItemRecord model, string? column) => column switch
    {
        GridColumns.StartDate => model.Task is { } task ? task.StartDate : model.Card!.StartDate,
        GridColumns.EndDate => model.Task is { } task ? task.EndDate : model.Card!.EndDate,
        _ => null,
    };

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end] + " …";
    }

    private static string Names<TId>(IReadOnlyList<LookupRef<TId>>? refs)
        where TId : struct => refs is { Count: > 0 } ? string.Join(", ", refs.Select(r => r.Name)) : string.Empty;

    private bool TokensEqual(IEnumerable<Guid>? committed) => DraftTokens is null || DraftTokens.Ids.SequenceEqual(committed ?? []);

    private void CompleteRevisionWait()
    {
        var waiter = _revisionWaiter;
        _revisionWaiter = null;
        waiter?.TrySetResult();
    }

    private string CustomDisplay(string column)
    {
        if (Model.Task is not { } task || FieldOf(column) is not { } field || task.CustomValues.GetValueOrDefault(field.Id) is not { } value)
        {
            return string.Empty;
        }

        var workflow = _workflow();
        return value switch
        {
            TextCustomValue text => text.Value,
            MultilineTextCustomValue text => FirstLine(text.Value),
            NumberCustomValue number => number.Value.ToString("G", CultureInfo.CurrentCulture),
            DateCustomValue date => date.Value.ToString("d", CultureInfo.CurrentCulture),
            LookupCustomValue lookup => field.Options.FirstOrDefault(o => o.Id == lookup.OptionId)?.Label.Display ?? string.Empty,
            PeopleCustomValue people => string.Join(", ", people.PersonIds.Select(id => workflow?.FindPerson(id)?.Name.Display ?? string.Empty).Where(n => n.Length > 0)),
            TagsCustomValue tags => string.Join(", ", tags.TagIds.Select(id => workflow?.FindTag(id)?.Name.Display ?? string.Empty).Where(n => n.Length > 0)),
            UrlCustomValue url => url.Url.ToString(),
            _ => string.Empty,
        };
    }

    private string DisplayValueOf(WorkItemRecord model, string column) => column switch
    {
        GridColumns.Title => model.Task?.EffectiveTitle ?? model.Card!.ParentTitle,
        GridColumns.Subtitle => model.Card?.Subtitle ?? string.Empty,
        GridColumns.Service => (model.Task is { } task ? task.Service : model.Card!.ParentService) ?? string.Empty,
        _ => DisplayValue(column),
    };

    private static string FormatDate(DateOnly? date) => date?.ToString("d", CultureInfo.CurrentCulture) ?? string.Empty;
}
