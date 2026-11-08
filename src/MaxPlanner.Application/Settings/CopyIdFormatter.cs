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

        // Split into literal text and known placeholders; literal[i] precedes placeholder i, literal[^1] trails.
        var literals = new List<string>();
        var placeholders = new List<string?>();
        var text = Normalize(format);
        var literal = new StringBuilder();
        var last = 0;
        foreach (Match match in PlaceholderPattern().Matches(text))
        {
            literal.Append(text, last, match.Index - last);
            last = match.Index + match.Length;
            if (!TryLookup(match.Groups[1].Value, values, out var value))
            {
                literal.Append(match.Value); // Unknown placeholder: left as typed.
                continue;
            }

            literals.Add(literal.ToString());
            literal.Clear();
            placeholders.Add(string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        }

        literal.Append(text, last, text.Length - last);
        literals.Add(literal.ToString());

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

    private static bool TryLookup(string name, CopyIdValues v, out string? value)
    {
        (var known, value) = name.ToUpperInvariant() switch
        {
            "CRNUM" => (true, v.CrNumber),
            "TASKNUM" => (true, v.TaskNumber),
            "TITLE" => (true, v.Title),
            "SERVICE" => (true, v.Service),
            "SQUAD" => (true, v.Squad),
            _ => (false, null),
        };
        return known;
    }

    [GeneratedRegex(@"\{([A-Za-z]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
