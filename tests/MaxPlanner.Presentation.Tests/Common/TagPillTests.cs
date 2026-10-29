using AwesomeAssertions;
using MaxPlanner.Presentation.Calendar;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Controls;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Common;

public sealed class TagPillTests
{
    [Fact]
    public void Palette_index_is_fixed_across_calls_and_runs()
    {
        // Pinned values: a change here recolors every tag (and CR bar) in every dataset.
        var id = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        TagPill.IndexOf(id).Should().Be(TagPill.IndexOf(id));
        TagPill.IndexOf(id).Should().Be(Fnv1a(id, TagPill.Count));
        TagPill.IndexOf(Guid.Empty).Should().Be(Fnv1a(Guid.Empty, TagPill.Count));
    }

    [Fact]
    public void Palette_indexes_stay_in_range_and_spread_over_every_color()
    {
        var buckets = Enumerable.Range(0, TagPill.Count * 200)
            .Select(i => TagPill.IndexOf(new Guid(i, 0, 0, new byte[8])))
            .GroupBy(i => i)
            .ToDictionary(g => g.Key, g => g.Count());

        buckets.Keys.Should().BeEquivalentTo(Enumerable.Range(0, TagPill.Count));
        buckets.Values.Should().OnlyContain(n => n >= 100 && n <= 300, "each color gets roughly 1/{0} of tags", TagPill.Count);
    }

    [Fact]
    public void Calendar_bars_and_tag_pills_share_one_hash()
    {
        var id = Guid.Parse("0b1d2c3e-4f50-6172-8394-a5b6c7d8e9f0");
        CalendarBarPalette.IndexOf(id).Should().Be(PaletteHash.IndexOf(id, CalendarBarPalette.Count));
        TagPill.IndexOf(id).Should().Be(PaletteHash.IndexOf(id, TagPill.Count));
    }

    [Fact]
    public void Only_tag_pickers_make_tag_chips()
    {
        (Guid, string)[] existing = [(Guid.NewGuid(), "Urgent")];
        var tags = new TokenPickerViewModel(new FakeStrings(), () => existing, new NewNameRegistry()) { IsTags = true };
        var services = new TokenPickerViewModel(new FakeStrings(), () => existing, new NewNameRegistry());
        var people = new TokenPickerViewModel(new FakeStrings(), () => existing, new NewNameRegistry()) { IsPeople = true };

        foreach (var picker in new[] { tags, services, people })
        {
            picker.Load([(existing[0].Item1, "Urgent", false)]);
        }

        tags.Tokens.Single().IsTag.Should().BeTrue();
        services.Tokens.Single().IsTag.Should().BeFalse();
        people.Tokens.Single().IsTag.Should().BeFalse();
    }

    private static int Fnv1a(Guid id, int count)
    {
        var hash = 2166136261u;
        foreach (var value in id.ToByteArray())
        {
            hash = (hash ^ value) * 16777619u;
        }

        return (int)(hash % (uint)count);
    }
}
