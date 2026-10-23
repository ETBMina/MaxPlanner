using System.Globalization;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Application.Filtering;

/// <summary>
/// Converts filter specs to and from the culture-independent <see cref="ViewFilterPreference"/> of
/// <c>configuration.json</c> (B-10: each scope persisted independently). IDs are stored as GUIDs, dates as ISO
/// dates, and numbers with the invariant culture. Unreadable values are dropped, never guessed.
/// </summary>
/// <remarks>Dashboard drill-down context is never stored; a restart shows the page's own filters.</remarks>
public static class FilterPersistence
{
    private const string General = "general";
    private const string NoCr = "none";
    private const string CustomPrefix = "custom:";

    public static ViewFilterPreference ToPreference(TaskFilterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        Put(fields, "status", spec.Statuses.Select(i => Id(i.Value)));
        Put(fields, "priority", spec.Priorities.Select(i => Id(i.Value)));
        Put(fields, "squad", spec.Squads.Select(Squad));
        Put(fields, "owner", spec.Owners.Select(i => Id(i.Value)));
        Put(fields, "designer", spec.Designers.Select(i => Id(i.Value)));
        Put(fields, "developer", spec.Developers.Select(i => Id(i.Value)));
        Put(fields, "tag", spec.Tags.Select(i => Id(i.Value)));
        if (spec.Tags.Count > 0 && spec.TagMode == TagMatchMode.All)
        {
            fields["tagMode"] = ["all"];
        }

        if (spec.Cr.Mode == CrLinkMode.NoCr)
        {
            fields["cr"] = [NoCr];
        }
        else if (spec.Cr is { Mode: CrLinkMode.Specific, ChangeRequestId: { } crId })
        {
            fields["cr"] = [Id(crId.Value)];
        }

        if (!string.IsNullOrWhiteSpace(spec.ServiceContains))
        {
            fields["service"] = [spec.ServiceContains.Trim()];
        }

        PutDate(fields, "start", spec.StartDate);
        PutDate(fields, "end", spec.EndDate);
        PutCustom(fields, spec.CustomFields);
        return new ViewFilterPreference(spec.HasSearch ? spec.SearchText.Trim() : null, fields);
    }

    public static TaskFilterSpec ToTaskSpec(ViewFilterPreference? preference)
    {
        if (preference is null)
        {
            return TaskFilterSpec.Empty;
        }

        var f = preference.Fields;
        return new TaskFilterSpec
        {
            SearchText = preference.SearchText ?? string.Empty,
            Statuses = [.. Ids(f, "status").Select(g => new TaskStatusId(g))],
            Priorities = [.. Ids(f, "priority").Select(g => new PriorityId(g))],
            Squads = Squads(f),
            Owners = [.. Ids(f, "owner").Select(g => new PersonId(g))],
            Designers = [.. Ids(f, "designer").Select(g => new PersonId(g))],
            Developers = [.. Ids(f, "developer").Select(g => new PersonId(g))],
            Tags = [.. Ids(f, "tag").Select(g => new TagId(g))],
            TagMode = Values(f, "tagMode").Contains("all") ? TagMatchMode.All : TagMatchMode.Any,
            Cr = Values(f, "cr") is [var cr] ? cr == NoCr ? CrLinkFilter.NoCr : Guid.TryParse(cr, out var crId) ? CrLinkFilter.For(new ChangeRequestId(crId)) : CrLinkFilter.Any : CrLinkFilter.Any,
            ServiceContains = Values(f, "service") is [var service, ..] ? service : null,
            StartDate = Date(f, "start"),
            EndDate = Date(f, "end"),
            CustomFields = Custom(f),
        };
    }

    public static ViewFilterPreference ToPreference(CrFilterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        Put(fields, "status", spec.Statuses.Select(i => Id(i.Value)));
        Put(fields, "priority", spec.Priorities.Select(i => Id(i.Value)));
        Put(fields, "squad", spec.Squads.Select(Squad));
        Put(fields, "owner", spec.Owners.Select(i => Id(i.Value)));
        PutDate(fields, "planned", spec.Planned);
        PutCustom(fields, spec.CustomFields);
        return new ViewFilterPreference(spec.HasSearch ? spec.SearchText.Trim() : null, fields);
    }

    public static CrFilterSpec ToCrSpec(ViewFilterPreference? preference)
    {
        if (preference is null)
        {
            return CrFilterSpec.Empty;
        }

        var f = preference.Fields;
        return new CrFilterSpec
        {
            SearchText = preference.SearchText ?? string.Empty,
            Statuses = [.. Ids(f, "status").Select(g => new CrStatusId(g))],
            Priorities = [.. Ids(f, "priority").Select(g => new PriorityId(g))],
            Squads = Squads(f),
            Owners = [.. Ids(f, "owner").Select(g => new PersonId(g))],
            Planned = Date(f, "planned"),
            CustomFields = Custom(f),
        };
    }

