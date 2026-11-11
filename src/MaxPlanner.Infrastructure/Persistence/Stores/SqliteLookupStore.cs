using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Stores;

/// <summary>People and tags added by a saved draft (B-02.6; the UNIQUE name_norm column refuses duplicates) and the default CR Owner (B-11).</summary>
internal sealed class SqliteLookupStore(SqliteWriteTransaction tx) : ILookupStore
{
    public void InsertPerson(PersonDefinition person)
    {
        ArgumentNullException.ThrowIfNull(person);
        Insert("INSERT INTO people (id, name, name_norm) VALUES ($id, $name, $norm);", "people.insert", person.Id.Value, person.Name);
    }

    public void InsertTag(TagDefinition tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        Insert("INSERT INTO tags (id, name, name_norm) VALUES ($id, $name, $norm);", "tags.insert", tag.Id.Value, tag.Name);
    }

    public void SetDefaultCrOwner(PersonId ownerId)
    {
        // The app_metadata column has a foreign key to people, so an unknown person is refused by SQLite.
        tx.BeforeWrite("app_metadata.default-cr-owner");
        var command = tx.Command("UPDATE app_metadata SET default_cr_owner_id = $owner WHERE id = 1;");
        command.Add("$owner", ownerId.Value);
        tx.Execute(command);
    }

    public void SaveWorkflow(WorkflowSnapshot workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        tx.BeforeWrite("workflow.save");
        var current = tx.Loader.LoadWorkflow();
        var deletes = new List<(string Table, Guid Id)>();

        // Inserts and updates run first and deletes last: the use case has already moved every record away from
        // a removed value, and the defaults row is rewritten before the delete, so the foreign keys of records
        // and app_metadata never point at a missing row. A remaining reference makes the delete fail and the
        // whole transaction roll back.
        Sync(current.TaskStatuses, workflow.TaskStatuses, s => s.Id.Value, "task_statuses", deletes,
            "INSERT INTO task_statuses (id, name, name_norm, sort_order, is_completed, sets_start_date, is_backlog) VALUES ($id, $name, $norm, $order, $completed, $sets_start, $backlog);",
            "UPDATE task_statuses SET name = $name, name_norm = $norm, sort_order = $order, is_completed = $completed, sets_start_date = $sets_start, is_backlog = $backlog WHERE id = $id;",
            (c, s) =>
            {
                BindName(c, s.Id.Value, s.Name, s.Order);
                c.Add("$completed", s.IsCompleted);
                c.Add("$sets_start", s.SetsStartDate);
                c.Add("$backlog", s.IsBacklog);
            });
        Sync(current.CrStatuses, workflow.CrStatuses, s => s.Id.Value, "cr_statuses", deletes,
            "INSERT INTO cr_statuses (id, name, name_norm, sort_order, is_completed, is_backlog) VALUES ($id, $name, $norm, $order, $completed, $backlog);",
            "UPDATE cr_statuses SET name = $name, name_norm = $norm, sort_order = $order, is_completed = $completed, is_backlog = $backlog WHERE id = $id;",
            (c, s) =>
            {
                BindName(c, s.Id.Value, s.Name, s.Order);
                c.Add("$completed", s.IsCompleted);
                c.Add("$backlog", s.IsBacklog);
            });
        Sync(current.Priorities, workflow.Priorities, p => p.Id.Value, "priorities", deletes,
            "INSERT INTO priorities (id, name, name_norm, sort_order) VALUES ($id, $name, $norm, $order);",
            "UPDATE priorities SET name = $name, name_norm = $norm, sort_order = $order WHERE id = $id;",
            (c, p) => BindName(c, p.Id.Value, p.Name, p.Order));
        Sync(current.Squads, workflow.Squads, s => s.Id.Value, "squads", deletes,
            "INSERT INTO squads (id, name, name_norm, sort_order) VALUES ($id, $name, $norm, $order);",
            "UPDATE squads SET name = $name, name_norm = $norm, sort_order = $order WHERE id = $id;",
            (c, s) => BindName(c, s.Id.Value, s.Name, s.Order));
        Sync(current.People, workflow.People, p => p.Id.Value, "people", deletes,
            "INSERT INTO people (id, name, name_norm) VALUES ($id, $name, $norm);",
            "UPDATE people SET name = $name, name_norm = $norm WHERE id = $id;",
            (c, p) => BindName(c, p.Id.Value, p.Name, null));
        Sync(current.Tags, workflow.Tags, t => t.Id.Value, "tags", deletes,
            "INSERT INTO tags (id, name, name_norm) VALUES ($id, $name, $norm);",
            "UPDATE tags SET name = $name, name_norm = $norm WHERE id = $id;",
            (c, t) => BindName(c, t.Id.Value, t.Name, null));

        // B-11: definitions are deactivated, never deleted, so stored values keep their definition.
        var fieldDeletes = new List<(string Table, Guid Id)>();
        Sync(current.Fields, workflow.Fields, f => f.Id.Value, "field_definitions", fieldDeletes,
            """
            INSERT INTO field_definitions (id, target, field_key, key_norm, label, kind, is_required, default_value_json, display_order, options_json, is_active)
            VALUES ($id, $target, $key, $key_norm, $label, $kind, $required, $default, $order, $options, $active);
            """,
            """
            UPDATE field_definitions SET label = $label, is_required = $required, default_value_json = $default, display_order = $order,
                options_json = $options, is_active = $active
            WHERE id = $id AND target = $target AND field_key = $key AND key_norm = $key_norm AND kind = $kind;
            """,
            BindField);
        if (fieldDeletes.Count > 0)
        {
            throw new InvalidOperationException("Field definitions are deactivated, never deleted (B-11).");
        }

        var defaults = workflow.Defaults;
        var metadata = tx.Command(
            """
            UPDATE app_metadata SET default_task_status_id = $task_status, default_cr_status_id = $cr_status, default_priority_id = $priority,
                default_cr_owner_id = $owner, calendar_scheduling_status_id = $scheduling, cr_backlog_status_id = $backlog,
                copy_id_format = $copy_id
            WHERE id = 1;
            """);
        metadata.Add("$task_status", defaults.DefaultTaskStatusId.Value);
        metadata.Add("$cr_status", defaults.DefaultCrStatusId.Value);
        metadata.Add("$priority", defaults.DefaultPriorityId.Value);
        metadata.Add("$owner", defaults.DefaultCrOwnerId?.Value);
        metadata.Add("$scheduling", defaults.CalendarSchedulingStatusId.Value);
        metadata.Add("$backlog", defaults.CrBacklogStatusId.Value);
        metadata.Add("$copy_id", defaults.CopyIdFormat == CopyIdFormatter.Default ? null : defaults.CopyIdFormat);
        RevisionGuard.EnsureOneRow(tx.Execute(metadata), "workspace defaults", Guid.Empty, "Update");

        foreach (var (table, id) in deletes)
        {
            // The table name is one of the constants above; only the ID is a parameter.
            var command = tx.Command($"DELETE FROM {table} WHERE id = $id;");
            command.Add("$id", id);
            RevisionGuard.EnsureOneRow(tx.Execute(command), table, id, "Delete");
        }
    }

