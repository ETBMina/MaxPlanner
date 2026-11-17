using MaxPlanner.Application.Common;
using MaxPlanner.Application.Startup;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Desktop.Startup;

/// <summary>
/// The window side of <see cref="StartupCoordinator"/>: shows its states in the shell, builds the recovery page, and
/// forwards the recovery page's actions back to the coordinator (T-09, AC-15). Every member runs on the UI thread.
/// </summary>
internal sealed partial class ShellStartupView(
    ShellViewModel shell,
    ILocalizedStrings strings,
    IDataFolderLauncher dataFolder,
    IFilePickerService pickers,
    StartupContext context,
    Action requestClose,
    ILogger<ShellStartupView> logger) : IStartupShell, IRecoveryActions
{
    private StartupCoordinator? _coordinator;

    public bool HasFailed => shell.HasFailed;

    private StartupCoordinator Coordinator => _coordinator ?? throw new InvalidOperationException("The startup coordinator is not connected.");

    /// <summary>Completes the composition: the coordinator calls this view, and the recovery page calls the coordinator.</summary>
    public void Connect(StartupCoordinator coordinator) => _coordinator = coordinator;

    public void ShowStarting() => shell.ShowStarting();

    public void ShowRecovery(StartupRecovery recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        var paths = context.Paths;
        var info = recovery.Kind switch
        {
            StartupRecoveryKind.InterruptedRestore => new RecoveryInfo(RecoveryKind.InterruptedRestore, paths.DataDirectory, paths.BackupsDirectory),
            StartupRecoveryKind.DatabaseMissing => new RecoveryInfo(RecoveryKind.DatabaseMissing, paths.DataDirectory, paths.BackupsDirectory),
            _ => RecoveryInfo.ForOpenFailure(
                recovery.Error ?? new StorageFailureError(StorageFailureKind.Corrupt),
                paths.DataDirectory,
                paths.BackupsDirectory,
                recovery.SafetyBackupDirectory),
        };
        var elapsedMs = context.ElapsedMilliseconds;
        var kind = info.Kind.ToString();
        var storageFailure = info.StorageFailure?.ToString() ?? "none";
        Log.Recovery(logger, kind, storageFailure, elapsedMs);
        shell.ShowRecovery(new RecoveryViewModel(info, strings, this, dataFolder));
    }

    public void ShowUnexpectedFailure() => shell.MarkFailed(strings.GetString("Shell_UnexpectedStartupFailure"));

    public void BeginDatasetReplacement() => shell.BeginDatasetReplacement();

    public void BeginShutdown() => shell.BeginShutdown();

    public void DetachWorkspace() => shell.DetachWorkspace();

    public void ReportDataNotice(DataNotice notice, string? safetyBackupDirectory = null)
    {
        switch (notice)
        {
            case DataNotice.RestoreDone:
                shell.ReportDataNotice(strings.Format("Restore_DoneNotice", safetyBackupDirectory), isError: false);
                break;
            case DataNotice.RestoreRolledBack:
                shell.ReportDataNotice(strings.Format("Restore_RolledBackNotice", safetyBackupDirectory), isError: true);
                break;
            case DataNotice.InterruptedRestoreFinished:
                shell.ReportDataNotice(strings.GetString("Recovery_RestoreFinishedNotice"), isError: false);
                break;
            case DataNotice.InterruptedRestoreUndone:
                shell.ReportDataNotice(strings.GetString("Recovery_RestoreUndoneNotice"), isError: false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(notice), notice, null);
        }
    }

    public void ReportBackupFailed(StorageFailureKind? kind) => shell.ReportBackupFailed(kind);

    public Task<string?> PickBackupFolderAsync() => pickers.PickFolderAsync();

    public async Task<RecoveryRestoreOutcome> FinishInterruptedRestoreAsync() => ToOutcome(await Coordinator.FinishInterruptedRestoreAsync());

    public async Task<RecoveryRestoreOutcome> RestoreFromBackupAsync() => ToOutcome(await Coordinator.RestoreFromBackupAsync());

    public async Task<CreateDatasetOutcome> CreateNewDatasetAsync() => await Coordinator.CreateNewDatasetAsync() switch
    {
        CreateDatasetResult.Created => CreateDatasetOutcome.Created,
        CreateDatasetResult.AlreadyExists => CreateDatasetOutcome.AlreadyExists,
        _ => CreateDatasetOutcome.Failed,
    };

    public Task RetryOpenAsync() => Coordinator.RetryOpenAsync();

    public void CloseApp() => requestClose();

    private static RecoveryRestoreOutcome ToOutcome(RecoveryRestoreResult result) => result switch
    {
        RecoveryRestoreResult.Completed => RecoveryRestoreOutcome.Completed,
        RecoveryRestoreResult.RolledBack => RecoveryRestoreOutcome.RolledBack,
        RecoveryRestoreResult.Unresolved => RecoveryRestoreOutcome.Unresolved,
        RecoveryRestoreResult.Cancelled => RecoveryRestoreOutcome.Cancelled,
        RecoveryRestoreResult.Invalid => RecoveryRestoreOutcome.Invalid,
        _ => RecoveryRestoreOutcome.Failed,
    };

    private static partial class Log
    {
        [LoggerMessage(EventId = 5004, Level = LogLevel.Warning, Message = "Startup recovery: {Kind:l} (storage failure: {StorageFailure:l}) at {ElapsedMs} ms after launch; the dataset was not opened.")]
        public static partial void Recovery(ILogger logger, string kind, string storageFailure, long elapsedMs);
    }
}
