using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Migrations;

/// <summary>
/// One ordered schema step: embedded SQL and/or C# work, run inside the migration's own transaction.
/// </summary>
/// <param name="Version">The schema version after this step; versions are consecutive from 1.</param>
/// <param name="Apply">Runs synchronously on the writer inside the step's <c>BEGIN IMMEDIATE</c> transaction.</param>
internal sealed record SchemaMigration(int Version, string Name, Action<SqliteConnection, SqliteTransaction, DateTimeOffset> Apply);

internal static class SchemaMigrations
{
    /// <summary>Every migration of this app version, in order. The last one's version is the supported schema.</summary>
    public static IReadOnlyList<SchemaMigration> All { get; } = [M0001InitialSchema.Migration, M0002SingleDesignerDeveloper.Migration];

    public static string ReadEmbeddedSql(string fileName)
    {
        var resource = $"MaxPlanner.Infrastructure.Persistence.Schema.{fileName}";
        using var stream = typeof(SchemaMigrations).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded migration script {resource} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
