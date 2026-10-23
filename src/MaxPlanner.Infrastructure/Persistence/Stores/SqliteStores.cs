using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Stores;

internal sealed class SqliteSubtaskCardStore(SqliteWriteTransaction tx) : ISubtaskCardStore
{
    private const string Entity = "Subtask card";
    private readonly RevisionGuard _revisions = new(Entity);

    public SubtaskCard? Find(SubtaskCardId id) => Remember(tx.Loader.LoadSubtaskCards("id", id.Value)).SingleOrDefault();

    public void Insert(SubtaskCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        tx.BeforeWrite("subtask-cards.insert");
        var command = tx.Command(
            """
            INSERT INTO subtask_cards (id, parent_task_id, subtitle, status_id, start_date, end_date, notes, rank, created_at_utc, updated_at_utc, revision)
            VALUES ($id, $parent, $subtitle, $status_id, $start_date, $end_date, $notes, $rank, $created_at_utc, $updated_at_utc, $revision);
            """);
        Bind(command, card);
        command.Add("$parent", card.ParentTaskId.Value);
        command.Add("$created_at_utc", card.CreatedAtUtc);
        tx.Execute(command);
        _revisions.Seen(card.Id.Value, card.Revision);
    }

    public void Update(SubtaskCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        tx.BeforeWrite("subtask-cards.update");
        var previous = _revisions.ExpectedPrevious(card.Id.Value, card.Revision);

        // The parent never changes (B-16.1), so it is not part of the update.
        var command = tx.Command(
            """
            UPDATE subtask_cards SET subtitle = $subtitle, status_id = $status_id, start_date = $start_date, end_date = $end_date,
                notes = $notes, rank = $rank, updated_at_utc = $updated_at_utc, revision = $revision
            WHERE id = $id AND revision = $previous;
            """);
        Bind(command, card);
        command.Add("$previous", previous);
        RevisionGuard.EnsureOneRow(tx.Execute(command), Entity, card.Id.Value, "Update");
        _revisions.Seen(card.Id.Value, card.Revision);
    }

    public void Delete(SubtaskCardId id)
    {
        tx.BeforeWrite("subtask-cards.delete");
        var command = tx.Command("DELETE FROM subtask_cards WHERE id = $id;");
        command.Add("$id", id.Value);
        RevisionGuard.EnsureOneRow(tx.Execute(command), Entity, id.Value, "Delete");
        _revisions.Forget(id.Value);
    }

    public IReadOnlyList<SubtaskCard> ListByParent(TaskId parentTaskId) =>
        [.. Remember(tx.Loader.LoadSubtaskCards("parent_task_id", parentTaskId.Value)).OrderBy(c => c.Rank).ThenBy(c => c.Id.Value)];

    public IReadOnlyList<SubtaskCard> ListAll() => Remember(tx.Loader.LoadSubtaskCards());

    private static void Bind(SqliteCommand command, SubtaskCard card)
    {
        command.Add("$id", card.Id.Value);
        command.Add("$subtitle", card.Subtitle.Value);
        command.Add("$status_id", card.StatusId.Value);
        command.Add("$start_date", card.Dates.Start);
        command.Add("$end_date", card.Dates.End);
        command.Add("$notes", card.Notes?.Value);
        command.Add("$rank", card.Rank);
        command.Add("$updated_at_utc", card.UpdatedAtUtc);
        command.Add("$revision", card.Revision);
    }

    private List<SubtaskCard> Remember(List<SubtaskCard> cards)
    {
        foreach (var card in cards)
        {
            _revisions.Seen(card.Id.Value, card.Revision);
        }

        return cards;
    }
}

internal sealed class SqliteChangeRequestStore(SqliteWriteTransaction tx) : IChangeRequestStore
{
    private const string Entity = "CR";
    private readonly RevisionGuard _revisions = new(Entity);

    public ChangeRequest? Find(ChangeRequestId id) => Remember(tx.Loader.LoadChangeRequests("id", id.Value).SingleOrDefault());

    public IReadOnlyList<ChangeRequest> ListAll()
    {
        var all = tx.Loader.LoadChangeRequests();
        foreach (var changeRequest in all)
        {
            Remember(changeRequest);
        }

        return all;
    }

