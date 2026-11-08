using AwesomeAssertions;
using MaxPlanner.Application.Settings;
using MaxPlanner.Infrastructure.Persistence;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

/// <summary>M0004 (design §4.6): a v3 dataset gains the Copy ID format column and reads the default format.</summary>
public sealed class CopyIdFormatMigrationTests
{
    [Fact]
    public async Task Upgrading_a_v3_dataset_adds_the_column_with_the_default_format()
    {
        await using var db = await TestDataset.CreateAsync();
        await db.CloseAsync();
        db.Execute(
            """
            UPDATE app_metadata SET schema_version = 3, migration_history_json = json_remove(migration_history_json, '$[3]');
            ALTER TABLE app_metadata DROP COLUMN copy_id_format;
            PRAGMA user_version = 3;
            """);

        await db.ReopenAsync();

        db.Dataset.SchemaVersion.Should().Be(SqliteDataset.SupportedSchemaVersion);
        db.Scalar("SELECT COUNT(*) FROM app_metadata WHERE copy_id_format IS NULL;").Should().Be(1);
        (await db.SnapshotAsync()).Workflow.Defaults.CopyIdFormat.Should().Be(CopyIdFormatter.Default);
    }
}
