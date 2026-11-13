using AwesomeAssertions;
using MaxPlanner.Domain.Calendar;
using MaxPlanner.Presentation.Common;
using GlobalizationDayOfWeek = Windows.Globalization.DayOfWeek;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>Every date picker and calendar view starts its weeks on the app workweek's first day, not the system's.</summary>
public sealed class DatePickersTests
{
    [Theory]
    [InlineData(DayOfWeek.Sunday, GlobalizationDayOfWeek.Sunday)]
    [InlineData(DayOfWeek.Monday, GlobalizationDayOfWeek.Monday)]
    [InlineData(DayOfWeek.Tuesday, GlobalizationDayOfWeek.Tuesday)]
    [InlineData(DayOfWeek.Wednesday, GlobalizationDayOfWeek.Wednesday)]
    [InlineData(DayOfWeek.Thursday, GlobalizationDayOfWeek.Thursday)]
    [InlineData(DayOfWeek.Friday, GlobalizationDayOfWeek.Friday)]
    [InlineData(DayOfWeek.Saturday, GlobalizationDayOfWeek.Saturday)]
    public void Each_day_maps_to_the_same_picker_day(DayOfWeek day, GlobalizationDayOfWeek expected) =>
        DatePickers.FirstDayOf(day).Should().Be(expected);

    [Fact]
    public void An_unknown_day_is_refused() =>
        FluentActions.Invoking(() => DatePickers.FirstDayOf((DayOfWeek)7)).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void The_pickers_start_the_week_on_the_workweeks_first_day()
    {
        Workweek.FirstDay.Should().Be(DayOfWeek.Sunday, "the release-1 workweek runs Sunday to Thursday (ADR 0020)");
        DatePickers.FirstDayOfWeek.Should().Be(GlobalizationDayOfWeek.Sunday);
    }
}