    public ChangeRequest? FindByNormalizedNumber(string normalizedNumber)
    {
        ArgumentNullException.ThrowIfNull(normalizedNumber);
        return Remember(tx.Loader.LoadChangeRequests("number_norm", normalizedNumber).SingleOrDefault());
    }

    public void Insert(ChangeRequest changeRequest)
    {
        ArgumentNullException.ThrowIfNull(changeRequest);
        tx.BeforeWrite("change-requests.insert");
        var command = tx.Command(
            """
            INSERT INTO change_requests (id, number, number_norm, title, owner_id, squad_id, status_id, priority_id, planned_start, planned_end,
                email_subject, solution_design_url, description, task_number, custom_values_json, preserved_json, created_at_utc, updated_at_utc, revision)
            VALUES ($id, $number, $number_norm, $title, $owner_id, $squad_id, $status_id, $priority_id, $planned_start, $planned_end,
                $email_subject, $solution_design_url, $description, $task_number, $custom_values_json, $preserved_json, $created_at_utc, $updated_at_utc, $revision);
            """);
        Bind(command, changeRequest);
        command.Add("$created_at_utc", changeRequest.CreatedAtUtc);
        tx.Execute(command);
        _revisions.Seen(changeRequest.Id.Value, changeRequest.Revision);
    }

    public void Update(ChangeRequest changeRequest)
    {
        ArgumentNullException.ThrowIfNull(changeRequest);
        tx.BeforeWrite("change-requests.update");
        var previous = _revisions.ExpectedPrevious(changeRequest.Id.Value, changeRequest.Revision);
        var command = tx.Command(
            """
            UPDATE change_requests SET number = $number, number_norm = $number_norm, title = $title, owner_id = $owner_id, squad_id = $squad_id,
                status_id = $status_id, priority_id = $priority_id, planned_start = $planned_start, planned_end = $planned_end,
                email_subject = $email_subject, solution_design_url = $solution_design_url, description = $description,
                task_number = $task_number, custom_values_json = $custom_values_json, preserved_json = $preserved_json,
                updated_at_utc = $updated_at_utc, revision = $revision
            WHERE id = $id AND revision = $previous;
            """);
        Bind(command, changeRequest);
        command.Add("$previous", previous);
        RevisionGuard.EnsureOneRow(tx.Execute(command), Entity, changeRequest.Id.Value, "Update");
        _revisions.Seen(changeRequest.Id.Value, changeRequest.Revision);
    }

    public void Delete(ChangeRequestId id)
    {
        tx.BeforeWrite("change-requests.delete");

        // Links are not cascaded: ADR 0007 cascade deletion removes them (and their tasks) explicitly first.
        var command = tx.Command("DELETE FROM change_requests WHERE id = $id;");
        command.Add("$id", id.Value);
        RevisionGuard.EnsureOneRow(tx.Execute(command), Entity, id.Value, "Delete");
        _revisions.Forget(id.Value);
    }

    private static void Bind(SqliteCommand command, ChangeRequest cr)
    {
        command.Add("$id", cr.Id.Value);
        command.Add("$number", cr.Number.Display);
        command.Add("$number_norm", cr.Number.Normalized);
        command.Add("$title", cr.Title.Value);
        command.Add("$owner_id", cr.OwnerId.Value);
        command.Add("$squad_id", cr.SquadId?.Value);
        command.Add("$status_id", cr.StatusId.Value);
        command.Add("$priority_id", cr.PriorityId.Value);
        command.Add("$planned_start", cr.Planned?.Start);
        command.Add("$planned_end", cr.Planned?.End);
        command.Add("$email_subject", cr.EmailSubject?.Value);
        command.Add("$solution_design_url", cr.SolutionDesignUrl?.ToString());
        command.Add("$description", cr.Description?.Value);
        command.Add("$task_number", cr.TaskNumber?.Value);
        command.Add("$custom_values_json", StorageJson.WriteCustomValues(cr.CustomValues));
        command.Add("$preserved_json", StorageJson.WritePreserved(cr.Preserved));
        command.Add("$updated_at_utc", cr.UpdatedAtUtc);
        command.Add("$revision", cr.Revision);
    }

