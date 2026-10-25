using MaxPlanner.Application.Abstractions;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Stores;

internal sealed class SqliteTaskStore(SqliteWriteTransaction tx) : ITaskStore
{
    private const string Entity = "task";

    // B-07/B-16.5: a Board column holds the active tasks and the Subtask cards of active parents.
    private const string BoardColumnSql = """
        SELECT 0 AS kind, id, rank FROM tasks WHERE status_id = $status AND is_archived = 0
        UNION ALL
        SELECT 1 AS kind, c.id, c.rank FROM subtask_cards c JOIN tasks p ON p.id = c.parent_task_id
        WHERE c.status_id = $status AND p.is_archived = 0
        """;

    private readonly RevisionGuard _revisions = new(Entity);

    public TaskItem? Find(TaskId id)
    {
        var task = tx.Loader.LoadTasks(id.Value).SingleOrDefault();
        if (task is not null)
        {
            _revisions.Seen(id.Value, task.Revision);
        }

        return task;
    }

    public IReadOnlyList<TaskItem> ListAll()
    {
        var tasks = tx.Loader.LoadTasks();
        foreach (var task in tasks)
        {
            _revisions.Seen(task.Id.Value, task.Revision);
        }

        return tasks;
    }

    public void Insert(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);
        tx.BeforeWrite("tasks.insert");
        var command = tx.Command(
            """
            INSERT INTO tasks (id, own_title, service, status_id, priority_id, start_date, end_date, notes, is_archived, rank,
                               custom_values_json, preserved_json, created_at_utc, updated_at_utc, revision)
            VALUES ($id, $own_title, $service, $status_id, $priority_id, $start_date, $end_date, $notes, $is_archived, $rank,
                    $custom_values_json, $preserved_json, $created_at_utc, $updated_at_utc, $revision);
            """);
        BindRow(command, task);
        command.Add("$created_at_utc", task.CreatedAtUtc);
        tx.Execute(command);

        foreach (var item in task.ChecklistItems)
        {
            InsertChecklistItem(task.Id, item);
        }

