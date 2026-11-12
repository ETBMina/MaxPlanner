using System.Globalization;
using AwesomeAssertions;
using MaxPlanner.Presentation.Common;

namespace MaxPlanner.Presentation.Tests.Common;

// Pins each DateText format, so moving the call sites to it changed no text. The invariant culture keeps the expected
// strings independent of the machine's ICU data.
public sealed class DateTextTests
{
    private static readonly DateOnly Day = new(2026, 11, 12);
    private static readonly DateTimeOffset Moment = new(2026, 11, 12, 15, 45, 0, TimeSpan.Zero);

    [Fact]
    public void Each_format_produces_its_pinned_text()
    {
        WithCulture(CultureInfo.InvariantCulture, () =>
        {
            DateText.ShortDate(Day).Should().Be("11/12/2026");
            DateText.ShortDate(Moment).Should().Be("11/12/2026");
            DateText.LongDate(Day).Should().Be("Thursday, 12 November 2026");
            DateText.MonthDay(Day).Should().Be("Nov 12");
            DateText.MonthDayYear(Day).Should().Be("Nov 12, 2026");
            DateText.MonthYear(Day).Should().Be("Nov 2026");
            DateText.LongMonthDay(Day).Should().Be("November 12");
            DateText.ShortDateTime(Moment).Should().Be("11/12/2026 15:45");
        });
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ar-EG")]
    public void Each_format_follows_the_current_culture(string name)
    {
        var culture = CultureInfo.GetCultureInfo(name);
        WithCulture(culture, () =>
        {
            DateText.ShortDate(Day).Should().Be(Day.ToString("d", culture));
            DateText.ShortDate(Moment).Should().Be(Moment.ToString("d", culture));
            DateText.LongDate(Day).Should().Be(Day.ToString("D", culture));
            DateText.MonthDay(Day).Should().Be(Day.ToString("MMM d", culture));
            DateText.MonthDayYear(Day).Should().Be(Day.ToString("MMM d, yyyy", culture));
            DateText.MonthYear(Day).Should().Be(Day.ToString("MMM yyyy", culture));
            DateText.LongMonthDay(Day).Should().Be(Day.ToString("M", culture));
            DateText.ShortDateTime(Moment).Should().Be(Moment.ToString("g", culture));
        });
    }

    private static void WithCulture(CultureInfo culture, Action assert)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            assert();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
