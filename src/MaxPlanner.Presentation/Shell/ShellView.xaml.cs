using System.ComponentModel;
using System.Diagnostics;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Calendar;
using MaxPlanner.Presentation.Checklists;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Dashboard;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Settings;
using MaxPlanner.Presentation.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MaxPlanner.Presentation.Shell;

/// <summary>
/// The window content: App InfoBars, startup/recovery/failure states, and in Ready the NavigationView with
/// the page host and status bar (design §2). Code-behind keeps one view per page in the host and shows only the
/// current one, so page views and their bindings are created once per session. After the first rendered frame the
/// main pages are preloaded one at a time, so their first visit is a page switch too (T-11, design §2.2).
/// </summary>
public sealed partial class ShellView : UserControl
{
    public static readonly DependencyProperty SaveStatusProperty = DependencyProperty.Register(
        nameof(SaveStatus), typeof(SaveStatusViewModel), typeof(ShellView), new PropertyMetadata(null));

    // Design §6.4: Setting; used only for Settings when the composition gives no Settings services (tests).
    private static readonly Dictionary<AppPage, string> PlaceholderGlyphs = new()
    {
        [AppPage.Settings] = "",
    };

    // T-11: the heaviest first visits (Board and Grid build a card or row per record) go first; the current page is
    // skipped. Settings is not preloaded.
    private static readonly AppPage[] PreloadOrder = [AppPage.Board, AppPage.Grid, AppPage.Calendar, AppPage.Checklists, AppPage.Dashboard];

    // A minimized or occluded window renders no frames; the preload then goes on without waiting for one.
    private static readonly TimeSpan PreloadFrameTimeout = TimeSpan.FromSeconds(1);

    // A page whose data never arrives (a faulted load) is preloaded in its Loading state after this.
    private static readonly TimeSpan PreloadDataTimeout = TimeSpan.FromSeconds(10);

    private readonly Dictionary<AppPage, PageSlot> _pageSlots = [];
    private PageSlot? _currentSlot;
    private WorkspaceViewModel? _preloadWorkspace;
    private int _preloadRun;
    private readonly DialogHost _dialogs;
    private ShellViewModel? _viewModel;
    private WorkspaceViewModel? _badgeSource;
    private DrillDownNavigator? _navigator;
    private WeakReference<UIElement>? _dashboardFocus;
    private bool _isReturningToDashboard;
    private long _navigationStarted;
    private SaveStatusViewModel? _saveStatus;
    private bool _isSyncingSelection;

    public ShellView()
    {
        InitializeComponent();
        _dialogs = new DialogHost(this);
        AddPageAccelerators();
        Nav.Loaded += (_, _) =>
        {
            if (Nav.SettingsItem is NavigationViewItem settings)
            {
                AutomationProperties.SetAutomationId(settings, "Shell_NavSettingsItem");
            }

            SyncSelection();
        };
    }

    /// <summary>Raised when <see cref="CanGoBack"/> changed, so the window shows or hides the TitleBar back button.</summary>
    public event EventHandler? BackStateChanged;

    /// <summary>A drill-down context can be popped (design §2.9).</summary>
    public bool CanGoBack => _viewModel?.CanGoBack ?? false;

    /// <summary>TitleBar Back: returns to the Dashboard with its filters and scroll position (AC-20).</summary>
    public void GoBack() => _viewModel?.GoBack();

    /// <summary>Raised when the NavigationView enters or leaves LeftMinimal, where the title bar shows the pane toggle (design §2.1).</summary>
    public event EventHandler? TitleBarPaneToggleChanged;

    /// <summary>
    /// T-11 evidence: raised once the first frame after a page switch has rendered, with the time from the navigation
    /// request to that frame. The Desktop host logs it for the performance gate (page switch p95 &lt;= 250 ms).
    /// </summary>
    public event EventHandler<PageShownEventArgs>? PageShown;

    /// <summary>
    /// T-11 evidence: raised once per opened dataset when the startup page preload finished or failed (not when a
    /// closing dataset cancelled it). The Desktop host logs it with the working set to measure its memory cost.
    /// </summary>
    public event EventHandler<PagesPreloadedEventArgs>? PagesPreloaded;

