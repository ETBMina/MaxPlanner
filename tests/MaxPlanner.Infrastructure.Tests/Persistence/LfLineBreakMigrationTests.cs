using System.Text.Json;
using AwesomeAssertions;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Infrastructure.Persistence.Migrations;
using MaxPlanner.Infrastructure.Tests.Transfer;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

/// <summary>
/// ADR 0025 on a real SQLite file: M0003 rewrites CR LF and bare CR to LF in stored multiline text, including
/// MultilineText custom values inside JSON, and leaves revisions, single-line custom text, and preserved source content
/// (pre-link titles, legacy JSON with escaped backslashes) exactly as they were.
/// </summary>
[Trait("Req", "T-09")]
[Trait("Req", "T-05")]
public sealed class LfLineBreakMigrationTests
{
    private const string Mixed = "One\r\nTwo\rThree\nFour";
    private const string Normalized = "One\nTwo\nThree\nFour";

    [Fact]
    public async Task Upgrading_a_v2_dataset_stores_lf_line_breaks_in_every_multiline_column()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var defaulted = db.AddField("summary", FieldKind.MultilineText);
        await db.CloseAsync();
        var customJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [fixture.Estimate.Value.ToString("D")] = new { Kind = "Number", Number = 3.25m },
            [defaulted.Value.ToString("D")] = new { Kind = "MultilineText", Text = Mixed },
        });
        var crCustomJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [defaulted.Value.ToString("D")] = new { Kind = "MultilineText", Text = "CR\r\nvalue" },
        });
        var preservedJson = JsonSerializer.Serialize(new { PreLinkTitle = "Own\rtitle", UnselectedCrReferences = Array.Empty<string>(), LegacyJson = """{"path":"C:\\reports","note":"a\r\nb"}""" });
        var revisions = Revisions(db);

        // A v2 dataset written before ADR 0025: CR LF from files and bare CR from WinUI text boxes.
        Set(db, "tasks", "notes", fixture.LinkedOwnTitle.Value, Mixed);
        Set(db, "tasks", "custom_values_json", fixture.LinkedOwnTitle.Value, customJson);
        Set(db, "tasks", "preserved_json", fixture.Unlinked.Value, preservedJson);
        db.Execute("UPDATE checklist_items SET text = $t WHERE task_id = $id;", ("$t", "Item\r\nline"), ("$id", fixture.LinkedOwnTitle.Value));
        Set(db, "subtask_cards", "notes", fixture.Card.Value, Mixed);
        Set(db, "change_requests", "description", fixture.Cr.Value, Mixed);
        Set(db, "change_requests", "custom_values_json", fixture.Cr.Value, crCustomJson);
        Set(db, "review_items", "comments", fixture.Review.Value, Mixed);
        Set(db, "general_todos", "text", fixture.Todo.Value, "Todo\rline");
        Set(db, "field_definitions", "default_value_json", defaulted.Value, JsonSerializer.Serialize(new { Kind = "MultilineText", Text = "Default\r\ntext" }));
        db.Execute(
            """
            UPDATE app_metadata SET schema_version = 2, migration_history_json = json_remove(migration_history_json, '$[3]', '$[2]');
            ALTER TABLE app_metadata DROP COLUMN copy_id_format;
            PRAGMA user_version = 2;
            """);

        await db.ReopenAsync();

        db.Dataset.SchemaVersion.Should().Be(4);
        Get(db, "tasks", "notes", fixture.LinkedOwnTitle.Value).Should().Be(Normalized);
        Get(db, "subtask_cards", "notes", fixture.Card.Value).Should().Be(Normalized);
        Get(db, "change_requests", "description", fixture.Cr.Value).Should().Be(Normalized);
        Get(db, "review_items", "comments", fixture.Review.Value).Should().Be(Normalized);
        Get(db, "general_todos", "text", fixture.Todo.Value).Should().Be("Todo\nline");
        db.Scalar("SELECT COUNT(*) FROM checklist_items WHERE instr(text, char(13)) > 0;").Should().Be(0);
        db.Scalar("SELECT COUNT(*) FROM checklist_items WHERE text = $t;", ("$t", "Item\nline")).Should().Be(2);
        Get(db, "preserved_json", fixture.Unlinked.Value).Should().Be(preservedJson, "preserved source content is not rewritten");
        Revisions(db).Should().Equal(revisions, "only the line-break form changed, not the content");

        var snapshot = await db.SnapshotAsync();
        var task = snapshot.Tasks.Single(t => t.Id == fixture.LinkedOwnTitle);
        task.CustomValues[defaulted].Should().Be(new MultilineTextCustomValue(Normalized));
        task.CustomValues[fixture.Estimate].Should().Be(new NumberCustomValue(3.25m));
        ((MultilineTextCustomValue)snapshot.ChangeRequests.Single(c => c.Id == fixture.Cr).CustomValues[defaulted]).Value.Should().Be("CR\nvalue");
        Get(db, "tasks", "custom_values_json", fixture.LinkedOwnTitle.Value).Should().NotContain("\\r");
        ((MultilineTextCustomValue)snapshot.Workflow.Fields.Single(f => f.Id == defaulted).DefaultValue!).Value.Should().Be("Default\ntext");
        Get(db, "field_definitions", "default_value_json", defaulted.Value).Should().NotContain("\\r");
    }

    [Fact]
    public async Task A_dataset_without_carriage_returns_upgrades_without_changing_any_row()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        await db.CloseAsync();
        var before = Dump(db);
        db.Execute(
            """
            UPDATE app_metadata SET schema_version = 2, migration_history_json = json_remove(migration_history_json, '$[3]', '$[2]');
            ALTER TABLE app_metadata DROP COLUMN copy_id_format;
            PRAGMA user_version = 2;
            """);

        await db.ReopenAsync();

        db.Dataset.SchemaVersion.Should().Be(4);
        Dump(db).Should().Equal(before);
        (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.LinkedOwnTitle).Notes!.Value.Should().Be(TransferFixture.StoredNotes);
    }

    [Fact]
    public async Task An_escaped_backslash_before_r_in_a_multiline_custom_value_is_not_a_line_break()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var field = db.AddField("path", FieldKind.MultilineText);
        await db.CloseAsync();
        var json = JsonSerializer.Serialize(new Dictionary<string, object> { [field.Value.ToString("D")] = new { Kind = "MultilineText", Text = """C:\reports\raw""" } });
        json.Should().Contain("""\\reports""", "the backslash is stored as the JSON escape \\\\, followed by r");
        Set(db, "tasks", "custom_values_json", fixture.Unlinked.Value, json);
        Set(db, "field_definitions", "default_value_json", field.Value, JsonSerializer.Serialize(new { Kind = "MultilineText", Text = """C:\reports""" }));
        var defaultJson = Get(db, "field_definitions", "default_value_json", field.Value);
        Downgrade(db);

        await db.ReopenAsync();

        Get(db, "tasks", "custom_values_json", fixture.Unlinked.Value).Should().Be(json, "no carriage return, so the row is not rewritten");
        Get(db, "field_definitions", "default_value_json", field.Value).Should().Be(defaultJson);
        ((MultilineTextCustomValue)(await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.Unlinked).CustomValues[field]).Value.Should().Be("""C:\reports\raw""");
    }

    [Fact]
    public async Task Running_the_migration_body_twice_changes_nothing_the_second_time()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var field = db.AddField("summary", FieldKind.MultilineText);
        await db.CloseAsync();
        Set(db, "tasks", "notes", fixture.LinkedOwnTitle.Value, Mixed);
        Set(db, "tasks", "custom_values_json", fixture.LinkedOwnTitle.Value,
            JsonSerializer.Serialize(new Dictionary<string, object> { [field.Value.ToString("D")] = new { Kind = "MultilineText", Text = Mixed } }));
        Set(db, "field_definitions", "default_value_json", field.Value, JsonSerializer.Serialize(new { Kind = "MultilineText", Text = Mixed }));

        RunMigrationBody(db);
        var once = Dump(db);
        RunMigrationBody(db);

        Dump(db).Should().Equal(once);
        Get(db, "tasks", "notes", fixture.LinkedOwnTitle.Value).Should().Be(Normalized);
        Get(db, "field_definitions", "default_value_json", field.Value).Should().NotContain("\\r");
    }

    private static void RunMigrationBody(TestDataset db)
    {
        using var connection = db.OpenRaw();
        using var transaction = connection.BeginTransaction();
        M0003LfLineBreaks.Migration.Apply(connection, transaction, DateTimeOffset.UnixEpoch);
        transaction.Commit();
    }

    private static void Downgrade(TestDataset db) => db.Execute(
        """
        UPDATE app_metadata SET schema_version = 2, migration_history_json = json_remove(migration_history_json, '$[3]', '$[2]');
        ALTER TABLE app_metadata DROP COLUMN copy_id_format;
        PRAGMA user_version = 2;
        """);

    private static void Set(TestDataset db, string table, string column, Guid id, string value) =>
        db.Execute($"UPDATE {table} SET {column} = $v WHERE id = $id;", ("$v", value), ("$id", id));

    private static string? Get(TestDataset db, string table, string column, Guid id) => Text(db, $"SELECT {column} FROM {table} WHERE id = $id;", id);

    private static string? Get(TestDataset db, string column, Guid id) => Get(db, "tasks", column, id);

    private static string? Text(TestDataset db, string sql, Guid id)
    {
        using var connection = db.OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToByteArray());
        return command.ExecuteScalar() as string;
    }

    private static List<long> Revisions(TestDataset db) => Strings(db,
        "SELECT revision FROM tasks UNION ALL SELECT revision FROM change_requests UNION ALL SELECT revision FROM subtask_cards "
        + "UNION ALL SELECT revision FROM review_items UNION ALL SELECT revision FROM general_todos;").Select(long.Parse).ToList();

    private static List<string> Dump(TestDataset db) => Strings(db,
        "SELECT quote(notes) || quote(custom_values_json) || quote(preserved_json) FROM tasks UNION ALL SELECT quote(text) FROM checklist_items "
        + "UNION ALL SELECT quote(notes) FROM subtask_cards UNION ALL SELECT quote(description) || quote(custom_values_json) FROM change_requests "
        + "UNION ALL SELECT quote(comments) FROM review_items UNION ALL SELECT quote(text) FROM general_todos "
        + "UNION ALL SELECT quote(default_value_json) FROM field_definitions "
        + "UNION ALL SELECT quote(operation_id) || quote(kind) || commit_sequence || committed_at_utc || quote(result_json) FROM operation_ledger;");

    private static List<string> Strings(TestDataset db, string sql)
    {
        using var connection = db.OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return values;
    }
}
