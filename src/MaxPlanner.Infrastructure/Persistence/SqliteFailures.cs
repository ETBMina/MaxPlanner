using MaxPlanner.Application.Common;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence;

/// <summary>Maps storage exceptions to the typed failures of T-06.7.</summary>
internal static class SqliteFailures
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteReadOnly = 8;
    private const int SqliteIoError = 10;
    private const int SqliteCorrupt = 11;
    private const int SqliteFull = 13;
    private const int SqliteCantOpen = 14;
    private const int SqliteNotADatabase = 26;

    /// <summary>
    /// The failure kind, or null when the exception is not a storage failure. Constraint violations
    /// return null on purpose: use cases check business rules first, so a violated constraint is a
    /// defect that must propagate, not a condition to report to the user.
    /// </summary>
    public static StorageFailureKind? Classify(Exception exception) => exception switch
    {
        CorruptStoredDataException => StorageFailureKind.Corrupt,
        // Extended result codes carry the primary code in their low byte (e.g. SQLITE_IOERR_WRITE).
        SqliteException sqlite => (sqlite.SqliteErrorCode & 0xFF) switch
        {
            SqliteFull => StorageFailureKind.DiskFull,
            SqliteBusy => StorageFailureKind.Busy,
            SqliteLocked => StorageFailureKind.FileLocked,
            SqliteCorrupt or SqliteNotADatabase => StorageFailureKind.Corrupt,
            SqliteIoError => StorageFailureKind.Io,
            SqliteCantOpen or SqliteReadOnly => StorageFailureKind.Unavailable,
            _ => null,
        },
        _ => null,
    };
}
