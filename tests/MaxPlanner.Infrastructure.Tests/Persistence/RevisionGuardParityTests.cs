using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

/// <summary>
/// The <c>IWriteTransaction</c> update rule on SQLite. These scenarios and expected messages mirror
/// Application.Tests <c>RevisionGuardContractTests</c>, which run them on the in-memory fake; keep the two in
/// step so a use case cannot pass Application tests and fail only here.
/// </summary>
[Trait("Req", "T-05")]
[Trait("Req", "T-11.2")]
public sealed class RevisionGuardParityTests
{
    private const string NotLoaded = "Update of task * without loading it in this transaction.";
    private const string NotAdvanced = "Update of task * does not advance its revision*";

    [Fact]
    public async Task Updating_a_record_not_loaded_in_the_transaction_is_refused()
    {
        await using var db = await CreateAsync();
        var id = await db.AddTaskAsync("Original");
        var fromReadModel = (await db.SnapshotAsync()).Tasks.Single();

        await RefusedAsync(db, tx =>
        {
            tx.Tasks.Update(Edited(fromReadModel, tx));
            return TxResult.Commit(0L);
        }, NotLoaded, id);
    }

    [Fact]
    public async Task An_update_that_does_not_advance_the_revision_is_refused()
    {
        await using var db = await CreateAsync();
        var id = await db.AddTaskAsync("Original");

        await RefusedAsync(db, tx =>
        {
            tx.Tasks.Update(tx.Tasks.Find(id)!);
            return TxResult.Commit(0L);
        }, NotAdvanced, id);
    }

    [Fact]
    public async Task An_entity_older_than_the_loaded_state_is_refused()
    {
        await using var db = await CreateAsync();
        var id = await db.AddTaskAsync("Original");
        var stale = (await db.SnapshotAsync()).Tasks.Single();
        Succeeded(await new UpdateTaskContent(db.Coordinator).ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), id, 1, Draft("Original")), Ct));

        await RefusedAsync(db, tx =>
        {
            tx.Tasks.Find(id);
            tx.Tasks.Update(Edited(stale, tx));
            return TxResult.Commit(0L);
        }, NotAdvanced, id);
    }

    [Fact]
    public async Task Several_transitions_of_a_loaded_record_are_one_valid_update()
    {
        await using var db = await CreateAsync();
        var id = await db.AddTaskAsync("Original");

        var result = await db.Coordinator.ExecuteAsync(OperationId.New(), "test.revision-guard", tx =>
        {
            var task = tx.Tasks.Find(id)!;
            var changed = Edited(task, tx).SetPriority(tx.Workflow.Read().Priorities[0].Id, tx.UtcNow).Value;
            tx.Tasks.Update(changed);
            return TxResult.Commit(changed.Revision);
        }, Ct);

        Succeeded(result).Value.Should().Be(3);
        (await db.SnapshotAsync()).Tasks.Single().Revision.Should().Be(3);
    }

    private static TaskItem Edited(TaskItem task, IWriteTransaction tx) =>
        task.UpdateContent(new TaskContent(TitleText.Create("Edited").Value, null, [], [], [], null, CustomValue.NoValues), tx.UtcNow).Value;

    private static async Task RefusedAsync(TestDataset db, Func<IWriteTransaction, TxResult<long>> work, string message, TaskId id)
    {
        var before = await db.SnapshotAsync();

        var act = () => db.Coordinator.ExecuteAsync(OperationId.New(), "test.revision-guard", work, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        var after = await db.SnapshotAsync();
        after.CommitSequence.Should().Be(before.CommitSequence);
        var task = after.Tasks.Single(t => t.Id == id);
        (task.OwnTitle, task.Revision).Should().Be((before.Tasks.Single(t => t.Id == id).OwnTitle, before.Tasks.Single(t => t.Id == id).Revision));
    }
}
