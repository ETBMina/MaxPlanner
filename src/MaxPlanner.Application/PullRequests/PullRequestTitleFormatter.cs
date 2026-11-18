using System.Text;
using System.Text.RegularExpressions;
using MaxPlanner.Application.Settings;

namespace MaxPlanner.Application.PullRequests;

/// <summary>The values a pull request title template can name (design §4.6). Null or blank means "no value".</summary>
public sealed record PullRequestTitleValues(string? CrNumber, string? TaskNumber, string? Title, string? Source, string? Target);

/// <summary>
/// Builds a pull request title from the Settings template (ADR 0031, design §4.6). Its placeholders are <c>{CRNum}</c>,
/// <c>{TaskNum}</c>, <c>{Title}</c>, <c>{Source}</c> and <c>{Target}</c>, matched case-insensitively with the Copy ID
/// placeholder core (<see cref="CopyIdFormatter"/>); any other name stays as typed. Unlike Copy ID, an empty value
/// removes only its placeholder and the words around it stay: runs of whitespace then become one space and the ends are
/// trimmed, so <c>PR {CRNum} {TaskNum} into {Target}</c> without a CR number gives <c>PR 12345 into uat</c>.
/// </summary>
public static partial class PullRequestTitleFormatter
{
    public const string Default = "PR {CRNum} {TaskNum} into {Target}";

    /// <summary>The template to store and use: blank falls back to <see cref="Default"/>.</summary>
    public static string Normalize(string? template) => string.IsNullOrWhiteSpace(template) ? Default : template;

    public static string Apply(string? template, PullRequestTitleValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var (literals, placeholders) = CopyIdFormatter.Split(Normalize(template), name => name.ToUpperInvariant() switch
        {
            "CRNUM" => (true, values.CrNumber),
            "TASKNUM" => (true, values.TaskNumber),
            "TITLE" => (true, values.Title),
            "SOURCE" => (true, values.Source),
            "TARGET" => (true, values.Target),
            _ => (false, null),
        });

        var title = new StringBuilder(literals[0]);
        for (var i = 0; i < placeholders.Count; i++)
        {
            title.Append(placeholders[i]).Append(literals[i + 1]);
        }

        return Whitespace().Replace(title.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
