namespace MaxPlanner.Infrastructure.Persistence;

/// <summary>
/// A stored row breaks a structural rule (wrong ID size, invalid date, a domain <c>Rehydrate</c> failure).
/// Translated to <c>StorageFailureError(Corrupt)</c>; the row is reported, never skipped or repaired (T-09).
/// </summary>
/// <remarks>The message names the table, column, and rule only, never the stored content (T-09 logging).</remarks>
public sealed class CorruptStoredDataException : Exception
{
    public CorruptStoredDataException()
    {
    }

    public CorruptStoredDataException(string message)
        : base(message)
    {
    }

    public CorruptStoredDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CorruptStoredDataException(string table, string column, string rule)
        : base($"Stored data in {table}.{column} is invalid ({rule}).")
    {
    }
}
