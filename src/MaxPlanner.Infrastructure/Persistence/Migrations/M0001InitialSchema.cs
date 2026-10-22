using MaxPlanner.Infrastructure.Persistence.Mapping;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Migrations;

/// <summary>
/// M0001: creates schema v1 and seeds the B-04 design defaults. Seed IDs are new per dataset; they are
/// stable afterwards because every record refers to lookups by ID, never by label.
/// </summary>
internal static class M0001InitialSchema
{
    public static SchemaMigration Migration { get; } = new(1, "M0001_initial", Apply);

    private static void Apply(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset utcNow)
    {
        Execute(connection, transaction, SchemaMigrations.ReadEmbeddedSql("M0001_initial.sql"));

        // B-04 task statuses in configured order, with their semantic flags.
        var taskStatuses = new (string Name, bool IsCompleted, bool SetsStartDate, bool IsBacklog)[]
        {
            ("Backlog", false, false, true),
            ("Blocked", false, false, false),
            ("In Progress", false, true, false),
            ("Review", false, false, false),
            ("Unit Testing", false, false, false),
            ("Completed", true, false, false),
            ("Designing", false, true, false),
            ("Developing", false, true, false),
        };
        var taskStatusIds = new Dictionary<string, Guid>();
        for (var order = 0; order < taskStatuses.Length; order++)
        {
            var (name, isCompleted, setsStart, isBacklog) = taskStatuses[order];
            taskStatusIds[name] = Insert(
                connection,
                transaction,
                "INSERT INTO task_statuses (id, name, name_norm, sort_order, is_completed, sets_start_date, is_backlog) VALUES ($id, $name, $norm, $order, $completed, $sets_start, $backlog);",
                name,
                order,
                c =>
                {
                    c.Add("$completed", isCompleted);
                    c.Add("$sets_start", setsStart);
                    c.Add("$backlog", isBacklog);
                });
        }

        var crStatuses = new (string Name, bool IsCompleted, bool IsBacklog)[] { ("Backlog", false, true), ("In Progress", false, false), ("Completed", true, false) };
        var crStatusIds = new Dictionary<string, Guid>();
        for (var order = 0; order < crStatuses.Length; order++)
        {
            var (name, isCompleted, isBacklog) = crStatuses[order];
            crStatusIds[name] = Insert(
                connection,
                transaction,
                "INSERT INTO cr_statuses (id, name, name_norm, sort_order, is_completed, is_backlog) VALUES ($id, $name, $norm, $order, $completed, $backlog);",
                name,
                order,
                c =>
                {
                    c.Add("$completed", isCompleted);
                    c.Add("$backlog", isBacklog);
                });
        }

        // Highest priority first (B-04).
        var priorityIds = new Dictionary<string, Guid>();
        string[] priorities = ["Urgent", "High", "Medium", "Low"];
        for (var order = 0; order < priorities.Length; order++)
        {
            priorityIds[priorities[order]] = Insert(
                connection, transaction, "INSERT INTO priorities (id, name, name_norm, sort_order) VALUES ($id, $name, $norm, $order);", priorities[order], order, null);
        }

        string[] squads = ["Retail Finance", "Retail Cards"];
        for (var order = 0; order < squads.Length; order++)
        {
            Insert(connection, transaction, "INSERT INTO squads (id, name, name_norm, sort_order) VALUES ($id, $name, $norm, $order);", squads[order], order, null);
        }

        // People and tags start empty and the default CR Owner is unset until the user chooses one (B-04).
        // schema_version starts at 0; the migration runner records version 1 and the history entry.
        using var metadata = Command(
            connection,
            transaction,
            """
            INSERT INTO app_metadata (id, schema_version, dataset_id, commit_sequence, default_task_status_id, default_cr_status_id,
                default_priority_id, default_cr_owner_id, calendar_scheduling_status_id, cr_backlog_status_id, migration_history_json)
            VALUES (1, 0, $dataset, 0, $task_status, $cr_status, $priority, NULL, $scheduling, $backlog, '[]');
            """);
        metadata.Add("$dataset", Guid.NewGuid());
        metadata.Add("$task_status", taskStatusIds["Backlog"]);
        metadata.Add("$cr_status", crStatusIds["Backlog"]);
        metadata.Add("$priority", priorityIds["Medium"]);
        metadata.Add("$scheduling", crStatusIds["In Progress"]);
        metadata.Add("$backlog", crStatusIds["Backlog"]);
        metadata.ExecuteNonQuery();
    }

    private static Guid Insert(SqliteConnection connection, SqliteTransaction transaction, string sql, string name, int order, Action<SqliteCommand>? bind)
    {
        var id = Guid.NewGuid();
        using var command = Command(connection, transaction, sql);
        command.Add("$id", id);
        command.Add("$name", name);
        command.Add("$norm", name.ToUpperInvariant());
        command.Add("$order", order);
        bind?.Invoke(command);
        command.ExecuteNonQuery();
        return id;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
