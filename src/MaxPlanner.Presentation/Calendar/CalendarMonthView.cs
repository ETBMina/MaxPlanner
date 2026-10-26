using System.Globalization;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Calendar;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace MaxPlanner.Presentation.Calendar;

/// <summary>
/// A bar edge handle (B-08.8): a <see cref="Thumb"/> for the drag inside a panel that shows the horizontal resize
/// cursor (Thumb is sealed, so the cursor is set on this host).
/// </summary>
public sealed partial class CalendarResizeHandle : WinGrid
{
    public CalendarResizeHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Width = 8;
        Thumb = new Thumb
        {
            IsTabStop = false,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Children.Add(Thumb);
    }

    public Thumb Thumb { get; }
}

/// <summary>
/// The custom Month/Week surface of design §4.4 (P1: no platform control fits). It renders a
/// <see cref="CalendarSurface"/>: per week row, five focusable day cells (Sun–Thu, drop targets with accessible
/// names), the Fri–Sat stub with the "Outside visible workdays" flyout, the bar lanes that fit (at most four) with
/// continuation cues and edge handles, and "+N more" links. Pure view mechanics; every command goes to
/// <see cref="CalendarPageViewModel"/>.
/// </summary>
/// <remarks>
/// The surface is rebuilt after each committed batch. The focused day or bar is remembered by key and focused
/// again in the new tree, so a commit elsewhere does not move keyboard focus (T-07). <see cref="Fit"/> sizes the
/// week rows to the viewport and derives the lane cap from the row height (design §9).
/// </remarks>
public sealed partial class CalendarMonthView : UserControl
{
    /// <summary>The in-process drag property that marks a backlog CR drag (design §9).</summary>
    public const string DragCrIdProperty = "MaxPlanner.CrId";

    private const double StubWidth = 28;
    private const double LaneGap = 2;
    private const double DayHeaderHeight = 30;
    private const double MoreRowHeight = 24;

    private static readonly TimeSpan KeyboardCommitDelay = TimeSpan.FromMilliseconds(600);

    private readonly WinGrid _root = new();
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibility = new();
    private readonly Dictionary<string, Control> _focusables = [];
    private readonly Dictionary<Guid, (DateOnly Start, DateOnly End)> _pendingKeyboard = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _keyboardTimer;
    private CalendarSurface _surface = CalendarSurface.Empty;
    private string? _focusKey;
    private string? _paletteTheme;
    private CalendarBarPalette? _palette;

    public CalendarMonthView()
    {
        IsTabStop = false;
        Content = _root;
        AutomationProperties.SetAutomationId(this, "Calendar_Surface");
        AutomationProperties.SetLandmarkType(this, AutomationLandmarkType.Main);

        // Bar colors are local brushes of one theme (CalendarBarPalette), so a Light/Dark or High Contrast switch
        // re-renders the surface. Loaded covers a switch while the cached page was out of the tree.
        ActualThemeChanged += (_, _) => RenderIfThemeChanged();
        Loaded += (_, _) =>
        {
            _uiSettings.ColorValuesChanged += OnColorValuesChanged;
            RenderIfThemeChanged();
        };
        Unloaded += (_, _) => _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
    }

    public CalendarPageViewModel? ViewModel { get; set; }

    /// <summary>The day under keyboard focus, used as the default date of "Schedule on…" (design §7.5).</summary>
    public DateOnly? FocusedDay { get; private set; }

