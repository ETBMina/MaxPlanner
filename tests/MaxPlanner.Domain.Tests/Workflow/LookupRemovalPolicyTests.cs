using AwesomeAssertions;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Domain.Tests.Workflow;

[Trait("Req", "B-04")]
public sealed class LookupRemovalPolicyTests
{
    private static readonly TaskStatusId Backlog = new(Guid.NewGuid());
    private static readonly TaskStatusId Review = new(Guid.NewGuid());
    private static readonly PersonId Alice = new(Guid.NewGuid());
    private static readonly PersonId Bob = new(Guid.NewGuid());

    [Theory]
    [InlineData(LookupKind.TaskStatus)]
    [InlineData(LookupKind.CrStatus)]
    [InlineData(LookupKind.Priority)]
    public void The_last_status_or_priority_cannot_be_removed(LookupKind kind)
    {
        LookupRemovalPolicy.Validate(kind, [Backlog], Backlog, null, LookupUsage.Unused)!.Code.Should().Be(DomainErrorCodes.LastLookupRequired);
    }

    [Fact]
    [Trait("Req", "AC-08")]
    public void An_in_use_or_default_status_needs_a_different_existing_replacement()
    {
        TaskStatusId[] all = [Backlog, Review];

        LookupRemovalPolicy.Validate(LookupKind.TaskStatus, all, Review, null, new LookupUsage(3, 0, false))!.Code.Should().Be(DomainErrorCodes.ReplacementRequired);
        LookupRemovalPolicy.Validate(LookupKind.TaskStatus, all, Backlog, null, new LookupUsage(0, 0, true))!.Code.Should().Be(DomainErrorCodes.ReplacementRequired);
        LookupRemovalPolicy.Validate(LookupKind.TaskStatus, all, Review, Review, new LookupUsage(3, 0, false))!.Code.Should().Be(DomainErrorCodes.ReplacementSameAsRemoved);
        LookupRemovalPolicy.Validate(LookupKind.TaskStatus, all, Review, new TaskStatusId(Guid.NewGuid()), new LookupUsage(3, 0, false))!
            .Code.Should().Be(DomainErrorCodes.ItemNotFound);
        LookupRemovalPolicy.Validate(LookupKind.TaskStatus, all, Review, Backlog, new LookupUsage(3, 0, false)).Should().BeNull();
        LookupRemovalPolicy.Validate(LookupKind.TaskStatus, all, Review, null, LookupUsage.Unused).Should().BeNull();
    }

    [Fact]
    [Trait("Req", "AC-31")]
    public void A_cr_owner_or_the_default_cr_owner_can_only_be_replaced_while_other_roles_may_be_cleared()
    {
        PersonId[] people = [Alice, Bob];

        LookupRemovalPolicy.Validate(LookupKind.Person, people, Alice, null, new LookupUsage(4, 1, false))!.Code.Should().Be(DomainErrorCodes.ReplacementRequired);
        LookupRemovalPolicy.Validate(LookupKind.Person, people, Alice, null, new LookupUsage(0, 0, true))!.Code.Should().Be(DomainErrorCodes.ReplacementRequired);
        LookupRemovalPolicy.Validate(LookupKind.Person, people, Alice, Bob, new LookupUsage(4, 1, true)).Should().BeNull();
        LookupRemovalPolicy.Validate(LookupKind.Person, people, Alice, null, new LookupUsage(7, 0, false)).Should().BeNull("designer, developer, and review developer uses may be cleared");
        LookupRemovalPolicy.Validate(LookupKind.Person, [Alice], Alice, null, LookupUsage.Unused).Should().BeNull("the people list may become empty");
    }

    [Theory]
    [InlineData(LookupKind.Squad)]
    [InlineData(LookupKind.Tag)]
    public void Squads_and_tags_may_be_cleared(LookupKind kind)
    {
        var only = Guid.NewGuid();

        LookupRemovalPolicy.Validate(kind, [only], only, null, new LookupUsage(12, 0, false)).Should().BeNull();
    }

    [Fact]
    public void Removing_an_unknown_value_is_rejected()
    {
        LookupRemovalPolicy.Validate(LookupKind.Tag, [Guid.NewGuid()], Guid.NewGuid(), null, LookupUsage.Unused)!.Code.Should().Be(DomainErrorCodes.ItemNotFound);
    }
}
