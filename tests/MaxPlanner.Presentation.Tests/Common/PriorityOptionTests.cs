using AwesomeAssertions;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Common;

public sealed class PriorityOptionTests
{
    [Fact]
    public void From_ListsPrioritiesInRankOrderWithTheirGlyphLevel()
    {
        PriorityDefinition Priority(string name, int order) => new(new PriorityId(Guid.NewGuid()), Records.Name(name), order);
        var workflow = Records.Workflow with { Priorities = [Priority("Low", 3), Priority("Urgent", 0), Priority("Medium", 2), Priority("High", 1)] };

        var options = PriorityOption.From(workflow);

        options.Select(o => (o.Name, o.Level)).Should().Equal(
            ("Urgent", PriorityLevel.Level1), ("High", PriorityLevel.Level2), ("Medium", PriorityLevel.Mid), ("Low", PriorityLevel.Lowest));
        ((IPriorityChoice)options[0]).DisplayName.Should().Be("Urgent", "the accessible name stays the priority text");
    }
}
