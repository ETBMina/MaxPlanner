using AwesomeAssertions;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Tests.Values;

/// <summary>ADR 0025: multiline text is stored with LF line breaks, whichever route (text box, file, import) supplied it.</summary>
[Trait("Req", "T-05")]
[Trait("Req", "B-12")]
public sealed class LineBreakTests
{
    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r\n\r\nb\r", "a\n\nb\n")]
    [InlineData("\r\r\n\n", "\n\n\n")]
    [InlineData("", "")]
    public void Normalizing_turns_cr_lf_and_bare_cr_into_lf_and_keeps_everything_else(string input, string expected) =>
        LineBreaks.Normalize(input).Should().Be(expected);

    [Fact]
    public void Other_unicode_separators_are_not_line_breaks_for_storage()
    {
        var text = "tab\tand" + (char)0x2028 + "separator";

        LineBreaks.Normalize(text).Should().Be(text);
    }

    [Fact]
    public void Text_without_a_carriage_return_is_returned_as_is() =>
        LineBreaks.Normalize("no breaks\nhere").Should().BeSameAs("no breaks\nhere");

    [Fact]
    public void Long_text_stores_lf_line_breaks_and_keeps_surrounding_whitespace() =>
        LongText.CreateOptional("  Line 1\r\nLine 2\rLine 3  ", "Notes").Value!.Value.Should().Be("  Line 1\nLine 2\nLine 3  ");

    [Fact]
    public void Long_text_limits_count_the_stored_lf_form()
    {
        var text = new string('x', DomainLimits.LongText - 1) + "\r\n";

        LongText.CreateOptional(text, "Notes").IsSuccess.Should().BeTrue("the stored text is one character shorter than the CR LF input");
    }

    [Fact]
    public void Whitespace_only_long_text_with_line_breaks_is_still_no_value() =>
        LongText.CreateOptional(" \r\n \r ", "Notes").Value.Should().BeNull();

    [Fact]
    public void Checklist_text_stores_lf_line_breaks_after_trimming() =>
        ChecklistText.Create("  First\r\nSecond\r").Value.Value.Should().Be("First\nSecond");

    [Fact]
    public void A_multiline_custom_value_stores_lf_line_breaks_and_compares_equal_across_routes()
    {
        var fromTextBox = new MultilineTextCustomValue("a\rb");
        var fromFile = new MultilineTextCustomValue("a\r\nb");

        fromTextBox.Value.Should().Be("a\nb");
        fromTextBox.Should().Be(fromFile);
    }
}
