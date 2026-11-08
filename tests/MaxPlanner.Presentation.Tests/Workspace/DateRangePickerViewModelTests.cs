using System.Globalization;
using AwesomeAssertions;
using MaxPlanner.Presentation.Common.Filters;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// The Customised range picker (design §2.6, owner feedback 2026-11-08): while the end is being picked the hovered or
/// focused day previews the range on the band without committing it, and the footer reads "{start} – Select an end
/// date", then "{start} – {end}" in the culture's short date format.
/// </summary>
[Trait("Req", "B-10")]
public sealed class DateRangePickerViewModelTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);
    private static readonly DateOnly Start = new(2026, 11, 3);
    private static readonly DateOnly End = new(2026, 11, 13);

    [Fact]
    public void The_footer_asks_for_an_end_date_after_the_first_pick_and_shows_both_dates_after_the_second()
    {
        var picker = Picker();
        var commits = 0;
        picker.Attach(() => commits++);
        picker.SelectionText.Should().BeEmpty();

        picker.Pick(Start);

        picker.IsPickingEnd.Should().BeTrue();
        picker.SelectionText.Should().Be(Short(Start) + " – Select an end date");
        commits.Should().Be(1, "the first pick applies a one-day range at once");

        picker.Pick(End);

        picker.IsPickingEnd.Should().BeFalse();
        picker.SelectionText.Should().Be(Short(Start) + " – " + Short(End));
        (picker.From, picker.To).Should().Be(((DateOnly?)Start, (DateOnly?)End));
        commits.Should().Be(2);
    }

    [Fact]
    public void Hovering_or_focusing_a_day_previews_the_range_without_committing_it()
    {
        var picker = Picker();
        var days = picker.Months.SelectMany(m => m.Days).Where(d => d.IsInMonth).ToList();
        var commits = 0;
        picker.Attach(() => commits++);
        picker.Pick(Start);

        picker.Preview(End);

        Day(days, new DateOnly(2026, 11, 8)).IsRangeMiddle.Should().BeTrue("today is inside the previewed band");
        Day(days, new DateOnly(2026, 11, 8)).ShowsBandText.Should().BeFalse("today keeps its accent circle and text");
        Day(days, Start).IsRangeEnd.Should().BeTrue();
        Day(days, End).IsRangeEnd.Should().BeTrue();
        Day(days, End).ShowsEndText.Should().BeTrue();
        Day(days, new DateOnly(2026, 11, 10)).ShowsBand.Should().BeTrue();
        Day(days, new DateOnly(2026, 11, 14)).IsInRange.Should().BeFalse();
        (picker.From, picker.To).Should().Be(((DateOnly?)Start, (DateOnly?)Start), "a preview is not committed");
        commits.Should().Be(1);

        picker.Preview(new DateOnly(2026, 10, 30));
        Day(days, new DateOnly(2026, 11, 1)).IsInRange.Should().BeTrue("a preview before the start runs backwards");
        Day(days, End).IsInRange.Should().BeFalse();

        picker.Preview(null);
        Day(days, new DateOnly(2026, 11, 1)).IsInRange.Should().BeFalse("leaving the days shows the committed range");
        Day(days, Start).IsRangeEnd.Should().BeTrue();
    }

    [Fact]
    public void Without_a_pending_end_a_hover_does_not_preview()
    {
        var picker = Picker();
        var days = picker.Months.SelectMany(m => m.Days).Where(d => d.IsInMonth).ToList();
        picker.Pick(Start);
        picker.Pick(End);

        picker.Preview(new DateOnly(2026, 11, 20));

        Day(days, new DateOnly(2026, 11, 20)).IsInRange.Should().BeFalse();
        Day(days, new DateOnly(2026, 11, 10)).ShowsBand.Should().BeTrue("the committed range keeps the same band");
        Day(days, new DateOnly(2026, 11, 10)).ShowsBandText.Should().BeTrue();
    }

    [Fact]
    public void Days_of_the_neighbouring_months_never_show_the_band()
    {
        var picker = Picker();
        picker.Pick(new DateOnly(2026, 10, 1));
        picker.Pick(new DateOnly(2027, 1, 31));

        picker.Months.SelectMany(m => m.Days).Where(d => !d.IsInMonth).Should().OnlyContain(d => d.IsInRange && !d.ShowsBand);
    }

    [Fact]
    public void Clearing_ends_the_pending_pick_and_empties_the_footer()
    {
        var picker = Picker();
        picker.Pick(Start);

        picker.ClearRangeCommand.Execute(null);

        picker.IsPickingEnd.Should().BeFalse();
        picker.HasRange.Should().BeFalse();
        picker.SelectionText.Should().BeEmpty();
    }

    [Fact]
    public void People_lists_show_avatars_and_other_lists_do_not()
    {
        var menu = new FilterMenuViewModel(new FakeStrings(), _ => { });
        var person = new[] { (Guid.NewGuid(), "Mina Talaat Hakim", string.Empty, FilterGlyphTone.Default) };

        foreach (var key in new[] { "Owner", "Designers", "Developers" })
        {
            MenuFields.Choices(menu, key, person, []).Choices.Should().OnlyContain(c => c.IsPerson && !c.HasGlyph, key);
        }

        MenuFields.Choices(menu, "Squad", person, []).Choices.Should().OnlyContain(c => !c.IsPerson);
        MenuFields.Choices(menu, "Status", [(Guid.NewGuid(), "Done", "x", FilterGlyphTone.Success)], []).Choices.Single().HasGlyph.Should().BeTrue();
    }

    private static DateRangePickerViewModel Picker() => new(
        null,
        null,
        Today,
        new FakeStrings().With("Filters_RangeSelectEnd", "{0} – Select an end date").With("Filters_RangeSelection", "{0} – {1}"));

    private static CalendarDayViewModel Day(List<CalendarDayViewModel> days, DateOnly date) => days.Single(d => d.Date == date);

    private static string Short(DateOnly day) => day.ToString("d", CultureInfo.CurrentCulture);
}