    private ChangeRequest? Remember(ChangeRequest? changeRequest)
    {
        if (changeRequest is not null)
        {
            _revisions.Seen(changeRequest.Id.Value, changeRequest.Revision);
        }

        return changeRequest;
    }
}

internal sealed class SqliteLinkStore(SqliteWriteTransaction tx) : ILinkStore
{
    public CrTaskLink? FindByTask(TaskId taskId) => tx.Loader.LoadLinks(taskId.Value).SingleOrDefault();

    public void Insert(CrTaskLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        tx.BeforeWrite("links.insert");
        var command = tx.Command("INSERT INTO cr_task_links (task_id, cr_id, linked_at_utc) VALUES ($task, $cr, $linked_at_utc);");
        command.Add("$task", link.TaskId.Value);
        command.Add("$cr", link.ChangeRequestId.Value);
        command.Add("$linked_at_utc", link.LinkedAtUtc);
        tx.Execute(command);
    }

    public IReadOnlyList<TaskId> ListTaskIds(ChangeRequestId changeRequestId) =>
        [.. tx.Loader.LoadLinks(changeRequestId.Value, "cr_id").Select(l => l.TaskId).OrderBy(id => id.Value)];

    public void DeleteForTask(TaskId taskId)
    {
        tx.BeforeWrite("links.delete");
        var command = tx.Command("DELETE FROM cr_task_links WHERE task_id = $task;");
        command.Add("$task", taskId.Value);
        RevisionGuard.EnsureOneRow(tx.Execute(command), "link of task", taskId.Value, "Delete");
    }
}

internal sealed class SqliteReviewStore(SqliteWriteTransaction tx) : IReviewStore
{
    private const string Entity = "review item";
    private readonly RevisionGuard _revisions = new(Entity);

    public IReadOnlyList<ReviewItem> ListAll()
    {
        var items = tx.Loader.LoadReviewItems();
        foreach (var item in items)
        {
            _revisions.Seen(item.Id.Value, item.Revision);
        }

        return items;
    }

    public ReviewItem? Find(ReviewItemId id)
    {
        var item = tx.Loader.LoadReviewItems(id.Value).SingleOrDefault();
        if (item is not null)
        {
            _revisions.Seen(id.Value, item.Revision);
        }

        return item;
    }

    public void Insert(ReviewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        tx.BeforeWrite("reviews.insert");
        var command = tx.Command(
            """
            INSERT INTO review_items (id, cr_number_text, title, task_number_text, developer_id, comments, is_done, completed_on, rank,
                created_at_utc, updated_at_utc, revision)
            VALUES ($id, $cr_number_text, $title, $task_number_text, $developer_id, $comments, $is_done, $completed_on, $rank,
                $created_at_utc, $updated_at_utc, $revision);
            """);
        Bind(command, item);
        command.Add("$created_at_utc", item.CreatedAtUtc);
        tx.Execute(command);
        WriteServices(item);
        _revisions.Seen(item.Id.Value, item.Revision);
    }

    public void Update(ReviewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        tx.BeforeWrite("reviews.update");
        var previous = _revisions.ExpectedPrevious(item.Id.Value, item.Revision);
        var command = tx.Command(
            """
            UPDATE review_items SET cr_number_text = $cr_number_text, title = $title, task_number_text = $task_number_text,
                developer_id = $developer_id, comments = $comments, is_done = $is_done, completed_on = $completed_on, rank = $rank,
                updated_at_utc = $updated_at_utc, revision = $revision
            WHERE id = $id AND revision = $previous;
            """);
        Bind(command, item);
        command.Add("$previous", previous);
        RevisionGuard.EnsureOneRow(tx.Execute(command), Entity, item.Id.Value, "Update");

        var delete = tx.Command("DELETE FROM review_services WHERE review_id = $id;");
        delete.Add("$id", item.Id.Value);
        tx.Execute(delete);
        WriteServices(item);
        _revisions.Seen(item.Id.Value, item.Revision);
    }

    public void Delete(ReviewItemId id)
    {
        tx.BeforeWrite("reviews.delete");
        var command = tx.Command("DELETE FROM review_items WHERE id = $id;");
        command.Add("$id", id.Value);
        RevisionGuard.EnsureOneRow(tx.Execute(command), Entity, id.Value, "Delete");
        _revisions.Forget(id.Value);
    }

