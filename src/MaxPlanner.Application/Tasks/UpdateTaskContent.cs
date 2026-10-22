using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;

namespace MaxPlanner.Application.Tasks;

public sealed record UpdateTaskContentCommand(OperationId OperationId, TaskId TaskId, long ExpectedRevision, TaskContentDraft Content);

/// <summary>Saves the editable fields of a task. Status, dates, order, and the CR link have their own commands.</summary>
public sealed class UpdateTaskContent
{
    public const string OperationKind = "task.update-content";

    private readonly IMutationCoordinator _coordinator;

    public UpdateTaskContent(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The task's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(UpdateTaskContentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = new ValidationCollector();
        var content = TaskContentParser.Parse(command.Content, isLinked: null, validation);
        if (validation.HasErrors || content is null)
        {
            return UseCaseResults.Failed<long>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command, content), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, UpdateTaskContentCommand command, TaskContent content)
    {
        if (!TransactionGuards.TryLoadTask(tx, command.TaskId, command.ExpectedRevision, out var task, out var loadError))
        {
            return TxResult.Abort<long>(loadError);
        }

        var workflow = tx.Workflow.Read();
        content = content with
        {
            CustomValues = CustomFieldPolicy.MergeForUpdate(FieldTarget.Task, workflow.Fields, task.CustomValues, content.CustomValues),
        };

        // Whether the task is linked is only known here, so the ownership rule (checked by the domain
        // call) is reported together with the reference checks.
        var validation = new ValidationCollector();
        var updated = validation.Check(task.UpdateContent(content, tx.UtcNow));
        TransactionGuards.CheckPeopleAndTags(workflow, content, validation);
        TransactionGuards.CheckCustomValues(workflow, FieldTarget.Task, content.CustomValues, validation);
        if (validation.HasErrors)
        {
            return TxResult.Abort<long>(validation.ToError());
        }

        tx.Tasks.Update(updated.Value);
        tx.Changes.UpsertTask(updated.Value);
        return TxResult.Commit(updated.Value.Revision);
    }
}
