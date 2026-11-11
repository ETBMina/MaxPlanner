using MaxPlanner.Application.Abstractions;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Stores;

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
