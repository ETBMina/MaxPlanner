using System.Globalization;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Shell;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Common.Filters;

/// <summary>Field keys shared by the drafts, chips, and AutomationIds (<c>{Page}_FilterChip_{Key}</c>).</summary>
internal static class FilterKeys
{
    public const string Status = "Status";
    public const string Priority = "Priority";
    public const string Squad = "Squad";
    public const string Owner = "Owner";
    public const string Designers = "Designers";
    public const string Developers = "Developers";
    public const string Tags = "Tags";
    public const string Cr = "Cr";
    public const string Service = "Service";
    public const string StartDate = "StartDate";
    public const string EndDate = "EndDate";
    public const string Planned = "Planned";
    public const string CustomPrefix = "Custom_";

    public static string Custom(FieldId id) => CustomPrefix + id.Value.ToString("N", CultureInfo.InvariantCulture);

    public static FieldId? ParseCustom(string key) =>
        key.StartsWith(CustomPrefix, StringComparison.Ordinal) && Guid.TryParse(key[CustomPrefix.Length..], out var id) ? new FieldId(id) : null;
}

/// <summary>Visible text for filter values; dates use the user's culture (T-08), stored values never do.</summary>
internal static class FilterText
{
    public static string Date(DateOnly day) => day.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    public static string Range(ILocalizedStrings strings, DateRangeFilter range)
    {
        var text = (range.From, range.To) switch
        {
            ({ } from, { } to) => strings.Format("Filters_RangeBoth", Date(from), Date(to)),
            ({ } from, null) => strings.Format("Filters_RangeFrom", Date(from)),
            (null, { } to) => strings.Format("Filters_RangeTo", Date(to)),
            _ => string.Empty,
        };
        return range.IncludeUndated ? strings.Format("Filters_RangeOrNone", text) : text;
    }

    public static string Number(ILocalizedStrings strings, NumberRangeFilter range) => (range.Min, range.Max) switch
    {
        ({ } min, { } max) => strings.Format("Filters_RangeBoth", min.ToString(CultureInfo.CurrentCulture), max.ToString(CultureInfo.CurrentCulture)),
        ({ } min, null) => strings.Format("Filters_NumberMin", min.ToString(CultureInfo.CurrentCulture)),
        (null, { } max) => strings.Format("Filters_NumberMax", max.ToString(CultureInfo.CurrentCulture)),
        _ => string.Empty,
    };
}

/// <summary>Builds the draft fields both scopes share: lookup chips and the custom-field editors.</summary>
internal static class DraftFields
{
    public static FilterChoiceGroupViewModel Choices(
        FilterDraftViewModel draft, string key, string header, IEnumerable<(Guid Id, string Label)> options, IEnumerable<Guid> selected)
    {
        var chosen = selected.ToHashSet();
        var list = options
            .Select(o => new FilterChoiceViewModel(o.Id, o.Label, "Filters_Chip_" + key + "_" + (o.Id == Guid.Empty ? "General" : o.Id.ToString("N", CultureInfo.InvariantCulture)), chosen.Contains(o.Id), draft.Changed))
            .ToList();
        var group = new FilterChoiceGroupViewModel(key, header, list);
        draft.ChoiceGroups.Add(group);
        return group;
    }

    public static void Custom(FilterDraftViewModel draft, ILocalizedStrings strings, IEnumerable<FieldDefinition> fields, IReadOnlyList<CustomFieldFilter> current)
    {
        var inverted = strings.GetString("Filters_InvertedRange");
        foreach (var field in fields.Where(f => f.IsActive && CustomFieldFilter.IsFilterable(f.Kind)).OrderBy(f => f.DisplayOrder).ThenBy(f => f.Id.Value))
        {
            var key = FilterKeys.Custom(field.Id);
            var filter = current.FirstOrDefault(f => f.FieldId == field.Id);
            var label = field.Label.Display;
            switch (field.Kind)
            {
                case FieldKind.Number:
                    draft.NumberFields.Add(new FilterNumberFieldViewModel(key, label, "Filters_Number_" + key, filter?.Number, inverted, draft.Changed));
                    break;
                case FieldKind.Date:
                    draft.DateFields.Add(new FilterDateFieldViewModel(key, label, strings.Format("Filters_IncludeWithout", label), "Filters_Date_" + key, filter?.Date, inverted, draft.Changed));
                    break;
                case FieldKind.Lookup:
                    Choices(draft, key, label, field.Options.OrderBy(o => o.Order).Select(o => (o.Id.Value, o.Label.Display)), filter?.Options.Select(o => o.Value) ?? []);
                    break;
                default:
                    draft.TextFields.Add(new FilterTextFieldViewModel(key, strings.Format("Filters_Contains", label), "Filters_Text_" + key, filter?.TextContains, draft.Changed));
                    break;
            }
        }
    }

