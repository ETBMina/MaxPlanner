using AwesomeAssertions;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Values;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Tests.Testing;
using static MaxPlanner.Presentation.Tests.Testing.Records;

namespace MaxPlanner.Presentation.Tests.Store;

[Trait("Req", "T-13")]
public sealed class StoreRecompositionTests
{
    [Fact]
    [Trait("Req", "AC-04")]
    [Trait("Req", "AC-22")]
    public void A_cr_edit_updates_every_linked_task_and_their_subtask_cards_but_nothing_else()
    {
        var cr = Cr(squad: RetailCards.Id, owner: Alice.Id);
        var (first, firstLink) = Link(Task("First"), cr);
        var (second, secondLink) = Link(Archive(Task("Second")), cr);
        var card = Card(first);
        var unlinked = Task("Unlinked");
        using var h = new StoreHarness().StartWith(Snapshot(1, [first, second, unlinked], [card], [cr], [firstLink, secondLink]));
        var unlinkedBefore = h.Task(unlinked.Id);

        var edited = EditCr(cr, c => c with
        {
            Title = TitleText.Create("Card controls").Value,
            Number = CrNumber.Create("CR-2001").Value,
            TaskNumber = TaskNumber.CreateOptional("0077").Value,
            SquadId = RetailFinance.Id,
            OwnerId = Bob.Id,
        });
        h.Publish(Change(2) with { UpsertedChangeRequests = [edited] });

        var expected = new InheritedCrValues(cr.Id, "CR-2001", "0077", RetailFinance.Id, "Retail Finance", Bob.Id, "Bob");
        foreach (var id in new[] { first.Id, second.Id })
        {
            h.Task(id).EffectiveTitle.Should().Be("Card controls");
            h.Task(id).Cr.Should().Be(expected);
        }

        h.Task(second.Id).IsArchived.Should().BeTrue("archived linked tasks keep their link and inherited values");
        h.Card(card.Id).ParentTitle.Should().Be("Card controls");
        h.Card(card.Id).Cr.Should().Be(expected);
        h.Cr(cr.Id).Title.Should().Be("Card controls");
        h.Cr(cr.Id).LinkedTaskCount.Should().Be(2);
        h.Task(unlinked.Id).Should().BeSameAs(unlinkedBefore, "only affected read models are recomposed");
    }

    [Fact]
    public void Renaming_a_squad_or_person_recomposes_every_record_that_shows_it()
    {
        var cr = Cr(squad: RetailCards.Id, owner: Alice.Id);
        var (task, link) = Link(Task(), cr);
        var card = Card(task);
        using var h = new StoreHarness().StartWith(Snapshot(1, [task], [card], [cr], [link]));
        var renamed = Workflow with
        {
            Squads = [RetailCards with { Name = Name("Cards & Payments") }, RetailFinance],
            People = [Alice with { Name = Name("Alice Smith") }, Bob],
        };

        h.Publish(Change(2) with { Workflow = renamed });

        h.Store.CurrentWorkflow.Should().BeSameAs(renamed);
        h.Cr(cr.Id).SquadName.Should().Be("Cards & Payments");
        h.Cr(cr.Id).Owner.Name.Should().Be("Alice Smith");
        h.Task(task.Id).Cr.SquadName.Should().Be("Cards & Payments");
        h.Task(task.Id).Cr.OwnerName.Should().Be("Alice Smith");
        h.Card(card.Id).Cr.SquadName.Should().Be("Cards & Payments");
    }

    [Fact]
    [Trait("Req", "B-05")]
    public void A_cascade_delete_removes_the_cr_its_tasks_and_their_subtask_cards()
    {
        var cr = Cr();
        var (task, link) = Link(Task(), cr);
        var card = Card(task);
        var other = Task("Unrelated");
        using var h = new StoreHarness().StartWith(Snapshot(1, [task, other], [card], [cr], [link]));

        h.Publish(Change(2) with
        {
            DeletedChangeRequests = [cr.Id],
            DeletedTasks = [task.Id],
            DeletedSubtaskCards = [card.Id],
            RemovedLinks = [link],
        });

        h.Store.ChangeRequests.Count.Should().Be(0);
        h.Store.Tasks.Keys.Should().Equal(other.Id);
        h.Store.SubtaskCards.Count.Should().Be(0);
        h.Status.Should().Be(ProjectionStatus.Ready);
    }

    [Fact]
    [Trait("Req", "B-03")]
    public void Linking_a_task_makes_it_and_its_cards_inherit_from_the_cr()
    {
        var cr = Cr(squad: RetailCards.Id, owner: Bob.Id);
        var task = Task("Own title");
        var card = Card(task);
        using var h = new StoreHarness().StartWith(Snapshot(1, [task], [card], [cr]));
        h.Task(task.Id).Cr.SquadName.Should().Be("General");

        var (linked, link) = Link(task, cr);
        h.Publish(Change(2) with { UpsertedTasks = [linked], AddedLinks = [link] });

        h.Task(task.Id).EffectiveTitle.Should().Be("Instant card freeze");
        h.Task(task.Id).IsTitleInherited.Should().BeTrue();
        h.Task(task.Id).Cr.Should().Be(new InheritedCrValues(cr.Id, "CR-1042", "0042", RetailCards.Id, "Retail Cards", Bob.Id, "Bob"));
        h.Card(card.Id).ParentTitle.Should().Be("Instant card freeze");
        h.Card(card.Id).Cr.OwnerName.Should().Be("Bob");
        h.Cr(cr.Id).LinkedTaskCount.Should().Be(1);
    }

