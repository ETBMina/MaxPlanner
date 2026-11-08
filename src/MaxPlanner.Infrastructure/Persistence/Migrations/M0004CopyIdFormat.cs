using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Migrations;

/// <summary>M0004 (design §4.6): adds the Copy ID format to the workspace defaults; existing datasets keep the default.</summary>
internal static class M0004CopyIdFormat
{
    public static SchemaMigration Migration { get; } = new(4, "M0004_copy_id_format", Apply);

    private static void Apply(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset utcNow)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaMigrations.ReadEmbeddedSql("M0004_copy_id_format.sql");
        command.ExecuteNonQuery();
    }
}
