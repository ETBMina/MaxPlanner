using AwesomeAssertions;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;

namespace MaxPlanner.Domain.Tests.Policies;

public sealed class OrderingAndScopeTests
{
    [Fact]
    [Trait("Req", "B-07")]
    public void Between_finds_a_rank_inside_a_gap_and_reports_when_the_gap_is_exhausted()
    {
        RankPolicy.Between(1_024, 2_048).Should().Be(1_536);
        RankPolicy.Between(null, 1_024).Should().BeLessThan(1_024);
        RankPolicy.Between(2_048, null).Should().BeGreaterThan(2_048);
        RankPolicy.Between(10, 11).Should().BeNull();
    }

    [Fact]
    [Trait("Req", "B-07")]
    public void Renumbering_spaces_the_whole_column_but_reports_only_records_that_change()
    {
        // The second card already sits on its target rank, so only the other two are rewritten.
        var column = new List<(string Key, long Rank)> { ("hidden-a", 5), ("visible-b", 2 * RankPolicy.Step), ("hidden-c", 2 * RankPolicy.Step + 1) };

        var changes = RankPolicy.Renumber(column);

        changes.Should().Equal(("hidden-a", RankPolicy.Step), ("hidden-c", 3 * RankPolicy.Step));
    }

    [Fact]
    [Trait("Req", "B-05")]
    [Trait("Req", "AC-30")]
    public void Cascade_deletion_proceeds_only_for_exactly_the_confirmed_tasks()
    {
        var a = new TaskId(Guid.NewGuid());
        var b = new TaskId(Guid.NewGuid());
        var lateArrival = new TaskId(Guid.NewGuid());

        CrDeletionPolicy.EnsureConfirmedScope([a, b], [b, a]).Should().BeNull();
        CrDeletionPolicy.EnsureConfirmedScope([a, b, lateArrival], [a, b])!.Code.Should().Be(DomainErrorCodes.CascadeScopeChanged);
        CrDeletionPolicy.EnsureConfirmedScope([a], [a, b])!.Code.Should().Be(DomainErrorCodes.CascadeScopeChanged);
    }
}