    private static void BindName(SqliteCommand command, Guid id, LookupName name, int? order)
    {
        command.Add("$id", id);
        command.Add("$name", name.Display);
        command.Add("$norm", name.Normalized);
        if (order is { } value)
        {
            command.Add("$order", value);
        }
    }

    private static void BindField(SqliteCommand command, FieldDefinition field)
    {
        command.Add("$id", field.Id.Value);
        command.Add("$target", StorageNames.Of(field.Target));
        command.Add("$key", field.Key.Display);
        command.Add("$key_norm", field.Key.Normalized);
        command.Add("$label", field.Label.Display);
        command.Add("$kind", StorageNames.Of(field.Kind));
        command.Add("$required", field.IsRequired);
        command.Add("$default", StorageJson.WriteDefaultValue(field.DefaultValue));
        command.Add("$order", field.DisplayOrder);
        command.Add("$options", StorageJson.WriteOptions(field.Options));
        command.Add("$active", field.IsActive);
    }

    private void Sync<T>(
        IReadOnlyList<T> current,
        IReadOnlyList<T> target,
        Func<T, Guid> idOf,
        string table,
        List<(string Table, Guid Id)> deletes,
        string insertSql,
        string updateSql,
        Action<SqliteCommand, T> bind)
    {
        var existing = current.ToDictionary(idOf);
        foreach (var item in target)
        {
            if (!existing.Remove(idOf(item), out var stored))
            {
                var insert = tx.Command(insertSql);
                bind(insert, item);
                tx.Execute(insert);
            }
            else if (!Equals(stored, item))
            {
                var update = tx.Command(updateSql);
                bind(update, item);
                RevisionGuard.EnsureOneRow(tx.Execute(update), table, idOf(item), "Update");
            }
        }

        deletes.AddRange(existing.Keys.Select(id => (table, id)));
    }

    private void Insert(string sql, string write, Guid id, LookupName name)
    {
        tx.BeforeWrite(write);
        var command = tx.Command(sql);
        command.Add("$id", id);
        command.Add("$name", name.Display);
        command.Add("$norm", name.Normalized);
        tx.Execute(command);
    }
}
