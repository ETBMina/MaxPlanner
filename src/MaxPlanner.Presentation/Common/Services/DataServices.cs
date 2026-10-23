using MaxPlanner.Application.Common;
using MaxPlanner.Application.Transfer;

namespace MaxPlanner.Presentation.Common.Services;

/// <summary>One backup folder as Manage backups lists it (T-09).</summary>
/// <param name="Kind">The ADR 0015 kind, for example <c>manual</c> or <c>restore-safety</c>.</param>
/// <param name="ManifestState"><c>Valid</c>, <c>Missing</c>, or <c>Invalid</c>.</param>
public sealed record BackupListItem(string Path, string Name, DateTimeOffset CreatedAtUtc, string Kind, long SizeBytes, string ManifestState, bool IsManual);

/// <param name="Kind">The check, for example <c>Checksums</c>; shown as <c>Restore_Check_{Kind}</c>.</param>
public sealed record RestoreCheckItem(string Kind, bool Passed, string? Detail);

/// <summary>A backup staged and checked for restore; nothing was replaced. <see cref="Token"/> belongs to the implementation.</summary>
public sealed record RestorePreparation(object Token, string SourceFolder, IReadOnlyList<RestoreCheckItem> Checks, bool IsValid, DateTimeOffset? BackupCreatedAtUtc, string? BackupKind);

public enum RestoreRunOutcome
{
    /// <summary>The dataset was replaced and reopened.</summary>
    Restored,

    /// <summary>The swap failed and the previous data was put back.</summary>
    RolledBack,

    /// <summary>Nothing was replaced (the safety backup or the close failed).</summary>
    NotStarted,
}

/// <summary>
/// Backup and restore for Settings › Data (T-09, ADR 0015), implemented by the Desktop composition, which owns the
/// dataset session that a restore closes and reopens. File work runs off the UI thread.
/// </summary>
public interface IDataMaintenance
{
    string BackupsDirectory { get; }

    Task<IReadOnlyList<BackupListItem>> ListBackupsAsync(CancellationToken cancellationToken);

    /// <param name="kind"><c>manual</c> (never pruned) or <c>import-safety</c> (B-13.9).</param>
    /// <returns>The new folder, or a <see cref="StorageFailureError"/>.</returns>
    Task<AppResult<string>> CreateBackupAsync(string kind, CancellationToken cancellationToken);

    /// <summary>Deletes a manual backup; any other kind is refused (returns false).</summary>
    Task<bool> DeleteManualBackupAsync(string path, CancellationToken cancellationToken);

    /// <summary>Opens the backups folder in File Explorer.</summary>
    void OpenBackupsFolder();

    /// <summary>Shows a written export in File Explorer; only that file is selected, nothing is opened or run.</summary>
    void ShowInFolder(string filePath);

    /// <summary>Stages <paramref name="backupFolder"/> and runs the checks of design §4.6 Restore step 2.</summary>
    Task<RestorePreparation> PrepareRestoreAsync(string backupFolder, CancellationToken cancellationToken);

    void DiscardRestore(RestorePreparation preparation);

    /// <summary>
    /// Takes a restore-safety backup, closes the dataset (its pages are disposed), swaps the database under the restore
    /// marker, and reopens. The outcome is also reported in the shell, because the calling page does not survive.
    /// </summary>
    Task<RestoreRunOutcome> RestoreAsync(RestorePreparation preparation);
}

/// <summary>Native file pickers bound to the main window (unpackaged apps pass the window handle).</summary>
public interface IFilePickerService
{
    /// <returns>The chosen path, or null when the user cancelled.</returns>
    Task<string?> PickSaveFileAsync(string suggestedName, string typeLabel, string extension);

    Task<IReadOnlyList<string>> PickOpenFilesAsync(IReadOnlyList<string> extensions, bool allowMultiple);

    Task<string?> PickFolderAsync();
}

/// <summary>Everything the Data page needs (B-12, B-13, T-09); null parts disable the matching workflow (tests).</summary>
public sealed record DataServices(
    ExportTransfer Export,
    ImportTransfer Import,
    ITransferFileReader Reader,
    IExternalSourceReader ExternalReader,
    IImportStateReader ImportState,
    IDataMaintenance Maintenance,
    IFilePickerService Pickers);
