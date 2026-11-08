using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Board;

/// <summary>Design §4.2 item 9: the Board card people footer.</summary>
[Trait("Req", "B-07")]
public sealed class BoardCardPeopleTests
{
    private static readonly LookupRef<PersonId> Mina = new(new PersonId(Guid.NewGuid()), "Mina Hakim");
    private static readonly LookupRef<PersonId> Sara = new(new PersonId(Guid.NewGuid()), "Sara Adel");
    private static readonly LookupRef<PersonId> Omar = new(new PersonId(Guid.NewGuid()), "Omar Nabil");

    private static readonly FakeStrings Strings = new FakeStrings()
        .With("Board_PersonRoleCrOwner", "CR Owner")
        .With("Field_Designer", "Designer")
        .With("Field_Developer", "Developer")
        .With("Board_PersonRoles2", "{0} · {1}")
        .With("Board_PersonRoles3", "{0} · {1} · {2}")
        .With("Board_PersonName1", "{0}, {1}")
        .With("Board_PersonName2", "{0}, {1}, {2}")
        .With("Board_PersonName3", "{0}, {1}, {2}, {3}");

    [Fact]
    public void A_person_in_every_role_appears_once_with_all_three_roles()
    {
        var people = BoardCardPerson.Build(Guid.NewGuid(), Mina, Mina, Mina, Strings);

        people.Should().ContainSingle();
        people[0].PersonId.Should().Be(Mina.Id);
        people[0].Name.Should().Be("Mina Hakim");
        people[0].RolesText.Should().Be("CR Owner · Designer · Developer");
        people[0].AccessibleName.Should().Be("Mina Hakim, CR Owner, Designer, Developer");
    }

    [Fact]
    public void Distinct_people_appear_in_role_order()
    {
        var people = BoardCardPerson.Build(Guid.NewGuid(), Mina, Sara, Omar, Strings);

        people.Select(p => p.Name).Should().Equal("Mina Hakim", "Sara Adel", "Omar Nabil");
        people.Select(p => p.RolesText).Should().Equal("CR Owner", "Designer", "Developer");
        people.Select(p => p.AccessibleName).Should().Equal("Mina Hakim, CR Owner", "Sara Adel, Designer", "Omar Nabil, Developer");
    }

    [Fact]
    public void Shared_roles_keep_the_first_role_position()
    {
        var people = BoardCardPerson.Build(Guid.NewGuid(), Mina, Sara, Mina, Strings);

        people.Select(p => p.RolesText).Should().Equal("CR Owner · Developer", "Designer");
        people[0].AccessibleName.Should().Be("Mina Hakim, CR Owner, Developer");
    }

    [Fact]
    public void Missing_roles_are_skipped()
    {
        BoardCardPerson.Build(Guid.NewGuid(), null, null, Omar, Strings)
            .Should().ContainSingle().Which.RolesText.Should().Be("Developer");
        BoardCardPerson.Build(Guid.NewGuid(), null, null, null, Strings).Should().BeEmpty();
    }

    [Fact]
    public void Automation_ids_are_stable_and_unique_per_card_and_person()
    {
        var card = Guid.NewGuid();
        var first = BoardCardPerson.Build(card, Mina, Sara, null, Strings);
        var again = BoardCardPerson.Build(card, Mina, Sara, null, Strings);

        first.Select(p => p.AutomationId).Should().Equal(again.Select(p => p.AutomationId)).And.OnlyHaveUniqueItems();
        first[0].AutomationId.Should().Be($"Board_CardPerson_{card:N}_{Mina.Id.Value:N}");
    }

    [Fact]
    public void A_task_card_lists_its_owner_designer_and_developer()
    {
        var people = BoardCardPerson.From(WorkItemRecord.From(Task(Mina, Sara, Mina)), Strings);

        people.Select(p => p.AccessibleName).Should().Equal("Mina Hakim, CR Owner, Developer", "Sara Adel, Designer");
    }

    [Fact]
    public void A_subtask_card_lists_only_the_inherited_cr_owner()
    {
        var card = new SubtaskCardReadModel(
            new SubtaskCardId(Guid.NewGuid()), BoardCardKind.SubtaskCard, new TaskId(Guid.NewGuid()), "Rollout", "Card freeze", null, false,
            Status, 0, false, Cr(Mina), null, null, null, 1000, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

        BoardCardPerson.From(WorkItemRecord.From(card), Strings)
            .Should().ContainSingle().Which.AccessibleName.Should().Be("Mina Hakim, CR Owner");
    }

    [Fact]
    public void An_unlinked_task_without_designer_or_developer_has_no_people()
    {
        var card = ViewModel(Task(null, null, null));

        card.People.Should().BeEmpty();
        card.HasPeople.Should().BeFalse();
    }

    [Fact]
    public void Changing_a_role_replaces_the_people_and_notifies()
    {
        var card = ViewModel(Task(Mina, null, null));
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        card.Update(WorkItemRecord.From(Task(Mina, Mina, Sara, id: card.Model.Task!.Id)));

        changed.Should().Contain([nameof(BoardCardViewModel.People), nameof(BoardCardViewModel.HasPeople)]);
        card.People.Select(p => p.RolesText).Should().Equal("CR Owner · Designer", "Developer");
    }

    [Fact]
    public void An_update_that_keeps_the_people_keeps_the_same_list()
    {
        var card = ViewModel(Task(Mina, Sara, null));
        var before = card.People;
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        card.Update(WorkItemRecord.From(Task(Mina, Sara, null, id: card.Model.Task!.Id, title: "Renamed")));

        card.People.Should().BeSameAs(before);
        changed.Should().NotContain(nameof(BoardCardViewModel.People)).And.Contain(nameof(BoardCardViewModel.Title));
    }

    private static readonly LookupRef<TaskStatusId> Status = new(new TaskStatusId(Guid.NewGuid()), "To do");

    private static BoardCardViewModel ViewModel(TaskReadModel task) =>
        new(WorkItemRecord.From(task), Strings, _ => PriorityLevel.Lowest);

    private static InheritedCrValues Cr(LookupRef<PersonId>? owner) =>
        new(owner is null ? null : new ChangeRequestId(Guid.NewGuid()), owner is null ? null : "CR-1067", null, null, "General", owner?.Id, owner?.Name);

    private static TaskReadModel Task(
        LookupRef<PersonId>? owner, LookupRef<PersonId>? designer, LookupRef<PersonId>? developer, TaskId? id = null, string title = "Card freeze") =>
        new(
            id ?? new TaskId(Guid.NewGuid()), BoardCardKind.Task, title, false, null, Status, 0, false,
            new LookupRef<PriorityId>(new PriorityId(Guid.NewGuid()), "High"), 0, Cr(owner), designer, developer,
            [], null, null, null, false, 1000, 0, 0, [], 0, new Dictionary<FieldId, CustomValue>(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);
}
