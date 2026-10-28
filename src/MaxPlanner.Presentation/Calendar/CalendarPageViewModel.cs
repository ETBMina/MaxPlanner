using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Calendar;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Filters;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Calendar;

/// <summary>The Calendar modes (B-08.1, design §4.4 SelectorBar).</summary>
public enum CalendarMode
{
    Month,
    Week,
    Records,
}

/// <summary>The sortable Records columns (B-08.4).</summary>
public enum RecordSortColumn
{
    Number,
    Title,
    Owner,
    Squad,
    Status,
    Priority,
    PlannedStart,
    PlannedEnd,
    LinkedTasks,
    Overdue,
}

/// <summary>One line of the S-breakpoint agenda (design §4.4): a day header or a CR on that day.</summary>
public sealed record CalendarAgendaRow(bool IsHeader, string HeaderText, CalendarRecordViewModel? Record, string AutomationId)
{
    public bool IsRecord => !IsHeader;
}

/// <summary>
/// The Calendar page (B-08, B-03.1, AC-11, design §4.4): CRs, never tasks, in Month, Week and Records modes, with
/// the Backlog pane. The surface is rebuilt from committed read models after every batch (B-08.10: the layout
/// depends only on data, never on window size). Use on the UI thread.
/// </summary>
public sealed partial class CalendarPageViewModel : ObservableObject, IPageViewModel
{
    private readonly WorkspaceContext _context;
    private readonly ProjectionView<ChangeRequestReadModel, ChangeRequestId, CalendarRecordViewModel> _records;
    private readonly ProjectionView<ChangeRequestReadModel, ChangeRequestId, CalendarRecordViewModel> _backlog;
    private readonly ObservableCollection<CalendarAgendaRow> _agenda = [];
    private readonly IDisposable _commits;
    private Dictionary<PriorityId, PriorityLevel> _priorityLevels = [];
    private bool _hasChosenMode;
    private int _fittedLanes = CalendarLayoutPolicy.DefaultMaxLanes;