    public static List<CustomFieldFilter> ReadCustom(FilterDraftViewModel draft)
    {
        var result = new List<CustomFieldFilter>();
        foreach (var field in draft.TextFields.Where(f => f.Key.StartsWith(FilterKeys.CustomPrefix, StringComparison.Ordinal) && f.Text.Trim().Length > 0))
        {
            result.Add(new CustomFieldFilter(FilterKeys.ParseCustom(field.Key)!.Value, field.Text.Trim(), null, null, []));
        }

        foreach (var field in draft.NumberFields.Where(f => f.Range.IsSet))
        {
            result.Add(new CustomFieldFilter(FilterKeys.ParseCustom(field.Key)!.Value, null, field.Range, null, []));
        }

        foreach (var field in draft.DateFields.Where(f => f.Key.StartsWith(FilterKeys.CustomPrefix, StringComparison.Ordinal) && f.Range.IsSet))
        {
            result.Add(new CustomFieldFilter(FilterKeys.ParseCustom(field.Key)!.Value, null, null, field.Range, []));
        }

        foreach (var group in draft.ChoiceGroups.Where(g => g.Key.StartsWith(FilterKeys.CustomPrefix, StringComparison.Ordinal) && g.CheckedIds.Count > 0))
        {
            result.Add(new CustomFieldFilter(FilterKeys.ParseCustom(group.Key)!.Value, null, null, null, [.. group.CheckedIds.Select(id => new FieldOptionId(id))]));
        }

        return result;
    }

    public static int Count(FilterDraftViewModel draft) =>
        draft.ChoiceGroups.Count(g => g.Choices.Any(c => c.IsChecked))
        + draft.PickerFields.Count(f => f.Picker.Tokens.Count > 0)
        + (draft.CrField is { Selected.Filter.IsSet: true } ? 1 : 0)
        + draft.TextFields.Count(f => f.Text.Trim().Length > 0)
        + draft.DateFields.Count(f => f.Range.IsSet)
        + draft.NumberFields.Count(f => f.Range.IsSet);

    public static IEnumerable<(string Key, string Label)> CustomChips(ILocalizedStrings strings, WorkflowSnapshot? workflow, IReadOnlyList<CustomFieldFilter> filters)
    {
        foreach (var filter in filters.Where(f => f.IsSet))
        {
            var field = workflow?.Fields.FirstOrDefault(f => f.Id == filter.FieldId);
            var label = field?.Label.Display ?? strings.GetString("Filters_Unknown");
            var value = filter switch
            {
                { TextContains: { Length: > 0 } text } => strings.Format("Filters_ContainsValue", text),
                { Number: { IsSet: true } number } => FilterText.Number(strings, number),
                { Date: { IsSet: true } date } => FilterText.Range(strings, date),
                _ => string.Join(", ", filter.Options.Select(o => field?.Options.FirstOrDefault(fo => fo.Id == o)?.Label.Display ?? strings.GetString("Filters_Unknown"))),
            };
            yield return (FilterKeys.Custom(filter.FieldId), strings.Format("Filters_ChipLabel", label, value));
        }
    }
}

/// <summary>
/// The shared Board/Grid task scope (B-10: "Board/Grid share task search and advanced filters"), persisted as
/// <c>tasks</c>. Its predicate is the Application <see cref="TaskFilterMatcher"/>, the same one Dashboard metrics use.
/// </summary>
public sealed class TaskFilterScopeViewModel : FilterScopeViewModel<TaskFilterSpec>
{
    internal const string Key = "tasks";

    internal TaskFilterScopeViewModel(WorkspaceContext context)
        : base(context, Key)
    {
    }

    public override string ScopeTitle => Context.Strings.GetString("Filters_TaskScopeTitle");

    internal override AppPage Destination => AppPage.Grid;

