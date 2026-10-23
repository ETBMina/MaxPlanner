using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Dashboard;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Dashboard;

/// <summary>A Dashboard filter choice: "All squads", a configured value, or "General" (a null <see cref="Id"/> with <see cref="IsGeneral"/>).</summary>
public sealed record DashboardOption(Guid? Id, string Label, bool IsGeneral = false)
{
    public bool IsAll => Id is null && !IsGeneral;

    public override string ToString() => Label;
}

/// <summary>A secondary metric on a summary card: "2 done this week" (a link of its own, never inside the card button).</summary>
public sealed partial class DashboardLinkViewModel : ObservableObject
{
    internal DashboardLinkViewModel(string automationId, IRelayCommand command)
    {
        AutomationId = automationId;
        Command = command;
    }

    public string AutomationId { get; }

    public IRelayCommand Command { get; }

    [ObservableProperty]
    public partial string Text { get; internal set; } = string.Empty;
}

/// <summary>
/// One summary card (design §4.1): label, value, definition caption, optional "Needs attention" badge, and the
/// drill-down with exactly the card's predicate. The value stays empty until the first result (B-15.7: no zeros
/// before data).
/// </summary>
public sealed partial class DashboardCardViewModel : ObservableObject
{
    internal DashboardCardViewModel(string automationId, string label, Action<DashboardCardViewModel> drillDown)
    {
        AutomationId = automationId;
        Label = label;
        DrillDownCommand = new RelayCommand(() => drillDown(this), () => DrillDown is not null);
    }

    public string AutomationId { get; }

    public string Label { get; }

    public IRelayCommand DrillDownCommand { get; }

    public ObservableCollection<DashboardLinkViewModel> Links { get; } = [];

    public bool HasLinks => Links.Count > 0;

    [ObservableProperty]
    public partial string Value { get; internal set; } = string.Empty;

