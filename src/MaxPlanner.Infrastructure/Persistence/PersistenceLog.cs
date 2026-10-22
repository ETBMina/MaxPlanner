using MaxPlanner.Application.Common;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Infrastructure.Persistence;

// T-09 logging: operation IDs, kinds, versions, and failure categories only; never record content.
internal static partial class PersistenceLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Applied schema migration {Version} ({Name}).")]
    public static partial void MigrationApplied(ILogger logger, int version, string name);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Schema migration {Version} failed; the dataset stays at version {CurrentVersion}.")]
    public static partial void MigrationFailed(ILogger logger, int version, int currentVersion, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error, Message = "Could not take the migration safety backup of schema version {Version}; nothing was migrated.")]
    public static partial void SafetyBackupFailed(ILogger logger, int version, Exception exception);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error, Message = "Opening the dataset failed with storage failure {Kind}.")]
    public static partial void OpenFailed(ILogger logger, StorageFailureKind kind, Exception exception);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error, Message = "Operation {OperationId} ({OperationKind}) rolled back after storage failure {Kind}.")]
    public static partial void TransactionFailed(ILogger logger, Guid operationId, string operationKind, StorageFailureKind kind, Exception exception);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Error, Message = "Operation {OperationId} ({OperationKind}) threw a non-storage exception and was rolled back.")]
    public static partial void TransactionDefect(ILogger logger, Guid operationId, string operationKind, Exception exception);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Critical, Message = "COMMIT of operation {OperationId} ({OperationKind}) failed; whether it committed is unknown until the ledger is queried.")]
    public static partial void CommitOutcomeUnknown(ILogger logger, Guid operationId, string operationKind, Exception exception);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Warning, Message = "Rollback after a failed operation also failed.")]
    public static partial void RollbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Error, Message = "A change-feed subscriber failed on sequence {Sequence}; it was unsubscribed and must resynchronize.")]
    public static partial void SubscriberFaulted(ILogger logger, long sequence, Exception exception);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "Closing the writer (WAL checkpoint) failed.")]
    public static partial void CloseFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Error, Message = "Loading a read snapshot failed with storage failure {Kind}.")]
    public static partial void SnapshotFailed(ILogger logger, StorageFailureKind kind, Exception exception);
}