    public CalendarPageViewModel(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        // Before the first batch the store has no day yet; the local clock stands in until it does.
        Today = context.Store.Today is { DayNumber: > 0 } today ? today : DateOnly.FromDateTime(DateTime.Now);
        Anchor = Today;
        Filters = context.CrFilters;
        RefreshPriorityLevels();
        _records = new ProjectionView<ChangeRequestReadModel, ChangeRequestId, CalendarRecordViewModel>(
            context.Store.ChangeRequests,
            context.Store.Scheduler,
            context.UiScheduler,
            Create,
            new RecordComparer(RecordSortColumn.PlannedStart, descending: false),
            Filters.Predicate(Today));

        // B-08.3: backlog CRs, newest first.
        _backlog = new ProjectionView<ChangeRequestReadModel, ChangeRequestId, CalendarRecordViewModel>(
            context.Store.ChangeRequests,
            context.Store.Scheduler,
            context.UiScheduler,
            Create,
            NewestFirst.Instance,
            BacklogPredicate(Filters.Predicate(Today)));
        _records.Initialized += (_, _) => Refresh();
        _backlog.Initialized += (_, _) => Refresh();
        ((INotifyCollectionChanged)_backlog.Items).CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(BacklogToggleText));
            OnPropertyChanged(nameof(BacklogCountText));
            OnPropertyChanged(nameof(BacklogCountName));
        };
        ((INotifyCollectionChanged)_records.Items).CollectionChanged += (_, _) => UpdateSummary();
        Agenda = new ReadOnlyObservableCollection<CalendarAgendaRow>(_agenda);

        // One batch notification follows the cache updates of its batch, so the surface reads a consistent state.
        _commits = context.Store.Committed.ObserveOn(context.UiScheduler).Subscribe(commit =>
        {
            var dayChanged = Today != commit.Today;
            Today = commit.Today;
            if (dayChanged)
            {
                // The overdue drill-down predicate is relative to the local day (B-15, AC-21).
                ApplyFilters();
            }

            RefreshPriorityLevels();
            Refresh();
        });
        Filters.Changed += OnFiltersChanged;
        context.ChangeRequestSaved += OnChangeRequestSaved;
        Refresh();
    }

    /// <summary>Raised when the surface was rebuilt, so the view re-renders the custom Month/Week control.</summary>
    public event EventHandler? SurfaceChanged;

    public ReadOnlyObservableCollection<CalendarRecordViewModel> Records => _records.Items;

    public ReadOnlyObservableCollection<CalendarRecordViewModel> Backlog => _backlog.Items;

    /// <summary>The S-breakpoint agenda of the current Month/Week period (design §4.4).</summary>
    public ReadOnlyObservableCollection<CalendarAgendaRow> Agenda { get; }

    public CalendarSurface Surface { get; private set; } = CalendarSurface.Empty;

    /// <summary>
    /// Sets how many bar lanes fit one week row of the visible surface (design §9); the rest goes to "+N more"
    /// (B-08.9). Month mode caps the lanes at <see cref="CalendarLayoutPolicy.DefaultMaxLanes"/>; Week mode shows
    /// every lane that fits. The surface is rebuilt only when the lanes of the current mode change, so window
    /// resizing causes no churn.
    /// </summary>
    /// <returns>True when the surface was rebuilt and <see cref="SurfaceChanged"/> raised.</returns>
    public bool SetLaneCapacity(int lanes)
    {
        var before = LaneCapacity;
        _fittedLanes = Math.Max(0, lanes);
        if (LaneCapacity == before || !IsSurfaceMode)
        {
            return false;
        }

        Refresh();
        return true;
    }

    // The fitted lanes come from the mode on screen when they were measured; after a mode switch the view refits
    // the rebuilt surface, and the Month cap keeps a Week fit from ever widening the Month rows meanwhile.
    private int LaneCapacity => Mode == CalendarMode.Week ? _fittedLanes : Math.Min(_fittedLanes, CalendarLayoutPolicy.DefaultMaxLanes);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordsMode), nameof(IsSurfaceMode), nameof(PeriodText), nameof(PreviousName), nameof(NextName))]
    public partial CalendarMode Mode { get; private set; } = CalendarMode.Month;

    /// <summary>The day the period is built around; Month mode shows its month, Week mode its Sunday–Thursday week.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText))]
    public partial DateOnly Anchor { get; private set; }

    /// <summary>The store's local day (B-15), so the today marker and overdue flags always agree.</summary>
    [ObservableProperty]
    public partial DateOnly Today { get; private set; }

    /// <summary>S breakpoint: Month and Week render as the agenda list (design §4.4).</summary>
    [ObservableProperty]
    public partial bool IsCompact { get; private set; }

    /// <summary>The Calendar CR scope (B-10): search, filters, chips, and the Overdue drill-down context.</summary>
    public CrFilterScopeViewModel Filters { get; }

    /// <summary>B-10 CR search (Number, Title, TaskNumber, Squad, Owner, EmailSubject, Description), part of the CR scope.</summary>
    public string SearchText
    {
        get => Filters.SearchText;
        set => Filters.SearchText = value;
    }

    /// <summary>B-08.12: a saved CR the current filters hide; the page InfoBar offers Clear filters.</summary>
    [ObservableProperty]
    public partial bool IsSavedHiddenOpen { get; set; }

    [ObservableProperty]
    public partial string SavedHiddenMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial ViewState State { get; private set; } = ViewState.Loading;

    /// <summary>B-10: "No matching results" is distinct from an empty dataset; the surface still renders.</summary>
    [ObservableProperty]
    public partial bool HasNoMatches { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBacklogOpen { get; set; } = true;

    [ObservableProperty]
    public partial RecordSortColumn SortColumn { get; private set; } = RecordSortColumn.PlannedStart;

    [ObservableProperty]
    public partial bool IsSortDescending { get; private set; }

    public bool IsRecordsMode => Mode == CalendarMode.Records;

    public bool IsSurfaceMode => Mode != CalendarMode.Records;

    public bool HasAgenda => _agenda.Count > 0;

    /// <summary>"Backlog (4)" (design §4.4 toggle).</summary>
    public string BacklogToggleText => _context.Strings.Format("Calendar_BacklogToggle", Backlog.Count);

    /// <summary>The backlog size alone, for the count badge in the backlog heading (design §4.4).</summary>
    public string BacklogCountText => Backlog.Count.ToString(CultureInfo.CurrentCulture);

    /// <summary>"4 CRs in the backlog": the accessible name of the count badge.</summary>
    public string BacklogCountName => _context.Strings.Format("Calendar_BacklogCountName", Backlog.Count);

    /// <summary>"Oct 2026" or "Oct 11 – Oct 15, 2026" (design §4.4 period button).</summary>
    public string PeriodText => PeriodTextFor(Mode, Anchor);

    /// <summary>
    /// The period button text of every period in the current year for a mode, so the view can give the button the width
    /// of the longest one and keep it from resizing as the user navigates.
    /// </summary>
    public IReadOnlyList<string> PeriodTextCandidates(CalendarMode mode)
    {
        var year = Today.Year;
        if (mode == CalendarMode.Week)
        {
            var weeks = new List<string>();
            for (var week = Workweek.WeekStart(new DateOnly(year, 1, 1)); week.Year <= year; week = week.AddDays(7))
            {
                weeks.Add(PeriodTextFor(CalendarMode.Week, week));
            }

            return weeks;
        }

        return Enumerable.Range(1, 12).Select(month => PeriodTextFor(CalendarMode.Month, new DateOnly(year, month, 1))).ToList();
    }

    private string PeriodTextFor(CalendarMode mode, DateOnly anchor) => mode == CalendarMode.Week
        ? _context.Strings.Format("Calendar_WeekPeriod", Workweek.WeekStart(anchor).ToString("MMM d", CultureInfo.CurrentCulture),
            Workweek.WeekStart(anchor).AddDays(4).ToString("MMM d", CultureInfo.CurrentCulture), Workweek.WeekStart(anchor).AddDays(4).Year)
        : anchor.ToString("MMM yyyy", CultureInfo.CurrentCulture);

    public string PreviousName => _context.Strings.GetString(Mode == CalendarMode.Week ? "Calendar_PreviousWeek" : "Calendar_PreviousMonth");

    public string NextName => _context.Strings.GetString(Mode == CalendarMode.Week ? "Calendar_NextWeek" : "Calendar_NextMonth");

    public string NoMatchesText => _context.Strings.GetString("Calendar_NoMatches");

    /// <summary>The committed read model of a CR, or null after it was deleted.</summary>
    public ChangeRequestReadModel? Find(Guid id) =>
        _context.Store.ChangeRequests.Lookup(new ChangeRequestId(id)) is { HasValue: true } cr ? cr.Value : null;

    /// <summary>Selects a mode from the SelectorBar. Records is also the Overdue-CR drill-down target (B-10).</summary>
    public void SetMode(CalendarMode mode)
    {
        _hasChosenMode = true;
        if (Mode != mode)
        {
            Mode = mode;
            Refresh();
        }
    }

    /// <summary>S breakpoint: the agenda presentation, and Week as the default until the user picks a mode (T-08.1).</summary>
    public void SetCompact(bool isCompact)
    {
        if (IsCompact == isCompact)
        {
            return;
        }

        IsCompact = isCompact;
        if (isCompact && !_hasChosenMode && Mode == CalendarMode.Month)
        {
            Mode = CalendarMode.Week;
        }

        Refresh();
    }

    /// <summary>The date selector or a focused day cell moved the period (B-08.1).</summary>
    public void GoTo(DateOnly day)
    {
        if (Anchor != day)
        {
            var periodChanged = Mode == CalendarMode.Week ? Workweek.WeekStart(day) != Workweek.WeekStart(Anchor) : (day.Year, day.Month) != (Anchor.Year, Anchor.Month);
            Anchor = day;
            if (periodChanged)
            {
                Refresh();
            }
        }
    }

    public CalendarRecordViewModel ViewModelFor(ChangeRequestReadModel cr) => Create(cr);

    [RelayCommand]
    private void GoToToday() => GoTo(Today);

    /// <summary>B-08.2: month steps are anchored on day 1, so a long month never skips a shorter one.</summary>
    [RelayCommand]
    private void Previous() => GoTo(Mode == CalendarMode.Week ? Anchor.AddDays(-7) : CalendarGrid.ShiftMonth(Anchor, -1));

    [RelayCommand]
    private void Next() => GoTo(Mode == CalendarMode.Week ? Anchor.AddDays(7) : CalendarGrid.ShiftMonth(Anchor, 1));

    /// <summary>Accent New CR (Ctrl+N), the CR editor's home (B-03.1).</summary>
    [RelayCommand]
    private Task NewChangeRequestAsync() => _context.ChangeRequests.NewAsync();

    /// <summary>Day flyout "New CR on this date".</summary>
    public Task NewChangeRequestOnAsync(DateOnly day) => _context.ChangeRequests.NewAsync(day);

    [RelayCommand]
    private void ToggleBacklog() => IsBacklogOpen = !IsBacklogOpen;

    /// <summary>A Records header: the first click sorts ascending, the next one descending (B-08.4).</summary>
    public void SortBy(RecordSortColumn column)
    {
        IsSortDescending = column == SortColumn && !IsSortDescending;
        SortColumn = column;
        _records.SetComparer(new RecordComparer(column, IsSortDescending));
    }

    public Task OpenAsync(Guid id) => _context.ChangeRequests.OpenAsync(new ChangeRequestId(id));

    public Task OpenLinkedTasksAsync(Guid id) => _context.ChangeRequests.OpenLinkedTasksAsync(new ChangeRequestId(id));

    /// <summary>Records ⋯ › Create task (B-03.5): a new task with a pending permanent link to this CR.</summary>
    public Task CreateTaskAsync(Guid id) => _context.Tasks.NewLinkedAsync(new ChangeRequestId(id));

    public Task DeleteAsync(Guid id) => _context.ChangeRequests.DeleteAsync(new ChangeRequestId(id));

    /// <summary>Schedule on… after its inline confirmation, or a drop on a CR without dates (B-08.5).</summary>
    public Task<bool> ScheduleAsync(Guid id, DateOnly day) =>
        Find(id) is { } cr ? _context.ChangeRequests.ScheduleAsync(cr, day) : Task.FromResult(false);

    /// <summary>
    /// A backlog card dropped on day D. A CR that already has dates asks first, so its range is never lost
    /// silently (B-08.5): "Replace Oct 4 – Oct 9 with Oct 13?".
    /// </summary>
    public async Task<bool> ScheduleFromDropAsync(Guid id, DateOnly day)
    {
        if (Find(id) is not { } cr)
        {
            return false;
        }

        if (cr.PlannedStart is not null)
        {
            var confirmation = new ConfirmationViewModel(
                "Calendar_ReplaceDatesDialog",
                _context.Strings.GetString("Calendar_ReplaceDatesTitle"),
                ReplacePrompt(cr, day),
                _context.Strings.GetString("Calendar_ScheduleButton"),
                null,
                _context.Strings.GetString("Common_Cancel"));
            await _context.ShowDialogAsync(confirmation).ConfigureAwait(true);
            if (confirmation.Result != ConfirmationResult.Primary)
            {
                return false;
            }
        }

        return await _context.ChangeRequests.ScheduleAsync(cr, day).ConfigureAwait(true);
    }

    /// <summary>"Replace Oct 4 – Oct 9 with Oct 13?" (design §4.4); empty when the CR has no dates.</summary>
    public string ReplacePrompt(ChangeRequestReadModel cr, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(cr);
        return cr.PlannedStart is null ? string.Empty : _context.Strings.Format("Calendar_ReplaceDates", Create(cr).RangeText, day.ToString("MMM d", CultureInfo.CurrentCulture));
    }

    /// <summary>One bar edge, a keyboard step, or the Edit dates flyout (B-08.8). Failure keeps the committed dates.</summary>
    public async Task<bool> ResizeAsync(Guid id, DateOnly? newStart, DateOnly? newEnd)
    {
        if (Find(id) is not { } cr)
        {
            return false;
        }

        var ok = await _context.ChangeRequests.ResizeAsync(cr, newStart, newEnd).ConfigureAwait(true);
        if (!ok)
        {
            // The view may show a dragged or stepped preview; the committed dates are shown again.
            RaiseSurfaceChanged();
        }

        return ok;
    }

    public Task<bool> MoveToBacklogAsync(Guid id) =>
        Find(id) is { } cr ? _context.ChangeRequests.MoveToBacklogAsync(cr) : Task.FromResult(false);

    /// <summary>"Tuesday, October 13, 2 CRs, today" (design §4.4 day cell name).</summary>
    public string DayName(CalendarDayCell day)
    {
        ArgumentNullException.ThrowIfNull(day);
        var name = _context.Strings.Format(day.Items.Count == 1 ? "Calendar_DayNameOne" : "Calendar_DayName", day.Date.ToString("D", CultureInfo.CurrentCulture), day.Items.Count);
        return day.IsToday ? name + ", " + _context.Strings.GetString("Calendar_TodayMarker") : name;
    }

    /// <summary>"CR-1042 Card limits, In Progress, Oct 13 – Oct 16, overdue, continues from previous week" (design §6.3, §8.2).</summary>
    public string BarName(CalendarBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        var record = Create(bar.Cr);
        var parts = new List<string> { record.Number + " " + record.Title, record.StatusName, record.RangeText };
        if (bar.Cr.IsOverdue)
        {
            parts.Add(record.OverdueText);
        }
        else if (bar.Cr.IsCompleted)
        {
            parts.Add(_context.Strings.GetString("Calendar_Completed"));
        }

        if (bar.ContinuesBefore)
        {
            parts.Add(_context.Strings.GetString("Calendar_ContinuesBefore"));
        }

        if (bar.ContinuesAfter)
        {
            parts.Add(_context.Strings.GetString("Calendar_ContinuesAfter"));
        }

        return string.Join(", ", parts);
    }

    public string MoreText(int count) => _context.Strings.Format("Calendar_More", count);

    public string MoreName(CalendarDayCell day)
    {
        ArgumentNullException.ThrowIfNull(day);
        return _context.Strings.Format("Calendar_MoreName", day.HiddenCount, day.Date.ToString("D", CultureInfo.CurrentCulture));
    }

    public string OutsideName(CalendarWeek week)
    {
        ArgumentNullException.ThrowIfNull(week);
        return _context.Strings.Format("Calendar_OutsideName", week.OutsideWorkdays.Count);
    }

    public string Text(string key) => _context.Strings.GetString(key);

    public string Format(string key, params object?[] args) => _context.Strings.Format(key, args);

    public void Dispose()
    {
        Filters.Changed -= OnFiltersChanged;
        _context.ChangeRequestSaved -= OnChangeRequestSaved;
        _commits.Dispose();
        _records.Dispose();
        _backlog.Dispose();
    }

    /// <summary>The InfoBar's Clear filters after a saved CR was hidden (B-08.12).</summary>
    [RelayCommand]
    private void ClearFiltersForSaved()
    {
        IsSavedHiddenOpen = false;
        Filters.ClearAll();
    }

    private void OnFiltersChanged(object? sender, EventArgs e)
    {
        IsSavedHiddenOpen = false;
        ApplyFilters();
        OnPropertyChanged(nameof(SearchText));
        Refresh();
    }

    /// <summary>B-08.12, B-10: bars, backlog, overflow and Records all use the one CR scope predicate.</summary>
    private void ApplyFilters()
    {
        var filter = Filters.Predicate(Today);
        _records.SetFilter(filter);
        _backlog.SetFilter(BacklogPredicate(filter));
    }

    /// <summary>
    /// B-08.12: editing a CR that the filters hide must stay discoverable, so the save result says it is hidden and
    /// offers Clear filters.
    /// </summary>
    private void OnChangeRequestSaved(object? sender, ChangeRequestId id)
    {
        if (Filters.IsFiltered && Find(id.Value) is { } cr && !Filters.Predicate(Today)(cr))
        {
            SavedHiddenMessage = _context.Strings.Format("Calendar_SavedHidden", cr.Number);
            IsSavedHiddenOpen = true;
            _context.Announce(SavedHiddenMessage);
        }
    }

    private static Func<ChangeRequestReadModel, bool> BacklogPredicate(Func<ChangeRequestReadModel, bool> filter) => cr => cr.IsBacklog && filter(cr);

    private CalendarRecordViewModel Create(ChangeRequestReadModel model) => new(model, _context.Strings, LevelOf);

    private PriorityLevel LevelOf(ChangeRequestReadModel cr) => _priorityLevels.TryGetValue(cr.Priority.Id, out var level) ? level : PriorityLevel.Mid;

    // Design §6.1: chips are assigned by configured rank, never by name.
    private void RefreshPriorityLevels()
    {
        if (_context.Store.CurrentWorkflow is not { } workflow)
        {
            return;
        }

        var ordered = workflow.Priorities.OrderBy(p => p.Order).ToList();
        var levels = new Dictionary<PriorityId, PriorityLevel>();
        for (var i = 0; i < ordered.Count; i++)
        {
            levels[ordered[i].Id] = i == 0 ? PriorityLevel.Level1
                : ordered.Count >= 3 && i == ordered.Count - 1 ? PriorityLevel.Lowest
                : i == 1 ? PriorityLevel.Level2
                : PriorityLevel.Mid;
        }

        _priorityLevels = levels;
    }

    private void Refresh()
    {
        var filter = Filters.Predicate(Today);
        var all = _context.Store.ChangeRequests.Items;
        var matching = all.Where(filter).ToList();
        if (Mode != CalendarMode.Records)
        {
            var rows = Mode == CalendarMode.Week ? CalendarGrid.Week(Anchor) : CalendarGrid.Month(Anchor.Year, Anchor.Month);
            var month = Mode == CalendarMode.Week ? (DateOnly?)null : new DateOnly(Anchor.Year, Anchor.Month, 1);
            Surface = CalendarSurfaceBuilder.Build(matching, rows, month, Today, LaneCapacity);
            if (IsCompact)
            {
                RebuildAgenda();
            }

            RaiseSurfaceChanged();
        }

        HasNoMatches = Filters.IsFiltered && all.Count > 0 && matching.Count == 0;
        State = !_records.IsInitialized || !_backlog.IsInitialized ? ViewState.Loading : all.Count == 0 ? ViewState.Empty : ViewState.Populated;
        UpdateSummary();
    }

    private void RaiseSurfaceChanged() => SurfaceChanged?.Invoke(this, EventArgs.Empty);

    // Design §4.4 S: one header per visible workday with CRs in the period, then the CRs wholly on hidden days.
    private void RebuildAgenda()
    {
        _agenda.Clear();
        foreach (var day in Surface.Weeks.SelectMany(w => w.Days).Where(d => d.IsInPeriod && d.Items.Count > 0))
        {
            var key = day.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            _agenda.Add(new CalendarAgendaRow(true, day.Date.ToString("D", CultureInfo.CurrentCulture), null, "Calendar_AgendaDay_" + key));
            foreach (var cr in day.Items)
            {
                _agenda.Add(new CalendarAgendaRow(false, string.Empty, Create(cr), "Calendar_AgendaRow_" + cr.Id.Value.ToString("N") + "_" + key));
            }
        }

        var outside = Surface.Weeks.SelectMany(w => w.OutsideWorkdays).DistinctBy(cr => cr.Id).ToList();
        if (outside.Count > 0)
        {
            _agenda.Add(new CalendarAgendaRow(true, _context.Strings.GetString("Calendar_OutsideHeader"), null, "Calendar_AgendaOutside"));
            foreach (var cr in outside)
            {
                _agenda.Add(new CalendarAgendaRow(false, string.Empty, Create(cr), "Calendar_AgendaRow_" + cr.Id.Value.ToString("N")));
            }
        }

        OnPropertyChanged(nameof(HasAgenda));
    }

    private void UpdateSummary()
    {
        var workweek = _context.Strings.GetString("Calendar_WorkweekCaption");
        Summary = Mode == CalendarMode.Records
            ? _context.Strings.Format("Calendar_RecordsSummary", Records.Count, _context.Store.ChangeRequests.Count, workweek)
            : workweek;
    }

    /// <summary>B-08.3: newest first, then ID.</summary>
    private sealed class NewestFirst : IComparer<CalendarRecordViewModel>
    {
        public static NewestFirst Instance { get; } = new();

        public int Compare(CalendarRecordViewModel? x, CalendarRecordViewModel? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : -1) : 1;
            }

            var byCreated = y.Model.CreatedAtUtc.CompareTo(x.Model.CreatedAtUtc);
            return byCreated != 0 ? byCreated : x.Model.Id.Value.CompareTo(y.Model.Id.Value);
        }
    }

    /// <summary>Records sorting; unscheduled CRs sort after scheduled ones on date columns, and the CR ID breaks ties.</summary>
    private sealed class RecordComparer(RecordSortColumn column, bool descending) : IComparer<CalendarRecordViewModel>
    {
        public int Compare(CalendarRecordViewModel? x, CalendarRecordViewModel? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : -1) : 1;
            }

            var a = x.Model;
            var b = y.Model;
            var result = column switch
            {
                RecordSortColumn.Number => Text(a.Number, b.Number),
                RecordSortColumn.Title => Text(a.Title, b.Title),
                RecordSortColumn.Owner => Text(a.Owner.Name, b.Owner.Name),
                RecordSortColumn.Squad => Text(a.SquadName, b.SquadName),
                RecordSortColumn.Status => a.StatusOrder.CompareTo(b.StatusOrder),
                RecordSortColumn.Priority => a.PriorityOrder.CompareTo(b.PriorityOrder),
                RecordSortColumn.PlannedStart => Date(a.PlannedStart, b.PlannedStart),
                RecordSortColumn.PlannedEnd => Date(a.PlannedEnd, b.PlannedEnd),
                RecordSortColumn.LinkedTasks => a.LinkedTaskCount.CompareTo(b.LinkedTaskCount),
                _ => a.DaysOverdue.CompareTo(b.DaysOverdue),
            };
            if (descending)
            {
                result = -result;
            }

            return result != 0 ? result : Text(a.Number, b.Number) is var byNumber && byNumber != 0 ? byNumber : a.Id.Value.CompareTo(b.Id.Value);
        }

        private static int Text(string a, string b) => StringComparer.CurrentCultureIgnoreCase.Compare(a, b);

        private static int Date(DateOnly? a, DateOnly? b) => (a, b) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => a.Value.CompareTo(b.Value),
        };
    }
}