    public long? MaxRank()
    {
        using var command = tx.Command("SELECT MAX(rank) FROM review_items;");
        return command.ExecuteScalar() is long rank ? rank : null;
    }

    public IReadOnlyList<(ReviewItemId Id, long Rank)> ListOrder()
    {
        using var command = tx.Command("SELECT id, rank FROM review_items;");
        using var reader = command.ExecuteReader();
        var order = new List<(ReviewItemId, long)>();
        while (reader.Read())
        {
            order.Add((new ReviewItemId(reader.GetGuid(0, "review_items", "id")), reader.GetInt64(1)));
        }

        return order;
    }

    private static void Bind(SqliteCommand command, ReviewItem item)
    {
        command.Add("$id", item.Id.Value);
        command.Add("$cr_number_text", item.Content.CrNumberText?.Value);
        command.Add("$title", item.Content.Title.Value);
        command.Add("$task_number_text", item.Content.TaskNumberText?.Value);
        command.Add("$developer_id", item.Content.DeveloperId?.Value);
        command.Add("$comments", item.Content.Comments?.Value);
        command.Add("$is_done", item.IsDone);
        command.Add("$completed_on", item.CompletedOn);
        command.Add("$rank", item.Rank);
        command.Add("$updated_at_utc", item.UpdatedAtUtc);
        command.Add("$revision", item.Revision);
    }

    private void WriteServices(ReviewItem item)
    {
        for (var position = 0; position < item.Content.Services.Count; position++)
        {
            var command = tx.Command("INSERT INTO review_services (review_id, position, service) VALUES ($id, $position, $service);");
            command.Add("$id", item.Id.Value);
            command.Add("$position", position);
            command.Add("$service", item.Content.Services[position].Value);
            tx.Execute(command);
        }
    }
}

internal sealed class SqliteGeneralTodoStore(SqliteWriteTransaction tx) : IGeneralTodoStore
{
    private const string Entity = "General to-do";
    private readonly RevisionGuard _revisions = new(Entity);

    public GeneralTodo? Find(GeneralTodoId id)
    {
        var todo = tx.Loader.LoadGeneralTodos(id.Value).SingleOrDefault();
        if (todo is not null)
        {
            _revisions.Seen(id.Value, todo.Revision);
        }

        return todo;
    }

    public void Insert(GeneralTodo todo)
    {
        ArgumentNullException.ThrowIfNull(todo);
        tx.BeforeWrite("general-todos.insert");
        var command = tx.Command(
            """
            INSERT INTO general_todos (id, text, is_completed, rank, created_at_utc, updated_at_utc, revision)
            VALUES ($id, $text, $is_completed, $rank, $created_at_utc, $updated_at_utc, $revision);
            """);
        Bind(command, todo);
        command.Add("$created_at_utc", todo.CreatedAtUtc);
        tx.Execute(command);
        _revisions.Seen(todo.Id.Value, todo.Revision);
    }

    public void Update(GeneralTodo todo)
    {
        ArgumentNullException.ThrowIfNull(todo);
        tx.BeforeWrite("general-todos.update");
        var previous = _revisions.ExpectedPrevious(todo.Id.Value, todo.Revision);
        var command = tx.Command(
            """
            UPDATE general_todos SET text = $text, is_completed = $is_completed, rank = $rank, updated_at_utc = $updated_at_utc, revision = $revision
            WHERE id = $id AND revision = $previous;
            """);
        Bind(command, todo);
        command.Add("$previous", previous);
        RevisionGuard.EnsureOneRow(tx.Execute(command), Entity, todo.Id.Value, "Update");
        _revisions.Seen(todo.Id.Value, todo.Revision);
    }

    public void Delete(GeneralTodoId id)
    {
        tx.BeforeWrite("general-todos.delete");
        var command = tx.Command("DELETE FROM general_todos WHERE id = $id;");
        command.Add("$id", id.Value);
        RevisionGuard.EnsureOneRow(tx.Execute(command), Entity, id.Value, "Delete");
        _revisions.Forget(id.Value);
    }

