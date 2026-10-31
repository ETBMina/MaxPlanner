using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;
using Microsoft.Data.Sqlite;
using static MaxPlanner.Infrastructure.Persistence.Mapping.SqlValues;

namespace MaxPlanner.Infrastructure.Persistence.Mapping;

/// <summary>
/// Reads stored rows into domain records through their <c>Rehydrate</c> factories, for both the write
/// stores (one record) and the read snapshot (every record). Results are fully materialized (T-06.4).
/// </summary>
/// <remarks>
/// A row that breaks a structural rule throws <see cref="CorruptStoredDataException"/>; it is never
/// skipped. Filters name a fixed column of this class and bind the value as a parameter (T-06.4).
/// </remarks>
internal sealed class RecordLoader(SqliteConnection connection, SqliteTransaction? transaction)
{
    public SqliteCommand Command(string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    public long ReadCommitSequence()
    {
        using var command = Command("SELECT commit_sequence FROM app_metadata WHERE id = 1;");
        return command.ExecuteScalar() is long sequence ? sequence : throw new CorruptStoredDataException("app_metadata", "commit_sequence", "row missing");
    }

    /// <param name="taskId">Only this task; null loads every task, archived included.</param>
    public List<TaskItem> LoadTasks(Guid? taskId = null)
    {
        var checklists = new Dictionary<Guid, List<ChecklistItem>>();
        using (var command = Filtered("SELECT task_id, id, text, is_completed, rank FROM checklist_items", "task_id", taskId))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var item = new ChecklistItem(
                    new ChecklistItemId(reader.GetGuid(1, "checklist_items", "id")),
                    Require(ChecklistText.Create(reader.GetString(2)), "checklist_items", "text"),
                    reader.GetFlag(3),
                    reader.GetInt64(4));
                GetList(checklists, reader.GetGuid(0, "checklist_items", "task_id")).Add(item);
            }
        }

        // ADR 0024: at most one person per task and role (unique index since M0002).
        var designers = new Dictionary<Guid, PersonId>();
        var developers = new Dictionary<Guid, PersonId>();
        using (var command = Filtered("SELECT task_id, role, person_id FROM task_people", "task_id", taskId, " ORDER BY position"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var role = reader.GetString(1);
                var target = role switch
                {
                    StorageNames.Designer => designers,
                    StorageNames.Developer => developers,
                    _ => throw new CorruptStoredDataException("task_people", "role", "unknown role"),
                };
                if (!target.TryAdd(reader.GetGuid(0, "task_people", "task_id"), new PersonId(reader.GetGuid(2, "task_people", "person_id"))))
                {
                    throw new CorruptStoredDataException("task_people", "role", "more than one person in a role");
                }
            }
        }

        var tags = new Dictionary<Guid, List<TagId>>();
        using (var command = Filtered("SELECT task_id, tag_id FROM task_tags", "task_id", taskId, " ORDER BY position"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                GetList(tags, reader.GetGuid(0, "task_tags", "task_id")).Add(new TagId(reader.GetGuid(1, "task_tags", "tag_id")));
            }
        }

        var links = LoadLinks(taskId).ToDictionary(l => l.TaskId.Value, l => l.ChangeRequestId);

        const string Table = "tasks";
        var tasks = new List<TaskItem>();
        using (var command = Filtered(
            """
            SELECT id, own_title, service, status_id, priority_id, start_date, end_date, notes, is_archived, rank,
                   custom_values_json, preserved_json, created_at_utc, updated_at_utc, revision
            FROM tasks
            """,
            "id",
            taskId))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetGuid(0, Table, "id");
                var ownTitle = reader.GetNullableString(1) is { } title ? Require(TitleText.Create(title), Table, "own_title") : null;
                var service = Require(ServiceText.CreateOptional(reader.GetNullableString(2)), Table, "service");
                var notes = Require(LongText.CreateOptional(reader.GetNullableString(7), "Notes"), Table, "notes");
                var content = new TaskContent(
                    ownTitle,
                    service,
                    designers.TryGetValue(id, out var designer) ? designer : null,
                    developers.TryGetValue(id, out var developer) ? developer : null,
                    tags.GetValueOrDefault(id) ?? [],
                    notes,
                    StorageJson.ReadCustomValues(reader.GetNullableString(10), Table));

