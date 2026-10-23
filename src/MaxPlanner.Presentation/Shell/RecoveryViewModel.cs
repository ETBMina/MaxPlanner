using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Common;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Shell;

/// <summary>Why the dataset was not opened at startup.</summary>
public enum RecoveryKind
{
    /// <summary><c>data.db</c> is missing although the folder was used before (T-09, AC-15).</summary>
    DatabaseMissing,

    /// <summary>The file is damaged or is not a Max Planner dataset.</summary>
    Corrupt,

    /// <summary>A newer app version wrote the dataset (T-03.8).</summary>
    UnsupportedSchema,

    /// <summary>An upgrade failed and was rolled back; a safety backup exists.</summary>
    MigrationFailed,

    /// <summary>ADR 0015: <c>restore.pending.json</c> exists; the dataset is not opened.</summary>
    InterruptedRestore,

    /// <summary>The file exists but could not be read now (locked, busy, disconnected drive, I/O error).</summary>
    StorageUnavailable,
}

/// <param name="StorageFailure">The failure category for <see cref="RecoveryKind.StorageUnavailable"/>.</param>
/// <param name="FoundSchemaVersion">For <see cref="RecoveryKind.UnsupportedSchema"/>: the dataset's version.</param>
/// <param name="SupportedSchemaVersion">For <see cref="RecoveryKind.UnsupportedSchema"/>: this app's version.</param>
/// <param name="CurrentSchemaVersion">For <see cref="RecoveryKind.MigrationFailed"/>: the version the data still has.</param>
/// <param name="SafetyBackupDirectory">For <see cref="RecoveryKind.MigrationFailed"/>: the safety backup taken before the upgrade.</param>
public sealed record RecoveryInfo(
    RecoveryKind Kind,
    string DataDirectory,
    string BackupsDirectory,
    StorageFailureKind? StorageFailure = null,
    int? FoundSchemaVersion = null,
    int? SupportedSchemaVersion = null,
    int? CurrentSchemaVersion = null,
    string? SafetyBackupDirectory = null)
{
    /// <summary>Maps the typed failure of opening a dataset to the recovery shown for it.</summary>
    public static RecoveryInfo ForOpenFailure(AppError error, string dataDirectory, string backupsDirectory, string? safetyBackupDirectory)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            UnsupportedSchemaError schema => new(RecoveryKind.UnsupportedSchema, dataDirectory, backupsDirectory,
                FoundSchemaVersion: schema.FoundVersion, SupportedSchemaVersion: schema.SupportedVersion),
            MigrationFailedError migration => new(RecoveryKind.MigrationFailed, dataDirectory, backupsDirectory,
                CurrentSchemaVersion: migration.CurrentVersion, SafetyBackupDirectory: safetyBackupDirectory),
            StorageFailureError { Kind: StorageFailureKind.Corrupt } => new(RecoveryKind.Corrupt, dataDirectory, backupsDirectory),
            StorageFailureError storage => new(RecoveryKind.StorageUnavailable, dataDirectory, backupsDirectory, StorageFailure: storage.Kind),
            _ => new(RecoveryKind.Corrupt, dataDirectory, backupsDirectory),
        };
    }
}

public enum CreateDatasetOutcome
{
    Created,

    /// <summary>A database file appeared in the meantime; nothing was overwritten.</summary>
    AlreadyExists,

    Failed,
}

/// <summary>The result of a restore started from the recovery page (ADR 0015).</summary>
public enum RecoveryRestoreOutcome
{
    /// <summary>The restored or recovered dataset is in place and was opened.</summary>
    Completed,

    /// <summary>An interrupted restore was undone; the previous data is back and was opened.</summary>
    RolledBack,

    /// <summary>Neither copy could be verified; nothing was guessed and the marker is kept (AC-15).</summary>
    Unresolved,

    /// <summary>The user cancelled the folder picker.</summary>
    Cancelled,

    /// <summary>The chosen backup failed validation; nothing was replaced.</summary>
    Invalid,

    Failed,
}