    [ObservableProperty]
    public partial string Caption { get; internal set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasBadge { get; internal set; }

    /// <summary>"Active tasks, 12, of 32 total · not in a completed status. Opens matching tasks in Grid."</summary>
    [ObservableProperty]
    public partial string AccessibleName { get; internal set; } = string.Empty;

    internal DashboardDrillDown? DrillDown { get; private set; }

    internal string DrillLabel { get; set; } = string.Empty;

    internal void SetDrillDown(DashboardDrillDown? drillDown)
    {
        DrillDown = drillDown;
        DrillDownCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// One bar row (ADR 0011, design §4.1): category label, a bar proportional to the largest count (at least 2 epx when
/// above zero, none at zero), and the count, always as text. Its accessible name carries category, count, share,
/// and where it opens, so the chart never depends on color or hover (design §8.2).
/// </summary>
public sealed partial class BarRowViewModel : ObservableObject
{
    internal BarRowViewModel(string key, string automationId, Action<BarRowViewModel> drillDown)
    {
        Key = key;
        AutomationId = automationId;
        DrillDownCommand = new RelayCommand(() => drillDown(this), () => HasDrillDown);
    }

    public string Key { get; }

    public string AutomationId { get; }

    public IRelayCommand DrillDownCommand { get; }

    [ObservableProperty]
    public partial string Label { get; internal set; } = string.Empty;

    [ObservableProperty]
    public partial string CountText { get; internal set; } = string.Empty;

    [ObservableProperty]
    public partial string ShareText { get; internal set; } = string.Empty;

    /// <summary>Count divided by the largest count in the chart (0–1).</summary>
    [ObservableProperty]
    public partial double Fraction { get; internal set; }

    [ObservableProperty]
    public partial string AccessibleName { get; internal set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasDrillDown { get; private set; }

    internal DashboardDrillDown? DrillDown { get; private set; }

    internal string DrillLabel { get; set; } = string.Empty;

    internal void SetDrillDown(DashboardDrillDown? drillDown)
    {
        DrillDown = drillDown;
        HasDrillDown = drillDown is not null;
        DrillDownCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>A native accessible bar chart (ADR 0011) with its Table toggle (design §4.1).</summary>
public sealed partial class BarChartViewModel : ObservableObject
{
    private readonly ILocalizedStrings _strings;
    private readonly string _keyPrefix;
    private readonly string _unitKey;
    private readonly string _opensKey;
    private readonly string _drillLabelKey;
    private readonly Action<BarRowViewModel> _drillDown;
    private readonly ObservableCollection<BarRowViewModel> _rows = [];

    /// <param name="keyPrefix">The bar AutomationId prefix, e.g. <c>Dashboard_StatusBar_</c> (design §8.1).</param>
    /// <param name="unitKey">The count unit resource prefix ("Dashboard_UnitTask" → "1 task" / "6 tasks").</param>
    internal BarChartViewModel(
        ILocalizedStrings strings, string title, string footnote, string keyPrefix, string unitKey, string opensKey, string drillLabelKey, Action<BarRowViewModel> drillDown)
    {
        _strings = strings;
        Title = title;
        Footnote = footnote;
        _keyPrefix = keyPrefix;
        _unitKey = unitKey;
        _opensKey = opensKey;
        _drillLabelKey = drillLabelKey;
        _drillDown = drillDown;
        Rows = new ReadOnlyObservableCollection<BarRowViewModel>(_rows);
    }

    public string Title { get; }

    public string Footnote { get; }

    public bool HasFootnote => Footnote.Length > 0;

    public ReadOnlyObservableCollection<BarRowViewModel> Rows { get; }

    [ObservableProperty]
    public partial string TotalText { get; private set; } = string.Empty;

    /// <summary>The Table toggle (design §4.1): the same items, order, and commands as a Category | Count | Share list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBarMode))]
    public partial bool IsTableMode { get; set; }

    public bool IsBarMode => !IsTableMode;

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    internal void Update(DashboardChart chart)
    {
        var max = chart.Bars.Count == 0 ? 0 : chart.Bars.Max(b => b.Count);
        KeyedCollectionSync.Sync(
            _rows,
            chart.Bars,
            bar => bar.Key,
            row => row.Key,
            bar => new BarRowViewModel(bar.Key, _keyPrefix + bar.Key, _drillDown),
            (_, _) => { });
        foreach (var (row, bar) in _rows.Zip(chart.Bars))
        {
            var share = chart.Total == 0 ? 0 : (int)Math.Round(100.0 * bar.Count / chart.Total, MidpointRounding.AwayFromZero);
            var countText = Unit(bar.Count);
            row.Label = bar.Label;
            row.CountText = bar.Count.ToString(CultureInfo.CurrentCulture);
            row.ShareText = _strings.Format("Dashboard_SharePercent", share);
            row.Fraction = max == 0 ? 0 : (double)bar.Count / max;
            row.DrillLabel = _strings.Format(_drillLabelKey, bar.Label);
            row.SetDrillDown(bar.DrillDown);
            row.AccessibleName = _strings.Format("Dashboard_BarName", bar.Label, countText, share, chart.Total)
                + (bar.DrillDown is null ? string.Empty : " " + _strings.GetString(_opensKey));
        }

        TotalText = _strings.Format("Dashboard_ChartTotal", Unit(chart.Total));
        IsEmpty = chart.Bars.Count == 0;
    }

    private string Unit(int count) => _strings.Format(count == 1 ? _unitKey + "One" : _unitKey, count);
}

/// <summary>An Overdue CRs row (B-15.4): number, title, owner, squad, status, priority, planned end, days overdue.</summary>
public sealed class OverdueRowViewModel(OverdueCrRow row, ILocalizedStrings strings)
{
    public Guid Id { get; } = row.ChangeRequest.Id.Value;

    public string Number { get; } = row.ChangeRequest.Number;

    public string Title { get; } = row.ChangeRequest.Title;

    public string Owner { get; } = row.ChangeRequest.Owner.Name;

    public string Squad { get; } = row.ChangeRequest.SquadName;

    public string Status { get; } = row.ChangeRequest.Status.Name;

    public string Priority { get; } = row.ChangeRequest.Priority.Name;

    public string PlannedEnd { get; } = row.ChangeRequest.PlannedEnd?.ToString("MMM d, yyyy", CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>Design §6.3: a number plus "days", never color alone.</summary>
    public string DaysText { get; } = strings.Format(row.DaysOverdue == 1 ? "Dashboard_DaysOverdueOne" : "Dashboard_DaysOverdue", row.DaysOverdue);

    /// <summary>The S-breakpoint second line: owner · end · days.</summary>
    public string Secondary => string.Join(" · ", Owner, Squad, Status, Priority, PlannedEnd, DaysText);

    public string AutomationId => "Dashboard_OverdueRow_" + Id.ToString("N", CultureInfo.InvariantCulture);

    public string AccessibleName => strings.Format("Dashboard_OverdueRowName", Number, Title, Owner, PlannedEnd, DaysText);
}

/// <summary>An Unlinked tasks row (B-15.5): title, service, status, priority, people. Opens the task editor to link a CR.</summary>
public sealed class UnlinkedRowViewModel(TaskReadModel task, ILocalizedStrings strings)
{
    public Guid Id { get; } = task.Id.Value;

    public string Title { get; } = task.EffectiveTitle;

    public string Service { get; } = task.Service ?? string.Empty;

    public string Status { get; } = task.Status.Name;

    public string Priority { get; } = task.Priority.Name;

    public string People { get; } = string.Join(", ", task.Designers.Concat(task.Developers).Select(p => p.Name).Distinct(StringComparer.CurrentCulture));

    public string Secondary => string.Join(" · ", new[] { Service, Status, Priority, People }.Where(p => p.Length > 0));

    public string AutomationId => "Dashboard_UnlinkedRow_" + Id.ToString("N", CultureInfo.InvariantCulture);

    public string AccessibleName => strings.Format("Dashboard_UnlinkedRowName", Title, Secondary);
}
