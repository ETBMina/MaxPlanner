using AwesomeAssertions;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Domain.Tests.Fixtures;

namespace MaxPlanner.Domain.Tests.Policies;

[Trait("Req", "B-15")]
[Trait("Req", "AC-19")]
public sealed class OverduePolicyTests
{
    [Theory]
    [InlineData(-1, false, true, 1)]   // ended yesterday, open: overdue by one day
    [InlineData(0, false, false, 0)]   // ends today: inclusive, not yet overdue
    [InlineData(1, false, false, 0)]   // ends tomorrow
    [InlineData(-10, true, false, 0)]  // long past but completed: never overdue
    public void Only_incomplete_crs_whose_inclusive_range_ended_before_today_are_overdue(int endOffsetDays, bool completed, bool expectedOverdue, int expectedDays)
    {
        var range = PlannedRange.Create(Today.AddDays(-20), Today.AddDays(endOffsetDays)).Value;

        OverduePolicy.IsOverdue(range, completed, Today).Should().Be(expectedOverdue);
        OverduePolicy.DaysOverdue(range, completed, Today).Should().Be(expectedDays);
    }

    [Fact]
    public void An_unscheduled_cr_is_never_overdue()
    {
        OverduePolicy.IsOverdue(null, statusIsCompleted: false, Today).Should().BeFalse();
    }
}
