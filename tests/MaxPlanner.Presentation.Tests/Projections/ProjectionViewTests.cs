using System.Collections.Specialized;
using System.Reactive.Concurrency;
using AwesomeAssertions;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Scheduling;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Tests.Testing;
using static MaxPlanner.Presentation.Tests.Testing.Records;

namespace MaxPlanner.Presentation.Tests.Projections;

[Trait("Req", "T-07")]
public sealed class ProjectionViewTests
{
    [Fact]
    public void A_record_whose_filtered_property_changes_moves_between_filtered_views()
    {
        var a = Task("A", rank: 1);
        var b = Task("B", rank: 2);
        var c = Task("C", InProgress, rank: 3);
        using var h = new StoreHarness().StartWith(Snapshot(1, [a, b, c]));
        using var backlog = View(h, new ImmediateUiDispatcher(), m => m.Status.Id == Backlog.Id);
        using var inProgress = View(h, new ImmediateUiDispatcher(), m => m.Status.Id == InProgress.Id);
        h.Scheduler.RunAll();
        Titles(backlog).Should().Equal("A", "B");

        h.Publish(Change(2) with { UpsertedTasks = [Move(b, InProgress)] });
        h.Publish(Change(3) with { UpsertedTasks = [Move(c, Backlog)] });

        Titles(backlog).Should().Equal("A", "C");
        Titles(inProgress).Should().Equal("B");
    }

    [Fact]
    public void Changing_the_filter_reevaluates_membership_on_the_store_scheduler()
    {
        using var h = new StoreHarness().StartWith(Snapshot(1, [Task("A", rank: 1), Task("B", InProgress, rank: 2)]));
        using var view = View(h, new ImmediateUiDispatcher());
        h.Scheduler.RunAll();

        view.SetFilter(m => m.Status.Id == InProgress.Id);
        Titles(view).Should().Equal("A", "B");
        h.Scheduler.RunAll();

        Titles(view).Should().Equal("B");
    }

    [Fact]
    [Trait("Req", "AC-06")]
    public void An_update_keeps_the_same_item_view_model_instance()
    {
        var task = Task("Before");
        using var h = new StoreHarness().StartWith(Snapshot(1, [task]));
        using var view = View(h, new ImmediateUiDispatcher());
        h.Scheduler.RunAll();
        var item = view.Items.Single();
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        h.Publish(Change(2) with { UpsertedTasks = [Rename(task, "After")] });

        view.Items.Single().Should().BeSameAs(item);
        item.Model.EffectiveTitle.Should().Be("After");
        changed.Should().Contain(nameof(TaskItemViewModel.Model));
    }

    [Fact]
    public void The_order_follows_the_comparer_and_its_changes()
    {
        using var h = new StoreHarness().StartWith(Snapshot(1, [Task("Bravo", rank: 1), Task("Alpha", rank: 2), Task("Charlie", rank: 3)]));
        using var view = View(h, new ImmediateUiDispatcher());
        h.Scheduler.RunAll();
        Titles(view).Should().Equal("Bravo", "Alpha", "Charlie");

        view.SetComparer(Comparer<TaskItemViewModel>.Create((x, y) => string.CompareOrdinal(x.Model.EffectiveTitle, y.Model.EffectiveTitle)));

        Titles(view).Should().Equal("Alpha", "Bravo", "Charlie");
    }

