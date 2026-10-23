using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Grid;

/// <summary>Stable column keys; they appear in AutomationIds (<c>Grid_Cell_{Id}_{Key}</c>) and never change with labels.</summary>
public static class GridColumns
{
    public const string Kind = "Kind";
    public const string CrNumber = "CrNumber";
    public const string Title = "Title";
    public const string Subtitle = "Subtitle";
    public const string Service = "Service";
    public const string Status = "Status";
    public const string Priority = "Priority";
    public const string StartDate = "StartDate";
    public const string EndDate = "EndDate";

    /// <summary>The Phase 2 column set in display order (B-07 subset; the full set arrives in Phase 9).</summary>
    public static IReadOnlyList<string> All { get; } = [Kind, CrNumber, Title, Subtitle, Service, Status, Priority, StartDate, EndDate];
}

/// <summary>A task status offered by the Grid's Status editor.</summary>
public sealed record StatusOption(TaskStatusId Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// One Grid row: a task or a Subtask card (B-07). Display properties always show the committed read model;
/// an open cell editor works on a draft that is replaced only by the user or by an explicit Reload (B-02.9).
/// </summary>
/// <remarks>
/// Use on the UI thread. While a cell editor is open the row's sort position is held (<see cref="SortModel"/>
/// stops following <see cref="ProjectionItemViewModel{TModel, TKey}.Model"/>) so the row the user is
/// typing in never jumps away; the page re-sorts once editing ends (design §7.7).
/// </remarks>
public sealed partial class GridRowViewModel : ProjectionItemViewModel<WorkItemRecord, Guid>
{
    private readonly ILocalizedStrings _strings;
    private TaskCompletionSource? _revisionWaiter;
    private long _awaitedRevision;

    public GridRowViewModel(WorkItemRecord model, ILocalizedStrings strings)
        : base(model)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        SortModel = model;
    }

    public override Guid Key => Model.Id;

    /// <summary>The model the sort order uses; frozen while the row is being edited.</summary>
    public WorkItemRecord SortModel { get; private set; }

    public bool IsTask => Model.Kind == BoardCardKind.Task;

    public bool IsSubtaskCard => !IsTask;

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
        TitleText);

    /// <summary>The column whose editor is open, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsEditingTitle), nameof(IsEditingSubtitle), nameof(IsEditingService), nameof(IsEditingStatus), nameof(ConflictSavedText))]
    public partial string? EditingColumn { get; private set; }

    /// <summary>The text editor's draft (Title, Subtitle, Service); bound two-way.</summary>
    [ObservableProperty]
    public partial string DraftText { get; set; } = string.Empty;

    /// <summary>The Status editor's draft; bound two-way.</summary>
    [ObservableProperty]
    public partial StatusOption? DraftStatus { get; set; }

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

    /// <summary>"Saved now" in the conflict popup: the committed value, which follows later store updates.</summary>
    public string? ConflictSavedText => HasConflict && EditingColumn is { } column ? DisplayValue(column) : null;

    /// <summary>The model the open edit started from; its revision is the expected revision of the commit.</summary>
    internal WorkItemRecord? BaseModel { get; private set; }

    /// <summary>The draft the last failed attempt sent; leaving the cell does not resend the same refused draft.</summary>
    internal string? FailedDraft { get; private set; }

    /// <summary>T-05: the operation to reuse when the last attempt's outcome was unknown, for the same draft.</summary>
    internal (OperationId Operation, string Draft)? UnknownOutcome { get; private set; }

    /// <summary>B-07: only these cells edit in Phase 2. Kind, CR Number, Priority and dates are read-only here.</summary>
    public bool CanEdit(string column) => Model.IsActive && column switch
    {
        GridColumns.Title => Model.Task is { IsTitleInherited: false },
        GridColumns.Subtitle => IsSubtaskCard,
        GridColumns.Service => IsTask,
        GridColumns.Status => true,
        _ => false,
    };

    public string DisplayValue(string column) => column switch
    {
        GridColumns.Kind => KindText,
        GridColumns.CrNumber => CrNumberText,
        GridColumns.Title => TitleText,
        GridColumns.Subtitle => SubtitleText,
        GridColumns.Service => ServiceText,
        GridColumns.Status => StatusText,
        GridColumns.Priority => PriorityText,
        GridColumns.StartDate => StartDateText,
        GridColumns.EndDate => EndDateText,
        _ => string.Empty,
    };

    /// <summary>The draft as text, for comparisons and the cell's UIA value while editing.</summary>
    public string DraftValue => EditingColumn == GridColumns.Status ? DraftStatus?.Name ?? string.Empty : DraftText;

    internal void BeginEdit(string column, string? initialText, IEnumerable<StatusOption> statusOptions)
    {
        EditingColumn = column;
        BaseModel = Model;
        UnknownOutcome = null;
        FailedDraft = null;
        ErrorMessage = null;
        HasConflict = false;
        DraftText = initialText ?? DisplayValue(column);
        DraftStatus = statusOptions.FirstOrDefault(o => o.Id == Model.Status.Id);
    }

    /// <summary>True when committing would change the record.</summary>
    internal bool IsDraftChanged() => EditingColumn switch
    {
        GridColumns.Status => DraftStatus is { } status && status.Id != BaseModel!.Status.Id,
        { } column => !string.Equals(DraftText, DisplayValueOf(BaseModel!, column), StringComparison.Ordinal),
        null => false,
    };

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

    protected override void OnModelUpdated(WorkItemRecord previous)
    {
        if (!IsEditing)
        {
            SortModel = Model;
        }

        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(IsSubtaskCard));
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
        if (_revisionWaiter is not null && Model.Revision >= _awaitedRevision)
        {
            CompleteRevisionWait();
        }
    }

    private void CompleteRevisionWait()
    {
        var waiter = _revisionWaiter;
        _revisionWaiter = null;
        waiter?.TrySetResult();
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
