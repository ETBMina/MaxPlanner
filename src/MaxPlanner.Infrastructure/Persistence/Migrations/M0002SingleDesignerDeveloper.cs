using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Migrations;

/// <summary>
/// M0002 (ADR 0024): keeps, for each task and role, the first Designer or Developer by position and deletes the
/// others, then allows at most one person per task and role with a unique index.
/// </summary>
internal static class M0002SingleDesignerDeveloper
{
    public static SchemaMigration Migration { get; } = new(2, "M0002_single_designer_developer", Apply);

    private static void Apply(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset utcNow)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaMigrations.ReadEmbeddedSql("M0002_single_designer_developer.sql");
        command.ExecuteNonQuery();
    }
}