    private protected override TaskFilterSpec Empty => TaskFilterSpec.Empty;

    /// <summary>
    /// The membership predicate for task and Subtask card lists. It runs on the store scheduler and reads the parent
    /// and the linked CR from the store, which is current there (ADR 0010).
    /// </summary>
    public Func<WorkItemRecord, bool> Predicate()
    {
        var spec = Spec;
        if (!spec.IsActive)
        {
            return _ => true;
        }

        var store = Context.Store;
        var context = new TaskFilterContext(
            id => store.Tasks.Lookup(id) is { HasValue: true } task ? task.Value : null,
            id => store.ChangeRequests.Lookup(id) is { HasValue: true } cr ? cr.Value : null);
        return record => record.Task is { } task ? TaskFilterMatcher.Matches(spec, task, context) : TaskFilterMatcher.Matches(spec, record.Card!, context);
    }

    public override FilterDraftViewModel CreateDraft()
    {
        var strings = Context.Strings;
        var spec = Spec;
        var draft = new FilterDraftViewModel(strings, ScopeTitle, DraftFields.Count);
        if (Workflow is { } workflow)
        {
            DraftFields.Choices(draft, FilterKeys.Status, strings.GetString("Filters_Status"), workflow.TaskStatuses.OrderBy(s => s.Order).Select(s => (s.Id.Value, s.Name.Display)), spec.Statuses.Select(s => s.Value));
            DraftFields.Choices(draft, FilterKeys.Priority, strings.GetString("Filters_Priority"), workflow.Priorities.OrderBy(p => p.Order).Select(p => (p.Id.Value, p.Name.Display)), spec.Priorities.Select(p => p.Value));
            DraftFields.Choices(draft, FilterKeys.Squad, strings.GetString("Filters_Squad"), Squads(workflow), spec.Squads.Select(s => s?.Value ?? Guid.Empty));
            DraftFields.Choices(draft, FilterKeys.Owner, strings.GetString("Filters_Owner"), Owners(workflow, spec.Owners), spec.Owners.Select(p => p.Value));
            draft.PickerFields.Add(Picker(FilterKeys.Designers, "Filters_Designers", People(workflow), spec.Designers.Select(p => p.Value), workflow, false, false));
            draft.PickerFields.Add(Picker(FilterKeys.Developers, "Filters_Developers", People(workflow), spec.Developers.Select(p => p.Value), workflow, false, false));
            draft.PickerFields.Add(Picker(FilterKeys.Tags, "Filters_Tags", () => workflow.Tags.Select(t => (t.Id.Value, t.Name.Display)), spec.Tags.Select(t => t.Value), workflow, true, spec.TagMode == TagMatchMode.All));
        }

        draft.CrField = new FilterCrFieldViewModel(strings.GetString("Filters_Cr"), CrChoice(spec.Cr), SuggestCrs, draft.Changed);
        draft.TextFields.Add(new FilterTextFieldViewModel(FilterKeys.Service, strings.GetString("Filters_ServiceContains"), "Filters_ServiceBox", spec.ServiceContains, draft.Changed));
        var inverted = strings.GetString("Filters_InvertedRange");
        draft.DateFields.Add(new FilterDateFieldViewModel(FilterKeys.StartDate, strings.GetString("Filters_StartDate"), strings.GetString("Filters_IncludeWithoutStart"), "Filters_StartDate", spec.StartDate, inverted, draft.Changed));
        draft.DateFields.Add(new FilterDateFieldViewModel(FilterKeys.EndDate, strings.GetString("Filters_EndDate"), strings.GetString("Filters_IncludeWithoutEnd"), "Filters_EndDate", spec.EndDate, inverted, draft.Changed));
        if (Workflow is { } fields)
        {
            DraftFields.Custom(draft, strings, fields.FieldsFor(FieldTarget.Task), spec.CustomFields);
        }

        draft.EndBuild();
        return draft;
    }