    [Fact]
    [Trait("Req", "B-07")]
    public void A_rank_change_moves_the_existing_row()
    {
        var a = Task("A", rank: 1);
        var b = Task("B", rank: 2);
        using var h = new StoreHarness().StartWith(Snapshot(1, [a, b]));
        using var view = View(h, new ImmediateUiDispatcher());
        h.Scheduler.RunAll();
        var actions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)view.Items).CollectionChanged += (_, e) => actions.Add(e.Action);

        h.Publish(Change(2) with { UpsertedTasks = [Move(b, Backlog, rank: 0)] });

        Titles(view).Should().Equal("B", "A");
        actions.Should().Equal(NotifyCollectionChangedAction.Move);
    }

    [Fact]
    public void The_selection_is_restored_after_a_row_moves_above_it()
    {
        var a = Task("A", rank: 1);
        var c = Task("C", rank: 3);
        var d = Task("D", rank: 4);
        using var h = new StoreHarness().StartWith(Snapshot(1, [a, c, d]));
        using var view = View(h, new ImmediateUiDispatcher());
        h.Scheduler.RunAll();
        var selected = view.Items.Single(i => i.Model.EffectiveTitle == "C");
        view.Selection.SelectedItem = selected;
        var events = new List<string>();
        view.Selection.PropertyChanged += (_, e) => events.Add($"{e.PropertyName}={view.Selection.SelectedItem?.Model.EffectiveTitle}");

        // Mimic the ListView: a row moving above the selected row clears its SelectedItem through the binding.
        ((INotifyCollectionChanged)view.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Move)
            {
                events.Add("list cleared selection");
                view.Selection.SelectedItem = null;
            }
        };

        h.Publish(Change(2) with { UpsertedTasks = [Move(d, Backlog, rank: 0)] });

        Titles(view).Should().Equal("D", "A", "C");
        view.Selection.SelectedItem.Should().BeSameAs(selected);
        view.Selection.SelectedKey.Should().Be(c.Id);
        events.Should().Equal("list cleared selection", "SelectedItem=C");
    }

    [Fact]
    public void The_selection_clears_when_the_selected_record_leaves_the_view()
    {
        var task = Task();
        using var h = new StoreHarness().StartWith(Snapshot(1, [task]));
        using var view = View(h, new ImmediateUiDispatcher(), m => m.Status.Id == Backlog.Id);
        h.Scheduler.RunAll();
        view.Selection.SelectedItem = view.Items.Single();

        h.Publish(Change(2) with { UpsertedTasks = [Move(task, InProgress)] });

        view.Items.Should().BeEmpty();
        view.Selection.HasSelection.Should().BeFalse();
    }

    [Fact]
    public void Disposing_a_view_stops_ui_updates_and_releases_its_cache_subscription()
    {
        var task = Task("Before");
        using var h = new StoreHarness().StartWith(Snapshot(1, [task]));
        var ui = new ManualUiDispatcher();
        var view = View(h, ui);
        h.Scheduler.RunAll();
        ui.RunAll();
        h.Store.ActiveCacheSubscriptions.Should().Be(1);
        var item = view.Items.Single();
        var collectionChanges = 0;
        ((INotifyCollectionChanged)view.Items).CollectionChanged += (_, _) => collectionChanges++;

        // The update reaches the UI queue, then the page is left before the queue runs.
        h.Publish(Change(2) with { UpsertedTasks = [Rename(task, "After"), Task("New")] });
        ui.Pending.Should().BeGreaterThan(0);
        view.Dispose();
        ui.RunAll();
        h.Scheduler.RunAll();
        h.Publish(Change(3) with { UpsertedTasks = [Task("Later")] });
        ui.RunAll();

        collectionChanges.Should().Be(0);
        item.Model.EffectiveTitle.Should().Be("Before");
        h.Store.ActiveCacheSubscriptions.Should().Be(0);
    }

    [Fact]
    [Trait("Req", "T-11")]
    public void Creating_and_disposing_views_one_hundred_times_leaves_no_subscriptions_behind()
    {
        using var h = new StoreHarness().StartWith(Snapshot(1, [Task("A"), Task("B")]));
        var ui = new ImmediateUiDispatcher();
        var sequence = 1L;

        for (var cycle = 0; cycle < 100; cycle++)
        {
            using (var view = View(h, ui))
            {
                h.Scheduler.RunAll();
                view.Items.Should().HaveCount(2 + cycle);
                h.Publish(Change(++sequence) with { UpsertedTasks = [Task($"Cycle {cycle}")] });
            }

            h.Scheduler.RunAll();
            h.Store.ActiveCacheSubscriptions.Should().Be(0, $"cycle {cycle} released its subscription");
        }

        h.Feed.SubscriberCount.Should().Be(1, "views never subscribe to the feed");
        h.Store.Dispose();
        h.Feed.SubscriberCount.Should().Be(0);
    }

    [Fact]
    public void The_dispatcher_scheduler_runs_work_through_the_dispatcher_and_honors_cancellation()
    {
        var ui = new ManualUiDispatcher();
        var scheduler = new DispatcherQueueScheduler(ui);
        var ran = new List<string>();

        scheduler.Schedule(() => ran.Add("first"));
        var cancelled = scheduler.Schedule(() => ran.Add("cancelled"));
        cancelled.Dispose();
        ran.Should().BeEmpty("nothing runs until the dispatcher does");
        ui.RunAll();

        ran.Should().Equal("first");
    }

    [Fact]
    public async Task The_dispatcher_scheduler_posts_delayed_work_when_it_is_due()
    {
        var scheduler = new DispatcherQueueScheduler(new ImmediateUiDispatcher());
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        scheduler.Schedule(TimeSpan.FromMilliseconds(20), () => ran.SetResult());

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private static ProjectionView<TaskReadModel, TaskId, TaskItemViewModel> View(StoreHarness h, IUiDispatcher ui, Func<TaskReadModel, bool>? filter = null) =>
        new(h.Store.Tasks, h.Store.Scheduler, new DispatcherQueueScheduler(ui), m => new TaskItemViewModel(m), ByRank, filter);

    private static IComparer<TaskItemViewModel> ByRank { get; } =
        Comparer<TaskItemViewModel>.Create((x, y) => x.Model.Rank.CompareTo(y.Model.Rank));

    private static IEnumerable<string> Titles(ProjectionView<TaskReadModel, TaskId, TaskItemViewModel> view) =>
        view.Items.Select(i => i.Model.EffectiveTitle);

    private sealed class TaskItemViewModel(TaskReadModel model) : ProjectionItemViewModel<TaskReadModel, TaskId>(model)
    {
        public override TaskId Key => Model.Id;
    }
}
