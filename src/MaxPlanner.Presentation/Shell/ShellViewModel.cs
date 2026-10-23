using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Shell;

/// <summary>
/// Window-level state around the pages: startup progress, the recovery page, the store's lifecycle
/// (ready, resyncing, faulted), app-wide notices (design §2.7), and navigation between the pages of the
/// open dataset (design §2.2). Use it on the UI thread.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizedStrings _strings;
    private readonly IDataFolderLauncher _dataFolder;
    private readonly IUiPreferencesStore _preferences;
    private readonly SerialDisposable _storeSubscription = new();
    private WorkspaceProjectionStore? _store;
    private IPageViewModel? _summarySource;
    private bool _isShuttingDown;

    public ShellViewModel(ILocalizedStrings strings, IDataFolderLauncher dataFolder, IUiPreferencesStore preferences)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _dataFolder = dataFolder ?? throw new ArgumentNullException(nameof(dataFolder));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        CurrentPage = AppPages.FromKey(preferences.Current.ActivePage);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarting), nameof(IsReady), nameof(IsInRecovery), nameof(HasFailed))]
    public partial ShellState State { get; private set; } = ShellState.Starting;

    /// <summary>The recovery page's ViewModel while <see cref="State"/> is <see cref="ShellState.Recovery"/>.</summary>
    [ObservableProperty]
    public partial RecoveryViewModel? Recovery { get; private set; }

    /// <summary>User-facing explanation when <see cref="State"/> is <see cref="ShellState.Failed"/>; otherwise null.</summary>
    [ObservableProperty]
    public partial string? FailureMessage { get; private set; }

    /// <summary>True when the failure is a projection load that <see cref="RetryCommand"/> can repeat.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool CanRetry { get; private set; }

    /// <summary>T-07: the store is reloading after a publication fault; the app InfoBar says so.</summary>
    [ObservableProperty]
    public partial bool IsResyncing { get; private set; }

    /// <summary>T-07: editors must not start commands while the displayed data may be behind the database.</summary>
    [ObservableProperty]
    public partial bool IsEditingBlocked { get; private set; } = true;

    /// <summary>The pages of the open dataset; null until a dataset has opened.</summary>
    [ObservableProperty]
    public partial WorkspaceViewModel? Workspace { get; private set; }

    /// <summary>
    /// The visible page. B-04: Dashboard on first run, then the last active page, persisted in
    /// <c>configuration.json</c>.
    /// </summary>
    [ObservableProperty]
    public partial AppPage CurrentPage { get; private set; }

    /// <summary>The current page's status-bar summary (design §2.8); empty for pages not built yet.</summary>
    [ObservableProperty]
    public partial string StatusSummary { get; private set; } = string.Empty;

    /// <summary>A Dashboard drill-down context can be popped (TitleBar Back, Alt+Left; design §2.9).</summary>
    [ObservableProperty]
    public partial bool CanGoBack { get; private set; }

    /// <summary>Bound two-way: the user may close the notice.</summary>
    [ObservableProperty]
    public partial bool IsPreferencesNoticeOpen { get; set; }

    [ObservableProperty]
    public partial string? PreferencesNoticeMessage { get; private set; }

    /// <summary>Bound two-way: the user may close the warning.</summary>
    [ObservableProperty]
    public partial bool IsBackupWarningOpen { get; set; }

    [ObservableProperty]
    public partial string? BackupWarningMessage { get; private set; }

    public bool IsStarting => State == ShellState.Starting;

    public bool IsReady => State == ShellState.Ready;

    public bool IsInRecovery => State == ShellState.Recovery;

    public bool HasFailed => State == ShellState.Failed;

    public string DataFolderPath => _dataFolder.DataDirectory;

    public void ShowStarting()
    {
        Recovery = null;
        FailureMessage = null;
        CanRetry = false;
        State = ShellState.Starting;
    }

    public void ShowRecovery(RecoveryViewModel recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        Recovery = recovery;
        IsEditingBlocked = true;
        State = ShellState.Recovery;
    }

    /// <summary>An unexpected startup failure with no automatic retry.</summary>
    public void MarkFailed(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        FailureMessage = message;
        CanRetry = false;
        IsEditingBlocked = true;
        State = ShellState.Failed;
    }

    /// <summary>
    /// Follows <paramref name="store"/>'s lifecycle until disposed or replaced. Store notifications arrive on
    /// the store scheduler and are moved to <paramref name="uiScheduler"/> first.
    /// </summary>
    public IDisposable AttachStore(WorkspaceProjectionStore store, IScheduler uiScheduler)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(uiScheduler);
        _store = store;
        var subscription = store.State.ObserveOn(uiScheduler).Subscribe(Apply);
        _storeSubscription.Disposable = subscription;
        return subscription;
    }

    /// <summary>Shows the pages of the opened dataset. The shell does not own (or dispose) the workspace.</summary>
    public void AttachWorkspace(WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
        workspace.Context.Navigator.NavigationRequested += OnNavigationRequested;
        workspace.Context.Navigator.StackChanged += OnDrillDownStackChanged;
        ActivatePage(CurrentPage);
    }

    /// <summary>TitleBar Back / Alt+Left: pops the drill-down stack and shows the Dashboard again (AC-20).</summary>
    public bool GoBack() => Workspace?.Context.Navigator.GoBack() ?? false;

    /// <summary>
    /// Shows <paramref name="page"/>. Its ViewModel is created on the first visit and kept afterwards, so
    /// repeated navigation never adds subscriptions (AC-17).
    /// </summary>
    public void NavigateTo(AppPage page)
    {
        if (page == CurrentPage)
        {
            return;
        }

        CurrentPage = page;
        _preferences.Update(p => p with { ActivePage = AppPages.Key(page) });
        ActivatePage(page);
    }

    /// <summary>A formatted shell string, for example the Checklists badge's accessible name.</summary>
    public string Format(string key, params object?[] args) => _strings.Format(key, args);

    /// <summary>The localized page name, e.g. for the placeholder of a page not built yet.</summary>
    public string PageTitle(AppPage page) => _strings.GetString($"Page_{page}_Title");

    /// <summary>The retained ViewModel of a built page, created on first use; null for pages not built yet.</summary>
    public IPageViewModel? PageViewModel(AppPage page) => Workspace is not { } workspace ? null : page switch
    {
        AppPage.Dashboard => workspace.Dashboard,
        AppPage.Board => workspace.Board,
        AppPage.Grid => workspace.Grid,
        AppPage.Calendar => workspace.Calendar,
        AppPage.Checklists => workspace.Checklists,
        AppPage.Settings => workspace.Settings,
        _ => null,
    };

    public void ReportPreferencesRecovery(PreferencesRecovery recovery)
    {
        if (recovery == PreferencesRecovery.None)
        {
            return;
        }

        PreferencesNoticeMessage = _strings.GetString(recovery == PreferencesRecovery.RestoredLastGood
            ? "Shell_PreferencesRestoredMessage"
            : "Shell_PreferencesResetMessage");
        IsPreferencesNoticeOpen = true;
    }

    public void ReportBackupFailed(StorageFailureKind? kind)
    {
        BackupWarningMessage = _strings.Format("Shell_BackupFailedMessage", StorageFailureText.Describe(kind, _strings));
        IsBackupWarningOpen = true;
    }

    /// <summary>Closing has begun: block edits from now on, whatever the store reports.</summary>
    public void BeginShutdown()
    {
        _isShuttingDown = true;
        IsEditingBlocked = true;
        Workspace?.BeginClose();
        _storeSubscription.Disposable = null;
    }

    /// <summary>The dataset is closing; its pages are about to be disposed and must no longer be shown.</summary>
    public void DetachWorkspace()
    {
        SetSummarySource(null);
        if (Workspace is { } workspace)
        {
            workspace.Context.Navigator.NavigationRequested -= OnNavigationRequested;
            workspace.Context.Navigator.StackChanged -= OnDrillDownStackChanged;
        }

        CanGoBack = false;
        Workspace = null;
    }

    private void OnNavigationRequested(object? sender, AppPage page) => NavigateTo(page);

    private void OnDrillDownStackChanged(object? sender, EventArgs e) => CanGoBack = Workspace?.Context.Navigator.CanGoBack ?? false;

    public void Dispose()
    {
        SetSummarySource(null);
        _storeSubscription.Dispose();
    }

    private void ActivatePage(AppPage page) => SetSummarySource(PageViewModel(page));

    private void SetSummarySource(IPageViewModel? page)
    {
        if (_summarySource is not null)
        {
            _summarySource.PropertyChanged -= OnPagePropertyChanged;
        }

        _summarySource = page;
        if (page is not null)
        {
            page.PropertyChanged += OnPagePropertyChanged;
        }

        StatusSummary = page?.Summary ?? string.Empty;
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPageViewModel.Summary) && sender is IPageViewModel page)
        {
            StatusSummary = page.Summary;
        }
    }

    private void Apply(ProjectionState state)
    {
        switch (state.Status)
        {
            case ProjectionStatus.Loading:
                ShowStarting();
                IsEditingBlocked = true;
                break;
            case ProjectionStatus.Ready:
                FailureMessage = null;
                CanRetry = false;
                IsResyncing = false;
                IsEditingBlocked = _isShuttingDown;
                State = ShellState.Ready;
                break;
            case ProjectionStatus.Resyncing:
                IsResyncing = true;
                IsEditingBlocked = true;
                State = ShellState.Ready;
                break;
            case ProjectionStatus.Faulted:
                IsResyncing = false;
                IsEditingBlocked = true;
                FailureMessage = state.Error is StorageFailureError storage
                    ? _strings.Format("Shell_LoadFailedStorageMessage", StorageFailureText.Describe(storage.Kind, _strings))
                    : _strings.GetString("Shell_LoadFailedMessage");
                CanRetry = true;
                State = ShellState.Failed;
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry()
    {
        CanRetry = false;
        _store?.Reload();
    }

    [RelayCommand]
    private void OpenDataFolder() => _dataFolder.OpenDataFolder();
}

/// <summary>The NavigationView destinations (design §2.2).</summary>
public enum AppPage
{
    Dashboard,
    Board,
    Grid,
    Calendar,
    Checklists,
    Settings,
}

public static class AppPages
{
    /// <summary>The <c>configuration.json</c> key (UiPreferences.KnownPageKeys).</summary>
    public static string Key(AppPage page) => page.ToString().ToLowerInvariant();

    /// <summary>B-04: an unknown or missing saved page opens the Dashboard.</summary>
    public static AppPage FromKey(string? key) =>
        Enum.GetValues<AppPage>().FirstOrDefault(p => string.Equals(Key(p), key, StringComparison.Ordinal), AppPage.Dashboard);
}

public enum ShellState
{
    Starting,
    Recovery,
    Ready,
    Failed,
}
