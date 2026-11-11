using MaxPlanner.Application.Abstractions;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Stores;

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
