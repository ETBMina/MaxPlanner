using System.Text.Json;
using MaxPlanner.Domain.Values;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Migrations;

/// <summary>
/// M0003 (ADR 0025): rewrites CR LF and bare CR to LF in stored multiline text, so stored text matches what the Domain
/// now creates. Plain columns change in SQL. MultilineText custom values (record values and field defaults) live in
/// JSON, where a textual replace could hit an escaped backslash, so they are parsed and written back with the storage
/// serializer. Preserved metadata (pre-link titles, legacy JSON) is source content and is left as it is.
/// </summary>
internal static class M0003LfLineBreaks
{
    private const string MultilineKind = "MultilineText";

    /// <summary>How JSON escapes a carriage return: a backslash followed by <c>r</c>.</summary>
    private const string JsonCrEscape = @"\r";

    public static SchemaMigration Migration { get; } = new(3, "M0003_lf_line_breaks", Apply);

    private static void Apply(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset utcNow)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SchemaMigrations.ReadEmbeddedSql("M0003_lf_line_breaks.sql");
            command.ExecuteNonQuery();
        }

        RewriteCustomValues(connection, transaction, "tasks");
        RewriteCustomValues(connection, transaction, "change_requests");
        RewriteDefaultValues(connection, transaction);
    }

    private static void RewriteCustomValues(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        foreach (var (id, json) in ReadRows(connection, transaction, $"SELECT id, custom_values_json FROM {table} WHERE instr(custom_values_json, $escape) > 0;"))
        {
            var map = JsonSerializer.Deserialize(json, StorageJsonContext.Default.DictionaryStringCustomValueJson)
                ?? throw new CorruptStoredDataException(table, "custom_values_json", "custom values");
            if (map.Values.Any(NeedsRewrite))
            {
                var rewritten = map.ToDictionary(v => v.Key, v => Normalize(v.Value));
                Update(connection, transaction, $"UPDATE {table} SET custom_values_json = $json WHERE id = $id;", id,
                    JsonSerializer.Serialize(rewritten, StorageJsonContext.Default.DictionaryStringCustomValueJson));
            }
        }
    }

    private static void RewriteDefaultValues(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var (id, json) in ReadRows(connection, transaction, "SELECT id, default_value_json FROM field_definitions WHERE instr(default_value_json, $escape) > 0;"))
        {
            var value = JsonSerializer.Deserialize(json, StorageJsonContext.Default.CustomValueJson)
                ?? throw new CorruptStoredDataException("field_definitions", "default_value_json", "default value");
            if (NeedsRewrite(value))
            {
                Update(connection, transaction, "UPDATE field_definitions SET default_value_json = $json WHERE id = $id;", id,
                    JsonSerializer.Serialize(Normalize(value), StorageJsonContext.Default.CustomValueJson));
            }
        }
    }

    private static bool NeedsRewrite(CustomValueJson value) => value.Kind == MultilineKind && value.Text is { } text && text.Contains('\r', StringComparison.Ordinal);

    private static CustomValueJson Normalize(CustomValueJson value) => NeedsRewrite(value) ? value with { Text = LineBreaks.Normalize(value.Text!) } : value;

    // The JSON escape of CR is the two characters \r, so instr() on that escape preselects candidate rows; the
    // typed parse above decides. Rows are read fully before any update runs on the same connection.
    private static List<(byte[] Id, string Json)> ReadRows(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$escape", JsonCrEscape);
        using var reader = command.ExecuteReader();
        var rows = new List<(byte[], string)>();
        while (reader.Read())
        {
            rows.Add(((byte[])reader.GetValue(0), reader.GetString(1)));
        }

        return rows;
    }

    private static void Update(SqliteConnection connection, SqliteTransaction transaction, string sql, byte[] id, string json)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$json", json);
        command.ExecuteNonQuery();
    }
}