    [Fact]
    [Trait("Req", "B-16")]
    public void Archived_tasks_stay_in_the_cache_and_their_cards_show_the_archived_parent()
    {
        var task = Task();
        var card = Card(task);
        using var h = new StoreHarness().StartWith(Snapshot(1, [task], [card]));

        h.Publish(Change(2) with { UpsertedTasks = [Archive(task)] });

        h.Task(task.Id).IsArchived.Should().BeTrue();
        h.Card(card.Id).IsParentArchived.Should().BeTrue();
    }

    [Fact]
    [Trait("Req", "B-16")]
    public void Adding_and_deleting_subtask_cards_updates_the_parent_count()
    {
        var task = Task();
        using var h = new StoreHarness().StartWith(Snapshot(1, [task]));
        var card = Card(task);

        h.Publish(Change(2) with { UpsertedSubtaskCards = [card] });
        h.Task(task.Id).SubtaskCardCount.Should().Be(1);
        h.Card(card.Id).ParentTitle.Should().Be("Card freeze");

        h.Publish(Change(3) with { DeletedSubtaskCards = [card.Id] });
        h.Task(task.Id).SubtaskCardCount.Should().Be(0);
        h.Store.SubtaskCards.Count.Should().Be(0);
    }

    [Fact]
    public void A_parent_edit_recomposes_its_subtask_cards()
    {
        var task = Task("Old title");
        var card = Card(task);
        using var h = new StoreHarness().StartWith(Snapshot(1, [task], [card]));

        h.Publish(Change(2) with { UpsertedTasks = [Rename(task, "New title")] });

        h.Card(card.Id).ParentTitle.Should().Be("New title");
    }

    [Fact]
    [Trait("Req", "AC-17")]
    public void Each_change_set_raises_exactly_one_commit_after_every_cache_is_updated()
    {
        var cr = Cr();
        var (task, link) = Link(Task(), cr);
        var card = Card(task);
        using var h = new StoreHarness().StartWith(Snapshot(1, [task], [card], [cr], [link]));
        var seenAtCommit = new List<(string CrTitle, string TaskTitle, string CardTitle, int Todos)>();
        using var subscription = h.Store.Committed.Subscribe(_ => seenAtCommit.Add((
            h.Cr(cr.Id).Title, h.Task(task.Id).EffectiveTitle, h.Card(card.Id).ParentTitle, h.Store.GeneralTodos.Count)));

        h.Publish(Change(2) with
        {
            UpsertedChangeRequests = [EditCr(cr, c => c with { Title = TitleText.Create("Renamed").Value })],
            UpsertedGeneralTodos = [Todo()],
        });

        seenAtCommit.Should().Equal(("Renamed", "Renamed", "Renamed", 1));
        h.Commits.Select(c => (c.Sequence, c.Reason)).Should().Equal(
            (1L, ProjectionCommitReason.Snapshot), (2L, ProjectionCommitReason.ChangeSet));
    }

    [Fact]
    public void Each_cache_receives_one_change_set_per_commit()
    {
        var cr = Cr();
        var (first, firstLink) = Link(Task("First"), cr);
        var (second, secondLink) = Link(Task("Second"), cr);
        using var h = new StoreHarness().StartWith(Snapshot(1, [first, second], [Card(first), Card(second)], [cr], [firstLink, secondLink]));
        var taskBatches = new List<int>();
        var cardBatches = new List<int>();
        using var tasks = h.Store.Tasks.Connect().Subscribe(c => taskBatches.Add(c.Count));
        using var cards = h.Store.SubtaskCards.Connect().Subscribe(c => cardBatches.Add(c.Count));

        h.Publish(Change(2) with { UpsertedChangeRequests = [EditCr(cr, c => c with { Title = TitleText.Create("Renamed").Value })] });

        taskBatches.Should().Equal(2, 2);
        cardBatches.Should().Equal(2, 2);
    }

    [Fact]
    [Trait("Req", "B-15")]
    [Trait("Req", "AC-19")]
    public void A_cr_becomes_overdue_when_the_local_day_advances_without_any_change_set()
    {
        var endsToday = Cr(planned: PlannedRange.Create(Today.AddDays(-3), Today).Value);
        using var h = new StoreHarness().StartWith(Snapshot(1, crs: [endsToday]));
        h.Cr(endsToday.Id).IsOverdue.Should().BeFalse("the planned range is inclusive of today");

        h.Days.Raise(Today.AddDays(1));
        h.Scheduler.RunAll();

        h.Cr(endsToday.Id).IsOverdue.Should().BeTrue();
        h.Cr(endsToday.Id).DaysOverdue.Should().Be(1);
        h.Commits[^1].Should().Be(new ProjectionCommit(1, ProjectionCommitReason.DayChanged, Today.AddDays(1)));
        h.Snapshots.LoadCount.Should().Be(1, "no database read is needed for a new day");

        h.Days.Raise(Today.AddDays(1));
        h.Scheduler.RunAll();
        h.Commits.Should().HaveCount(2, "a repeated date is not a new day");
    }

    [Fact]
    [Trait("Req", "B-15")]
    public void A_day_signal_before_the_first_load_is_ignored_and_the_load_uses_the_clock()
    {
        using var h = new StoreHarness();
        var endedYesterday = Cr(planned: PlannedRange.Create(Today.AddDays(-2), Today.AddDays(-1)).Value);

        h.Days.Raise(Today.AddDays(5));
        h.StartWith(Snapshot(1, crs: [endedYesterday]));

        h.Cr(endedYesterday.Id).DaysOverdue.Should().Be(1);
        h.Commits.Should().ContainSingle().Which.Today.Should().Be(Today);
    }
}
