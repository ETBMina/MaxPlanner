using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Coordinator;

/// <summary>
/// The <c>IWriteTransaction</c> update rule on the in-memory fake. The same scenarios, with the same expected
/// messages, run against SQLite in Infrastructure.Tests <c>RevisionGuardParityTests</c>; keep the two in step so
/// a use case cannot pass here and fail only on SQLite.
/// </summary>
[Trait("Req", "T-05")]
public sealed class RevisionGuardContractTests
{
    private const string NotLoaded = "Update of task * without loading it in this transaction.";
    private const string NotAdvanced = "Update of task * does not advance its revision*";

    private readonly TestWorkspace _workspace = new();

    [Fact]
    public async Task Updating_a_record_not_loaded_in_the_transaction_is_refused()
    {
        var id = await _workspace.AddTaskAsync("Original");
        var fromReadModel = _workspace.Task(id);

        await RefusedAsync(tx =>
        {
            tx.Tasks.Update(Edited(fromReadModel, tx));
            return TxResult.Commit(0L);
        }, NotLoaded, id);
    }

    [Fact]
    public async Task An_update_that_does_not_advance_the_revision_is_refused()
    {
        var id = await _workspace.AddTaskAsync("Original");

        await RefusedAsync(tx =>
        {
            tx.Tasks.Update(tx.Tasks.Find(id)!);
            return TxResult.Commit(0L);
        }, NotAdvanced, id);
    }

    [Fact]
    public async Task An_entity_older_than_the_loaded_state_is_refused()
    {
        var id = await _workspace.AddTaskAsync("Original");
        var stale = _workspace.Task(id);
        Succeeded(await new UpdateTaskContent(_workspace.Coordinator).ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), id, 1, Draft("Original")), CancellationToken.None));

        await RefusedAsync(tx =>
        {
            tx.Tasks.Find(id);
            tx.Tasks.Update(Edited(stale, tx));
            return TxResult.Commit(0L);
        }, NotAdvanced, id);
    }

    [Fact]
    public async Task Several_transitions_of_a_loaded_record_are_one_valid_update()
    {
        var id = await _workspace.AddTaskAsync("Original");

        var result = await _workspace.Coordinator.ExecuteAsync(OperationId.New(), "test.revision-guard", tx =>
        {
            var task = tx.Tasks.Find(id)!;
            var changed = Edited(task, tx).SetPriority(tx.Workflow.Read().Priorities[0].Id, tx.UtcNow).Value;
            tx.Tasks.Update(changed);
            return TxResult.Commit(changed.Revision);
        }, CancellationToken.None);

        Succeeded(result).Value.Should().Be(3);
        _workspace.Task(id).Revision.Should().Be(3);
    }

    private static TaskItem Edited(TaskItem task, IWriteTransaction tx) =>
        task.UpdateContent(new TaskContent(TitleText.Create("Edited").Value, null, null, null, [], null, CustomValue.NoValues), tx.UtcNow).Value;

    private async Task RefusedAsync(Func<IWriteTransaction, TxResult<long>> work, string message, TaskId id)
    {
        var sequence = _workspace.Data.CommitSequence;
        var before = _workspace.Task(id);

        var act = () => _workspace.Coordinator.ExecuteAsync(OperationId.New(), "test.revision-guard", work, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        _workspace.Data.CommitSequence.Should().Be(sequence);
        _workspace.Task(id).Should().BeSameAs(before, "the refused transaction changed nothing");
    }
}
