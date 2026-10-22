using System.ComponentModel;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Checklists;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Grid;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MaxPlanner.Presentation.Shell;

/// <summary>
/// The window content: App InfoBars, startup/recovery/failure states, and in Ready the NavigationView with
/// the page host and status bar (design §2). Code-behind keeps one view per visited page and swaps it into
/// the host, so page views and their bindings are created once per session.
/// </summary>
public sealed partial class ShellView : UserControl
{
    public static readonly DependencyProperty SaveStatusProperty = DependencyProperty.Register(
        nameof(SaveStatus), typeof(SaveStatusViewModel), typeof(ShellView), new PropertyMetadata(null));

    // Design §6.4: ViewDashboard, Calendar, Setting; used on the pages that are not built yet.
    private static readonly Dictionary<AppPage, string> PlaceholderGlyphs = new()
    {
        [AppPage.Dashboard] = "",
        [AppPage.Calendar] = "",
        [AppPage.Settings] = "",
    };

    private readonly Dictionary<AppPage, UIElement> _pageViews = [];
    private ShellViewModel? _viewModel;
    private SaveStatusViewModel? _saveStatus;
    private bool _isSyncingSelection;

    public ShellView()
    {
        InitializeComponent();
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

    /// <summary>Raised when the NavigationView enters or leaves LeftMinimal, where the title bar shows the pane toggle (design §2.1).</summary>
    public event EventHandler? TitleBarPaneToggleChanged;

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
            ShowCurrentPage(moveFocus: false);
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
                if (_viewModel?.Workspace is null)
                {
                    // The dataset is closing; drop the views so nothing binds to disposed page ViewModels.
                    _pageViews.Clear();
                    PageHost.Content = null;
                }

                AttachSaveStatus(_viewModel?.Workspace?.SaveStatus);
                ShowCurrentPage(moveFocus: false);
                break;
            case nameof(ShellViewModel.CurrentPage):
                ShowCurrentPage(moveFocus: true);
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

        var page = _viewModel.CurrentPage;
        if (!_pageViews.TryGetValue(page, out var view))
        {
            view = page switch
            {
                AppPage.Board => new BoardPageView(workspace.Board),
                AppPage.Grid => new GridPageView(workspace.Grid),
                AppPage.Checklists => new ChecklistsPageView(workspace.Checklists),
                _ => new PlaceholderPageView(page, _viewModel.PageTitle(page), PlaceholderGlyphs[page]),
            };
            _pageViews[page] = view;
        }

        PageHost.Content = view;
        SyncSelection();

        // Design §7.7: after navigation focus goes to the page title, so the heading is announced.
        if (moveFocus)
        {
            DispatcherQueue.TryEnqueue(() => FocusTitle(view));
        }
    }

    private static void FocusTitle(UIElement view)
    {
        switch (view)
        {
            case GridPageView grid:
                grid.FocusTitle();
                break;
            case BoardPageView board:
                board.FocusTitle();
                break;
            case ChecklistsPageView checklists:
                checklists.FocusTitle();
                break;
            case PlaceholderPageView placeholder:
                placeholder.FocusTitle();
                break;
            default:
                break;
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