                var items = checklists.GetValueOrDefault(id) ?? [];
                tasks.Add(Require(
                    TaskItem.Rehydrate(
                        new TaskId(id),
                        content,
                        new TaskStatusId(reader.GetGuid(3, Table, "status_id")),
                        new PriorityId(reader.GetGuid(4, Table, "priority_id")),
                        reader.GetDate(5, Table, "start_date"),
                        reader.GetDate(6, Table, "end_date"),
                        reader.GetFlag(8),
                        reader.GetInt64(9),
                        [.. items.OrderBy(i => i.Rank).ThenBy(i => i.Id.Value)],
                        links.TryGetValue(id, out var crId) ? crId : null,
                        StorageJson.ReadPreserved(reader.GetNullableString(11), Table),
                        reader.GetUtc(12, Table, "created_at_utc"),
                        reader.GetUtc(13, Table, "updated_at_utc"),
                        reader.GetInt64(14)),
                    Table,
                    "row"));
            }
        }

        return tasks;
    }

    /// <param name="column">Either <c>"id"</c> or <c>"parent_task_id"</c>; null with <paramref name="value"/> null loads all.</param>
    public List<SubtaskCard> LoadSubtaskCards(string column = "id", Guid? value = null)
    {
        const string Table = "subtask_cards";
        var cards = new List<SubtaskCard>();
        using var command = Filtered(
            """
            SELECT id, parent_task_id, subtitle, status_id, start_date, end_date, notes, rank, created_at_utc, updated_at_utc, revision
            FROM subtask_cards
            """,
            column,
            value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cards.Add(Require(
                SubtaskCard.Rehydrate(
                    new SubtaskCardId(reader.GetGuid(0, Table, "id")),
                    new TaskId(reader.GetGuid(1, Table, "parent_task_id")),
                    Require(SubtitleText.Create(reader.GetString(2)), Table, "subtitle"),
                    new TaskStatusId(reader.GetGuid(3, Table, "status_id")),
                    reader.GetDate(4, Table, "start_date"),
                    reader.GetDate(5, Table, "end_date"),
                    Require(LongText.CreateOptional(reader.GetNullableString(6), "Notes"), Table, "notes"),
                    reader.GetInt64(7),
                    reader.GetUtc(8, Table, "created_at_utc"),
                    reader.GetUtc(9, Table, "updated_at_utc"),
                    reader.GetInt64(10)),
                Table,
                "row"));
        }

        return cards;
    }

    /// <param name="column">Either <c>"id"</c> or <c>"number_norm"</c>.</param>
    public List<ChangeRequest> LoadChangeRequests(string column = "id", object? value = null)
    {
        const string Table = "change_requests";
        var changeRequests = new List<ChangeRequest>();
        using var command = Filtered(
            """
            SELECT id, number, title, owner_id, squad_id, status_id, priority_id, planned_start, planned_end, email_subject,
                   solution_design_url, description, task_number, custom_values_json, created_at_utc, updated_at_utc, revision,
                   preserved_json
            FROM change_requests
            """,
            column,
            value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var content = new ChangeRequestContent(
                Require(CrNumber.Create(reader.GetString(1)), Table, "number"),
                Require(TitleText.Create(reader.GetString(2)), Table, "title"),
                new PersonId(reader.GetGuid(3, Table, "owner_id")),
                reader.GetNullableGuid(4, Table, "squad_id") is { } squad ? new SquadId(squad) : null,
                new PriorityId(reader.GetGuid(6, Table, "priority_id")),
                Require(PlannedRange.CreateOptional(reader.GetDate(7, Table, "planned_start"), reader.GetDate(8, Table, "planned_end")), Table, "planned_start"),
                Require(EmailSubject.CreateOptional(reader.GetNullableString(9)), Table, "email_subject"),
                Require(HttpUrl.CreateOptional(reader.GetNullableString(10)), Table, "solution_design_url"),
                Require(LongText.CreateOptional(reader.GetNullableString(11), "Description"), Table, "description"),
                Require(TaskNumber.CreateOptional(reader.GetNullableString(12)), Table, "task_number"),
                StorageJson.ReadCustomValues(reader.GetNullableString(13), Table));

            changeRequests.Add(Require(
                ChangeRequest.Rehydrate(
                    new ChangeRequestId(reader.GetGuid(0, Table, "id")),
                    content,
                    new CrStatusId(reader.GetGuid(5, Table, "status_id")),
                    StorageJson.ReadPreserved(reader.GetNullableString(17), Table),
                    reader.GetUtc(14, Table, "created_at_utc"),
                    reader.GetUtc(15, Table, "updated_at_utc"),
                    reader.GetInt64(16)),
                Table,
                "row"));
        }

        return changeRequests;
    }

    /// <param name="column">Either <c>"task_id"</c> or <c>"cr_id"</c>.</param>
    public List<CrTaskLink> LoadLinks(Guid? value = null, string column = "task_id")
    {
        const string Table = "cr_task_links";
        var links = new List<CrTaskLink>();
        using var command = Filtered("SELECT task_id, cr_id, linked_at_utc FROM cr_task_links", column, value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            links.Add(new CrTaskLink(
                new TaskId(reader.GetGuid(0, Table, "task_id")),
                new ChangeRequestId(reader.GetGuid(1, Table, "cr_id")),
                reader.GetUtc(2, Table, "linked_at_utc")));
        }

        return links;
    }

    public List<ReviewItem> LoadReviewItems(Guid? id = null)
    {
        const string Table = "review_items";
        var services = new Dictionary<Guid, List<ServiceText>>();
        using (var command = Filtered("SELECT review_id, service FROM review_services", "review_id", id, " ORDER BY position"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var service = Require(ServiceText.CreateOptional(reader.GetString(1)), "review_services", "service")
                    ?? throw new CorruptStoredDataException("review_services", "service", "blank");
                GetList(services, reader.GetGuid(0, "review_services", "review_id")).Add(service);
            }
        }

        var items = new List<ReviewItem>();
        using (var command = Filtered(
            """
            SELECT id, cr_number_text, title, task_number_text, developer_id, comments, is_done, completed_on, rank,
                   created_at_utc, updated_at_utc, revision
            FROM review_items
            """,
            "id",
            id))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var reviewId = reader.GetGuid(0, Table, "id");
                var content = new ReviewContent(
                    Require(TitleText.Create(reader.GetString(2)), Table, "title"),
                    Require(TaskNumber.CreateOptional(reader.GetNullableString(1), "CrNumberText"), Table, "cr_number_text"),
                    Require(TaskNumber.CreateOptional(reader.GetNullableString(3), "TaskNumberText"), Table, "task_number_text"),
                    services.GetValueOrDefault(reviewId) ?? [],
                    reader.GetNullableGuid(4, Table, "developer_id") is { } developer ? new PersonId(developer) : null,
                    Require(LongText.CreateOptional(reader.GetNullableString(5), "Comments"), Table, "comments"));

                items.Add(Require(
                    ReviewItem.Rehydrate(
                        new ReviewItemId(reviewId),
                        content,
                        reader.GetFlag(6),
                        reader.GetDate(7, Table, "completed_on"),
                        reader.GetInt64(8),
                        reader.GetUtc(9, Table, "created_at_utc"),
                        reader.GetUtc(10, Table, "updated_at_utc"),
                        reader.GetInt64(11)),
                    Table,
                    "row"));
            }
        }

        return items;
    }

    public List<GeneralTodo> LoadGeneralTodos(Guid? id = null)
    {
        const string Table = "general_todos";
        var todos = new List<GeneralTodo>();
        using var command = Filtered(
            "SELECT id, text, is_completed, rank, created_at_utc, updated_at_utc, revision FROM general_todos", "id", id);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            todos.Add(Require(
                GeneralTodo.Rehydrate(
                    new GeneralTodoId(reader.GetGuid(0, Table, "id")),
                    Require(ChecklistText.Create(reader.GetString(1)), Table, "text"),
                    reader.GetFlag(2),
                    reader.GetInt64(3),
                    reader.GetUtc(4, Table, "created_at_utc"),
                    reader.GetUtc(5, Table, "updated_at_utc"),
                    reader.GetInt64(6)),
                Table,
                "row"));
        }

        return todos;
    }

    public WorkflowSnapshot LoadWorkflow()
    {
        var taskStatuses = ReadRows(
            "SELECT id, name, sort_order, is_completed, sets_start_date, is_backlog FROM task_statuses",
            r => new TaskStatusDefinition(
                new TaskStatusId(r.GetGuid(0, "task_statuses", "id")), Name(r, 1, "task_statuses"), r.GetInt32(2), r.GetFlag(3), r.GetFlag(4), r.GetFlag(5)));
        var crStatuses = ReadRows(
            "SELECT id, name, sort_order, is_completed, is_backlog FROM cr_statuses",
            r => new CrStatusDefinition(new CrStatusId(r.GetGuid(0, "cr_statuses", "id")), Name(r, 1, "cr_statuses"), r.GetInt32(2), r.GetFlag(3), r.GetFlag(4)));
        var priorities = ReadRows(
            "SELECT id, name, sort_order FROM priorities",
            r => new PriorityDefinition(new PriorityId(r.GetGuid(0, "priorities", "id")), Name(r, 1, "priorities"), r.GetInt32(2)));
        var squads = ReadRows(
            "SELECT id, name, sort_order FROM squads",
            r => new SquadDefinition(new SquadId(r.GetGuid(0, "squads", "id")), Name(r, 1, "squads"), r.GetInt32(2)));
        var people = ReadRows("SELECT id, name FROM people", r => new PersonDefinition(new PersonId(r.GetGuid(0, "people", "id")), Name(r, 1, "people")));
        var tags = ReadRows("SELECT id, name FROM tags", r => new TagDefinition(new TagId(r.GetGuid(0, "tags", "id")), Name(r, 1, "tags")));
        var fields = ReadRows(
            "SELECT id, target, field_key, label, kind, is_required, default_value_json, display_order, options_json, is_active FROM field_definitions",
            ReadFieldDefinition);

        var defaults = ReadRows(
            """
            SELECT default_task_status_id, default_cr_status_id, default_priority_id, default_cr_owner_id,
                   calendar_scheduling_status_id, cr_backlog_status_id
            FROM app_metadata WHERE id = 1
            """,
            r => new WorkflowDefaults(
                new TaskStatusId(r.GetGuid(0, "app_metadata", "default_task_status_id")),
                new CrStatusId(r.GetGuid(1, "app_metadata", "default_cr_status_id")),
                new PriorityId(r.GetGuid(2, "app_metadata", "default_priority_id")),
                r.GetNullableGuid(3, "app_metadata", "default_cr_owner_id") is { } owner ? new PersonId(owner) : null,
                new CrStatusId(r.GetGuid(4, "app_metadata", "calendar_scheduling_status_id")),
                new CrStatusId(r.GetGuid(5, "app_metadata", "cr_backlog_status_id"))));

        return new WorkflowSnapshot(
            [.. taskStatuses.OrderBy(s => s.Order).ThenBy(s => s.Id.Value)],
            [.. crStatuses.OrderBy(s => s.Order).ThenBy(s => s.Id.Value)],
            [.. priorities.OrderBy(p => p.Order).ThenBy(p => p.Id.Value)],
            [.. squads.OrderBy(s => s.Order).ThenBy(s => s.Id.Value)],
            [.. people.OrderBy(p => p.Name.Normalized, StringComparer.Ordinal).ThenBy(p => p.Id.Value)],
            [.. tags.OrderBy(t => t.Name.Normalized, StringComparer.Ordinal).ThenBy(t => t.Id.Value)],
            [.. fields.OrderBy(f => f.DisplayOrder).ThenBy(f => f.Id.Value)],
            defaults is [var single] ? single : throw new CorruptStoredDataException("app_metadata", "id", "row missing"));
    }

    private static FieldDefinition ReadFieldDefinition(SqliteDataReader reader)
    {
        const string Table = "field_definitions";
        return Require(
            FieldDefinition.Rehydrate(
                new FieldId(reader.GetGuid(0, Table, "id")),
                Require(FieldKey.Rehydrate(reader.GetString(2)), Table, "field_key"),
                StorageNames.ParseTarget(reader.GetString(1)),
                Require(LookupName.Create(reader.GetString(3)), Table, "label"),
                StorageNames.ParseKind(reader.GetString(4)),
                reader.GetFlag(5),
                StorageJson.ReadDefaultValue(reader.GetNullableString(6), Table, "default_value_json"),
                reader.GetInt32(7),
                StorageJson.ReadOptions(reader.GetString(8), Table),
                reader.GetFlag(9)),
            Table,
            "row");
    }

    private static LookupName Name(SqliteDataReader reader, int ordinal, string table) =>
        Require(LookupName.Create(reader.GetString(ordinal)), table, "name");

    private static List<T> GetList<T>(Dictionary<Guid, List<T>> map, Guid key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        return list;
    }

    private List<T> ReadRows<T>(string sql, Func<SqliteDataReader, T> map)
    {
        var rows = new List<T>();
        using var command = Command(sql);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private SqliteCommand Filtered(string select, string column, object? value, string orderBy = "")
    {
        // The column name is always one of the constants passed by this class; only the value is user data.
        var command = Command(value is null ? select + orderBy + ";" : $"{select} WHERE {column} = $filter{orderBy};");
        switch (value)
        {
            case null:
                break;
            case Guid id:
                command.Add("$filter", id);
                break;
            case string text:
                command.Add("$filter", text);
                break;
            default:
                throw new ArgumentException($"Unsupported filter value type {value.GetType()}.", nameof(value));
        }

        return command;
    }
}
