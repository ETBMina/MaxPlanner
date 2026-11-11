using MaxPlanner.Application.Startup;
using MaxPlanner.Infrastructure.Backup;
using MaxPlanner.Infrastructure.Storage;

namespace MaxPlanner.Infrastructure.Startup;

/// <summary>A <see cref="RestoreValidation"/> as the startup coordinator and Settings › Data see it.</summary>
public sealed class StagedSqliteRestore : IStagedRestore
{
    internal StagedSqliteRestore(RestoreValidation validation)
    {
        Validation = validation;
        Checks = [.. validation.Checks.Select(c => new RestoreCheckResult(c.Kind.ToString(), c.Passed, c.Detail))];
    }

    public RestoreValidation Validation { get; }

    public bool IsValid => Validation.IsValid;

    public IReadOnlyList<RestoreCheckResult> Checks { get; }

    public DateTimeOffset? BackupCreatedAtUtc => Validation.BackupCreatedAtUtc;

    public string? BackupKind => Validation.BackupKind;
}

/// <summary><see cref="IRestoreOperations"/> over <see cref="RestoreService"/> in the portable data folder.</summary>
public sealed class FileRestoreOperations : IRestoreOperations
{
    private readonly DataPaths _paths;
    private readonly string _appVersion;

    public FileRestoreOperations(DataPaths paths, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        _paths = paths;
        _appVersion = appVersion;
    }

    public IStagedRestore Validate(string backupFolder, CancellationToken cancellationToken) =>
        new StagedSqliteRestore(RestoreService.Validate(_paths, backupFolder, cancellationToken));

    public void Discard(IStagedRestore staged) => RestoreService.Discard(Unwrap(staged));

    public string? CreateRawSafetyCopy(DateTimeOffset utcNow) => RestoreService.CreateRawSafetyCopy(_paths, utcNow, _appVersion);

    public bool Swap(IStagedRestore staged) => RestoreService.Swap(_paths, Unwrap(staged));

    public InterruptedRestoreOutcome RecoverInterrupted() => RestoreService.RecoverInterrupted(_paths) switch
    {
        RestoreRecoveryOutcome.RolledForward => InterruptedRestoreOutcome.RolledForward,
        RestoreRecoveryOutcome.RolledBack => InterruptedRestoreOutcome.RolledBack,
        _ => InterruptedRestoreOutcome.Unresolved,
    };

    private static RestoreValidation Unwrap(IStagedRestore staged) => staged switch
    {
        StagedSqliteRestore sqlite => sqlite.Validation,
        null => throw new ArgumentNullException(nameof(staged)),
        _ => throw new ArgumentException("The staged restore was not produced by this service.", nameof(staged)),
    };
}
