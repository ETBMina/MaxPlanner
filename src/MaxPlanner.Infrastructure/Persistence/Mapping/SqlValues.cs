using System.Globalization;
using MaxPlanner.Domain.Common;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Mapping;

/// <summary>
/// The column encodings of schema v1 (ADR 0009, docs/schemas/sqlite-schema-v1.md).
/// </summary>
/// <remarks>
/// <para>GUIDs are 16-byte BLOBs in <see cref="Guid.ToByteArray()"/> order (the little-endian
/// "Microsoft" layout, read back with <c>new Guid(bytes)</c>). Microsoft.Data.Sqlite would bind a
/// <see cref="Guid"/> as TEXT by default, so IDs are always bound through these helpers, and the STRICT
/// tables reject a TEXT ID instead of storing a second encoding. SQLite
/// compares BLOBs byte by byte, which does not match <see cref="Guid.CompareTo(Guid)"/>, so any ordering
/// that uses the ID as a tie-breaker is done in C# after reading, never with <c>ORDER BY id</c>.</para>
/// <para>Business dates are invariant <c>yyyy-MM-dd</c> TEXT and never pass through UTC (T-05). UTC
/// timestamps are INTEGER <see cref="DateTimeOffset.UtcTicks"/> and read back with a zero offset.</para>
/// </remarks>
internal static class SqlValues
{
    public const string DateFormat = "yyyy-MM-dd";

    public static string FormatDate(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static SqliteParameter Add(this SqliteCommand command, string name, Guid value) =>
        command.Parameters.Add(name, SqliteType.Blob).With(value.ToByteArray());

    public static SqliteParameter Add(this SqliteCommand command, string name, Guid? value) =>
        command.Parameters.Add(name, SqliteType.Blob).With(value?.ToByteArray());

    public static SqliteParameter Add(this SqliteCommand command, string name, string? value) =>
        command.Parameters.Add(name, SqliteType.Text).With(value);

    public static SqliteParameter Add(this SqliteCommand command, string name, long value) =>
        command.Parameters.Add(name, SqliteType.Integer).With(value);

    public static SqliteParameter Add(this SqliteCommand command, string name, bool value) =>
        command.Parameters.Add(name, SqliteType.Integer).With(value ? 1L : 0L);

    public static SqliteParameter Add(this SqliteCommand command, string name, DateOnly? value) =>
        command.Parameters.Add(name, SqliteType.Text).With(value is { } date ? FormatDate(date) : null);

    public static SqliteParameter Add(this SqliteCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(name, SqliteType.Integer).With(value.UtcTicks);

    public static Guid GetGuid(this SqliteDataReader reader, int ordinal, string table, string column)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw new CorruptStoredDataException(table, column, "missing ID");
        }

        var bytes = reader.GetFieldValue<byte[]>(ordinal);
        return bytes.Length == 16 ? new Guid(bytes) : throw new CorruptStoredDataException(table, column, "ID is not 16 bytes");
    }

    public static Guid? GetNullableGuid(this SqliteDataReader reader, int ordinal, string table, string column) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal, table, column);

    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static bool GetFlag(this SqliteDataReader reader, int ordinal) => reader.GetInt64(ordinal) != 0;

    public static DateTimeOffset GetUtc(this SqliteDataReader reader, int ordinal, string table, string column)
    {
        var ticks = reader.GetInt64(ordinal);
        return ticks >= 0 && ticks <= DateTime.MaxValue.Ticks
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : throw new CorruptStoredDataException(table, column, "timestamp out of range");
    }

    public static DateOnly? GetDate(this SqliteDataReader reader, int ordinal, string table, string column)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateOnly.TryParseExact(reader.GetString(ordinal), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new CorruptStoredDataException(table, column, "not a yyyy-MM-dd date");
    }

    /// <summary>Unwraps a domain factory result for a stored value; a rule violation means the row is corrupt.</summary>
    public static T Require<T>(DomainResult<T> result, string table, string column) =>
        result.IsSuccess ? result.Value : throw new CorruptStoredDataException(table, column, result.Error!.Code);

    private static SqliteParameter With(this SqliteParameter parameter, object? value)
    {
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }
}
