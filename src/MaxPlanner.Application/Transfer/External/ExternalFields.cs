using System.Text.RegularExpressions;

namespace MaxPlanner.Application.Transfer.External;

/// <summary>A target a source column can be mapped to (B-13.1). Which ones apply depends on the table's entity.</summary>
public enum ExternalField
{
    /// <summary>The column is not mapped; its values are preserved as legacy content (T-05).</summary>
    Ignore,

    /// <summary>The record's identity in the source, used as the import-ledger key (B-13.5).</summary>
    SourceId,
    Title,
    Service,
    Status,
    Priority,
    Designers,
    Developers,
    Tags,
    Notes,
    StartDate,
    EndDate,
    Archived,

    /// <summary>A task's CR reference, or a CR's own number; also a review's CR number text.</summary>
    CrNumber,
    TaskNumber,
    Squad,
    Owner,
    PlannedStart,
    PlannedEnd,
    EmailSubject,
    SolutionDesignUrl,

    /// <summary>A review's developer.</summary>
    Developer,
    Done,
    CompletedOn,
}

/// <summary>
/// B-13.2 column aliases. A header is normalized (trimmed, lower case) and matched against each field's aliases with
/// compiled, culture-invariant patterns that have a bounded match timeout (B-04). A header that matches nothing is
/// left unmapped; one that matches more than one candidate, or a field matched by two columns, needs review.
/// </summary>
public static partial class ExternalFieldAliases
{
    private const int TimeoutMs = 100;

    /// <summary>The fields that make sense for <paramref name="entity"/>, in the wizard's display order.</summary>
    public static IReadOnlyList<ExternalField> FieldsFor(ExternalEntity entity) => entity switch
    {
        ExternalEntity.Task =>
        [
            ExternalField.Ignore, ExternalField.SourceId, ExternalField.Title, ExternalField.Service, ExternalField.Status, ExternalField.Priority,
            ExternalField.Designers, ExternalField.Developers, ExternalField.Tags, ExternalField.Notes, ExternalField.StartDate, ExternalField.EndDate,
            ExternalField.Archived, ExternalField.CrNumber, ExternalField.TaskNumber, ExternalField.Squad, ExternalField.Owner,
        ],
        ExternalEntity.ChangeRequest =>
        [
            ExternalField.Ignore, ExternalField.SourceId, ExternalField.CrNumber, ExternalField.Title, ExternalField.Owner, ExternalField.Squad,
            ExternalField.Status, ExternalField.Priority, ExternalField.PlannedStart, ExternalField.PlannedEnd, ExternalField.EmailSubject,
            ExternalField.SolutionDesignUrl, ExternalField.Notes, ExternalField.TaskNumber,
        ],
        ExternalEntity.Review =>
        [
            ExternalField.Ignore, ExternalField.SourceId, ExternalField.Title, ExternalField.CrNumber, ExternalField.TaskNumber, ExternalField.Service,
            ExternalField.Developer, ExternalField.Notes, ExternalField.Done, ExternalField.CompletedOn,
        ],
        ExternalEntity.GeneralTodo => [ExternalField.Ignore, ExternalField.SourceId, ExternalField.Title, ExternalField.Done],
        _ => [ExternalField.Ignore],
    };

    /// <summary>The proposed field of each column; ambiguous or unknown headers propose <see cref="ExternalField.Ignore"/>.</summary>
    /// <returns>The proposal per column and whether the header was ambiguous (matched two candidate fields).</returns>
    public static IReadOnlyList<(ExternalField Field, bool IsAmbiguous)> Propose(ExternalEntity entity, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var candidates = FieldsFor(entity);
        var result = new List<(ExternalField, bool)>();
        foreach (var column in columns)
        {
            var header = (column ?? string.Empty).Trim().ToLowerInvariant();
            var matches = candidates.Where(f => f != ExternalField.Ignore && Matches(entity, f, header)).ToList();
            result.Add(matches.Count == 1 ? (matches[0], false) : (ExternalField.Ignore, matches.Count > 1));
        }

        return result;
    }