/// <summary>What the recovery page can ask the startup coordinator to do.</summary>
public interface IRecoveryActions
{
    /// <summary>
    /// ADR 0015: finishes an interrupted restore (roll forward when the restored copy verifies, otherwise roll back),
    /// then opens the result. The default refuses, for hosts without restore support.
    /// </summary>
    Task<RecoveryRestoreOutcome> FinishInterruptedRestoreAsync() => Task.FromResult(RecoveryRestoreOutcome.Failed);

    /// <summary>
    /// T-09: lets the user pick a backup folder, validates it in staging, keeps a raw copy of the current files, swaps
    /// the database under the restore marker, and opens it. Called only after the user confirmed it.
    /// </summary>
    Task<RecoveryRestoreOutcome> RestoreFromBackupAsync() => Task.FromResult(RecoveryRestoreOutcome.Failed);

    /// <summary>Creates a new empty dataset; called only after the user confirmed it (T-09, AC-15).</summary>
    Task<CreateDatasetOutcome> CreateNewDatasetAsync();

    /// <summary>Tries to open the dataset again; the shell shows the new outcome.</summary>
    Task RetryOpenAsync();

    /// <summary>Closes the app with the exit code for this recovery (ADR 0003).</summary>
    void CloseApp();
}

/// <summary>
/// The startup recovery page (T-09, AC-15): explains why the data was not opened and offers only safe
/// actions. Nothing here deletes or replaces data; creating an empty dataset needs an explicit confirmation
/// and is offered only when the database file is missing.
/// </summary>
public sealed partial class RecoveryViewModel : ObservableObject
{
    private readonly ILocalizedStrings _strings;
    private readonly IRecoveryActions _actions;
    private readonly IDataFolderLauncher _dataFolder;

