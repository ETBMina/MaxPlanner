using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Tests.Testing;
using static MaxPlanner.Presentation.Tests.Testing.Records;

namespace MaxPlanner.Presentation.Tests.Store;

[Trait("Req", "T-07")]
public sealed class StoreHandoffTests
{
    [Fact]
    [Trait("Req", "AC-06")]
    public void Changes_committed_while_the_snapshot_loads_are_neither_lost_nor_applied_twice()
    {
        using var h = new StoreHarness();
        var task = Task("Card freeze");
        var inSnapshot = Rename(task, "Snapshot title");
        var later = Task("Committed after the snapshot");

        h.Store.Start();
        h.Scheduler.RunAll();

        // Sequence 5 was committed before the snapshot's read transaction, so the snapshot already
        // reflects it; its (deliberately different) payload must be dropped, not replayed over the snapshot.
        h.Feed.Publish(Change(5) with { UpsertedTasks = [Rename(task, "Stale buffered title")] });
        h.Feed.Publish(Change(6) with { UpsertedTasks = [later] });
        h.Scheduler.RunAll();

        h.Status.Should().Be(ProjectionStatus.Loading);
        h.Store.Tasks.Count.Should().Be(0, "nothing is shown before the snapshot is applied");

        h.CompleteLoad(Snapshot(5, tasks: [inSnapshot]));

        h.Task(task.Id).EffectiveTitle.Should().Be("Snapshot title");
        h.Task(later.Id).EffectiveTitle.Should().Be("Committed after the snapshot");
        h.Commits.Select(c => (c.Sequence, c.Reason)).Should().Equal(
            (5L, ProjectionCommitReason.Snapshot), (6L, ProjectionCommitReason.ChangeSet));
        h.Store.Sequence.Should().Be(6);
        h.Status.Should().Be(ProjectionStatus.Ready);
        h.Store.IsEditingBlocked.Should().BeFalse();
    }

