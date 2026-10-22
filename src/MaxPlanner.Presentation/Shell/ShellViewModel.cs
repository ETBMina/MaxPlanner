using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;

namespace MaxPlanner.Presentation.Shell;

/// <summary>
/// Window-level state around the pages: startup progress, the recovery page, the store's lifecycle
/// (ready, resyncing, faulted), and app-wide notices (design §2.7). Use it on the UI thread.
/// </summary>
/// <remarks>
/// Until Phase 2 adds the NavigationView, the ready state shows a summary of the loaded dataset
/// (record counts from the projection store) rather than pages.
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizedStrings _strings;
    private readonly IDataFolderLauncher _dataFolder;
    private readonly SerialDisposable _storeSubscription = new();
    private WorkspaceProjectionStore? _store;
    private bool _isShuttingDown;

    public ShellViewModel(ILocalizedStrings strings, IDataFolderLauncher dataFolder)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _dataFolder = dataFolder ?? throw new ArgumentNullException(nameof(dataFolder));
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

    [ObservableProperty]
    public partial string TaskCountText { get; private set; } = "0";

    [ObservableProperty]
    public partial string ChangeRequestCountText { get; private set; } = "0";

    [ObservableProperty]
    public partial string ReviewCountText { get; private set; } = "0";

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
    /// Follows <paramref name="store"/>'s lifecycle and record counts until disposed or replaced. Store
    /// notifications arrive on the store scheduler and are moved to <paramref name="uiScheduler"/> first.
    /// </summary>
    public IDisposable AttachStore(WorkspaceProjectionStore store, IScheduler uiScheduler)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(uiScheduler);
        _store = store;
        var subscriptions = new CompositeDisposable(
            store.State.ObserveOn(uiScheduler).Subscribe(state => Apply(state, store)),
            store.Committed.ObserveOn(uiScheduler).Subscribe(_ => UpdateCounts(store)));
        _storeSubscription.Disposable = subscriptions;
        return subscriptions;
    }

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
        _storeSubscription.Disposable = null;
    }

    public void Dispose() => _storeSubscription.Dispose();

    private void Apply(ProjectionState state, WorkspaceProjectionStore store)
    {
        switch (state.Status)
        {
            case ProjectionStatus.Loading:
                ShowStarting();
                IsEditingBlocked = true;
                break;
            case ProjectionStatus.Ready:
                UpdateCounts(store);
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

    private void UpdateCounts(WorkspaceProjectionStore store)
    {
        TaskCountText = store.Tasks.Count.ToString("N0", CultureInfo.CurrentCulture);
        ChangeRequestCountText = store.ChangeRequests.Count.ToString("N0", CultureInfo.CurrentCulture);
        ReviewCountText = store.ReviewItems.Count.ToString("N0", CultureInfo.CurrentCulture);
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

public enum ShellState
{
    Starting,
    Recovery,
    Ready,
    Failed,
}