    private protected override TaskFilterSpec FromDraft(FilterDraftViewModel draft, TaskFilterSpec current)
    {
        IReadOnlyList<Guid> Checked(string key) => draft.ChoiceGroups.FirstOrDefault(g => g.Key == key)?.CheckedIds ?? [];
        IReadOnlyList<Guid> Picked(string key) => draft.PickerFields.FirstOrDefault(f => f.Key == key)?.Picker.Ids ?? [];
        var tags = draft.PickerFields.FirstOrDefault(f => f.Key == FilterKeys.Tags);
        var service = draft.TextFields.FirstOrDefault(f => f.Key == FilterKeys.Service)?.Text.Trim();
        var start = draft.DateFields.FirstOrDefault(f => f.Key == FilterKeys.StartDate)?.Range;
        var end = draft.DateFields.FirstOrDefault(f => f.Key == FilterKeys.EndDate)?.Range;
        return current with
        {
            Statuses = [.. Checked(FilterKeys.Status).Select(id => new TaskStatusId(id))],
            Priorities = [.. Checked(FilterKeys.Priority).Select(id => new PriorityId(id))],
            Squads = [.. Checked(FilterKeys.Squad).Select(id => id == Guid.Empty ? (SquadId?)null : new SquadId(id))],
            Owners = [.. Checked(FilterKeys.Owner).Select(id => new PersonId(id))],
            Designers = [.. Picked(FilterKeys.Designers).Select(id => new PersonId(id))],
            Developers = [.. Picked(FilterKeys.Developers).Select(id => new PersonId(id))],
            Tags = [.. Picked(FilterKeys.Tags).Select(id => new TagId(id))],
            TagMode = tags?.MatchModeIndex == 1 ? TagMatchMode.All : TagMatchMode.Any,
            Cr = draft.CrField?.Selected.Filter ?? CrLinkFilter.Any,
            ServiceContains = string.IsNullOrEmpty(service) ? null : service,
            StartDate = start is { IsSet: true } ? start : null,
            EndDate = end is { IsSet: true } ? end : null,
            CustomFields = DraftFields.ReadCustom(draft),
        };
    }

    private protected override TaskFilterSpec Load(ViewFilterPreference? preference) => FilterPersistence.ToTaskSpec(preference);

    private protected override ViewFilterPreference Save(TaskFilterSpec spec) => FilterPersistence.ToPreference(spec);

    private protected override string SearchOf(TaskFilterSpec spec) => spec.SearchText;

    private protected override TaskFilterSpec WithSearch(TaskFilterSpec spec, string search) => spec with { SearchText = search };

    private protected override int FieldCount(TaskFilterSpec spec) => spec.FieldCount;

    private protected override bool IsActive(TaskFilterSpec spec) => spec.IsActive;

    private protected override TaskFilterSpec Without(TaskFilterSpec spec, string key) => key switch
    {
        FilterKeys.Status => spec with { Statuses = [] },
        FilterKeys.Priority => spec with { Priorities = [] },
        FilterKeys.Squad => spec with { Squads = [] },
        FilterKeys.Owner => spec with { Owners = [] },
        FilterKeys.Designers => spec with { Designers = [] },
        FilterKeys.Developers => spec with { Developers = [] },
        FilterKeys.Tags => spec with { Tags = [], TagMode = TagMatchMode.Any },
        FilterKeys.Cr => spec with { Cr = CrLinkFilter.Any },
        FilterKeys.Service => spec with { ServiceContains = null },
        FilterKeys.StartDate => spec with { StartDate = null },
        FilterKeys.EndDate => spec with { EndDate = null },
        _ when FilterKeys.ParseCustom(key) is { } field => spec with { CustomFields = [.. spec.CustomFields.Where(f => f.FieldId != field)] },
        _ => spec,
    };