    [Fact]
    public void Live_change_sets_apply_in_sequence_and_a_repeated_sequence_is_ignored()
    {
        using var h = new StoreHarness().StartWith(Snapshot(10));
        var first = Task("First");
        var second = Task("Second");

        h.Publish(Change(11) with { UpsertedTasks = [first] });
        h.Publish(Change(11) with { UpsertedTasks = [Rename(first, "Replayed")] });
        h.Publish(Change(12) with { UpsertedTasks = [second] });

        h.Task(first.Id).EffectiveTitle.Should().Be("First");
        h.Commits.Select(c => c.Sequence).Should().Equal(10, 11, 12);
        h.Feed.SubscribeCount.Should().Be(1);
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public void A_sequence_gap_blocks_editing_and_reloads_through_the_same_handoff()
    {
        using var h = new StoreHarness().StartWith(Snapshot(5));
        var task = Task("Before the gap");
        h.Publish(Change(6) with { UpsertedTasks = [task] });

        h.Publish(Change(8) with { UpsertedTasks = [Task("After the gap")] });

        h.Status.Should().Be(ProjectionStatus.Resyncing);
        h.Store.IsEditingBlocked.Should().BeTrue();
        h.Snapshots.LoadCount.Should().Be(2);
        h.Feed.SubscribeCount.Should().Be(2, "the resync subscribes again before reloading");
        h.Feed.SubscriberCount.Should().Be(1, "the gapped subscription was released");
        h.Task(task.Id).EffectiveTitle.Should().Be("Before the gap", "the previous projection stays visible while resyncing");

        var duringReload = Task("During the reload");
        h.Publish(Change(9) with { UpsertedTasks = [duringReload] });
        var afterGap = Task("Reloaded");
        h.CompleteLoad(Snapshot(8, tasks: [task, afterGap]));

        h.Status.Should().Be(ProjectionStatus.Ready);
        h.Store.Tasks.Keys.Should().BeEquivalentTo([task.Id, afterGap.Id, duringReload.Id]);
        h.Store.Sequence.Should().Be(9);
        h.States.Select(s => s.Status).Should().Equal(
            ProjectionStatus.Loading, ProjectionStatus.Ready, ProjectionStatus.Resyncing, ProjectionStatus.Ready);
    }

    [Fact]
    public void A_gap_inside_the_buffer_after_the_snapshot_resyncs_again()
    {
        using var h = new StoreHarness();
        h.Store.Start();
        h.Scheduler.RunAll();
        h.Feed.Publish(Change(7));
        h.Scheduler.RunAll();

        h.CompleteLoad(Snapshot(5));

        h.Status.Should().Be(ProjectionStatus.Resyncing, "sequence 6 is missing between the snapshot and the buffer");
        h.Snapshots.LoadCount.Should().Be(2);
        h.CompleteLoad(Snapshot(7));
        h.Status.Should().Be(ProjectionStatus.Ready);
    }

    [Fact]
    public void A_feed_fault_resubscribes_and_reloads()
    {
        using var h = new StoreHarness().StartWith(Snapshot(3));
        var fault = new InvalidOperationException("Subscriber failed.");

        h.Feed.Fault(fault);
        h.Scheduler.RunAll();

        h.Store.CurrentState.Should().Be(new ProjectionState(ProjectionStatus.Resyncing, Exception: fault));
        h.Feed.SubscriberCount.Should().Be(1);
        h.CompleteLoad(Snapshot(3));
        h.Status.Should().Be(ProjectionStatus.Ready);
    }

    [Fact]
    public void A_failed_snapshot_load_faults_with_the_typed_error_and_can_be_retried()
    {
        using var h = new StoreHarness();
        var error = new StorageFailureError(StorageFailureKind.FileLocked);
        h.Store.Start();
        h.Scheduler.RunAll();

        h.Snapshots.Fail(error);
        h.Scheduler.RunAll();

        h.Store.CurrentState.Should().Be(new ProjectionState(ProjectionStatus.Faulted, error));
        h.Store.IsEditingBlocked.Should().BeTrue();
        h.Feed.SubscriberCount.Should().Be(0);
        h.Commits.Should().BeEmpty("a failure is not a successful empty load (B-15.7)");

        h.Store.Reload();
        h.Scheduler.RunAll();
        h.Status.Should().Be(ProjectionStatus.Loading);
        h.CompleteLoad(Snapshot(1, tasks: [Task()]));

        h.Status.Should().Be(ProjectionStatus.Ready);
        h.Store.Tasks.Count.Should().Be(1);
    }

    [Fact]
    public void A_failed_resync_keeps_the_previous_projection()
    {
        using var h = new StoreHarness();
        var task = Task();
        h.StartWith(Snapshot(1, tasks: [task]));

        h.Publish(Change(3));
        h.Snapshots.Fail(new StorageFailureError(StorageFailureKind.Busy));
        h.Scheduler.RunAll();

        h.Status.Should().Be(ProjectionStatus.Faulted);
        h.Store.Tasks.Keys.Should().Equal(task.Id);
    }

    [Fact]
    public void A_bulk_change_reloads_once_instead_of_applying_records()
    {
        using var h = new StoreHarness().StartWith(Snapshot(1));
        var imported = Task("Imported");

        h.Publish(Change(2) with { RequiresFullReload = true });
        h.Status.Should().Be(ProjectionStatus.Resyncing);
        h.CompleteLoad(Snapshot(2, tasks: [imported]));

        h.Snapshots.LoadCount.Should().Be(2);
        h.Store.Tasks.Keys.Should().Equal(imported.Id);
        h.Status.Should().Be(ProjectionStatus.Ready);
    }

    [Fact]
    public void A_change_set_that_contradicts_known_links_resyncs_instead_of_guessing()
    {
        using var h = new StoreHarness().StartWith(Snapshot(1));
        var (linked, _) = Link(Task(), Cr());

        // The task claims a CR link, but neither the link row nor the CR was ever published.
        h.Publish(Change(2) with { UpsertedTasks = [linked] });

        h.Status.Should().Be(ProjectionStatus.Resyncing);
        h.Store.CurrentState.Exception.Should().BeOfType<InvalidOperationException>();
        h.Store.Tasks.Count.Should().Be(0);
    }

    [Fact]
    public void Disposing_the_store_releases_the_feed_and_stops_applying_changes()
    {
        var h = new StoreHarness().StartWith(Snapshot(1));
        h.Feed.SubscriberCount.Should().Be(1);

        h.Feed.Publish(Change(2) with { UpsertedTasks = [Task()] });
        h.Store.Dispose();
        h.Scheduler.RunAll();

        h.Feed.SubscriberCount.Should().Be(0);
        h.Commits.Select(c => c.Sequence).Should().Equal(1);
        h.Store.CurrentState.Status.Should().Be(ProjectionStatus.Ready);
    }

    [Fact]
    public void Starting_twice_loads_once()
    {
        using var h = new StoreHarness();
        h.Store.Start();
        h.Store.Start();
        h.Scheduler.RunAll();

        h.Snapshots.LoadCount.Should().Be(1);
        h.Feed.SubscriberCount.Should().Be(1);
    }

    [Fact]
    public void Review_items_and_general_todos_follow_their_change_sets()
    {
        var review = Review();
        var todo = Todo();
        using var h = new StoreHarness().StartWith(Snapshot(1, reviews: [review], todos: [todo]));
        var newTodo = Todo("Order badges");

        h.Publish(Change(2) with { DeletedReviewItems = [review.Id], UpsertedGeneralTodos = [newTodo] });

        h.Store.ReviewItems.Count.Should().Be(0);
        h.Store.GeneralTodos.Keys.Should().BeEquivalentTo([todo.Id, newTodo.Id]);
    }

    [Fact]
    public void A_resync_snapshot_removes_records_deleted_while_out_of_sync()
    {
        var kept = Task("Kept");
        var removed = Task("Removed elsewhere");
        var cr = Cr();
        using var h = new StoreHarness().StartWith(Snapshot(1, tasks: [kept, removed], crs: [cr]));

        h.Publish(Change(5));
        h.CompleteLoad(Snapshot(5, tasks: [kept]));

        h.Store.Tasks.Keys.Should().Equal(kept.Id);
        h.Store.ChangeRequests.Count.Should().Be(0);
    }

    [Fact]
    public void Editing_stays_blocked_until_the_first_snapshot_is_applied()
    {
        using var h = new StoreHarness();

        h.Store.IsEditingBlocked.Should().BeTrue();
        h.Store.Start();
        h.Scheduler.RunAll();
        h.Store.IsEditingBlocked.Should().BeTrue();
        h.Store.CurrentWorkflow.Should().BeNull();

        h.CompleteLoad(Snapshot(1));

        h.Store.IsEditingBlocked.Should().BeFalse();
        h.Store.CurrentWorkflow.Should().BeSameAs(Workflow);
    }
}