    /// <summary>Rebuilds the visual tree for <paramref name="surface"/>, keeping keyboard focus on the same day or bar.</summary>
    public void Render(CalendarSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var themeKey = ThemeKey();
        if (themeKey != _paletteTheme)
        {
            _paletteTheme = themeKey;
            _palette = CalendarBarPalette.Resolve(themeKey);
        }

        var hadFocus = _focusKey is not null && _focusables.TryGetValue(_focusKey, out var focused) && focused.FocusState != FocusState.Unfocused;
        _surface = surface;
        _focusables.Clear();
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        var scale = _uiSettings.TextScaleFactor;
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(NamesRowHeight(scale)) });
        _root.Children.Add(HeaderRow());
        foreach (var week in surface.Weeks)
        {
            // Star rows share the fitted height equally; without a fitted height (the scroll fallback) they size to content.
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var row = WeekRow(viewModel, week, surface.IsWeekMode, surface.MaxLanes, scale);
            WinGrid.SetRow(row, _root.RowDefinitions.Count - 1);
            _root.Children.Add(row);
        }

        if (hadFocus && _focusKey is not null && _focusables.TryGetValue(_focusKey, out var again))
        {
            again.Focus(FocusState.Keyboard);
        }
    }

    /// <summary>
    /// Design §9: fits the surface to <paramref name="viewportHeight"/>. The week rows share the height, and the
    /// lane cap is the number of bar lanes one row can hold (at most four); "+N more" absorbs the rest (B-08.9).
    /// </summary>
    /// <remarks>
    /// Only the viewport height is read, never this control's desired size, so fitting cannot start a layout
    /// cycle. When not even a lane-less row fits, the surface keeps its natural height and the host scrolls.
    /// </remarks>
    /// <returns>The lane capacity for <see cref="CalendarPageViewModel.SetLaneCapacity"/>.</returns>
    public int Fit(double viewportHeight, int weekCount, bool isWeekMode)
    {
        var scale = _uiSettings.TextScaleFactor;
        var rowHeight = (viewportHeight - NamesRowHeight(scale)) / Math.Max(1, weekCount);
        var reserved = (DayHeaderHeight + MoreRowHeight) * scale;
        if (rowHeight < reserved)
        {
            Height = double.NaN;
            return 0;
        }

        Height = Math.Floor(viewportHeight);
        var lanes = (int)Math.Floor((rowHeight - reserved) / LaneHeight(isWeekMode, scale));
        return Math.Clamp(lanes, 0, CalendarLayoutPolicy.DefaultMaxLanes);
    }

    /// <summary>Focuses a day cell, for example after keyboard navigation moved the period.</summary>
    public bool FocusDay(DateOnly day) =>
        _focusables.TryGetValue(DayKey(day), out var cell) && cell.Focus(FocusState.Keyboard);

    private string ThemeKey() => CalendarBarPalette.ThemeKey(ActualTheme, _accessibility.HighContrast);

    private void RenderIfThemeChanged()
    {
        if (_paletteTheme is not null && ThemeKey() != _paletteTheme)
        {
            Render(_surface);
        }
    }

    // UISettings raises this off the UI thread; it also fires when High Contrast turns on or off.
    private void OnColorValuesChanged(Windows.UI.ViewManagement.UISettings sender, object args) =>
        DispatcherQueue?.TryEnqueue(RenderIfThemeChanged);

    private static string DayKey(DateOnly day) => "day:" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string BarKey(CalendarBar bar) => "bar:" + bar.Cr.Id.Value.ToString("N") + ":" + bar.SegmentStart.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string DateId(DateOnly day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static Style StyleOf(string key) => (Style)Microsoft.UI.Xaml.Application.Current.Resources[key];

    // A caption line (16 epx at 100% text) plus the 4-epx top and bottom margins of the day-name header.
    private static double NamesRowHeight(double scale) => (16 * scale) + 8;

    private static double LaneHeight(bool isWeekMode, double scale) => ((isWeekMode ? 40 : 22) * scale) + LaneGap;

    private static WinGrid Columns()
    {
        var grid = new WinGrid();
        for (var i = 0; i < Workweek.VisibleDays.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StubWidth) });
        return grid;
    }

    private static WinGrid HeaderRow()
    {
        var grid = Columns();
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        for (var column = 0; column < Workweek.VisibleDays.Count; column++)
        {
            var text = new TextBlock
            {
                Text = names[(int)Workweek.VisibleDays[column]],
                Style = StyleOf("CaptionTextBlockStyle"),
                Margin = new Thickness(8, 4, 0, 4),
            };
            WinGrid.SetColumn(text, column);
            grid.Children.Add(text);
        }

        return grid;
    }

    private WinGrid WeekRow(CalendarPageViewModel viewModel, CalendarWeek week, bool isWeekMode, int maxLanes, double scale)
    {
        // Heights scale with TextScaleFactor and match Fit, so the fitted lanes always fit the row (design §9).
        var grid = Columns();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DayHeaderHeight * scale) });
        for (var lane = 0; lane < maxLanes; lane++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LaneHeight(isWeekMode, scale)) });
        }

        // The "+N more" row takes the rest of the fitted row height.
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = MoreRowHeight * scale });

        // Tab order follows child order: each day cell, then the bars starting on it, then its "+N more" (design §7.5).
        foreach (var day in week.Days)
        {
            grid.Children.Add(DayCell(viewModel, week, day, maxLanes));
            foreach (var bar in week.Bars.Where(b => b.StartColumn == day.Column))
            {
                grid.Children.Add(Bar(viewModel, week, bar, isWeekMode));
            }

            if (day.HiddenCount > 0)
            {
                grid.Children.Add(MoreLink(viewModel, day, maxLanes));
            }
        }

        grid.Children.Add(Stub(viewModel, week, maxLanes));
        return grid;
    }

    private Button DayCell(CalendarPageViewModel viewModel, CalendarWeek week, CalendarDayCell day, int maxLanes)
    {
        // Design §4.4: the 1st of a month (and the first cell shown) carries the month name.
        var label = day.Date.Day == 1 || (day.Column == 0 && ReferenceEquals(week, _surface.Weeks[0]))
            ? day.Date.ToString("MMM d", CultureInfo.CurrentCulture)
            : day.Date.Day.ToString(CultureInfo.CurrentCulture);
        UIElement header = day.IsToday
            ? new Border { Style = StyleOf("CalendarTodayBadgeStyle"), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = label, Style = StyleOf("CalendarTodayTextStyle") } }
            : new TextBlock { Text = label, Style = StyleOf(day.IsInPeriod ? "CalendarDayTextStyle" : "CalendarAdjacentDayTextStyle") };
        var cell = new Button
        {
            Style = StyleOf("CalendarDayCellButtonStyle"),
            Content = header,
            AllowDrop = true,
        };
        WinGrid.SetColumn(cell, day.Column);
        WinGrid.SetRowSpan(cell, maxLanes + 2);
        AutomationProperties.SetAutomationId(cell, "Calendar_Day_" + DateId(day.Date));
        AutomationProperties.SetName(cell, viewModel.DayName(day));
        var key = DayKey(day.Date);
        _focusables[key] = cell;
        cell.GotFocus += (_, _) =>
        {
            _focusKey = key;
            FocusedDay = day.Date;
        };
        cell.Click += (_, _) => ShowDayFlyout(viewModel, cell, day);
        cell.KeyDown += (_, e) => OnDayKeyDown(viewModel, day, e);
        cell.DragOver += (_, e) => OnDayDragOver(viewModel, cell, day, e);
        cell.DragLeave += (_, _) => cell.Style = StyleOf("CalendarDayCellButtonStyle");
        cell.Drop += (_, e) => OnDayDrop(viewModel, cell, day, e);
        return cell;
    }

    private WinGrid Bar(CalendarPageViewModel viewModel, CalendarWeek week, CalendarBar bar, bool isWeekMode)
    {
        var cr = bar.Cr;
        var record = viewModel.ViewModelFor(cr);
        var state = cr.IsOverdue ? CalendarBarState.Overdue : cr.IsCompleted ? CalendarBarState.Completed : CalendarBarState.Scheduled;
        var style = state switch
        {
            CalendarBarState.Overdue => "CalendarOverdueBarButtonStyle",
            CalendarBarState.Completed => "CalendarCompletedBarButtonStyle",
            _ => "CalendarBarButtonStyle",
        };
        var content = new WinGrid { ColumnSpacing = 4 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Design §6.3: overdue and completed carry a glyph and text, never color alone (P3).
        var stateGlyph = cr.IsOverdue ? "" : cr.IsCompleted ? "" : null;
        var lead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        if (bar.ContinuesBefore)
        {
            lead.Children.Add(Glyph(""));
        }

        if (stateGlyph is not null)
        {
            lead.Children.Add(Glyph(stateGlyph));
        }

        content.Children.Add(lead);
        var status = cr.IsOverdue ? viewModel.Text("Calendar_OverdueLabel") + " · " + record.StatusName : record.StatusName;
        var text = isWeekMode
            ? new TextBlock
            {
                Text = record.Number + " " + record.Title + Environment.NewLine + record.OwnerName + " · " + status,
                Style = StyleOf("CaptionTextBlockStyle"),
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            }
            : new TextBlock
            {
                Text = record.Number + " " + record.Title + " · " + status,
                Style = StyleOf("CaptionTextBlockStyle"),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
        WinGrid.SetColumn(text, 1);
        content.Children.Add(text);
        if (bar.ContinuesAfter)
        {
            var after = Glyph("");
            WinGrid.SetColumn(after, 2);
            content.Children.Add(after);
        }

        var name = viewModel.BarName(bar);
        var body = new Button { Style = StyleOf(style), Content = content, Tag = cr.Id.Value };
        ApplyBarColors(body, cr.Id.Value, state);
        AutomationProperties.SetAutomationId(body, "Calendar_Bar_" + cr.Id.Value.ToString("N") + "_" + DateId(bar.SegmentStart));
        AutomationProperties.SetName(body, name);
        AutomationProperties.SetHelpText(body, viewModel.Text("Calendar_BarHelp"));
        ToolTipService.SetToolTip(body, name);
        body.Click += (_, _) => _ = viewModel.OpenAsync(cr.Id.Value);
        body.KeyDown += (_, e) => OnBarKeyDown(viewModel, body, cr, e);
        body.ContextFlyout = BarMenu(viewModel, body, cr);
        var key = BarKey(bar);
        _focusables[key] = body;
        body.GotFocus += (_, _) => _focusKey = key;

        var host = new WinGrid { Margin = new Thickness(2, LaneGap / 2, 2, LaneGap / 2) };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        WinGrid.SetColumnSpan(body, 3);
        host.Children.Add(body);
        WinGrid.SetColumn(host, bar.StartColumn);
        WinGrid.SetColumnSpan(host, bar.EndColumn - bar.StartColumn + 1);
        WinGrid.SetRow(host, 1 + bar.Lane);

        // The day cells of the later days the bar spans come after it in the row (tab order, §7.5) and would sit
        // on top of it, sending clicks past its first day to the day flyout; ZIndex lifts the bar without
        // changing the tab order.
        Canvas.SetZIndex(host, 1);
        if (bar.ShowStartHandle)
        {
            host.Children.Add(Handle(viewModel, week, bar, host, isStart: true));
        }

        if (bar.ShowEndHandle)
        {
            host.Children.Add(Handle(viewModel, week, bar, host, isStart: false));
        }

        return host;
    }

    /// <summary>
    /// Design §6.3: the CR's palette fill and stripe, also for hover and pressed through lightweight styling (the
    /// template's visual states read the Button* resources, which would otherwise replace the bar's color). In High
    /// Contrast there is no palette and the bar keeps its style's system brushes.
    /// </summary>
    private void ApplyBarColors(Button body, Guid id, CalendarBarState state)
    {
        if (_palette is null)
        {
            return;
        }

        var colors = _palette.ColorsFor(id, state);
        body.Background = colors.Fill;
        body.BorderBrush = colors.Stroke;
        body.Resources["ButtonBackground"] = colors.Fill;
        body.Resources["ButtonBackgroundPointerOver"] = colors.Hover;
        body.Resources["ButtonBackgroundPressed"] = colors.Pressed;
        body.Resources["ButtonBorderBrush"] = colors.Stroke;
        body.Resources["ButtonBorderBrushPointerOver"] = colors.Stroke;
        body.Resources["ButtonBorderBrushPressed"] = colors.Stroke;
    }

    // Decorative: the owner's accessible name already says it (continues, overdue, completed).
    private static FontIcon Glyph(string glyph) =>
        new FontIcon { Glyph = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Hidden();

    private static CalendarResizeHandle Handle(CalendarPageViewModel viewModel, CalendarWeek week, CalendarBar bar, WinGrid host, bool isStart)
    {
        var handle = new CalendarResizeHandle { HorizontalAlignment = isStart ? HorizontalAlignment.Left : HorizontalAlignment.Right };
        var thumb = handle.Thumb;
        WinGrid.SetColumn(handle, isStart ? 0 : 2);
        AutomationProperties.SetAutomationId(thumb, (isStart ? "Calendar_BarStartThumb_" : "Calendar_BarEndThumb_") + bar.Cr.Id.Value.ToString("N"));
        AutomationProperties.SetName(thumb, viewModel.Format(isStart ? "Calendar_StartHandleName" : "Calendar_EndHandleName", bar.Cr.Number));
        double total = 0;
        var shift = 0;
        thumb.DragStarted += (_, _) =>
        {
            total = 0;
            shift = 0;
        };
        thumb.DragDelta += (_, e) =>
        {
            total += e.HorizontalChange;
            var columnWidth = Math.Max(1, (((host.Parent as FrameworkElement)?.ActualWidth ?? 0) - StubWidth) / Workweek.VisibleDays.Count);
            var next = (int)Math.Round(total / columnWidth);

            // The ghost stays within this row; other weeks use the keyboard or Edit dates route (B-08.8).
            var start = isStart ? Math.Clamp(bar.StartColumn + next, 0, bar.EndColumn) : bar.StartColumn;
            var end = isStart ? bar.EndColumn : Math.Clamp(bar.EndColumn + next, bar.StartColumn, Workweek.VisibleDays.Count - 1);
            shift = isStart ? start - bar.StartColumn : end - bar.EndColumn;
            WinGrid.SetColumn(host, start);
            WinGrid.SetColumnSpan(host, end - start + 1);
            var day = week.Row.VisibleDays[isStart ? start : end];
            ToolTipService.SetToolTip(thumb, day.ToString("D", CultureInfo.CurrentCulture));
        };
        thumb.DragCompleted += (_, _) =>
        {
            if (shift == 0)
            {
                WinGrid.SetColumn(host, bar.StartColumn);
                WinGrid.SetColumnSpan(host, bar.EndColumn - bar.StartColumn + 1);
                return;
            }

            var day = week.Row.VisibleDays[(isStart ? bar.StartColumn : bar.EndColumn) + shift];
            _ = viewModel.ResizeAsync(bar.Cr.Id.Value, isStart ? day : null, isStart ? null : day);
        };
        return handle;
    }

    private HyperlinkButton MoreLink(CalendarPageViewModel viewModel, CalendarDayCell day, int maxLanes)
    {
        var link = new HyperlinkButton { Content = viewModel.MoreText(day.HiddenCount), Padding = new Thickness(6, 0, 6, 2), FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
        WinGrid.SetColumn(link, day.Column);
        WinGrid.SetRow(link, maxLanes + 1);
        AutomationProperties.SetAutomationId(link, "Calendar_MoreLink_" + DateId(day.Date));
        AutomationProperties.SetName(link, viewModel.MoreName(day));
        link.Click += (_, _) => ShowCrListFlyout(viewModel, link, day.Date.ToString("D", CultureInfo.CurrentCulture), day.Items, day.Date);
        _focusables["more:" + DateId(day.Date)] = link;
        return link;
    }

    private static Border Stub(CalendarPageViewModel viewModel, CalendarWeek week, int maxLanes)
    {
        var friday = week.Row.WeekStart.AddDays(5);
        var saturday = friday.AddDays(1);
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 6, 0, 6) };
        foreach (var day in new[] { friday, saturday })
        {
            panel.Children.Add(new TextBlock
            {
                Text = day.ToString("ddd", CultureInfo.CurrentCulture)[..1] + Environment.NewLine + day.Day.ToString(CultureInfo.CurrentCulture),
                Style = StyleOf("CaptionTextBlockStyle"),
                TextAlignment = TextAlignment.Center,
            }.Hidden());
        }

        if (week.OutsideWorkdays.Count > 0)
        {
            // B-08.7: CRs wholly on the hidden days have no bar; this indicator lists them.
            var button = new Button
            {
                Padding = new Thickness(2),
                Content = new StackPanel
                {
                    Children =
                    {
                        Glyph(""),
                        new TextBlock { Text = week.OutsideWorkdays.Count.ToString(CultureInfo.CurrentCulture), Style = StyleOf("CaptionTextBlockStyle"), HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            };
            AutomationProperties.SetAutomationId(button, "Calendar_OutsideWorkdays_" + DateId(friday));
            AutomationProperties.SetName(button, viewModel.OutsideName(week));
            ToolTipService.SetToolTip(button, viewModel.OutsideName(week));
            button.Click += (_, _) => ShowCrListFlyout(viewModel, button, viewModel.Text("Calendar_OutsideHeader"), week.OutsideWorkdays, null);
            panel.Children.Add(button);
        }

        // A short fitted row scales the stub down instead of clipping the outside-workdays button.
        var fitted = new Viewbox { Child = panel, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top };
        var stub = new Border { Style = StyleOf("CalendarStubBorderStyle"), Child = fitted };
        AutomationProperties.SetName(stub, viewModel.Format("Calendar_StubName", friday.ToString("D", CultureInfo.CurrentCulture), saturday.ToString("D", CultureInfo.CurrentCulture)));
        WinGrid.SetColumn(stub, Workweek.VisibleDays.Count);
        WinGrid.SetRowSpan(stub, maxLanes + 2);
        return stub;
    }

    private static MenuFlyout BarMenu(CalendarPageViewModel viewModel, Button bar, ChangeRequestReadModel cr)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem(viewModel, "Calendar_MenuOpen", "Calendar_BarMenuOpen_", cr, () => _ = viewModel.OpenAsync(cr.Id.Value)));
        menu.Items.Add(MenuItem(viewModel, "Calendar_MenuEditDates", "Calendar_BarMenuEditDates_", cr, () => ShowEditDatesFlyout(viewModel, bar, cr)));
        menu.Items.Add(MenuItem(viewModel, "Calendar_MenuOpenLinkedTasks", "Calendar_BarMenuLinkedTasks_", cr, () => _ = viewModel.OpenLinkedTasksAsync(cr.Id.Value)));
        menu.Items.Add(MenuItem(viewModel, "Calendar_MenuMoveToBacklog", "Calendar_BarMenuBacklog_", cr, () => ShowMoveToBacklogFlyout(viewModel, bar, cr)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem(viewModel, "Calendar_MenuDelete", "Calendar_BarMenuDelete_", cr, () => _ = viewModel.DeleteAsync(cr.Id.Value)));
        return menu;
    }

    private static MenuFlyoutItem MenuItem(CalendarPageViewModel viewModel, string textKey, string idPrefix, ChangeRequestReadModel cr, Action action)
    {
        var item = new MenuFlyoutItem { Text = viewModel.Text(textKey) };
        AutomationProperties.SetAutomationId(item, idPrefix + cr.Id.Value.ToString("N"));
        item.Click += (_, _) => action();
        return item;
    }

    // Design §7.5: Enter opens the day flyout (the CRs on that date + "New CR on this date").
    private static void ShowDayFlyout(CalendarPageViewModel viewModel, FrameworkElement anchor, CalendarDayCell day) =>
        ShowCrListFlyout(viewModel, anchor, day.Date.ToString("D", CultureInfo.CurrentCulture), day.Items, day.Date);

    /// <summary>The "+N more", day and outside-workdays flyouts: every CR listed, each opening its editor (B-08.9).</summary>
    private static void ShowCrListFlyout(CalendarPageViewModel viewModel, FrameworkElement anchor, string heading, IReadOnlyList<ChangeRequestReadModel> items, DateOnly? newOn)
    {
        var flyout = new Flyout { ShouldConstrainToRootBounds = true };
        var panel = new StackPanel { Spacing = 4, MinWidth = 240, MaxWidth = 360 };
        var title = new TextBlock { Text = heading, Style = StyleOf("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        panel.Children.Add(title);
        if (items.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = viewModel.Text("Calendar_NoCrsOnDay"), Style = StyleOf("CaptionTextBlockStyle") });
        }

        foreach (var cr in items)
        {
            var record = viewModel.ViewModelFor(cr);
            var row = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new TextBlock { Text = record.Number + " " + record.Title + " · " + record.RangeText + (cr.IsOverdue ? " · " + record.OverdueText : string.Empty), TextWrapping = TextWrapping.Wrap },
            };
            AutomationProperties.SetAutomationId(row, "Calendar_FlyoutCr_" + cr.Id.Value.ToString("N"));
            row.Click += (_, _) =>
            {
                flyout.Hide();
                _ = viewModel.OpenAsync(cr.Id.Value);
            };
            panel.Children.Add(row);
        }

        if (newOn is { } day)
        {
            var create = new Button { Content = viewModel.Text("Calendar_NewCrOnDate"), Style = StyleOf("AccentButtonStyle"), Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(create, "Calendar_NewCrOnDateButton");
            create.Click += (_, _) =>
            {
                flyout.Hide();
                _ = viewModel.NewChangeRequestOnAsync(day);
            };
            panel.Children.Add(create);
        }

        flyout.Content = new ScrollViewer { Content = panel, MaxHeight = 420 };
        flyout.ShowAt(anchor);
    }

    /// <summary>Ctrl+E / menu "Edit dates…" (design §7.5): two date pickers, one command for both endpoints.</summary>
    private static void ShowEditDatesFlyout(CalendarPageViewModel viewModel, FrameworkElement anchor, ChangeRequestReadModel cr)
    {
        if (cr.PlannedStart is not { } start || cr.PlannedEnd is not { } end)
        {
            return;
        }

        var flyout = new Flyout { ShouldConstrainToRootBounds = true };
        var startPicker = new CalendarDatePicker { Header = viewModel.Text("Calendar_PlannedStartHeader"), Date = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue)), FirstDayOfWeek = Windows.Globalization.DayOfWeek.Sunday };
        var endPicker = new CalendarDatePicker { Header = viewModel.Text("Calendar_PlannedEndHeader"), Date = new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue)), FirstDayOfWeek = Windows.Globalization.DayOfWeek.Sunday };
        AutomationProperties.SetAutomationId(startPicker, "Calendar_EditStartDate");
        AutomationProperties.SetAutomationId(endPicker, "Calendar_EditEndDate");
        var error = new TextBlock { Style = StyleOf("ErrorCaptionTextStyle"), Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        var save = new Button { Content = viewModel.Text("TaskEditor_SaveButton"), Style = StyleOf("AccentButtonStyle") };
        AutomationProperties.SetAutomationId(save, "Calendar_EditDatesSaveButton");
        save.Click += async (_, _) =>
        {
            if (startPicker.Date is not { } s || endPicker.Date is not { } e)
            {
                return;
            }

            var newStart = DateOnly.FromDateTime(s.DateTime);
            var newEnd = DateOnly.FromDateTime(e.DateTime);
            if (newStart > newEnd)
            {
                // B-08.8: an inverted range is rejected before any command runs.
                error.Text = viewModel.Text("Error_DateRangeInverted");
                error.Visibility = Visibility.Visible;
                return;
            }

            flyout.Hide();
            await viewModel.ResizeAsync(cr.Id.Value, newStart == start ? null : newStart, newEnd == end ? null : newEnd).ConfigureAwait(true);
        };
        flyout.Content = new StackPanel { Spacing = 8, MinWidth = 260, Children = { startPicker, endPicker, error, save } };
        flyout.ShowAt(anchor);
    }

    /// <summary>Ctrl+Shift+B / menu "Move to backlog" with its inline confirmation (design §7.5, B-08.11).</summary>
    private static void ShowMoveToBacklogFlyout(CalendarPageViewModel viewModel, FrameworkElement anchor, ChangeRequestReadModel cr)
    {
        var flyout = new Flyout { ShouldConstrainToRootBounds = true };
        var move = new Button { Content = viewModel.Text("Calendar_MenuMoveToBacklog"), Style = StyleOf("AccentButtonStyle") };
        var cancel = new Button { Content = viewModel.Text("Common_Cancel") };
        AutomationProperties.SetAutomationId(move, "Calendar_MoveToBacklogConfirmButton");
        move.Click += (_, _) =>
        {
            flyout.Hide();
            _ = viewModel.MoveToBacklogAsync(cr.Id.Value);
        };
        cancel.Click += (_, _) => flyout.Hide();
        flyout.Content = new StackPanel
        {
            Spacing = 8,
            MaxWidth = 320,
            Children =
            {
                new TextBlock { Text = viewModel.Format("Calendar_MoveToBacklogPrompt", cr.Number), TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { move, cancel } },
            },
        };
        flyout.ShowAt(anchor);
    }

    private void OnDayKeyDown(CalendarPageViewModel viewModel, CalendarDayCell day, KeyRoutedEventArgs e)
    {
        // Design §7.5: arrows move between visible days (Fri/Sat skipped, rows wrap); PageUp/PageDown change the period.
        DateOnly? target = e.Key switch
        {
            VirtualKey.Left => Workweek.PreviousVisibleDay(day.Date),
            VirtualKey.Right => Workweek.NextVisibleDay(day.Date),
            VirtualKey.Up => day.Date.AddDays(-7),
            VirtualKey.Down => day.Date.AddDays(7),
            _ => null,
        };
        if (target is { } next)
        {
            e.Handled = true;
            if (!FocusDay(next))
            {
                viewModel.GoTo(next);
                DispatcherQueue.TryEnqueue(() => FocusDay(next));
            }

            return;
        }

        if (e.Key is VirtualKey.PageUp or VirtualKey.PageDown)
        {
            e.Handled = true;
            var command = e.Key == VirtualKey.PageUp ? viewModel.PreviousCommand : viewModel.NextCommand;
            command.Execute(null);
            DispatcherQueue.TryEnqueue(() => FocusDay(viewModel.Anchor));
        }
    }

    private void OnBarKeyDown(CalendarPageViewModel viewModel, Button bar, ChangeRequestReadModel cr, KeyRoutedEventArgs e)
    {
        var shift = IsDown(VirtualKey.Shift);
        var control = IsDown(VirtualKey.Control);
        if (shift && e.Key is VirtualKey.Left or VirtualKey.Right && cr.PlannedStart is { } start && cr.PlannedEnd is { } end)
        {
            // Design §7.5: Shift+arrows step the end, Ctrl+Shift+arrows the start; one command after 600 ms or Enter.
            e.Handled = true;
            var step = e.Key == VirtualKey.Left ? -1 : 1;
            (DateOnly Start, DateOnly End) pending = _pendingKeyboard.TryGetValue(cr.Id.Value, out var current) ? current : (start, end);
            pending = control ? (pending.Start.AddDays(step), pending.End) : (pending.Start, pending.End.AddDays(step));
            _pendingKeyboard[cr.Id.Value] = pending;
            ToolTipService.SetToolTip(bar, viewModel.Format("Calendar_PendingRange", pending.Start.ToString("D", CultureInfo.CurrentCulture), pending.End.ToString("D", CultureInfo.CurrentCulture)));
            RestartKeyboardTimer(viewModel);
            return;
        }

        if (e.Key == VirtualKey.Enter && _pendingKeyboard.Count > 0)
        {
            e.Handled = true;
            CommitKeyboardChanges(viewModel);
            return;
        }

        if (control && !shift && e.Key == VirtualKey.E)
        {
            e.Handled = true;
            ShowEditDatesFlyout(viewModel, bar, cr);
        }
        else if (control && shift && e.Key == VirtualKey.B)
        {
            e.Handled = true;
            ShowMoveToBacklogFlyout(viewModel, bar, cr);
        }
        else if (e.Key == VirtualKey.Delete)
        {
            e.Handled = true;
            _ = viewModel.DeleteAsync(cr.Id.Value);
        }
    }

    private void RestartKeyboardTimer(CalendarPageViewModel viewModel)
    {
        if (_keyboardTimer is null)
        {
            _keyboardTimer = DispatcherQueue.CreateTimer();
            _keyboardTimer.IsRepeating = false;
            _keyboardTimer.Interval = KeyboardCommitDelay;
            _keyboardTimer.Tick += (_, _) => CommitKeyboardChanges(viewModel);
        }

        _keyboardTimer.Stop();
        _keyboardTimer.Start();
    }

    private void CommitKeyboardChanges(CalendarPageViewModel viewModel)
    {
        _keyboardTimer?.Stop();
        var pending = _pendingKeyboard.ToList();
        _pendingKeyboard.Clear();
        foreach (var (id, (start, end)) in pending)
        {
            if (viewModel.Find(id) is not { PlannedStart: { } oldStart, PlannedEnd: { } oldEnd })
            {
                continue;
            }

            var newStart = start == oldStart ? (DateOnly?)null : start;
            var newEnd = end == oldEnd ? (DateOnly?)null : end;
            if (newStart is not null || newEnd is not null)
            {
                // An inverted range is refused by the use case; the bar keeps its committed dates (B-08.8).
                _ = viewModel.ResizeAsync(id, newStart, newEnd);
            }
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static void OnDayDragOver(CalendarPageViewModel viewModel, Button cell, CalendarDayCell day, DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(DragCrIdProperty))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        // Design §9: the drop target shows a stroke and a caption, never color only.
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = viewModel.Format("Calendar_DropCaption", day.Date.ToString("MMM d", CultureInfo.CurrentCulture));
        e.DragUIOverride.IsCaptionVisible = true;
        cell.Style = StyleOf("CalendarDayCellDropTargetButtonStyle");
    }

    private static void OnDayDrop(CalendarPageViewModel viewModel, Button cell, CalendarDayCell day, DragEventArgs e)
    {
        cell.Style = StyleOf("CalendarDayCellButtonStyle");
        if (e.DataView.Properties.TryGetValue(DragCrIdProperty, out var value) && value is string text && Guid.TryParse(text, out var id))
        {
            _ = viewModel.ScheduleFromDropAsync(id, day.Date);
        }
    }
}

internal static class CalendarElementExtensions
{
    /// <summary>Removes a decorative element from the UIA tree; its meaning is in the owner's accessible name.</summary>
    public static T Hidden<T>(this T element)
        where T : UIElement
    {
        AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
        return element;
    }
}
