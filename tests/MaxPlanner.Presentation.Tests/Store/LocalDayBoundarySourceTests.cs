using AwesomeAssertions;
using MaxPlanner.Presentation.Common.Store;
using Microsoft.Extensions.Time.Testing;

namespace MaxPlanner.Presentation.Tests.Store;

[Trait("Req", "T-07")]
[Trait("Req", "B-15")]
public sealed class LocalDayBoundarySourceTests
{
    private static readonly TimeZoneInfo PlusThree = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");

    [Fact]
    public void The_midnight_timer_emits_the_new_local_date_once()
    {
        // 23:59 local in UTC+3.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 20, 59, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(PlusThree);
        using var source = new LocalDayBoundarySource(time);
        var dates = new List<DateOnly>();
        using var subscription = source.LocalDates.Subscribe(dates.Add);

        time.Advance(TimeSpan.FromSeconds(59));
        dates.Should().BeEmpty();

        time.Advance(TimeSpan.FromSeconds(2));
        dates.Should().Equal(new DateOnly(2026, 10, 23));

        time.Advance(TimeSpan.FromHours(23));
        dates.Should().ContainSingle("the timer re-arms for the next midnight, not for the same one");

        time.Advance(TimeSpan.FromHours(1));
        dates.Should().Equal(new DateOnly(2026, 10, 23), new DateOnly(2026, 10, 24));
    }

    [Fact]
    public void Checks_emit_only_when_the_local_date_changed()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
        using var source = new LocalDayBoundarySource(time);
        var dates = new List<DateOnly>();
        using var subscription = source.LocalDates.Subscribe(dates.Add);

        source.Check(DayCheckReason.Activation);
        source.Check(DayCheckReason.Resume);

        dates.Should().BeEmpty();
        source.CurrentDate.Should().Be(new DateOnly(2026, 10, 22));
    }

    [Fact]
    public void A_time_zone_change_that_crosses_midnight_is_detected_by_the_change_check()
    {
        // 22:30 UTC on the 22nd: still the 22nd in UTC, already the 23rd in UTC+3.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 22, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        using var source = new LocalDayBoundarySource(time);
        var dates = new List<DateOnly>();
        using var subscription = source.LocalDates.Subscribe(dates.Add);

        time.SetLocalTimeZone(PlusThree);
        source.Check(DayCheckReason.ClockOrTimeZoneChange);

        dates.Should().Equal(new DateOnly(2026, 10, 23));
    }

    [Fact]
    public void Resume_after_sleeping_past_midnight_emits_the_new_date()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 21, 0, 0, TimeSpan.Zero));
        using var source = new LocalDayBoundarySource(time);
        var dates = new List<DateOnly>();
        using var subscription = source.LocalDates.Subscribe(dates.Add);

        // The wall clock jumps while the machine sleeps; timers do not see the time pass.
        time.SetUtcNow(new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.Zero));
        source.Check(DayCheckReason.Resume);

        dates.Should().Equal(new DateOnly(2026, 10, 24));
    }

    [Fact]
    public void The_next_midnight_follows_daylight_saving_changes()
    {
        // Europe/Berlin-like rule: clocks go back from 03:00 to 02:00 on the last Sunday of October,
        // so that local day lasts 25 hours.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        var zone = TimeZoneInfo.CreateCustomTimeZone("CET-test", TimeSpan.FromHours(1), "CET", "CET", "CEST", [rule]);

        // 00:00 local (CEST, UTC+2) on Sunday 2026-10-25.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(zone);

        LocalDayBoundarySource.DelayUntilNextLocalMidnight(time).Should().Be(TimeSpan.FromHours(25));
    }

    [Fact]
    public void Disposing_stops_the_timer_and_completes_the_stream()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 20, 59, 0, TimeSpan.Zero));
        var source = new LocalDayBoundarySource(time);
        var completed = false;
        using var subscription = source.LocalDates.Subscribe(_ => { }, () => completed = true);

        source.Dispose();
        time.Advance(TimeSpan.FromDays(2));
        source.Check(DayCheckReason.Activation);

        completed.Should().BeTrue();
    }
}
