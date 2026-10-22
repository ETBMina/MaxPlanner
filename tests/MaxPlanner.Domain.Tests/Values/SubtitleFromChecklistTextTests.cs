using AwesomeAssertions;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Tests.Values;

[Trait("Req", "B-16.3")]
public sealed class SubtitleFromChecklistTextTests
{
    private const string Emoji = "😀";

    [Theory]
    [InlineData(1)]
    [InlineData(DomainLimits.Subtitle)]
    public void Single_line_text_that_fits_is_used_unchanged(int length)
    {
        var text = new string('a', length);

        Subtitle(text).Should().Be(text);
    }

    [Fact]
    public void Longer_text_is_cut_at_the_last_word_boundary_and_ends_with_an_ellipsis()
    {
        var text = new string('a', 240) + " " + new string('b', 20);

        Subtitle(text).Should().Be(new string('a', 240) + "…");
    }

    [Fact]
    public void A_word_ending_exactly_at_the_limit_is_kept_whole()
    {
        // 249 characters plus the ellipsis is exactly the 250-character limit.
        var result = Subtitle(new string('a', DomainLimits.Subtitle - 1) + " tail");

        result.Should().Be(new string('a', DomainLimits.Subtitle - 1) + "…");
        result.Length.Should().Be(DomainLimits.Subtitle);
    }

    [Theory]
    [InlineData("First line\nsecond line")]
    [InlineData("First line   \r\nsecond line")]
    public void Multiline_text_uses_its_first_line(string text)
    {
        Subtitle(text).Should().Be("First line…");
    }

    [Fact]
    public void A_long_first_line_of_multiline_text_is_shortened_too()
    {
        Subtitle(new string('a', 200) + " " + new string('b', 100) + "\nmore").Should().Be(new string('a', 200) + "…");
    }

    [Fact]
    public void A_single_word_longer_than_the_limit_is_cut_hard()
    {
        var result = Subtitle(new string('x', 300));

        result.Should().Be(new string('x', DomainLimits.Subtitle - 1) + "…");
    }

    [Fact]
    public void A_hard_cut_never_splits_a_surrogate_pair()
    {
        // 150 emoji are 300 UTF-16 units; a cut at 249 would land inside the 125th one.
        var result = Subtitle(string.Concat(Enumerable.Repeat(Emoji, 150)));

        result.Should().Be(string.Concat(Enumerable.Repeat(Emoji, 124)) + "…");
    }

    [Fact]
    public void Unicode_whitespace_counts_as_a_word_boundary()
    {
        var text = new string('字', 240) + "　" + new string('字', 20);

        Subtitle(text).Should().Be(new string('字', 240) + "…");
    }

    private static string Subtitle(string text) => SubtitleText.FromChecklistText(ChecklistText.Create(text).Value).Value;
}
