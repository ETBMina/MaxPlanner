using AwesomeAssertions;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.ReadModels;

[Trait("Req", "T-13")]
public sealed class ReadModelComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private readonly ReadModelComposer _composer = new();

    [Fact]
    [Trait("Req", "AC-22")]
    public void An_unlinked_task_shows_its_own_title_the_general_squad_and_no_owner()
    {
        var model = _composer.ComposeTask(Unlinked(), null, subtaskCardCount: 2, SeedWorkflow);

        model.EffectiveTitle.Should().Be("Card freeze");
        model.IsTitleInherited.Should().BeFalse();
        model.Cr.Should().Be(new InheritedCrValues(null, null, null, null, "General", null, null));
        model.Status.Should().Be(new LookupRef<TaskStatusId>(Backlog.Id, "Backlog"));
        model.SubtaskCardCount.Should().Be(2);
    }

    [Fact]
    [Trait("Req", "AC-04")]
    [Trait("Req", "AC-22")]
    public void A_linked_task_and_its_cards_always_show_the_current_cr_values()
    {
        var cr = Cr(squad: RetailCards.Id, owner: Alice.Id);
        var task = Unlinked().LinkTo(cr.Id, Now).Value;
        var card = SubtaskCard.Create(new SubtaskCardId(Guid.NewGuid()), task.Id, SubtitleText.Create("Copy fix").Value, null, Backlog,
            DateIntent.Unchanged, DateIntent.Unchanged, 1, Today, Now).Value;
        var edited = cr.Update(Content(cr, "Renamed CR", squad: null, owner: Bob.Id), CrBacklog, Now);

        var before = _composer.ComposeTask(task, cr, 1, SeedWorkflow);
        var after = _composer.ComposeTask(task, edited, 1, SeedWorkflow);
        var cardAfter = _composer.ComposeSubtaskCard(card, task, edited, SeedWorkflow);

        before.EffectiveTitle.Should().Be("Instant card freeze");
        before.Cr.Should().Be(new InheritedCrValues(cr.Id, "CR-1042", "0042", RetailCards.Id, "Retail Cards", Alice.Id, "Alice"));
        after.EffectiveTitle.Should().Be("Renamed CR");
        after.IsTitleInherited.Should().BeTrue();
        after.Cr.Should().Be(new InheritedCrValues(cr.Id, "CR-1042", "0042", null, "General", Bob.Id, "Bob"));
        cardAfter.ParentTitle.Should().Be("Renamed CR");
        cardAfter.ParentService.Should().Be("Mobile Banking");
        cardAfter.Cr.Should().Be(after.Cr);
    }

    [Fact]
    public void Renaming_a_lookup_changes_the_composed_name_but_not_the_reference()
    {
        var renamed = SeedWorkflow with { Squads = [RetailCards with { Name = LookupName.Create("Cards & Payments").Value }] };
        var cr = Cr(squad: RetailCards.Id, owner: Alice.Id);

        var model = _composer.ComposeChangeRequest(cr, 0, Today, renamed);

        model.SquadId.Should().Be(RetailCards.Id);
        model.SquadName.Should().Be("Cards & Payments");
    }

    [Fact]
    public void The_general_label_comes_from_the_caller_for_localization()
    {
        new ReadModelComposer("Allgemein").ComposeTask(Unlinked(), null, 0, SeedWorkflow).Cr.SquadName.Should().Be("Allgemein");
    }

    [Fact]
    [Trait("Req", "B-15")]
    [Trait("Req", "AC-19")]
    public void Cr_overdue_flags_follow_the_inclusive_planned_end_and_the_cr_status_only()
    {
        var endedYesterday = Cr(planned: PlannedRange.Create(Today.AddDays(-5), Today.AddDays(-1)).Value);
        var endsToday = Cr(planned: PlannedRange.Create(Today, Today).Value);

        var overdue = _composer.ComposeChangeRequest(endedYesterday, linkedTaskCount: 3, Today, SeedWorkflow);
        var completed = _composer.ComposeChangeRequest(endedYesterday.Update(Content(endedYesterday), CrCompleted, Now), 3, Today, SeedWorkflow);
        var onTime = _composer.ComposeChangeRequest(endsToday, 0, Today, SeedWorkflow);

        overdue.IsOverdue.Should().BeTrue();
        overdue.DaysOverdue.Should().Be(1);
        overdue.LinkedTaskCount.Should().Be(3);
        completed.IsOverdue.Should().BeFalse();
        onTime.IsOverdue.Should().BeFalse();
    }

    [Fact]
    public void Composing_a_task_with_a_cr_it_is_not_linked_to_is_a_programming_error()
    {
        var act = () => _composer.ComposeTask(Unlinked(), Cr(), 0, SeedWorkflow);

        act.Should().Throw<ArgumentException>();
    }

    private static TaskItem Unlinked() =>
        TaskItem.Create(new TaskId(Guid.NewGuid()),
            new TaskContent(TitleText.Create("Card freeze").Value, ServiceText.CreateOptional("Mobile Banking").Value, null, null, [], null, CustomValue.NoValues),
            Backlog, Medium.Id, DateIntent.Unchanged, DateIntent.Unchanged, null, 1, Today, Now).Value;

    private static ChangeRequest Cr(SquadId? squad = null, PersonId? owner = null, PlannedRange? planned = null)
    {
        var content = new ChangeRequestContent(CrNumber.Create("CR-1042").Value, TitleText.Create("Instant card freeze").Value, owner ?? Alice.Id,
            squad, Medium.Id, planned, null, null, null, TaskNumber.CreateOptional("0042").Value, CustomValue.NoValues);
        return ChangeRequest.Create(new ChangeRequestId(Guid.NewGuid()), content, CrInProgress, Now);
    }

    private static ChangeRequestContent Content(ChangeRequest cr, string? title = null, SquadId? squad = null, PersonId? owner = null) =>
        new(cr.Number, title is null ? cr.Title : TitleText.Create(title).Value, owner ?? cr.OwnerId, squad, cr.PriorityId, cr.Planned,
            cr.EmailSubject, cr.SolutionDesignUrl, cr.Description, cr.TaskNumber, cr.CustomValues);
}