    private protected override IEnumerable<(string Key, string Label)> FieldChips(TaskFilterSpec spec)
    {
        var s = Context.Strings;
        var w = Workflow;
        string Chip(string field, string value) => s.Format("Filters_ChipLabel", s.GetString(field), value);
        if (spec.Statuses.Count > 0)
        {
            yield return (FilterKeys.Status, Chip("Filters_Status", Names(spec.Statuses, id => w?.FindTaskStatus(id)?.Name.Display)));
        }

        if (spec.Priorities.Count > 0)
        {
            yield return (FilterKeys.Priority, Chip("Filters_Priority", Names(spec.Priorities, id => w?.FindPriority(id)?.Name.Display)));
        }

        if (spec.Squads.Count > 0)
        {
            yield return (FilterKeys.Squad, Chip("Filters_Squad", Names(spec.Squads, id => id is { } squad ? w?.FindSquad(squad)?.Name.Display : s.GetString("Squad_General"))));
        }

        if (spec.Owners.Count > 0)
        {
            yield return (FilterKeys.Owner, Chip("Filters_Owner", Names(spec.Owners, id => w?.FindPerson(id)?.Name.Display)));
        }

        if (spec.Designers.Count > 0)
        {
            yield return (FilterKeys.Designers, Chip("Filters_Designers", Names(spec.Designers, id => w?.FindPerson(id)?.Name.Display)));
        }

        if (spec.Developers.Count > 0)
        {
            yield return (FilterKeys.Developers, Chip("Filters_Developers", Names(spec.Developers, id => w?.FindPerson(id)?.Name.Display)));
        }

        if (spec.Tags.Count > 0)
        {
            yield return (FilterKeys.Tags, Chip(spec.TagMode == TagMatchMode.All ? "Filters_TagsAll" : "Filters_Tags", Names(spec.Tags, id => w?.FindTag(id)?.Name.Display)));
        }

        if (spec.Cr.IsSet)
        {
            yield return (FilterKeys.Cr, Chip("Filters_Cr", CrChoice(spec.Cr).Display));
        }

        if (!string.IsNullOrWhiteSpace(spec.ServiceContains))
        {
            yield return (FilterKeys.Service, Chip("Filters_Service", s.Format("Filters_ContainsValue", spec.ServiceContains.Trim())));
        }

        if (spec.StartDate is { IsSet: true } startDate)
        {
            yield return (FilterKeys.StartDate, Chip("Filters_StartDate", FilterText.Range(s, startDate)));
        }

        if (spec.EndDate is { IsSet: true } endDate)
        {
            yield return (FilterKeys.EndDate, Chip("Filters_EndDate", FilterText.Range(s, endDate)));
        }

        foreach (var chip in DraftFields.CustomChips(s, w, spec.CustomFields))
        {
            yield return chip;
        }
    }

    private protected override (TaskFilterSpec Spec, IReadOnlyList<RemovedFilterKind> Removed) Clean(TaskFilterSpec spec, WorkflowSnapshot workflow) =>
        // The workflow can reach the UI before the same batch's CRs are in the cache; until the first batch has
        // completed (sequence 0) a CR is assumed to exist, so a saved CR filter is never cleared by that race.
        FilterValidation.Clean(spec, workflow, id => Context.Store.Sequence == 0 || Context.Store.ChangeRequests.Lookup(id).HasValue);

    private IEnumerable<(Guid Id, string Label)> Squads(WorkflowSnapshot workflow) =>
        workflow.Squads.OrderBy(s => s.Order).Select(s => (s.Id.Value, s.Name.Display)).Append((Guid.Empty, Context.Strings.GetString("Squad_General")));

    /// <summary>CR Owners are the people who own a CR now, plus any already selected (design §2.6 lookup chips).</summary>
    private IEnumerable<(Guid Id, string Label)> Owners(WorkflowSnapshot workflow, IReadOnlyList<PersonId> selected)
    {
        var owners = Context.Store.ChangeRequests.Items.Select(c => c.Owner.Id).Concat(selected).ToHashSet();
        return workflow.People.Where(p => owners.Contains(p.Id)).OrderBy(p => p.Name.Display, StringComparer.CurrentCultureIgnoreCase).Select(p => (p.Id.Value, p.Name.Display));
    }

    private static Func<IEnumerable<(Guid Id, string Name)>> People(WorkflowSnapshot workflow) =>
        () => workflow.People.Select(p => (p.Id.Value, p.Name.Display));

    private FilterPickerFieldViewModel Picker(
        string key, string header, Func<IEnumerable<(Guid Id, string Name)>> existing, IEnumerable<Guid> selected, WorkflowSnapshot workflow, bool isTags, bool matchAll)
    {
        var picker = new TokenPickerViewModel(Context.Strings, existing, new NewNameRegistry()) { AllowNew = false, IsPeople = !isTags };
        picker.Load(selected.Select(id => (id, (isTags ? workflow.FindTag(new TagId(id))?.Name.Display : workflow.FindPerson(new PersonId(id))?.Name.Display) ?? string.Empty, false)));
        return new FilterPickerFieldViewModel(key, Context.Strings.GetString(header), "Filters_" + key + "Picker", picker, isTags, matchAll);
    }