    /// <summary>
    /// Design §5.8: asks before the main window closes. An open editor with unsaved changes shows its dirty prompt and
    /// the close waits; <paramref name="resumeClose"/> runs when the user chose Save (and it succeeded) or Discard.
    /// </summary>
    /// <returns>True when the window may close now.</returns>
    public bool TryApproveWindowClose(Action resumeClose) => _dialogs.TryApproveWindowClose(resumeClose);

    public ShellViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = value;
            if (value is not null)
            {
                value.PropertyChanged += OnViewModelPropertyChanged;
            }

            AttachSaveStatus(value?.Workspace?.SaveStatus);
            _dialogs.Attach(value?.Workspace?.Context);
            AttachBadge(value?.Workspace);
            AttachNavigator(value?.Workspace?.Context.Navigator);
            CancelPreload();
            ShowCurrentPage(moveFocus: false);
            StartPreload();
        }
    }

    /// <summary>The open dataset's save indicator, or null before a dataset opens (bound by the status bar).</summary>
    public SaveStatusViewModel? SaveStatus
    {
        get => (SaveStatusViewModel?)GetValue(SaveStatusProperty);
        private set => SetValue(SaveStatusProperty, value);
    }

    /// <summary>True while the NavigationView is in LeftMinimal (S), so the title bar should show its pane toggle.</summary>
    public bool IsTitleBarPaneToggleVisible { get; private set; }

    /// <summary>Opens or closes the overlay pane from the title bar's toggle (S breakpoint).</summary>
    public void TogglePane() => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    /// <summary>The App InfoBar text for a save failure: the actionable message, then the operation id line.</summary>
    public static string FailureText(string? message, string? details) =>
        message is null ? string.Empty : details is null ? message : message + Environment.NewLine + details;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.Workspace):
                // A preload of the previous dataset stops at its next step, before it touches the workspace again.
                CancelPreload();
                if (_viewModel?.Workspace is null)
                {
                    // The dataset is closing; drop the views so nothing binds to disposed page ViewModels. Leaving the
                    // tree runs each view's Unloaded cleanup (row detach, timers, popups).
                    _pageSlots.Clear();
                    _currentSlot = null;
                    PageHost.Children.Clear();
                }

                AttachSaveStatus(_viewModel?.Workspace?.SaveStatus);
                _dialogs.Attach(_viewModel?.Workspace?.Context);
                AttachBadge(_viewModel?.Workspace);
                AttachNavigator(_viewModel?.Workspace?.Context.Navigator);
                ShowCurrentPage(moveFocus: false);
                StartPreload();
                break;
            case nameof(ShellViewModel.IsReady):
                StartPreload();
                break;
            case nameof(ShellViewModel.CurrentPage):
                RememberDashboardFocus();
                _navigationStarted = Stopwatch.GetTimestamp();
                ShowCurrentPage(moveFocus: true);
                ReportWhenRendered(_viewModel?.CurrentPage ?? AppPage.Dashboard, _navigationStarted);
                break;
            case nameof(ShellViewModel.CanGoBack):
                BackStateChanged?.Invoke(this, EventArgs.Empty);
                break;
            default:
                break;
        }
    }

    private void ShowCurrentPage(bool moveFocus)
    {
        if (_viewModel?.Workspace is not { } workspace)
        {
            return;
        }

        // A page that is not created or preloaded yet is created now; navigation never waits for the preload.
        var page = _viewModel.CurrentPage;
        var slot = _pageSlots.TryGetValue(page, out var existing) ? existing : AddPage(page, workspace, isPreload: false);
        var view = slot.Page;
        ShowView(slot);
        SyncSelection();

        // Design §7.7: after navigation focus goes to the page title, so the heading is announced. Returning from a
        // drill-down gives focus back to the Dashboard element the drill-down started from, when it still exists.
        if (moveFocus)
        {
            var returning = _isReturningToDashboard && page == AppPage.Dashboard;
            _isReturningToDashboard = false;
            var origin = returning && _dashboardFocus is { } weak && weak.TryGetTarget(out var element) ? element : null;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (origin is FrameworkElement { IsLoaded: true } && IsWithin(origin, view) && origin.Focus(FocusState.Keyboard))
                {
                    return;
                }

                (view as IPageView)?.FocusTitle();
            });
        }
    }

    private PageSlot AddPage(AppPage page, WorkspaceViewModel workspace, bool isPreload)
    {
        UIElement view = page switch
        {
            AppPage.Dashboard => new DashboardPageView(workspace.Dashboard),
            AppPage.Board => new BoardPageView(workspace.Board),
            AppPage.Grid => new GridPageView(workspace.Grid),
            AppPage.Calendar => new CalendarPageView(workspace.Calendar),
            AppPage.Checklists => new ChecklistsPageView(workspace.Checklists),
            AppPage.Settings when workspace.Settings is { } settings => new SettingsPageView(settings),
            _ => new PlaceholderPageView(page, _viewModel!.PageTitle(page), PlaceholderGlyphs[page]),
        };
        var slot = new PageSlot(view);
        if (isPreload)
        {
            // A preloaded page starts out hidden, as if another page had been shown after it: its StateView neither
            // starts the loading delay nor announces, and OnShown runs only when the user navigates to it.
            (view as IPageView)?.OnHidden();
            slot.BeginPreload();

            // Below the current page; the slot is transparent anyway, and only one slot is visible once it is collapsed.
            PageHost.Children.Insert(0, slot);
        }
        else
        {
            PageHost.Children.Add(slot);
        }

        _pageSlots[page] = slot;
        return slot;
    }

    /// <summary>
    /// T-11/AC-17: makes <paramref name="slot"/> the visible page. Replacing the host's content took the previous page
    /// out of the tree, so every switch re-ran Unloaded/Loaded across it: each realized Grid row rebuilt its cells,
    /// each Board column list re-realized its cards, and templates were re-applied and re-measured. A collapsed page
    /// keeps its realized items, scroll offsets, selection and drafts, and costs no layout until it is shown again.
    /// </summary>
    /// <remarks>
    /// A collapsed page cannot take input: WinUI raises a keyboard accelerator only when its owner and all of its
    /// ancestors are visible, and collapsed elements cannot take focus and are left out of UI Automation, so the
    /// page-scoped Ctrl+N, Ctrl+Shift+F and Ctrl+Shift+L keep acting on the visible page only.
    /// </remarks>
    private void ShowView(PageSlot slot)
    {
        if (ReferenceEquals(_currentSlot, slot))
        {
            return;
        }

        if (_currentSlot is { } previous)
        {
            previous.Visibility = Visibility.Collapsed;
            (previous.Page as IPageView)?.OnHidden();
            CloseLightDismissPopups();
        }

        // A page navigated to during its preload frame becomes an ordinary visible page at once.
        slot.EndPreload();
        slot.Visibility = Visibility.Visible;
        _currentSlot = slot;
        (slot.Page as IPageView)?.OnShown();
    }

    private void CancelPreload()
    {
        _preloadRun++;
        _preloadWorkspace = null;
    }

    // T-11/AC-17: once per opened dataset, after it is Ready. A run that stopped because the shell left Ready (a faulted
    // load) starts again at the next Ready and skips the pages it already created.
    private void StartPreload()
    {
        if (_viewModel is not { IsReady: true, Workspace: { } workspace } || ReferenceEquals(_preloadWorkspace, workspace))
        {
            return;
        }

        _preloadWorkspace = workspace;
        _ = PreloadPagesAsync(++_preloadRun, workspace);
    }

    private bool IsPreloadCurrent(int run, WorkspaceViewModel workspace) =>
        run == _preloadRun && _viewModel is { IsReady: true } shell && ReferenceEquals(shell.Workspace, workspace);

    /// <summary>
    /// Creates the page ViewModels and views that are not created yet, one step at a time at low dispatcher priority,
    /// so input, committed changes and navigation stay ahead of it. A preloaded view is laid out for one frame while
    /// transparent (<see cref="PageSlot.BeginPreload"/>), because a collapsed element is not measured and its first
    /// visit would otherwise still apply its templates and realize its rows and cards.
    /// </summary>
    /// <remarks>
    /// Every step runs on the UI thread and checks <paramref name="run"/> first: the shell drops the views and starts a
    /// new run before the dataset's workspace is disposed, so no step touches a closed workspace.
    /// </remarks>
    private async Task PreloadPagesAsync(int run, WorkspaceViewModel workspace)
    {
        var count = 0;
        var started = Stopwatch.GetTimestamp();
        try
        {
            // After the first frame with the initial page (and its queued data), so startup to a usable view is
            // not spent on the other pages.
            if (!await YieldAsync())
            {
                return;
            }

            await NextFrameAsync();
            started = Stopwatch.GetTimestamp();
            foreach (var page in PreloadOrder)
            {
                if (!await YieldAsync() || !IsPreloadCurrent(run, workspace))
                {
                    StopPreload(run);
                    return;
                }

                if (_pageSlots.ContainsKey(page))
                {
                    continue;
                }

                // The ViewModel first: Board and Grid build their item ViewModels when the store's snapshot reaches
                // them on the UI thread. The view is created in a later step, once that data is in place.
                if (_viewModel?.PageViewModel(page) is { } pageViewModel)
                {
                    await WhenLoadedAsync(pageViewModel);
                }

                if (!await YieldAsync() || !IsPreloadCurrent(run, workspace))
                {
                    StopPreload(run);
                    return;
                }

                if (_pageSlots.ContainsKey(page))
                {
                    continue;
                }

                var slot = AddPage(page, workspace, isPreload: true);
                await NextFrameAsync();
                slot.EndPreload();
                count++;
            }
        }
        catch (Exception ex)
        {
            // The preload only saves time: a page it did not create is created on its first visit, as before.
            if (run == _preloadRun)
            {
                PagesPreloaded?.Invoke(this, new PagesPreloadedEventArgs(count, Stopwatch.GetElapsedTime(started), ex));
            }

            return;
        }

        if (run == _preloadRun)
        {
            PagesPreloaded?.Invoke(this, new PagesPreloadedEventArgs(count, Stopwatch.GetElapsedTime(started), null));
        }
    }

    // Stopped by a closing dataset (a newer run) or by leaving Ready; only the latter starts again at the next Ready.
    private void StopPreload(int run)
    {
        if (run == _preloadRun)
        {
            _preloadWorkspace = null;
        }
    }

    // False when the dispatcher no longer accepts work (the window is closing).
    private Task<bool> YieldAsync()
    {
        var done = new TaskCompletionSource<bool>();
        return DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => done.SetResult(true))
            ? done.Task
            : Task.FromResult(false);
    }

    // The next rendered frame, or the timeout when no frame renders (a minimized window).
    private Task NextFrameAsync()
    {
        var done = new TaskCompletionSource();
        var timer = DispatcherQueue.CreateTimer();
        void OnRendered(object? sender, RenderedEventArgs e) => Finish();
        void Finish()
        {
            CompositionTarget.Rendered -= OnRendered;
            timer.Stop();
            done.TrySetResult();
        }

        timer.Interval = PreloadFrameTimeout;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Finish();
        CompositionTarget.Rendered += OnRendered;
        timer.Start();
        return done.Task;
    }

    // Completes when the page has its first data (State leaves Loading), or after the timeout.
    private Task WhenLoadedAsync(IPageViewModel page)
    {
        if (page.State != ViewState.Loading)
        {
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource();
        var timer = DispatcherQueue.CreateTimer();
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (page.State != ViewState.Loading)
            {
                Finish();
            }
        }

        void Finish()
        {
            page.PropertyChanged -= OnChanged;
            timer.Stop();
            done.TrySetResult();
        }

        timer.Interval = PreloadDataTimeout;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Finish();
        page.PropertyChanged += OnChanged;
        timer.Start();
        return done.Task;
    }

    // The previous page is still in the tree, so a menu, flyout or drop-down it opened would stay over the next page.
    // Navigation dismisses that transient UI like a click outside it would; ContentDialogs are not light-dismiss and
    // stay open. Closing the popup is what FlyoutBase.Hide does itself, so a flyout's own state stays consistent.
    private void CloseLightDismissPopups()
    {
        if (XamlRoot is not { } root)
        {
            return;
        }

        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup.IsLightDismissEnabled)
            {
                popup.IsOpen = false;
            }
        }
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject? ancestor)
    {
        if (ancestor is null)
        {
            return false;
        }

        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    // Design §7.7: remembers the focused Dashboard element while the Dashboard is left, for Return/Back.
    private void RememberDashboardFocus()
    {
        if (_currentSlot?.Page is DashboardPageView dashboard && XamlRoot is { } root
            && FocusManager.GetFocusedElement(root) is UIElement focused && IsWithin(focused, dashboard))
        {
            _dashboardFocus = new WeakReference<UIElement>(focused);
        }
    }

    private void AttachNavigator(DrillDownNavigator? navigator)
    {
        if (ReferenceEquals(_navigator, navigator))
        {
            return;
        }

        if (_navigator is not null)
        {
            _navigator.Returning -= OnReturningToDashboard;
        }

        _navigator = navigator;
        _dashboardFocus = null;
        if (navigator is not null)
        {
            navigator.Returning += OnReturningToDashboard;
        }
    }

    private void OnReturningToDashboard(object? sender, EventArgs e) => _isReturningToDashboard = true;

    // T-11: the time to the first rendered frame after the page swap and its queued layout work. The low-priority
    // enqueue runs after the binding updates that the page swap scheduled, so the next frame shows the page.
    private void ReportWhenRendered(AppPage page, long started)
    {
        if (PageShown is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            void OnRendered(object? sender, RenderedEventArgs e)
            {
                CompositionTarget.Rendered -= OnRendered;
                if (started == _navigationStarted)
                {
                    PageShown?.Invoke(this, new PageShownEventArgs(page, Stopwatch.GetElapsedTime(started)));
                }
            }

            CompositionTarget.Rendered += OnRendered;
        });
    }

    // Design §7.1: F6 / Shift+F6 cycle navigation pane → page header → content → status bar.
    private void CycleFocus(bool backwards)
    {
        if (_currentSlot?.Page is not IPageView page || XamlRoot is not { } root)
        {
            return;
        }

        var focused = FocusManager.GetFocusedElement(root) as DependencyObject;
        var zone = IsWithin(focused, StatusBar) ? 3
            : page.ContentElement is { } content && IsWithin(focused, content) ? 2
            : IsWithin(focused, page.HeaderElement) ? 1
            : IsWithin(focused, Nav) ? 0
            : -1;
        for (var step = 1; step <= 4; step++)
        {
            var next = zone < 0 ? (backwards ? 4 - step : step - 1) : (((zone + (backwards ? -step : step)) % 4) + 4) % 4;
            if (FocusZone(next, page))
            {
                return;
            }
        }
    }

    private bool FocusZone(int zone, IPageView page)
    {
        switch (zone)
        {
            case 0:
                return Nav.SelectedItem is Control { IsLoaded: true } item && item.Focus(FocusState.Keyboard);
            case 1:
                page.FocusTitle();
                return true;
            case 2:
                return page.ContentElement is { } content && FocusManager.FindFirstFocusableElement(content) is UIElement first && first.Focus(FocusState.Keyboard);
            default:
                return FocusManager.FindFirstFocusableElement(StatusBar) is UIElement status && status.Focus(FocusState.Keyboard);
        }
    }

    private void SyncSelection()
    {
        if (_viewModel is null)
        {
            return;
        }

        var page = _viewModel.CurrentPage;
        var target = page == AppPage.Settings
            ? Nav.SettingsItem
            : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == page.ToString());
        if (target is null || ReferenceEquals(Nav.SelectedItem, target))
        {
            return;
        }

        _isSyncingSelection = true;
        Nav.SelectedItem = target;
        _isSyncingSelection = false;
    }

    // The WinUI Gallery guidance: navigate on SelectionChanged, not ItemInvoked.
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isSyncingSelection || _viewModel is null)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            _viewModel.NavigateTo(AppPage.Settings);
        }
        else if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Enum.TryParse<AppPage>(tag, out var page))
        {
            _viewModel.NavigateTo(page);
        }
    }

    private void OnNavDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        // Design §2.1: in LeftMinimal the title bar's toggle opens the pane; otherwise the pane's own button is used.
        var minimal = args.DisplayMode == NavigationViewDisplayMode.Minimal;
        Nav.IsPaneToggleButtonVisible = !minimal;
        if (IsTitleBarPaneToggleVisible != minimal)
        {
            IsTitleBarPaneToggleVisible = minimal;
            TitleBarPaneToggleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void AddPageAccelerators()
    {
        // Design §7.1: Ctrl+1 … Ctrl+5 for the five destinations, Ctrl+, for Settings.
        (VirtualKey Key, AppPage Page)[] map =
        [
            (VirtualKey.Number1, AppPage.Dashboard),
            (VirtualKey.Number2, AppPage.Board),
            (VirtualKey.Number3, AppPage.Grid),
            (VirtualKey.Number4, AppPage.Calendar),
            (VirtualKey.Number5, AppPage.Checklists),
            ((VirtualKey)188, AppPage.Settings),
        ];
        foreach (var (key, page) in map)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, e) =>
            {
                if (_viewModel is { IsReady: true })
                {
                    e.Handled = true;
                    _viewModel.NavigateTo(page);
                }
            };
            KeyboardAccelerators.Add(accelerator);
        }

        // Design §7.1: Ctrl+F focuses the page search (at S it expands the compact search row first).
        var search = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        search.Invoked += (_, e) =>
        {
            if (_viewModel is { IsReady: true } && _currentSlot?.Page is IPageView page)
            {
                e.Handled = page.FocusSearch();
            }
        };
        KeyboardAccelerators.Add(search);

        // Design §7.1: F6 / Shift+F6 move between the navigation pane, page header, content and status bar.
        foreach (var modifiers in new[] { VirtualKeyModifiers.None, VirtualKeyModifiers.Shift })
        {
            var cycle = new KeyboardAccelerator { Key = VirtualKey.F6, Modifiers = modifiers };
            cycle.Invoked += (_, e) =>
            {
                if (_viewModel is { IsReady: true })
                {
                    e.Handled = true;
                    CycleFocus(backwards: modifiers == VirtualKeyModifiers.Shift);
                }
            };
            KeyboardAccelerators.Add(cycle);
        }

        // Design §7.1: Alt+Left returns to the drill-down origin, like the TitleBar Back button.
        var back = new KeyboardAccelerator { Key = VirtualKey.Left, Modifiers = VirtualKeyModifiers.Menu };
        back.Invoked += (_, e) =>
        {
            if (_viewModel is { IsReady: true, CanGoBack: true })
            {
                e.Handled = _viewModel.GoBack();
            }
        };
        KeyboardAccelerators.Add(back);
    }

    private void AttachBadge(WorkspaceViewModel? workspace)
    {
        if (ReferenceEquals(_badgeSource, workspace))
        {
            return;
        }

        if (_badgeSource is not null)
        {
            _badgeSource.OpenChecklistCountChanged -= OnOpenChecklistCountChanged;
        }

        _badgeSource = workspace;
        if (workspace is not null)
        {
            workspace.OpenChecklistCountChanged += OnOpenChecklistCountChanged;
        }

        OnOpenChecklistCountChanged(this, EventArgs.Empty);
    }

    // Design §2.2: the Checklists item carries an InfoBadge with the open count; its accessible name includes it.
    private void OnOpenChecklistCountChanged(object? sender, EventArgs e)
    {
        if (Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == nameof(AppPage.Checklists)) is not { } item)
        {
            return;
        }

        var count = _badgeSource?.OpenChecklistCount ?? 0;
        item.InfoBadge = count > 0 ? new InfoBadge { Value = count } : null;
        if (count > 0 && _viewModel is not null)
        {
            AutomationProperties.SetName(item, _viewModel.Format("Shell_NavChecklistsBadgeName", count));
        }
        else
        {
            item.ClearValue(AutomationProperties.NameProperty);
        }
    }

    private void AttachSaveStatus(SaveStatusViewModel? saveStatus)
    {
        if (ReferenceEquals(_saveStatus, saveStatus))
        {
            return;
        }

        if (_saveStatus is not null)
        {
            _saveStatus.PropertyChanged -= OnSaveStatusChanged;
        }

        _saveStatus = saveStatus;
        SaveStatus = saveStatus;
        if (saveStatus is not null)
        {
            saveStatus.PropertyChanged += OnSaveStatusChanged;
        }
    }

    // Design §2.8/§8.3: the status text is a polite live region; a failure is announced assertively.
    private void OnSaveStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SaveStatusViewModel.State) || sender is not SaveStatusViewModel status)
        {
            return;
        }

        var peer = FrameworkElementAutomationPeer.FromElement(SaveStatusText) ?? FrameworkElementAutomationPeer.CreatePeerForElement(SaveStatusText);
        if (status.State == SaveState.Failed && status.FailureMessage is { } message)
        {
            peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionAborted, AutomationNotificationProcessing.ImportantMostRecent, message, "SaveFailed");
        }
        else if (status.State is SaveState.Saved or SaveState.Resyncing)
        {
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
