using MaxPlanner.Application.Abstractions;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
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
