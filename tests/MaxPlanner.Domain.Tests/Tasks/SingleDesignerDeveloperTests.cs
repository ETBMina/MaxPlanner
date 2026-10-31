using AwesomeAssertions;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Workflow;
using static MaxPlanner.Domain.Tests.Fixtures;

namespace MaxPlanner.Domain.Tests.Tasks;

/// <summary>ADR 0024: a task has at most one Designer and at most one Developer.</summary>
[Trait("Req", "B-02.2")]
public sealed class SingleDesignerDeveloperTests
{
    private static readonly PersonId Alice = new(Guid.NewGuid());
    private static readonly PersonId Bob = new(Guid.NewGuid());

    [Fact]
    public void Content_sets_one_designer_and_one_developer_and_the_same_person_may_hold_both()
    {
        var task = UnlinkedTask().UpdateContent(Content() with { DesignerId = Alice, DeveloperId = Alice }, Now).Value;

        task.DesignerId.Should().Be(Alice);
        task.DeveloperId.Should().Be(Alice);

        var cleared = task.UpdateContent(Content() with { DesignerId = null, DeveloperId = Bob }, Now).Value;
        cleared.DesignerId.Should().BeNull();
        cleared.DeveloperId.Should().Be(Bob);
        cleared.Revision.Should().Be(task.Revision + 1);
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void Removing_a_person_replaces_them_in_both_roles()
    {
        var task = WithPeople(designer: Alice, developer: Alice);

        var remapped = task.Remap(new LookupRemap(LookupKind.Person, Alice.Value, Bob.Value), null, Now)!;

        remapped.DesignerId.Should().Be(Bob);
        remapped.DeveloperId.Should().Be(Bob);
        remapped.Revision.Should().Be(task.Revision + 1);
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void Removing_a_person_with_clear_empties_only_the_role_they_held()
    {
        var task = WithPeople(designer: Alice, developer: Bob);

        var remapped = task.Remap(new LookupRemap(LookupKind.Person, Bob.Value, null), null, Now)!;

        remapped.DesignerId.Should().Be(Alice);
        remapped.DeveloperId.Should().BeNull();
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void Removing_a_person_the_task_does_not_name_changes_nothing()
    {
        var task = WithPeople(designer: Alice, developer: null);

        task.Remap(new LookupRemap(LookupKind.Person, Bob.Value, null), null, Now).Should().BeNull();
    }

    private static TaskItem WithPeople(PersonId? designer, PersonId? developer) =>
        UnlinkedTask().UpdateContent(Content() with { DesignerId = designer, DeveloperId = developer }, Now).Value;
}
