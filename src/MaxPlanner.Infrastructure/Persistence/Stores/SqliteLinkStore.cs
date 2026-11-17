using MaxPlanner.Application.Abstractions;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Infrastructure.Persistence.Mapping;

namespace MaxPlanner.Infrastructure.Persistence.Stores;

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