    private FilterCrChoice CrChoice(CrLinkFilter filter) => filter.Mode switch
    {
        CrLinkMode.NoCr => new FilterCrChoice(filter, Context.Strings.GetString("Filters_NoCr")),
        CrLinkMode.Specific when filter.ChangeRequestId is { } id => new FilterCrChoice(
            filter, Context.Store.ChangeRequests.Lookup(id) is { HasValue: true } cr ? cr.Value.Number + " · " + cr.Value.Title : Context.Strings.GetString("Filters_Unknown")),
        _ => new FilterCrChoice(CrLinkFilter.Any, Context.Strings.GetString("Filters_AnyCr")),
    };

    private List<FilterCrChoice> SuggestCrs(string query)
    {
        var q = query.Trim();
        var list = new List<FilterCrChoice>
        {
            new(CrLinkFilter.Any, Context.Strings.GetString("Filters_AnyCr")),
            new(CrLinkFilter.NoCr, Context.Strings.GetString("Filters_NoCr")),
        };
        list.AddRange(Context.Store.ChangeRequests.Items
            .Where(c => q.Length == 0 || CrFilterMatcher.Search(c, q))
            .OrderBy(c => c.Number, StringComparer.CurrentCultureIgnoreCase)
            .Take(20)
            .Select(c => new FilterCrChoice(CrLinkFilter.For(c.Id), c.Number + " · " + c.Title)));
        return list;
    }
}

/// <summary>
/// The Calendar CR scope (B-10, B-08.12), persisted as <c>calendar</c>. Bars, backlog, overflow and Records all use
/// its one predicate.
/// </summary>
public sealed class CrFilterScopeViewModel : FilterScopeViewModel<CrFilterSpec>
{
    internal const string Key = "calendar";

    internal CrFilterScopeViewModel(WorkspaceContext context)
        : base(context, Key)
    {
    }

    public override string ScopeTitle => Context.Strings.GetString("Filters_CrScopeTitle");

    internal override AppPage Destination => AppPage.Calendar;

    private protected override CrFilterSpec Empty => CrFilterSpec.Empty;

    /// <param name="today">The store's local day, so the overdue drill-down agrees with the Dashboard (B-15).</param>
    public Func<ChangeRequestReadModel, bool> Predicate(DateOnly today)
    {
        var spec = Spec;
        return spec.IsActive ? cr => CrFilterMatcher.Matches(spec, cr, today) : _ => true;
    }

    public override FilterDraftViewModel CreateDraft()
    {
        var strings = Context.Strings;
        var spec = Spec;
        var draft = new FilterDraftViewModel(strings, ScopeTitle, DraftFields.Count);
        if (Workflow is { } workflow)
        {
            DraftFields.Choices(draft, FilterKeys.Status, strings.GetString("Filters_Status"), workflow.CrStatuses.OrderBy(s => s.Order).Select(s => (s.Id.Value, s.Name.Display)), spec.Statuses.Select(s => s.Value));
            DraftFields.Choices(draft, FilterKeys.Priority, strings.GetString("Filters_Priority"), workflow.Priorities.OrderBy(p => p.Order).Select(p => (p.Id.Value, p.Name.Display)), spec.Priorities.Select(p => p.Value));
            DraftFields.Choices(
                draft,
                FilterKeys.Squad,
                strings.GetString("Filters_Squad"),
                workflow.Squads.OrderBy(s => s.Order).Select(s => (s.Id.Value, s.Name.Display)).Append((Guid.Empty, strings.GetString("Squad_General"))),
                spec.Squads.Select(s => s?.Value ?? Guid.Empty));
            var owners = Context.Store.ChangeRequests.Items.Select(c => c.Owner.Id).Concat(spec.Owners).ToHashSet();
            DraftFields.Choices(
                draft,
                FilterKeys.Owner,
                strings.GetString("Filters_Owner"),
                workflow.People.Where(p => owners.Contains(p.Id)).OrderBy(p => p.Name.Display, StringComparer.CurrentCultureIgnoreCase).Select(p => (p.Id.Value, p.Name.Display)),
                spec.Owners.Select(p => p.Value));
        }

        draft.DateFields.Add(new FilterDateFieldViewModel(
            FilterKeys.Planned, strings.GetString("Filters_Planned"), strings.GetString("Filters_IncludeUnscheduled"), "Filters_Planned", spec.Planned, strings.GetString("Filters_InvertedRange"), draft.Changed));
        if (Workflow is { } fields)
        {
            DraftFields.Custom(draft, strings, fields.FieldsFor(FieldTarget.ChangeRequest), spec.CustomFields);
        }

        draft.EndBuild();
        return draft;
    }