    private static bool Matches(ExternalEntity entity, ExternalField field, string header)
    {
        try
        {
            return field switch
            {
                ExternalField.SourceId => SourceId().IsMatch(header),
                ExternalField.Title => entity == ExternalEntity.GeneralTodo ? TodoText().IsMatch(header) : Title().IsMatch(header),
                ExternalField.Service => entity == ExternalEntity.Review ? Services().IsMatch(header) : Service().IsMatch(header),
                ExternalField.Status => Status().IsMatch(header),
                ExternalField.Priority => Priority().IsMatch(header),
                ExternalField.Designers => Designers().IsMatch(header),
                ExternalField.Developers => Developers().IsMatch(header),
                ExternalField.Developer => Developers().IsMatch(header),
                ExternalField.Tags => Tags().IsMatch(header),
                ExternalField.Notes => entity switch
                {
                    ExternalEntity.ChangeRequest => Description().IsMatch(header),
                    ExternalEntity.Review => Comments().IsMatch(header),
                    _ => Notes().IsMatch(header),
                },
                ExternalField.StartDate => StartDate().IsMatch(header),
                ExternalField.EndDate => EndDate().IsMatch(header),
                ExternalField.Archived => Archived().IsMatch(header),
                ExternalField.CrNumber => entity == ExternalEntity.ChangeRequest ? CrOwnNumber().IsMatch(header) : CrNumber().IsMatch(header),
                ExternalField.TaskNumber => TaskNumber().IsMatch(header),
                ExternalField.Squad => Squad().IsMatch(header),
                ExternalField.Owner => Owner().IsMatch(header),
                ExternalField.PlannedStart => PlannedStart().IsMatch(header),
                ExternalField.PlannedEnd => PlannedEnd().IsMatch(header),
                ExternalField.EmailSubject => EmailSubject().IsMatch(header),
                ExternalField.SolutionDesignUrl => SolutionDesign().IsMatch(header),
                ExternalField.Done => Done().IsMatch(header),
                ExternalField.CompletedOn => CompletedOn().IsMatch(header),
                _ => false,
            };
        }
        catch (RegexMatchTimeoutException)
        {
            // A header that cannot be matched in time is left for the user to map.
            return false;
        }
    }

    [GeneratedRegex(@"^(id|key|uid|source[ _-]?(id|key)|external[ _-]?id)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SourceId();

    [GeneratedRegex(@"^(title|name|summary|subject|task)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Title();

    [GeneratedRegex(@"^(title|text|name|item|to-?do)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex TodoText();

    [GeneratedRegex(@"^service$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Service();

    [GeneratedRegex(@"^services?$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Services();

    [GeneratedRegex(@"^(status|state|stage)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Status();

    [GeneratedRegex(@"^(priority|prio)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Priority();

    [GeneratedRegex(@"^(designers?|design[ _-]?by)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Designers();

    [GeneratedRegex(@"^(developers?|devs?|assignees?|assigned[ _-]?to)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Developers();

    [GeneratedRegex(@"^(tags?|labels?)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Tags();

    [GeneratedRegex(@"^(notes?|body|details?|description)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Notes();

    [GeneratedRegex(@"^(description|notes?|body|details?)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Description();

    [GeneratedRegex(@"^(review[ _-]?)?comments?$|^notes?$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Comments();

    [GeneratedRegex(@"^(start|started|start[ _-]?date)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex StartDate();

    [GeneratedRegex(@"^(end|finish|finished|end[ _-]?date|finish[ _-]?date)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex EndDate();

    [GeneratedRegex(@"^(is[ _-]?)?archived$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Archived();

    [GeneratedRegex(@"^(cr|cr[ _-]?(number|no|num|#|ref|reference)|crnumber|change[ _-]?request|number)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex CrNumber();

    [GeneratedRegex(@"^(number|no|cr|cr[ _-]?(number|no|num|#)|crnumber)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex CrOwnNumber();

    [GeneratedRegex(@"^task[ _-]?(number|no|num|#|ref)$|^tasknumber$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex TaskNumber();

    [GeneratedRegex(@"^(squad|team)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Squad();

    [GeneratedRegex(@"^((cr|change[ _-]?request)[ _-]?)?owner$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Owner();

    [GeneratedRegex(@"^planned[ _-]?start([ _-]?date)?$|^plannedstart$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex PlannedStart();

    [GeneratedRegex(@"^planned[ _-]?end([ _-]?date)?$|^plannedend$|^due([ _-]?date)?$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex PlannedEnd();

    [GeneratedRegex(@"^(email|e-mail)([ _-]?subject)?$|^emailsubject$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex EmailSubject();

    [GeneratedRegex(@"^solution[ _-]?design([ _-]?(url|link))?$|^solutiondesign(url)?$|^design[ _-]?(url|link)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SolutionDesign();

    [GeneratedRegex(@"^(is[ _-]?)?(done|completed?|complete)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Done();

    [GeneratedRegex(@"^completed[ _-]?(on|date|at)$|^completedon$|^done[ _-]?(on|date)$", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex CompletedOn();
}
