using MaxPlanner.Application.Abstractions;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Stores;

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
