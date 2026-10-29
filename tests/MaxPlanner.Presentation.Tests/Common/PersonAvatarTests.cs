using AwesomeAssertions;
using MaxPlanner.Presentation.Common.Controls;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Common;

public sealed class PersonAvatarTests
{
    [Theory]
    [InlineData("Wingtip Toys", "WT")]
    [InlineData("fourth coffee", "FC")]
    [InlineData("Ana María López", "AL")]
    [InlineData("Sam", "S")]
    [InlineData("  Sam   Lee  ", "SL")]
    [InlineData("Sam\tLee", "SL")]
    [InlineData("élodie durand", "ÉD")]
    [InlineData("Sam (QA)", "SQ")]
    [InlineData("Sam -", "S")]
    [InlineData("محمد علي", "مع")]
    [InlineData("王小明", "王")]
    [InlineData("Иван Петров", "ИП")]
    [InlineData("R2 D2", "RD")]
    [InlineData("3M Team", "3T")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("— · —", "")]
    [InlineData(null, "")]
    public void Initials_take_the_first_letter_of_the_first_and_last_word(string? name, string expected) =>
        PersonAvatarIdentity.Initials(name).Should().Be(expected);

    [Fact]
    public void Initials_keep_surrogate_pairs_and_combining_marks_whole()
    {
        // U+1D49C MATHEMATICAL SCRIPT CAPITAL A is a surrogate pair; "e" + U+0301 is one text element.
        PersonAvatarIdentity.Initials("\U0001D49Cda Lovelace").Should().Be("\U0001D49CL");
        PersonAvatarIdentity.Initials("émile Zola").Should().Be("ÉZ");
        PersonAvatarIdentity.Initials("😀 Smile").Should().Be("S", "an emoji is not a letter, so the next word gives the initial");
    }

    [Fact]
    public void Palette_index_is_fixed_across_runs_and_ignores_case_and_outer_spaces()
    {
        // Pinned values: a change here recolors every person in every dataset.
        PersonAvatarIdentity.PaletteIndex("Wingtip Toys").Should().Be(PersonAvatarIdentity.PaletteIndex("Wingtip Toys"));
        PersonAvatarIdentity.PaletteIndex(" wingtip toys ").Should().Be(PersonAvatarIdentity.PaletteIndex("Wingtip Toys"));
        PersonAvatarIdentity.PaletteIndex("Wingtip Toys").Should().Be(Fnv1a("WINGTIP TOYS"));
        PersonAvatarIdentity.PaletteIndex(null).Should().Be(PersonAvatarIdentity.PaletteIndex(string.Empty));
    }

    [Fact]
    public void Palette_indexes_spread_names_over_every_color()
    {
        var count = PersonAvatarIdentity.Palette.Count;
        var names = Enumerable.Range(0, count * 200).Select(i => $"Person {i}").ToList();
        var buckets = names.GroupBy(PersonAvatarIdentity.PaletteIndex).ToDictionary(g => g.Key, g => g.Count());

        buckets.Keys.Should().BeEquivalentTo(Enumerable.Range(0, count));
        buckets.Values.Should().OnlyContain(n => n >= 100 && n <= 300, "each color gets roughly 1/{0} of names", count);

        // Short, realistic names are spread too, not only numbered ones.
        string[] people = ["Alice", "Bob", "Carol", "Dave", "Erin", "Frank", "Grace", "Heidi", "Ivan", "Judy", "Mallory", "Niaj", "Olivia", "Peggy", "Rupert", "Sybil", "Trent", "Victor", "Walter"];
        people.Select(PersonAvatarIdentity.PaletteIndex).Distinct().Count().Should().BeGreaterThanOrEqualTo(7);
    }

    [Fact]
    public void Every_palette_color_keeps_white_initials_at_WCAG_AA_contrast()
    {
        PersonAvatarIdentity.Palette.Should().HaveCountGreaterThanOrEqualTo(10);
        PersonAvatarIdentity.Palette.Should().OnlyHaveUniqueItems();
        foreach (var (r, g, b) in PersonAvatarIdentity.Palette)
        {
            var contrast = 1.05 / (Luminance(r, g, b) + 0.05);
            contrast.Should().BeGreaterThanOrEqualTo(4.5, "white on #{0:X2}{1:X2}{2:X2}", r, g, b);
        }
    }

    [Theory]
    [InlineData(16, 8)]
    [InlineData(20, 10)]
    [InlineData(24, 10)]
    [InlineData(32, 12)]
    [InlineData(48, 19)]
    public void Initials_font_follows_the_avatar_size(double diameter, double expected) =>
        PersonAvatarIdentity.FontSizeFor(diameter).Should().Be(expected);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 2)]
    [InlineData(9, 2)]
    public void A_group_shows_up_to_three_circles_with_the_overflow_taking_the_last(int people, int avatars) =>
        PersonAvatarIdentity.VisibleCount(people, PersonAvatarGroup.MaxVisible).Should().Be(avatars);

    [Fact]
    public void People_pickers_mark_their_chips_and_suggestions_as_people_and_tag_pickers_do_not()
    {
        var alice = Guid.NewGuid();
        (Guid, string)[] existing = [(alice, "Alice Smith"), (Guid.NewGuid(), "Bob")];
        var people = new TokenPickerViewModel(new FakeStrings(), () => existing, new NewNameRegistry()) { IsPeople = true };
        var tags = new TokenPickerViewModel(new FakeStrings(), () => existing, new NewNameRegistry());

        people.ShowSuggestions();
        people.Suggestions.Should().OnlyContain(s => s.IsPerson && s.ShowsAvatar);
        people.Query = "Carol";
        people.Suggestions.Should().ContainSingle().Which.Should().Match<TokenSuggestion>(s => s.IsPerson && s.IsAddNew && !s.ShowsAvatar);
        people.Load([(alice, "Alice Smith", false)]);
        people.Tokens.Should().ContainSingle().Which.IsPerson.Should().BeTrue();

        tags.ShowSuggestions();
        tags.Suggestions.Should().OnlyContain(s => !s.IsPerson && !s.ShowsAvatar);
        tags.Load([(alice, "Alice Smith", false)]);
        tags.Tokens.Should().ContainSingle().Which.IsPerson.Should().BeFalse();
    }

    private static int Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var c in value)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)(hash % (uint)PersonAvatarIdentity.Palette.Count);
    }

    private static double Luminance(byte r, byte g, byte b) => (0.2126 * Linear(r)) + (0.7152 * Linear(g)) + (0.0722 * Linear(b));

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
