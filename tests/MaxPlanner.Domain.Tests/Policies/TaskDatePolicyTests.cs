using AwesomeAssertions;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Domain.Tests.Fixtures;

namespace MaxPlanner.Domain.Tests.Policies;

[Trait("Req", "B-04")]
public sealed class TaskDatePolicyTests
{
    private static readonly DateOnly LastWeek = Today.AddDays(-7);

    [Fact]
    public void Creating_in_a_start_setting_status_stamps_today_as_start()
    {
        var dates = TaskDatePolicy.Apply(ActualDates.None, null, InProgress, DateIntent.Unchanged, DateIntent.Unchanged, Today).Value;

        dates.Start.Should().Be(Today);
        dates.End.Should().BeNull();
    }

    [Fact]
    public void Creating_in_a_completed_status_stamps_today_as_end()
    {
        TaskDatePolicy.Apply(ActualDates.None, null, Completed, DateIntent.Unchanged, DateIntent.Unchanged, Today)
            .Value.End.Should().Be(Today);
    }

    [Fact]
    public void Moving_into_a_start_setting_status_keeps_an_existing_start_date()
    {
        var existing = ActualDates.Create(LastWeek, null).Value;

        TaskDatePolicy.Apply(existing, Backlog, Developing, DateIntent.Unchanged, DateIntent.Unchanged, Today)
            .Value.Start.Should().Be(LastWeek);
    }

    [Fact]
    public void An_explicit_clear_in_the_same_command_wins_over_automation()
    {
        var dates = TaskDatePolicy.Apply(ActualDates.None, Backlog, InProgress, DateIntent.Cleared, DateIntent.Unchanged, Today).Value;

        dates.Start.Should().BeNull();
    }

    [Fact]
    public void An_explicitly_supplied_date_is_used_instead_of_today()
    {
        TaskDatePolicy.Apply(ActualDates.None, Backlog, InProgress, DateIntent.Set(LastWeek), DateIntent.Unchanged, Today)
            .Value.Start.Should().Be(LastWeek);
    }

    [Fact]
    public void Staying_in_the_same_status_is_not_a_transition_so_nothing_is_filled()
    {
        // A reorder within In Progress, or a rename of the status, must not backfill a cleared date.
        TaskDatePolicy.Apply(ActualDates.None, InProgress, InProgress, DateIntent.Unchanged, DateIntent.Unchanged, Today)
            .Value.Start.Should().BeNull();
    }

    [Fact]
    public void Reopening_a_completed_task_preserves_its_recorded_dates()
    {
        var done = ActualDates.Create(LastWeek, Today.AddDays(-1)).Value;

        var reopened = TaskDatePolicy.Apply(done, Completed, InProgress, DateIntent.Unchanged, DateIntent.Unchanged, Today).Value;

        reopened.Should().Be(done);
    }

    [Fact]
    public void Automation_that_would_invert_the_range_is_a_validation_error_not_a_silent_fix()
    {
        // The start is in the future; completing today would put End before Start.
        var futureStart = ActualDates.Create(Today.AddDays(3), null).Value;

        var result = TaskDatePolicy.Apply(futureStart, InProgress, Completed, DateIntent.Unchanged, DateIntent.Unchanged, Today);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(DomainErrorCodes.DateRangeInverted);
    }
}