    private protected override CrFilterSpec FromDraft(FilterDraftViewModel draft, CrFilterSpec current)
    {
        IReadOnlyList<Guid> Checked(string key) => draft.ChoiceGroups.FirstOrDefault(g => g.Key == key)?.CheckedIds ?? [];
        var planned = draft.DateFields.FirstOrDefault(f => f.Key == FilterKeys.Planned)?.Range;
        return current with
        {
            Statuses = [.. Checked(FilterKeys.Status).Select(id => new CrStatusId(id))],
            Priorities = [.. Checked(FilterKeys.Priority).Select(id => new PriorityId(id))],
            Squads = [.. Checked(FilterKeys.Squad).Select(id => id == Guid.Empty ? (SquadId?)null : new SquadId(id))],
            Owners = [.. Checked(FilterKeys.Owner).Select(id => new PersonId(id))],
            Planned = planned is { IsSet: true } ? planned : null,
            CustomFields = DraftFields.ReadCustom(draft),
        };
    }

    private protected override CrFilterSpec Load(ViewFilterPreference? preference) => FilterPersistence.ToCrSpec(preference);

    private protected override ViewFilterPreference Save(CrFilterSpec spec) => FilterPersistence.ToPreference(spec);

    private protected override string SearchOf(CrFilterSpec spec) => spec.SearchText;

    private protected override CrFilterSpec WithSearch(CrFilterSpec spec, string search) => spec with { SearchText = search };

    private protected override int FieldCount(CrFilterSpec spec) => spec.FieldCount;

    private protected override bool IsActive(CrFilterSpec spec) => spec.IsActive;

    private protected override CrFilterSpec Without(CrFilterSpec spec, string key) => key switch
    {
        FilterKeys.Status => spec with { Statuses = [] },
        FilterKeys.Priority => spec with { Priorities = [] },
        FilterKeys.Squad => spec with { Squads = [] },
        FilterKeys.Owner => spec with { Owners = [] },
        FilterKeys.Planned => spec with { Planned = null },
        _ when FilterKeys.ParseCustom(key) is { } field => spec with { CustomFields = [.. spec.CustomFields.Where(f => f.FieldId != field)] },
        _ => spec,
    };

    private protected override IEnumerable<(string Key, string Label)> FieldChips(CrFilterSpec spec)
    {
        var s = Context.Strings;
        var w = Workflow;
        string Chip(string field, string value) => s.Format("Filters_ChipLabel", s.GetString(field), value);
        if (spec.Statuses.Count > 0)
        {
            yield return (FilterKeys.Status, Chip("Filters_Status", Names(spec.Statuses, id => w?.FindCrStatus(id)?.Name.Display)));
        }

        if (spec.Priorities.Count > 0)
        {
            yield return (FilterKeys.Priority, Chip("Filters_Priority", Names(spec.Priorities, id => w?.FindPriority(id)?.Name.Display)));
        }

        if (spec.Squads.Count > 0)
        {
            yield return (FilterKeys.Squad, Chip("Filters_Squad", Names(spec.Squads, id => id is { } squad ? w?.FindSquad(squad)?.Name.Display : s.GetString("Squad_General"))));
        }

        if (spec.Owners.Count > 0)
        {
            yield return (FilterKeys.Owner, Chip("Filters_Owner", Names(spec.Owners, id => w?.FindPerson(id)?.Name.Display)));
        }

        if (spec.Planned is { IsSet: true } planned)
        {
            yield return (FilterKeys.Planned, Chip("Filters_Planned", FilterText.Range(s, planned)));
        }

        foreach (var chip in DraftFields.CustomChips(s, w, spec.CustomFields))
        {
            yield return chip;
        }
    }

    private protected override (CrFilterSpec Spec, IReadOnlyList<RemovedFilterKind> Removed) Clean(CrFilterSpec spec, WorkflowSnapshot workflow) =>
        FilterValidation.Clean(spec, workflow);
}