        WriteAssignments(task);
        _revisions.Seen(task.Id.Value, task.Revision);
    }

    public void Update(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);
        tx.BeforeWrite("tasks.update");
        var previous = _revisions.ExpectedPrevious(task.Id.Value, task.Revision);
        var command = tx.Command(
            """
            UPDATE tasks SET own_title = $own_title, service = $service, status_id = $status_id, priority_id = $priority_id,
                start_date = $start_date, end_date = $end_date, notes = $notes, is_archived = $is_archived, rank = $rank,
                custom_values_json = $custom_values_json, preserved_json = $preserved_json,
                updated_at_utc = $updated_at_utc, revision = $revision
            WHERE id = $id AND revision = $previous;
            """);
        BindRow(command, task);
        command.Add("$previous", previous);
        RevisionGuard.EnsureOneRow(tx.Execute(command), Entity, task.Id.Value, "Update");

        SyncChecklist(task);
        if (!ReadAssignments(task.Id).SequenceEqual(Assignments(task)))
        {
            DeleteAssignments(task.Id);
            WriteAssignments(task);
        }

        _revisions.Seen(task.Id.Value, task.Revision);
    }

    public void Delete(TaskId id)
    {
        tx.BeforeWrite("tasks.delete");

        // Checklist items and people/tag rows cascade. The link and Subtask cards do not: the use case
        // deletes them first, and the foreign keys reject a delete that forgot them (B-05).
        var command = tx.Command("DELETE FROM tasks WHERE id = $id;");
        command.Add("$id", id.Value);
        RevisionGuard.EnsureOneRow(tx.Execute(command), Entity, id.Value, "Delete");
        _revisions.Forget(id.Value);
    }

    public long? MaxBoardRank(TaskStatusId status)
    {
        using var command = tx.Command($"SELECT MAX(rank) FROM ({BoardColumnSql});");
        command.Add("$status", status.Value);
        return command.ExecuteScalar() is long rank ? rank : null;
    }

    public long? MinBoardRank(TaskStatusId status)
    {
        using var command = tx.Command($"SELECT MIN(rank) FROM ({BoardColumnSql});");
        command.Add("$status", status.Value);
        return command.ExecuteScalar() is long rank ? rank : null;
    }

    public IReadOnlyList<(BoardCardKey Key, long Rank)> ListBoardColumn(TaskStatusId status)
    {
        var cards = new List<(BoardCardKey Key, long Rank)>();
        using (var command = tx.Command(BoardColumnSql + ";"))
        {
            command.Add("$status", status.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var kind = reader.GetInt64(0) == 0 ? BoardCardKind.Task : BoardCardKind.SubtaskCard;
                cards.Add((new BoardCardKey(kind, reader.GetGuid(1, "tasks", "id")), reader.GetInt64(2)));
            }
        }

        // B-07: ties on rank are broken by the ID using .NET Guid ordering. SQLite compares the stored BLOBs
        // byte by byte, which orders differently, so sorting there would swap tied cards when renumbering.
        return [.. cards.OrderBy(c => c.Rank).ThenBy(c => c.Key.Id)];
    }

    private static void BindRow(SqliteCommand command, TaskItem task)
    {
        command.Add("$id", task.Id.Value);
        command.Add("$own_title", task.OwnTitle?.Value);
        command.Add("$service", task.Service?.Value);
        command.Add("$status_id", task.StatusId.Value);
        command.Add("$priority_id", task.PriorityId.Value);
        command.Add("$start_date", task.Dates.Start);
        command.Add("$end_date", task.Dates.End);
        command.Add("$notes", task.Notes?.Value);
        command.Add("$is_archived", task.IsArchived);
        command.Add("$rank", task.Rank);
        command.Add("$custom_values_json", StorageJson.WriteCustomValues(task.CustomValues));
        command.Add("$preserved_json", StorageJson.WritePreserved(task.Preserved));
        command.Add("$updated_at_utc", task.UpdatedAtUtc);
        command.Add("$revision", task.Revision);
    }

    private static IEnumerable<(string Table, string Role, Guid Id)> Assignments(TaskItem task) =>
        task.DesignerIds.Select(p => ("task_people", StorageNames.Designer, p.Value))
            .Concat(task.DeveloperIds.Select(p => ("task_people", StorageNames.Developer, p.Value)))
            .Concat(task.TagIds.Select(t => ("task_tags", string.Empty, t.Value)));

    private List<(string Table, string Role, Guid Id)> ReadAssignments(TaskId taskId)
    {
        var rows = new List<(string, string, Guid)>();
        using var command = tx.Command(
            """
            SELECT 'task_people', role, person_id FROM task_people WHERE task_id = $task
            ORDER BY CASE role WHEN 'Designer' THEN 0 ELSE 1 END, position;
            """);
        command.Add("$task", taskId.Value);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetGuid(2, "task_people", "person_id")));
            }
        }

        using var tags = tx.Command("SELECT tag_id FROM task_tags WHERE task_id = $task ORDER BY position;");
        tags.Add("$task", taskId.Value);
        using var tagReader = tags.ExecuteReader();
        while (tagReader.Read())
        {
            rows.Add(("task_tags", string.Empty, tagReader.GetGuid(0, "task_tags", "tag_id")));
        }

        return rows;
    }

    private void WriteAssignments(TaskItem task)
    {
        var position = 0L;
        foreach (var (table, role, id) in Assignments(task))
        {
            var command = table == "task_people"
                ? tx.Command("INSERT INTO task_people (task_id, person_id, role, position) VALUES ($task, $id, $role, $position);")
                : tx.Command("INSERT INTO task_tags (task_id, tag_id, position) VALUES ($task, $id, $position);");
            command.Add("$task", task.Id.Value);
            command.Add("$id", id);
            command.Add("$position", position++);
            if (table == "task_people")
            {
                command.Add("$role", role);
            }

            tx.Execute(command);
        }
    }

    private void DeleteAssignments(TaskId taskId)
    {
        foreach (var sql in new[] { "DELETE FROM task_people WHERE task_id = $task;", "DELETE FROM task_tags WHERE task_id = $task;" })
        {
            var command = tx.Command(sql);
            command.Add("$task", taskId.Value);
            tx.Execute(command);
        }
    }

    /// <summary>Writes only the checklist rows that changed, so toggling one item never rewrites the whole list.</summary>
    private void SyncChecklist(TaskItem task)
    {
        var stored = new Dictionary<Guid, (string Text, bool IsCompleted, long Rank)>();
        using (var command = tx.Command("SELECT id, text, is_completed, rank FROM checklist_items WHERE task_id = $task;"))
        {
            command.Add("$task", task.Id.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                stored[reader.GetGuid(0, "checklist_items", "id")] = (reader.GetString(1), reader.GetFlag(2), reader.GetInt64(3));
            }
        }

        foreach (var item in task.ChecklistItems)
        {
            if (!stored.Remove(item.Id.Value, out var row))
            {
                InsertChecklistItem(task.Id, item);
            }
            else if (row != (item.Text.Value, item.IsCompleted, item.Rank))
            {
                var update = tx.Command("UPDATE checklist_items SET text = $text, is_completed = $is_completed, rank = $rank WHERE id = $id AND task_id = $task;");
                BindChecklistItem(update, task.Id, item);
                RevisionGuard.EnsureOneRow(tx.Execute(update), "checklist item", item.Id.Value, "Update");
            }
        }

        foreach (var removed in stored.Keys)
        {
            var delete = tx.Command("DELETE FROM checklist_items WHERE id = $id AND task_id = $task;");
            delete.Add("$id", removed);
            delete.Add("$task", task.Id.Value);
            tx.Execute(delete);
        }
    }

    private void InsertChecklistItem(TaskId taskId, ChecklistItem item)
    {
        var command = tx.Command("INSERT INTO checklist_items (id, task_id, text, is_completed, rank) VALUES ($id, $task, $text, $is_completed, $rank);");
        BindChecklistItem(command, taskId, item);
        tx.Execute(command);
    }

    private static void BindChecklistItem(SqliteCommand command, TaskId taskId, ChecklistItem item)
    {
        command.Add("$id", item.Id.Value);
        command.Add("$task", taskId.Value);
        command.Add("$text", item.Text.Value);
        command.Add("$is_completed", item.IsCompleted);
        command.Add("$rank", item.Rank);
    }
}
