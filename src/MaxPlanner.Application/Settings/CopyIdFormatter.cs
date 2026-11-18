using System.Text;
using System.Text.RegularExpressions;
using MaxPlanner.Application.ReadModels;

namespace MaxPlanner.Application.Settings;

/// <summary>The values a Copy ID format can name (design §4.2, §4.6). Null or blank means "no value".</summary>
public sealed record CopyIdValues(string? CrNumber, string? TaskNumber, string? Title, string? Service, string? Squad)
{
    /// <summary>A task's values: its CR numbers and squad (T-13, none when unlinked), its title and service.</summary>
    public static CopyIdValues From(TaskReadModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new(task.Cr.CrNumber, task.Cr.TaskNumber, task.EffectiveTitle, task.Service, SquadOf(task.Cr));
    }

    /// <summary>A Subtask card's values: the CR values inherited through its parent, and the parent's title and service (B-16.2).</summary>
    public static CopyIdValues From(SubtaskCardReadModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return new(card.Cr.CrNumber, card.Cr.TaskNumber, card.ParentTitle, card.ParentService, SquadOf(card.Cr));
    }

    // An unlinked task's SquadName is the "General" label, not a value.
    private static string? SquadOf(InheritedCrValues cr) => cr.SquadId is null ? null : cr.SquadName;
}

/// <summary>
/// Builds the Copy ID text from the Settings › Defaults format (design §4.6). Placeholders match case-insensitively;
/// unknown ones stay as typed. Gaps are trimmed on the format's literal text only, never inside values.
/// </summary>
public static partial class CopyIdFormatter
{
    public const string Default = "{CRNum} - {TaskNum}";

    /// <summary>The format to store and show: blank falls back to <see cref="Default"/>.</summary>
    public static string Normalize(string? format) => string.IsNullOrWhiteSpace(format) ? Default : format;

    public static string Apply(string? format, CopyIdValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Apply(Normalize(format), name => Lookup(name, values));
    }

    /// <summary>
    /// Fills a format with the Copy ID gap rules: <c>{Name}</c> placeholders match case-insensitively through
    /// <paramref name="lookup"/>; unknown ones stay as typed. A known placeholder with no value drops the literal text next
    /// to it, so a run of empty placeholders leaves exactly one separator.
    /// </summary>
    /// <param name="format">The text to fill; it is used as given (no default).</param>
    /// <param name="lookup">Whether a placeholder name is known and, when it is, its value (null or blank means none).</param>
    public static string Apply(string format, Func<string, (bool Known, string? Value)> lookup)
    {
        var (literals, placeholders) = Split(format, lookup);
        if (placeholders.Count == 0)
        {
            return literals[0].Trim();
        }

        var result = new StringBuilder();
        for (var i = 0; i < placeholders.Count; i++)
        {
            // Leading text needs the first placeholder's value; a separator needs a value before it (the placeholder
            // just before) and one still to come, so a run of empty placeholders leaves exactly one separator.
            var keepLiteral = i == 0
                ? placeholders[0] is not null
                : placeholders[i - 1] is not null && result.Length > 0 && placeholders.Skip(i).Any(p => p is not null);
            if (keepLiteral)
            {
                result.Append(literals[i]);
            }

            result.Append(placeholders[i]);
        }

        if (placeholders[^1] is not null)
        {
            result.Append(literals[^1]);
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// The placeholder core shared with other formats (the pull request title, ADR 0031): splits <paramref name="format"/>
    /// into literal text and the values of its known placeholders. <c>Literals[i]</c> precedes <c>Values[i]</c> and
    /// <c>Literals[^1]</c> trails, so there is always one more literal than values. Names match case-insensitively through
    /// <paramref name="lookup"/>; an unknown placeholder stays in the literal text as typed. A value is trimmed, and null
    /// when it is null or blank.
    /// </summary>
    internal static (IReadOnlyList<string> Literals, IReadOnlyList<string?> Values) Split(string format, Func<string, (bool Known, string? Value)> lookup)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(lookup);

        var literals = new List<string>();
        var values = new List<string?>();
        var literal = new StringBuilder();
        var last = 0;
        foreach (Match match in PlaceholderPattern().Matches(format))
        {
            literal.Append(format, last, match.Index - last);
            last = match.Index + match.Length;
            var (known, value) = lookup(match.Groups[1].Value);
            if (!known)
            {
                literal.Append(match.Value); // Unknown placeholder: left as typed.
                continue;
            }

            literals.Add(literal.ToString());
            literal.Clear();
            values.Add(string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        }

        literal.Append(format, last, format.Length - last);
        literals.Add(literal.ToString());
        return (literals, values);
    }

    private static (bool Known, string? Value) Lookup(string name, CopyIdValues v) => name.ToUpperInvariant() switch
    {
        "CRNUM" => (true, v.CrNumber),
        "TASKNUM" => (true, v.TaskNumber),
        "TITLE" => (true, v.Title),
        "SERVICE" => (true, v.Service),
        "SQUAD" => (true, v.Squad),
        _ => (false, null),
    };

    [GeneratedRegex(@"\{([A-Za-z]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
