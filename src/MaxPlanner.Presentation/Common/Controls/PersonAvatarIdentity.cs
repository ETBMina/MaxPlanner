using System.Globalization;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// The initials and palette color of a person's avatar. Both derive from the display name only, so every surface
/// shows the same circle for the same person and a rename updates it; no data is stored.
/// </summary>
public static class PersonAvatarIdentity
{
    /// <summary>
    /// Fill colors (RGB) for white initials. Each one keeps white text at WCAG AA contrast (4.5:1 or more) and is the
    /// same in Light and Dark, so a person keeps one color in either theme. High Contrast uses system colors instead.
    /// </summary>
    public static IReadOnlyList<(byte R, byte G, byte B)> Palette { get; } =
    [
        (0x0F, 0x6C, 0xBD), // blue
        (0x82, 0x40, 0xA6), // purple
        (0x00, 0x66, 0x6D), // teal
        (0xA8, 0x3F, 0x00), // pumpkin
        (0x0E, 0x70, 0x0E), // green
        (0x9B, 0x00, 0x62), // magenta
        (0xC5, 0x0F, 0x1F), // red
        (0x4F, 0x52, 0xB2), // indigo
        (0x5C, 0x6B, 0x00), // olive
        (0x8E, 0x56, 0x2E), // brown
        (0x3A, 0x6A, 0x8C), // steel
    ];

    /// <summary>
    /// "Wingtip Toys" → "WT", "Sam" → "S", "Ana María López" → "AL". Each initial is the first letter or digit of
    /// the first and last word, uppercased; surrogate pairs and combining marks stay whole. Empty when the name has
    /// no letter or digit.
    /// </summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string? first = null;
        string? last = null;
        var count = 0;
        foreach (var word in name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (FirstLetter(word) is { } letter)
            {
                first ??= letter;
                last = letter;
                count++;
            }
        }

        return count switch
        {
            0 => string.Empty,
            1 => first!,
            _ => first + last,
        };
    }

    /// <summary>
    /// The palette entry of a name. A fixed FNV-1a hash over the trimmed, uppercased name keeps the color stable across
    /// runs and casing; <see cref="string.GetHashCode()"/> is randomized per process and would recolor people on each start.
    /// </summary>
    public static int PaletteIndex(string? name)
    {
        var hash = 2166136261u;
        foreach (var c in (name ?? string.Empty).Trim().ToUpperInvariant())
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)(hash % (uint)Palette.Count);
    }

    /// <summary>The initials' font size for a circle of <paramref name="diameter"/> epx (Fluent avatar ramp).</summary>
    public static double FontSizeFor(double diameter) => diameter switch
    {
        < 20 => 8,
        <= 24 => 10,
        <= 32 => 12,
        _ => Math.Round(diameter * 0.4),
    };

    /// <summary>
    /// How many people an avatar group of <paramref name="count"/> shows as circles when it holds at most
    /// <paramref name="maxVisible"/> circles: all of them when they fit, otherwise one fewer so "+N" takes the last slot.
    /// </summary>
    public static int VisibleCount(int count, int maxVisible) => count <= maxVisible ? count : Math.Max(0, maxVisible - 1);

    private static string? FirstLetter(string word)
    {
        var elements = StringInfo.GetTextElementEnumerator(word);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (char.IsLetterOrDigit(element, 0))
            {
                return element.ToUpperInvariant();
            }
        }

        return null;
    }
}