    public RecoveryViewModel(RecoveryInfo info, ILocalizedStrings strings, IRecoveryActions actions, IDataFolderLauncher dataFolder)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _dataFolder = dataFolder ?? throw new ArgumentNullException(nameof(dataFolder));
        Title = strings.GetString($"Recovery_{info.Kind}_Title");
        Message = BuildMessage(info, strings);
    }

    public RecoveryInfo Info { get; }

    public string Title { get; }

    public string Message { get; }

    public string DataFolderPath => Info.DataDirectory;

    /// <summary>A newer-schema dataset or an unfinished restore is not damage, so it is shown as a warning.</summary>
    public bool IsWarning => Info.Kind is RecoveryKind.UnsupportedSchema or RecoveryKind.InterruptedRestore;

    /// <summary>Only a missing database may be replaced by a new empty one, and only after confirmation.</summary>
    public bool CanCreateNew => Info.Kind == RecoveryKind.DatabaseMissing;

    /// <summary>Retrying helps only when the file was temporarily unreadable.</summary>
    public bool CanRetry => Info.Kind == RecoveryKind.StorageUnavailable;

    /// <summary>ADR 0015: an interrupted restore is finished or undone from the marker, never guessed.</summary>
    public bool CanFinishRestore => Info.Kind == RecoveryKind.InterruptedRestore;

    /// <summary>T-09: a damaged, missing, or failed-upgrade dataset can be replaced from a validated backup after confirmation.</summary>
    public bool CanRestoreFromBackup => Info.Kind is RecoveryKind.Corrupt or RecoveryKind.DatabaseMissing or RecoveryKind.MigrationFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRestoreOfferVisible))]
    [NotifyCanExecuteChangedFor(nameof(BeginRestoreCommand), nameof(ConfirmRestoreCommand), nameof(CancelRestoreCommand))]
    public partial bool IsConfirmingRestore { get; private set; }

    public bool IsRestoreOfferVisible => CanRestoreFromBackup && !IsConfirmingRestore;

    private bool CanBeginRestore => CanRestoreFromBackup && !IsConfirmingRestore && !IsBusy;

    private bool CanAnswerRestore => IsConfirmingRestore && !IsBusy;

    private bool CanFinishRestoreNow => CanFinishRestore && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanFinishRestoreNow))]
    private async Task FinishRestoreAsync()
    {
        IsBusy = true;
        try
        {
            ShowRestoreOutcome(await _actions.FinishInterruptedRestoreAsync());
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanBeginRestore))]
    private void BeginRestore()
    {
        ActionError = null;
        IsConfirmingRestore = true;
    }

    [RelayCommand(CanExecute = nameof(CanAnswerRestore))]
    private void CancelRestore() => IsConfirmingRestore = false;

    [RelayCommand(CanExecute = nameof(CanAnswerRestore))]
    private async Task ConfirmRestoreAsync()
    {
        IsBusy = true;
        try
        {
            ShowRestoreOutcome(await _actions.RestoreFromBackupAsync());
            IsConfirmingRestore = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowRestoreOutcome(RecoveryRestoreOutcome outcome)
    {
        // Success replaces this page with the opened dataset; only the other outcomes stay visible here.
        ActionError = outcome is RecoveryRestoreOutcome.Completed or RecoveryRestoreOutcome.RolledBack or RecoveryRestoreOutcome.Cancelled
            ? null
            : _strings.GetString("Recovery_RestoreOutcome_" + outcome);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreateOfferVisible))]
    [NotifyCanExecuteChangedFor(nameof(BeginCreateCommand), nameof(ConfirmCreateCommand), nameof(CancelCreateCommand))]
    public partial bool IsConfirmingCreate { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BeginCreateCommand), nameof(ConfirmCreateCommand), nameof(CancelCreateCommand), nameof(RetryCommand))]
    [NotifyCanExecuteChangedFor(nameof(FinishRestoreCommand), nameof(BeginRestoreCommand), nameof(ConfirmRestoreCommand), nameof(CancelRestoreCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionError))]
    public partial string? ActionError { get; private set; }

    public bool HasActionError => ActionError is not null;

    public bool IsCreateOfferVisible => CanCreateNew && !IsConfirmingCreate;

    private bool CanBeginCreate => CanCreateNew && !IsConfirmingCreate && !IsBusy;

    private bool CanAnswerConfirmation => IsConfirmingCreate && !IsBusy;

    private bool CanRetryNow => CanRetry && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBeginCreate))]
    private void BeginCreate()
    {
        ActionError = null;
        IsConfirmingCreate = true;
    }

    [RelayCommand(CanExecute = nameof(CanAnswerConfirmation))]
    private void CancelCreate() => IsConfirmingCreate = false;

    [RelayCommand(CanExecute = nameof(CanAnswerConfirmation))]
    private async Task ConfirmCreateAsync()
    {
        IsBusy = true;
        try
        {
            var outcome = await _actions.CreateNewDatasetAsync();
            if (outcome != CreateDatasetOutcome.Created)
            {
                ActionError = _strings.GetString(outcome == CreateDatasetOutcome.AlreadyExists ? "Recovery_CreateAlreadyExists" : "Recovery_CreateFailed");
                IsConfirmingCreate = false;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRetryNow))]
    private async Task RetryAsync()
    {
        IsBusy = true;
        try
        {
            await _actions.RetryOpenAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => _dataFolder.OpenDataFolder();

    [RelayCommand]
    private void Exit() => _actions.CloseApp();

    private static string BuildMessage(RecoveryInfo info, ILocalizedStrings strings)
    {
        var key = $"Recovery_{info.Kind}_Message";
        return info.Kind switch
        {
            RecoveryKind.UnsupportedSchema => strings.Format(key, info.FoundSchemaVersion, info.SupportedSchemaVersion),
            RecoveryKind.MigrationFailed => strings.Format(key, info.CurrentSchemaVersion, info.SafetyBackupDirectory ?? info.BackupsDirectory),
            RecoveryKind.StorageUnavailable => strings.Format(key, StorageFailureText.Describe(info.StorageFailure, strings)),
            _ => strings.Format(key, info.BackupsDirectory),
        };
    }
}

/// <summary>Short, actionable descriptions of storage failure categories (T-06.7).</summary>
public static class StorageFailureText
{
    public static string Describe(StorageFailureKind? kind, ILocalizedStrings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return strings.GetString(kind is { } k ? $"StorageFailure_{k}" : "StorageFailure_Unknown");
    }
}
