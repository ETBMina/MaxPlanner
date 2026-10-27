using System.ComponentModel;
using System.Globalization;
using MaxPlanner.Application.ReadModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MaxPlanner.Presentation.Calendar;

/// <summary>
/// The Calendar page view. Code-behind handles view mechanics only: mode and breakpoint switching, the period
/// flyout, menus and flyouts built for a row, drag data, and re-rendering the custom surface.
/// </summary>
public sealed partial class CalendarPageView : UserControl, Shell.IPageView
{
    private static readonly (RecordSortColumn Column, string Key)[] SortHeaders =
    [
        (RecordSortColumn.Number, "Calendar_ColumnNumber"),
        (RecordSortColumn.Title, "Calendar_ColumnTitle"),
        (RecordSortColumn.Owner, "Calendar_ColumnOwner"),
        (RecordSortColumn.Squad, "Calendar_ColumnSquad"),
        (RecordSortColumn.Status, "Calendar_ColumnStatus"),
        (RecordSortColumn.Priority, "Calendar_ColumnPriority"),
        (RecordSortColumn.PlannedStart, "Calendar_ColumnPlannedStart"),
        (RecordSortColumn.PlannedEnd, "Calendar_ColumnPlannedEnd"),
        (RecordSortColumn.LinkedTasks, "Calendar_ColumnLinkedTasks"),
        (RecordSortColumn.Overdue, "Calendar_ColumnOverdue"),
    ];

    private bool _isSyncingMode;
    private bool _isShown;
    private bool _isSurfaceStale;
    private bool? _wasWide;
    private double _gutter = 32;

    public CalendarPageView(CalendarPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        MonthView.ViewModel = viewModel;
        BuildSortHeaders();
        SyncModeBar();
        ApplyModeVisibility();
        MonthView.Render(viewModel.Surface);
        viewModel.SurfaceChanged += OnSurfaceChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SizeChanged += (_, e) =>
        {
            // A collapsed (hidden) page has no width to adapt to; it applies the real width when shown again.
            if (e.NewSize.Width > 0)
            {
                ApplyBreakpoint(e.NewSize.Width);
            }
        };
        SurfaceScroller.SizeChanged += (_, _) => FitSurface();
    }

    public CalendarPageViewModel ViewModel { get; }

    /// <inheritdoc/>
    public FrameworkElement HeaderElement => HeaderRoot;

    /// <inheritdoc/>
    public FrameworkElement? ContentElement => PageContent;

    public void FocusTitle() => PageTitle.Focus(FocusState.Programmatic);

    /// <inheritdoc/>
    public bool FocusSearch()
    {
        SearchBox.FocusSearch();
        return true;
    }