    private static string Id(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private static string Squad(SquadId? id) => id is { } squad ? Id(squad.Value) : General;

    private static void Put(Dictionary<string, IReadOnlyList<string>> fields, string key, IEnumerable<string> values)
    {
        var list = values.ToList();
        if (list.Count > 0)
        {
            fields[key] = list;
        }
    }

    private static void PutDate(Dictionary<string, IReadOnlyList<string>> fields, string key, DateRangeFilter? range)
    {
        if (range is { IsSet: true })
        {
            fields[key] = DateParts(range);
        }
    }

    private static string[] DateParts(DateRangeFilter range) =>
        [IsoDate(range.From), IsoDate(range.To), range.IncludeUndated ? "undated" : string.Empty];

    private static void PutCustom(Dictionary<string, IReadOnlyList<string>> fields, IReadOnlyList<CustomFieldFilter> filters)
    {
        foreach (var filter in filters.Where(f => f.IsSet))
        {
            var key = CustomPrefix + Id(filter.FieldId.Value);
            if (!string.IsNullOrWhiteSpace(filter.TextContains))
            {
                fields[key] = ["text", filter.TextContains.Trim()];
            }
            else if (filter.Number is { IsSet: true } number)
            {
                fields[key] = ["number", Number(number.Min), Number(number.Max)];
            }
            else if (filter.Date is { IsSet: true } date)
            {
                fields[key] = ["date", .. DateParts(date)];
            }
            else
            {
                fields[key] = ["options", .. filter.Options.Select(o => Id(o.Value))];
            }
        }
    }

    private static string IsoDate(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static IReadOnlyList<string> Values(IReadOnlyDictionary<string, IReadOnlyList<string>> fields, string key) =>
        fields.TryGetValue(key, out var values) ? values : [];

    private static IEnumerable<Guid> Ids(IReadOnlyDictionary<string, IReadOnlyList<string>> fields, string key) =>
        Values(fields, key).Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct();

    private static List<SquadId?> Squads(IReadOnlyDictionary<string, IReadOnlyList<string>> fields) =>
        [.. Values(fields, "squad")
            .Select(v => v == General ? (SquadId?)null : Guid.TryParse(v, out var id) ? new SquadId(id) : (SquadId?)new SquadId(Guid.Empty))
            .Where(s => s is null || s.Value.Value != Guid.Empty)
            .Distinct()];

    private static DateRangeFilter? Date(IReadOnlyDictionary<string, IReadOnlyList<string>> fields, string key) =>
        ParseDate(Values(fields, key));

    private static DateRangeFilter? ParseDate(IReadOnlyList<string> parts)
    {
        if (parts.Count < 2)
        {
            return null;
        }

        var range = new DateRangeFilter(ParseIso(parts[0]), ParseIso(parts[1]), parts.Count > 2 && parts[2] == "undated");
        return range.IsSet && !range.IsInverted ? range : null;
    }

    private static DateOnly? ParseIso(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private static decimal? ParseNumber(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static List<CustomFieldFilter> Custom(IReadOnlyDictionary<string, IReadOnlyList<string>> fields)
    {
        var result = new List<CustomFieldFilter>();
        foreach (var (key, values) in fields)
        {
            if (!key.StartsWith(CustomPrefix, StringComparison.Ordinal) || !Guid.TryParse(key[CustomPrefix.Length..], out var id) || values.Count < 2)
            {
                continue;
            }

            var field = new FieldId(id);
            CustomFieldFilter? filter = values[0] switch
            {
                "text" => new CustomFieldFilter(field, values[1], null, null, []),
                "number" when values.Count >= 3 => new CustomFieldFilter(field, null, new NumberRangeFilter(ParseNumber(values[1]), ParseNumber(values[2])), null, []),
                "date" => new CustomFieldFilter(field, null, null, ParseDate([.. values.Skip(1)]), []),
                "options" => new CustomFieldFilter(field, null, null, null, [.. values.Skip(1).Select(v => Guid.TryParse(v, out var o) ? new FieldOptionId(o) : default).Where(o => o.Value != Guid.Empty)]),
                _ => null,
            };
            if (filter is { IsSet: true } && filter.Number is not { IsInverted: true })
            {
                result.Add(filter);
            }
        }

        return result;
    }
}

/// <summary>The kind of saved filter value that no longer resolves (B-10 startup check).</summary>
public enum RemovedFilterKind
{
    Status,
    Priority,
    Squad,
    Person,
    Tag,
    ChangeRequest,
    CustomField,
}

/// <summary>
/// B-10: a saved filter that refers to a removed lookup value is cleared visibly rather than silently hiding every
/// record. Returns the cleaned spec and what was removed, so the page can say so.
/// </summary>
public static class FilterValidation
{
    public static (TaskFilterSpec Spec, IReadOnlyList<RemovedFilterKind> Removed) Clean(
        TaskFilterSpec spec, WorkflowSnapshot workflow, Func<ChangeRequestId, bool> changeRequestExists)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(changeRequestExists);
        var removed = new HashSet<RemovedFilterKind>();
        var cleaned = spec with
        {
            Statuses = Keep(spec.Statuses, s => workflow.FindTaskStatus(s) is not null, RemovedFilterKind.Status, removed),
            Priorities = Keep(spec.Priorities, p => workflow.FindPriority(p) is not null, RemovedFilterKind.Priority, removed),
            Squads = Keep(spec.Squads, s => s is not { } id || workflow.FindSquad(id) is not null, RemovedFilterKind.Squad, removed),
            Owners = Keep(spec.Owners, p => workflow.FindPerson(p) is not null, RemovedFilterKind.Person, removed),
            Designers = Keep(spec.Designers, p => workflow.FindPerson(p) is not null, RemovedFilterKind.Person, removed),
            Developers = Keep(spec.Developers, p => workflow.FindPerson(p) is not null, RemovedFilterKind.Person, removed),
            Tags = Keep(spec.Tags, t => workflow.FindTag(t) is not null, RemovedFilterKind.Tag, removed),
            CustomFields = CleanCustom(spec.CustomFields, workflow, FieldTarget.Task, removed),
        };
        if (spec.Cr is { Mode: CrLinkMode.Specific, ChangeRequestId: { } crId } && !changeRequestExists(crId))
        {
            cleaned = cleaned with { Cr = CrLinkFilter.Any };
            removed.Add(RemovedFilterKind.ChangeRequest);
        }

        return (cleaned, [.. removed.Order()]);
    }

    public static (CrFilterSpec Spec, IReadOnlyList<RemovedFilterKind> Removed) Clean(CrFilterSpec spec, WorkflowSnapshot workflow)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(workflow);
        var removed = new HashSet<RemovedFilterKind>();
        var cleaned = spec with
        {
            Statuses = Keep(spec.Statuses, s => workflow.FindCrStatus(s) is not null, RemovedFilterKind.Status, removed),
            Priorities = Keep(spec.Priorities, p => workflow.FindPriority(p) is not null, RemovedFilterKind.Priority, removed),
            Squads = Keep(spec.Squads, s => s is not { } id || workflow.FindSquad(id) is not null, RemovedFilterKind.Squad, removed),
            Owners = Keep(spec.Owners, p => workflow.FindPerson(p) is not null, RemovedFilterKind.Person, removed),
            CustomFields = CleanCustom(spec.CustomFields, workflow, FieldTarget.ChangeRequest, removed),
        };
        return (cleaned, [.. removed.Order()]);
    }

    private static List<T> Keep<T>(IReadOnlyList<T> values, Func<T, bool> exists, RemovedFilterKind kind, HashSet<RemovedFilterKind> removed)
    {
        var kept = values.Where(exists).ToList();
        if (kept.Count != values.Count)
        {
            removed.Add(kind);
        }

        return kept;
    }

    private static List<CustomFieldFilter> CleanCustom(
        IReadOnlyList<CustomFieldFilter> filters, WorkflowSnapshot workflow, FieldTarget target, HashSet<RemovedFilterKind> removed)
    {
        var result = new List<CustomFieldFilter>();
        foreach (var filter in filters)
        {
            var field = workflow.Fields.FirstOrDefault(f => f.Id == filter.FieldId && f.Target == target);
            if (field is not { IsActive: true } || !CustomFieldFilter.IsFilterable(field.Kind))
            {
                removed.Add(RemovedFilterKind.CustomField);
                continue;
            }

            var options = filter.Options.Where(o => field.Options.Any(fo => fo.Id == o)).ToList();
            if (options.Count != filter.Options.Count)
            {
                removed.Add(RemovedFilterKind.CustomField);
            }

            var kept = filter with { Options = options };
            if (kept.IsSet)
            {
                result.Add(kept);
            }
        }

        return result;
    }
}
