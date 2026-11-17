using System.ComponentModel;
using System.Diagnostics;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Startup;
using MaxPlanner.Infrastructure.Backup;
using MaxPlanner.Infrastructure.Startup;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Desktop.Maintenance;

/// <summary>
/// Settings › Data backup and restore over the open dataset (T-09, ADR 0015), through the Application backup and restore
/// ports. The restore itself is delegated to the startup coordinator, which owns the dataset session it must close and reopen.
/// </summary>
internal sealed partial class DataMaintenanceService(
    IDatasetBackups backups,
    IRestoreOperations restores,
    TimeProvider time,
    Func<IStagedRestore, Task<RestoreRunResult>> restore,
    ILogger<DataMaintenanceService> logger) : IDataMaintenance
{
    public string BackupsDirectory => backups.BackupsDirectory;

    public Task<IReadOnlyList<BackupListItem>> ListBackupsAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<BackupListItem>>(() =>
        {
            try
            {
                return [.. backups.List().Select(e => new BackupListItem(e.Path, e.Name, e.CreatedAtUtc, e.Kind, e.SizeBytes, e.ManifestState, e.IsManual))];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.ListFailed(logger, ex);
                return [];
            }
        }, cancellationToken);

    public Task<AppResult<string>> CreateBackupAsync(string kind, CancellationToken cancellationToken)
    {
        // Only the kinds the Data page may write; daily and the other safety kinds have their own owners.
        var safeKind = kind == BackupKinds.ImportSafety ? BackupKind.ImportSafety : BackupKind.Manual;
        var kindName = SqliteDatasetBackups.KindName(safeKind);
        return Task.Run(() =>
        {
            var result = backups.Create(safeKind, time.GetUtcNow());
            if (result.IsSuccess)
            {
                var folder = Path.GetFileName(result.Value);
                Log.Created(logger, kindName, folder);
            }
            else
            {
                var failure = (result.Error as StorageFailureError)?.Kind;
                Log.CreateFailed(logger, kindName, failure);
            }

            return result;
        }, cancellationToken);
    }

    public Task<bool> DeleteManualBackupAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                var deleted = backups.DeleteManual(path);
                var folder = Path.GetFileName(path);
                Log.Deleted(logger, folder, deleted);
                return deleted;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.DeleteFailed(logger, ex);
                return false;
            }
        }, cancellationToken);

    public void OpenBackupsFolder()
    {
        Directory.CreateDirectory(BackupsDirectory);
        StartExplorer(BackupsDirectory);
    }

    public void ShowInFolder(string filePath)
    {
        // Only "/select," and the written file's full path are passed, so nothing is opened or run.
        StartExplorer("/select," + Path.GetFullPath(filePath));
    }

    public Task<RestorePreparation> PrepareRestoreAsync(string backupFolder, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                var staged = restores.Validate(backupFolder, cancellationToken);
                var failed = string.Join(",", staged.Checks.Where(c => !c.Passed).Select(c => c.Kind));
                Log.Validated(logger, staged.IsValid, failed);
                return new RestorePreparation(
                    staged,
                    backupFolder,
                    [.. staged.Checks.Select(c => new RestoreCheckItem(c.Kind, c.Passed, c.Detail))],
                    staged.IsValid,
                    staged.BackupCreatedAtUtc,
                    staged.BackupKind);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.ValidateFailed(logger, ex);
                return new RestorePreparation(new object(), backupFolder, [new RestoreCheckItem(nameof(RestoreCheckKind.Manifest), false, null)], false, null, null);
            }
        }, cancellationToken);

    public void DiscardRestore(RestorePreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (preparation.Token is IStagedRestore staged)
        {
            restores.Discard(staged);
        }
    }

    public Task<RestoreRunOutcome> RestoreAsync(RestorePreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return preparation.Token is IStagedRestore { IsValid: true } staged
            ? ToOutcomeAsync(restore(staged))
            : Task.FromResult(RestoreRunOutcome.NotStarted);
    }

    private static async Task<RestoreRunOutcome> ToOutcomeAsync(Task<RestoreRunResult> run) => await run switch
    {
        RestoreRunResult.Restored => RestoreRunOutcome.Restored,
        RestoreRunResult.RolledBack => RestoreRunOutcome.RolledBack,
        _ => RestoreRunOutcome.NotStarted,
    };

    private void StartExplorer(string argument)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
            start.ArgumentList.Add(argument);
            Process.Start(start)?.Dispose();
        }
        catch (Win32Exception ex)
        {
            Log.ExplorerFailed(logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5701, Level = LogLevel.Information, Message = "Backup ({Kind}) {Folder} created from Settings › Data.")]
        public static partial void Created(ILogger logger, string kind, string folder);

        [LoggerMessage(EventId = 5702, Level = LogLevel.Error, Message = "Backup ({Kind}) failed with storage failure {Failure}.")]
        public static partial void CreateFailed(ILogger logger, string kind, StorageFailureKind? failure);

        [LoggerMessage(EventId = 5703, Level = LogLevel.Information, Message = "Manual backup {Folder} deletion: {Deleted}.")]
        public static partial void Deleted(ILogger logger, string folder, bool deleted);

        [LoggerMessage(EventId = 5704, Level = LogLevel.Warning, Message = "Deleting a manual backup failed.")]
        public static partial void DeleteFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 5705, Level = LogLevel.Information, Message = "Restore validation: valid {IsValid}; failed checks: {Failed}.")]
        public static partial void Validated(ILogger logger, bool isValid, string failed);

        [LoggerMessage(EventId = 5706, Level = LogLevel.Error, Message = "Restore validation could not stage the backup.")]
        public static partial void ValidateFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 5707, Level = LogLevel.Warning, Message = "Listing backups failed.")]
        public static partial void ListFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 5708, Level = LogLevel.Warning, Message = "Opening File Explorer failed.")]
        public static partial void ExplorerFailed(ILogger logger, Exception exception);
    }
}
