using AwesomeAssertions;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Tests.Values;

public sealed class ValueRulesTests
{
    [Fact]
    [Trait("Req", "B-03.2")]
    public void Cr_numbers_keep_their_spelling_but_compare_by_a_trimmed_case_insensitive_form()
    {
        var number = CrNumber.Create("  cr-105 ").Value;

        number.Display.Should().Be("cr-105");
        number.Normalized.Should().Be(CrNumber.Create("CR-105").Value.Normalized);
    }

    [Fact]
    [Trait("Req", "B-03.3")]
    public void Task_numbers_are_text_that_keeps_leading_zeros_and_punctuation()
    {
        TaskNumber.CreateOptional(" 00457/B ").Value!.Value.Should().Be("00457/B");
        TaskNumber.CreateOptional("   ").Value.Should().BeNull();
    }

    [Fact]
    [Trait("Req", "B-02.11")]
    public void Only_http_and_https_links_are_accepted()
    {
        HttpUrl.CreateOptional("https://wiki.example/cr-1042").IsSuccess.Should().BeTrue();
        HttpUrl.CreateOptional("file:///C:/Windows/system32/calc.exe").Error!.Code.Should().Be(DomainErrorCodes.InvalidUrl);
        HttpUrl.CreateOptional("javascript:alert(1)").Error!.Code.Should().Be(DomainErrorCodes.InvalidUrl);
    }

    [Fact]
    [Trait("Req", "T-05")]
    public void Notes_keep_line_breaks_and_unicode_exactly()
    {
        const string notes = "  Line one\r\n\tالعربية — ✓\n";

        LongText.CreateOptional(notes, "Notes").Value!.Value.Should().Be(notes);
    }

    [Fact]
    [Trait("Req", "B-02.10")]
    public void Over_limit_text_is_rejected_with_the_limit_instead_of_being_truncated()
    {
        var result = TitleText.Create(new string('x', DomainLimits.Title + 1));

        result.Error!.Code.Should().Be(DomainErrorCodes.TooLong);
        result.Error.Limit.Should().Be(DomainLimits.Title);
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void Planned_ranges_are_inclusive_and_need_both_ends()
    {
        var oneDay = PlannedRange.Create(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 14)).Value;

        oneDay.DurationDays.Should().Be(1);
        PlannedRange.CreateOptional(new DateOnly(2026, 10, 14), null).IsSuccess.Should().BeFalse();
        PlannedRange.Create(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 14)).Error!.Code.Should().Be(DomainErrorCodes.DateRangeInverted);
    }

    [Fact]
    [Trait("Req", "B-10")]
    public void Planned_range_filters_use_interval_overlap_with_open_bounds()
    {
        var range = PlannedRange.Create(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 14)).Value;

        range.Overlaps(new DateOnly(2026, 10, 14), null).Should().BeTrue();
        range.Overlaps(new DateOnly(2026, 10, 15), null).Should().BeFalse();
        range.Overlaps(null, new DateOnly(2026, 10, 5)).Should().BeTrue();
        range.Overlaps(null, null).Should().BeTrue();
    }
}