    /// <inheritdoc/>
    public void OnShown()
    {
        _isShown = true;
        PageContent.OnPageShown();
        if (_isSurfaceStale)
        {
            _isSurfaceStale = false;
            OnSurfaceChanged(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc/>
    public void OnHidden()
    {
        _isShown = false;
        PageContent.OnPageHidden();
    }

    private void OnSurfaceChanged(object? sender, EventArgs e)
    {
        // T-11: the surface is rebuilt in code for every committed batch; a hidden page renders the latest one once,
        // when it is shown again, instead of after each batch.
        if (!_isShown)
        {
            _isSurfaceStale = true;
            return;
        }

        // A new period can change the week count and so the lane capacity; the rebuilt surface re-enters here.
        if (FitSurface())
        {
            return;
        }

        MonthView.Render(ViewModel.Surface);
    }

    /// <summary>Design §9: the Month/Week surface fills the viewport without scrolling; true when the surface was rebuilt.</summary>
    private bool FitSurface()
    {
        var surface = ViewModel.Surface;
        if (SurfaceScroller.Visibility != Visibility.Visible || SurfaceScroller.ActualHeight <= 0 || surface.Weeks.Count == 0)
        {
            return false;
        }

        var lanes = MonthView.Fit(SurfaceScroller.ActualHeight, surface.Weeks.Count, surface.IsWeekMode);
        return ViewModel.SetLaneCapacity(lanes);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CalendarPageViewModel.Mode):
                SyncModeBar();
                ApplyModeVisibility();
                break;
            case nameof(CalendarPageViewModel.IsCompact):
                ApplyModeVisibility();
                break;
            case nameof(CalendarPageViewModel.IsBacklogOpen):
                ApplyContentPadding();
                break;
            case nameof(CalendarPageViewModel.SortColumn) or nameof(CalendarPageViewModel.IsSortDescending):
                BuildSortHeaders();
                break;
            default:
                break;
        }
    }

    private void SyncModeBar()
    {
        _isSyncingMode = true;
        ModeBar.SelectedItem = ViewModel.Mode switch
        {
            CalendarMode.Week => WeekItem,
            CalendarMode.Records => RecordsItem,
            _ => MonthItem,
        };
        _isSyncingMode = false;
    }

    private void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_isSyncingMode && sender.SelectedItem is SelectorBarItem { Tag: string tag } && Enum.TryParse<CalendarMode>(tag, out var mode))
        {
            ViewModel.SetMode(mode);
        }
    }

    private void ApplyModeVisibility()
    {
        var surface = ViewModel.IsSurfaceMode;
        var compact = ViewModel.IsCompact;
        SurfaceScroller.Visibility = surface && !compact ? Visibility.Visible : Visibility.Collapsed;
        AgendaList.Visibility = surface && compact ? Visibility.Visible : Visibility.Collapsed;
        RecordsScroller.Visibility = !surface && !compact ? Visibility.Visible : Visibility.Collapsed;
        CompactRecordsList.Visibility = !surface && compact ? Visibility.Visible : Visibility.Collapsed;
    }

    // Design §4.4 responsive table: backlog Inline and open at L; Overlay (closed) at M; full-width overlay at S.
    private void ApplyBreakpoint(double width)
    {
        var wide = width >= 1008;
        var padding = wide ? 32 : width >= 640 ? 24 : 16;
        HeaderRoot.Padding = wide ? new Thickness(32, 24, 32, 12) : width >= 640 ? new Thickness(24, 16, 24, 12) : new Thickness(16, 12, 16, 8);
        _gutter = padding;
        ViewModel.SetCompact(width < 640);
        ChipsBar.IsCompact = width < 640;
        Split.DisplayMode = wide ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;

        // The backlog card sits inside the page gutter; at S the full-width pane keeps the gutter on both sides.
        BacklogCard.Margin = new Thickness(padding, 0, width < 640 ? padding : 0, 16);
        Split.OpenPaneLength = width < 640 ? Math.Max(280, width) : 280 + padding;
        ApplyContentPadding();
        if (_wasWide != wide)
        {
            _wasWide = wide;
            ViewModel.IsBacklogOpen = wide;
        }
    }

    // An open inline backlog already supplies the left gutter, so the content keeps only a 16-epx gap beside it.
    private void ApplyContentPadding()
    {
        var left = Split.DisplayMode == SplitViewDisplayMode.Inline && ViewModel.IsBacklogOpen ? 16 : _gutter;
        ContentRoot.Padding = new Thickness(left, 0, _gutter, 16);
    }

    private void BuildSortHeaders()
    {
        RecordsHeader.Children.Clear();
        for (var i = 0; i < SortHeaders.Length; i++)
        {
            var (column, key) = SortHeaders[i];
            var label = ViewModel.Text(key);
            var isSorted = ViewModel.SortColumn == column;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(new TextBlock { Text = label, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CaptionTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
            if (isSorted)
            {
                content.Children.Add(new FontIcon { Glyph = ViewModel.IsSortDescending ? "" : "", FontSize = 10 }.Hidden());
            }

            var button = new Button
            {
                Content = content,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetAutomationId(button, "Calendar_RecordsHeader_" + column);
            AutomationProperties.SetName(button, ViewModel.Format(
                isSorted ? (ViewModel.IsSortDescending ? "Calendar_SortedDescendingName" : "Calendar_SortedAscendingName") : "Calendar_SortByName", label));
            button.Click += (_, _) => ViewModel.SortBy(column);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(button, i);
            RecordsHeader.Children.Add(button);
        }
    }

    private void OnPeriodFlyoutOpening(object? sender, object e)
    {
        // Design §4.4: Year display for Month mode, Month display for Week mode.
        PeriodCalendar.DisplayMode = ViewModel.Mode == CalendarMode.Week ? CalendarViewDisplayMode.Month : CalendarViewDisplayMode.Year;
        PeriodCalendar.SetDisplayDate(new DateTimeOffset(ViewModel.Anchor.ToDateTime(TimeOnly.MinValue)));
    }

    private void OnPeriodDateSelected(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (args.AddedDates.Count > 0)
        {
            ViewModel.GoTo(DateOnly.FromDateTime(args.AddedDates[0].DateTime));
            sender.SelectedDates.Clear();
            PeriodFlyout.Hide();
        }
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e) => ViewModel.Filters.ClearAll();

    private void OnAgendaItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CalendarAgendaRow { Record: { } record })
        {
            _ = ViewModel.OpenAsync(record.Id);
        }
    }

    private void OnRecordItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CalendarRecordViewModel record)
        {
            _ = ViewModel.OpenAsync(record.Id);
        }
    }

    // A tap, click, Enter or Space on a backlog card opens the CR editor. ListView raises ItemClick only for a
    // completed press without a drag, and never for presses the card's links and menu button already handled.
    private void OnBacklogItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CalendarRecordViewModel record)
        {
            _ = ViewModel.OpenAsync(record.Id);
        }
    }

    private void OnBacklogOpenClick(object sender, RoutedEventArgs e)
    {
        if (Record(sender) is { } record)
        {
            _ = ViewModel.OpenAsync(record.Id);
        }
    }

    private void OnBacklogLinkedTasksClick(object sender, RoutedEventArgs e)
    {
        if (Record(sender) is { } record)
        {
            _ = ViewModel.OpenLinkedTasksAsync(record.Id);
        }
    }

    // Design §4.4 backlog card menu: Open, Schedule on…, Open linked tasks, Delete….
    private void OnBacklogMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor || Record(sender) is not { } record)
        {
            return;
        }

        var menu = new MenuFlyout();
        menu.Items.Add(Item("Calendar_MenuOpen", "Calendar_BacklogMenuOpen_", record, () => _ = ViewModel.OpenAsync(record.Id)));
        menu.Items.Add(Item("Calendar_MenuScheduleOn", "Calendar_BacklogMenuSchedule_", record, () => ShowScheduleFlyout(anchor, record.Model)));
        menu.Items.Add(Item("Calendar_MenuOpenLinkedTasks", "Calendar_BacklogMenuLinkedTasks_", record, () => _ = ViewModel.OpenLinkedTasksAsync(record.Id)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Calendar_MenuDelete", "Calendar_BacklogMenuDelete_", record, () => _ = ViewModel.DeleteAsync(record.Id)));
        menu.ShowAt(anchor);
    }

    // Design §4.4 Records ⋯: Open, Open linked tasks, Create task, Move to backlog, Delete….
    private void OnRecordMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor || Record(sender) is not { } record)
        {
            return;
        }

        var menu = new MenuFlyout();
        menu.Items.Add(Item("Calendar_MenuOpen", "Calendar_RecordMenuOpen_", record, () => _ = ViewModel.OpenAsync(record.Id)));
        menu.Items.Add(Item("Calendar_MenuOpenLinkedTasks", "Calendar_RecordMenuLinkedTasks_", record, () => _ = ViewModel.OpenLinkedTasksAsync(record.Id)));
        menu.Items.Add(Item("Calendar_MenuCreateTask", "Calendar_RecordMenuCreateTask_", record, () => _ = ViewModel.CreateTaskAsync(record.Id)));
        menu.Items.Add(Item("Calendar_MenuScheduleOn", "Calendar_RecordMenuSchedule_", record, () => ShowScheduleFlyout(anchor, record.Model)));
        var backlog = Item("Calendar_MenuMoveToBacklog", "Calendar_RecordMenuBacklog_", record, () => _ = ViewModel.MoveToBacklogAsync(record.Id));
        backlog.IsEnabled = record.CanMoveToBacklog;
        menu.Items.Add(backlog);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Calendar_MenuDelete", "Calendar_RecordMenuDelete_", record, () => _ = ViewModel.DeleteAsync(record.Id)));
        menu.ShowAt(anchor);
    }

    // Design §7.5: Ctrl+Shift+S on a backlog card opens Schedule on…; Delete deletes (confirmed in the CR editor step).
    // Enter and Space open the card through ItemClick.
    private void OnBacklogKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (BacklogList.SelectedItem is not CalendarRecordViewModel record || BacklogList.ContainerFromItem(record) is not FrameworkElement container)
        {
            return;
        }

        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && shift && e.Key == VirtualKey.S)
        {
            e.Handled = true;
            ShowScheduleFlyout(container, record.Model);
        }
        else if (e.Key == VirtualKey.Delete)
        {
            e.Handled = true;
            _ = ViewModel.DeleteAsync(record.Id);
        }
    }

    private void OnBacklogDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not CalendarRecordViewModel record)
        {
            e.Cancel = true;
            return;
        }

        // The drop target reads the ID from an in-process property, so foreign drags are never accepted (design §9).
        e.Data.Properties.Add(CalendarMonthView.DragCrIdProperty, record.Id.ToString());
        e.Data.SetText(record.Number);
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    /// <summary>
    /// "Schedule on…" (B-08.5, design §4.4): a date selector defaulting to the focused day, else today, and the
    /// inline "Replace Oct 4 – Oct 9 with Oct 13?" confirmation when the CR already has dates.
    /// </summary>
    private void ShowScheduleFlyout(FrameworkElement anchor, ChangeRequestReadModel cr)
    {
        var flyout = new Flyout { ShouldConstrainToRootBounds = true };
        var day = MonthView.FocusedDay ?? ViewModel.Today;
        var calendar = new CalendarView { SelectionMode = CalendarViewSelectionMode.Single, FirstDayOfWeek = Windows.Globalization.DayOfWeek.Sunday };
        AutomationProperties.SetAutomationId(calendar, "Calendar_ScheduleCalendar");
        calendar.SelectedDates.Add(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue)));
        calendar.SetDisplayDate(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue)));
        var prompt = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"] };
        AutomationProperties.SetLiveSetting(prompt, AutomationLiveSetting.Polite);
        var schedule = new Button { Content = ViewModel.Text("Calendar_ScheduleButton"), Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetAutomationId(schedule, "Calendar_ScheduleConfirmButton");
        void UpdatePrompt()
        {
            prompt.Text = ViewModel.ReplacePrompt(cr, day);
            prompt.Visibility = prompt.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            schedule.Content = ViewModel.Format("Calendar_ScheduleOnButton", day.ToString("MMM d", CultureInfo.CurrentCulture));
        }

        calendar.SelectedDatesChanged += (_, args) =>
        {
            if (args.AddedDates.Count > 0)
            {
                day = DateOnly.FromDateTime(args.AddedDates[0].DateTime);
                UpdatePrompt();
            }
        };
        schedule.Click += (_, _) =>
        {
            flyout.Hide();
            _ = ViewModel.ScheduleAsync(cr.Id.Value, day);
        };
        UpdatePrompt();
        flyout.Content = new StackPanel { Spacing = 8, Children = { calendar, prompt, schedule } };
        flyout.ShowAt(anchor);
    }

    private MenuFlyoutItem Item(string key, string idPrefix, CalendarRecordViewModel record, Action action)
    {
        var item = new MenuFlyoutItem { Text = ViewModel.Text(key) };
        AutomationProperties.SetAutomationId(item, idPrefix + record.Id.ToString("N"));
        item.Click += (_, _) => action();
        return item;
    }

    private static CalendarRecordViewModel? Record(object sender) => (sender as FrameworkElement)?.Tag as CalendarRecordViewModel;
}