    public long? MaxRank()
    {
        using var command = tx.Command("SELECT MAX(rank) FROM general_todos;");
        return command.ExecuteScalar() is long rank ? rank : null;
    }

    public IReadOnlyList<(GeneralTodoId Id, long Rank)> ListOrder()
    {
        using var command = tx.Command("SELECT id, rank FROM general_todos;");
        using var reader = command.ExecuteReader();
        var order = new List<(GeneralTodoId, long)>();
        while (reader.Read())
        {
            order.Add((new GeneralTodoId(reader.GetGuid(0, "general_todos", "id")), reader.GetInt64(1)));
        }

        return order;
    }

    private static void Bind(SqliteCommand command, GeneralTodo todo)
    {
        command.Add("$id", todo.Id.Value);
        command.Add("$text", todo.Text.Value);
        command.Add("$is_completed", todo.IsCompleted);
        command.Add("$rank", todo.Rank);
        command.Add("$updated_at_utc", todo.UpdatedAtUtc);
        command.Add("$revision", todo.Revision);
    }
}

internal sealed class SqliteWorkflowReader(SqliteWriteTransaction tx) : IWorkflowReader
{
    public WorkflowSnapshot Read() => tx.Loader.LoadWorkflow();
}

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
                default_cr_owner_id = $owner, calendar_scheduling_status_id = $scheduling, cr_backlog_status_id = $backlog
            WHERE id = 1;
            """);
        metadata.Add("$task_status", defaults.DefaultTaskStatusId.Value);
        metadata.Add("$cr_status", defaults.DefaultCrStatusId.Value);
        metadata.Add("$priority", defaults.DefaultPriorityId.Value);
        metadata.Add("$owner", defaults.DefaultCrOwnerId?.Value);
        metadata.Add("$scheduling", defaults.CalendarSchedulingStatusId.Value);
        metadata.Add("$backlog", defaults.CrBacklogStatusId.Value);
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

internal sealed class SqliteTombstoneStore(SqliteWriteTransaction tx) : ITombstoneStore
{
    public void Add(EntityKind kind, Guid id, DateTimeOffset deletedAtUtc)
    {
        tx.BeforeWrite("tombstones.insert");

        // A deleted identity stays deleted; recording it twice keeps the first deletion time.
        var command = tx.Command("INSERT OR IGNORE INTO tombstones (entity_kind, id, deleted_at_utc) VALUES ($kind, $id, $deleted_at_utc);");
        command.Add("$kind", StorageNames.Of(kind));
        command.Add("$id", id);
        command.Add("$deleted_at_utc", deletedAtUtc);
        tx.Execute(command);
    }

    public bool Contains(EntityKind kind, Guid id)
    {
        using var command = tx.Command("SELECT EXISTS (SELECT 1 FROM tombstones WHERE entity_kind = $kind AND id = $id);");
        command.Add("$kind", StorageNames.Of(kind));
        command.Add("$id", id);
        return command.ExecuteScalar() is long exists && exists != 0;
    }
}

/// <summary>The B-13.5 <c>import_ledger</c>: insert-only; the primary key refuses a second row for one source identity.</summary>
internal sealed class SqliteImportLedgerStore(SqliteWriteTransaction tx) : IImportLedgerStore
{
    public Guid? Find(string sourceNamespace, string entityKind, string sourceKey)
    {
        using var command = tx.Command("SELECT record_id FROM import_ledger WHERE namespace = $ns AND entity_kind = $kind AND source_key = $key;");
        command.Add("$ns", sourceNamespace);
        command.Add("$kind", entityKind);
        command.Add("$key", sourceKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.GetGuid(0, "import_ledger", "record_id") : null;
    }

    public void Insert(string sourceNamespace, string entityKind, string sourceKey, Guid recordId)
    {
        tx.BeforeWrite("import_ledger.insert");
        var command = tx.Command("INSERT INTO import_ledger (namespace, entity_kind, source_key, record_id) VALUES ($ns, $kind, $key, $record);");
        command.Add("$ns", sourceNamespace);
        command.Add("$kind", entityKind);
        command.Add("$key", sourceKey);
        command.Add("$record", recordId);
        tx.Execute(command);
    }
}
