using System.Globalization;
using System.Reactive.Linq;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Shell;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Common.Filters;

/// <summary>
/// Field keys shared by the menu rows, chips, and AutomationIds (<c>Filters_Row_{Key}</c>, <c>Filters_{Key}_{Value}</c>,
/// <c>{Page}_FilterChip_{Key}</c>).
/// </summary>
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
    public const string ActiveDate = "ActiveDate";
    public const string CustomPrefix = "Custom_";

    public static string Custom(FieldId id) => CustomPrefix + id.Value.ToString("N", CultureInfo.InvariantCulture);

    public static FieldId? ParseCustom(string key) =>
        key.StartsWith(CustomPrefix, StringComparison.Ordinal) && Guid.TryParse(key[CustomPrefix.Length..], out var id) ? new FieldId(id) : null;
}

/// <summary>Visible text for filter values; dates use the user's culture (T-08), stored values never do.</summary>
internal static class FilterText
{
    public static string Date(DateOnly day) => day.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    /// <summary>An inclusive range: "Nov 1, 2026 – Nov 7, 2026", "from …", "until …", or empty.</summary>
    public static string Range(ILocalizedStrings strings, DateOnly? from, DateOnly? to) => (from, to) switch
    {
        ({ } f, { } t) when f == t => Date(f),
        ({ } f, { } t) => strings.Format("Filters_RangeBoth", Date(f), Date(t)),
        ({ } f, null) => strings.Format("Filters_RangeFrom", Date(f)),
        (null, { } t) => strings.Format("Filters_RangeTo", Date(t)),
        _ => string.Empty,
    };

    /// <summary>The selected presets, the custom range, and No date, in menu order: "Today, This week, No date".</summary>
    public static string Dates(ILocalizedStrings strings, DateRangeFilter filter)
    {
        var parts = DatePresetRanges.All.Where(p => filter.Presets.HasFlag(p)).Select(p => strings.GetString("Filters_Date" + p)).ToList();
        if (filter.HasCustomRange)
        {
            parts.Add(Range(strings, filter.From, filter.To));
        }

        if (filter.IncludeUndated)
        {
            parts.Add(strings.GetString("Filters_DateNoDate"));
        }

        return string.Join(", ", parts);
    }

    public static string Number(ILocalizedStrings strings, NumberRangeFilter range) => (range.Min, range.Max) switch
    {
        ({ } min, { } max) => strings.Format("Filters_RangeBoth", min.ToString(CultureInfo.CurrentCulture), max.ToString(CultureInfo.CurrentCulture)),
        ({ } min, null) => strings.Format("Filters_NumberMin", min.ToString(CultureInfo.CurrentCulture)),
        (null, { } max) => strings.Format("Filters_NumberMax", max.ToString(CultureInfo.CurrentCulture)),
        _ => string.Empty,
    };
}

/// <summary>Builds and reads the menu rows both scopes share: lookups, dates, Active date, and custom fields.</summary>
internal static class MenuFields
{
    // Row icons (Segoe Fluent Icons).
    public const string PriorityGlyph = "";
    public const string StatusGlyph = "";
    public const string DateGlyph = "";
    public const string ActiveDateGlyph = "";
    public const string SquadGlyph = "";
    public const string PersonGlyph = "";
    public const string TagGlyph = "";
    public const string CrGlyph = "";
    public const string TextGlyph = "";
    public const string NumberGlyph = "";
    public const string ListGlyph = "";

    /// <summary>A choice for each option; checked when its ID is selected.</summary>
    public static FilterChoiceGroupViewModel Choices(
        FilterMenuViewModel menu,
        string key,
        IEnumerable<(Guid Id, string Label, string Glyph, FilterGlyphTone Tone)> options,
        IEnumerable<Guid> selected,
        bool showsMatchMode = false,
        bool matchAll = false)
    {
        var chosen = selected.ToHashSet();
        var isPerson = key is FilterKeys.Owner or FilterKeys.Designers or FilterKeys.Developers;
        var list = options
            .Select(o => new FilterChoiceViewModel(o.Id, o.Label, ChoiceId(key, o.Id), chosen.Contains(o.Id), o.Glyph, o.Tone) { IsPerson = isPerson })
            .ToList();
        return new FilterChoiceGroupViewModel(key, list, menu.Changed, showsMatchMode, matchAll);
    }

    public static IEnumerable<(Guid, string, string, FilterGlyphTone)> Plain(IEnumerable<(Guid Id, string Label)> options) =>
        options.Select(o => (o.Id, o.Label, string.Empty, FilterGlyphTone.Default));

    /// <summary>Priorities in rank order with the chip glyph and colour of their rank (design §6.1).</summary>
    public static IEnumerable<(Guid, string, string, FilterGlyphTone)> Priorities(WorkflowSnapshot workflow)
    {
        var levels = PriorityLevels.Of(workflow.Priorities);
        return workflow.Priorities.OrderBy(p => p.Order).Select(p =>
        {
            var level = levels.GetValueOrDefault(p.Id, PriorityLevel.Mid);
            var tone = level switch
            {
                PriorityLevel.Level1 => FilterGlyphTone.Critical,
                PriorityLevel.Level2 => FilterGlyphTone.Caution,
                _ => FilterGlyphTone.Default,
            };
            return (p.Id.Value, p.Name.Display, WorkflowGlyphs.Priority(level), tone);
        });
    }

    public static FilterGlyphTone StatusTone(bool isCompleted) => isCompleted ? FilterGlyphTone.Success : FilterGlyphTone.Default;

    /// <summary>The checked IDs of a choice row, or the current values when the menu has no such row.</summary>
    public static IReadOnlyList<T> Checked<T>(FilterMenuViewModel menu, string key, IReadOnlyList<T> current, Func<Guid, T> map) =>
        menu.Find<FilterChoiceGroupViewModel>(key) is { } group ? [.. group.CheckedIds.Select(map)] : current;

    public static DateRangeFilter? Date(FilterMenuViewModel menu, string key, DateRangeFilter? current) =>
        menu.Find<FilterDateFieldViewModel>(key) is { } field ? field.Filter is { IsSet: true } filter ? filter : null : current;

    public static PeriodFilter? ActiveDate(FilterMenuViewModel menu, PeriodFilter? current) =>
        menu.Find<ActiveDateFieldViewModel>(FilterKeys.ActiveDate) is { } field ? field.Filter : current;

    public static void AddDates(FilterMenuViewModel menu, ILocalizedStrings strings, string key, string label, DateRangeFilter? current, DateOnly today) =>
        menu.Add(new FilterDateFieldViewModel(key, current, today, strings, menu.Changed), strings.GetString(label), DateGlyph);

    public static void AddActiveDate(FilterMenuViewModel menu, ILocalizedStrings strings, PeriodFilter? current, DateOnly today) =>
        menu.Add(new ActiveDateFieldViewModel(current, today, strings, menu.Changed), strings.GetString("Filters_ActiveDate"), ActiveDateGlyph);

    public static void AddCustom(
        FilterMenuViewModel menu, ILocalizedStrings strings, IEnumerable<FieldDefinition> fields, IReadOnlyList<CustomFieldFilter> current, DateOnly today)
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
                    menu.Add(new FilterNumberFieldViewModel(key, filter?.Number, inverted, menu.Changed), label, NumberGlyph);
                    break;
                case FieldKind.Date:
                    menu.Add(new FilterDateFieldViewModel(key, filter?.Date, today, strings, menu.Changed), label, DateGlyph);
                    break;
                case FieldKind.Lookup:
                    menu.Add(
                        Choices(menu, key, Plain(field.Options.OrderBy(o => o.Order).Select(o => (o.Id.Value, o.Label.Display))), filter?.Options.Select(o => o.Value) ?? []),
                        label,
                        ListGlyph);
                    break;
                default:
                    menu.Add(new FilterTextFieldViewModel(key, filter?.TextContains, menu.Changed), label, TextGlyph);
                    break;
            }
        }
    }

    /// <summary>
    /// The custom field filters of the menu, in row order. An inverted number range is not applied: the field keeps
    /// the filter the scope already had (B-10). Filters on fields the menu has no row for (it opened before the workflow
    /// arrived) are kept as they are.
    /// </summary>
    public static List<CustomFieldFilter> ReadCustom(FilterMenuViewModel menu, IReadOnlyList<CustomFieldFilter> current)
    {
        var result = new List<CustomFieldFilter>();
        foreach (var row in menu.Rows)
        {
            if (FilterKeys.ParseCustom(row.Key) is not { } id)
            {
                continue;
            }

            CustomFieldFilter? filter = row.Editor switch
            {
                FilterTextFieldViewModel { IsSet: true } text => new CustomFieldFilter(id, text.CommittedText, null, null, []),
                FilterNumberFieldViewModel { HasError: true } => current.FirstOrDefault(f => f.FieldId == id),
                FilterNumberFieldViewModel { Range.IsSet: true } number => new CustomFieldFilter(id, null, number.Range, null, []),
                FilterDateFieldViewModel { IsSet: true } date => new CustomFieldFilter(id, null, null, date.Filter, []),
                FilterChoiceGroupViewModel { IsSet: true } group => new CustomFieldFilter(id, null, null, null, [.. group.CheckedIds.Select(o => new FieldOptionId(o))]),
                _ => null,
            };
            if (filter is not null)
            {
                result.Add(filter);
            }
        }

        var rows = menu.Rows.Select(r => FilterKeys.ParseCustom(r.Key)).OfType<FieldId>().ToHashSet();
        result.AddRange(current.Where(f => !rows.Contains(f.FieldId)));
        return result;
    }

    /// <summary>"Active date: Last 7 days" or "Active date: Nov 1, 2026 – Nov 7, 2026".</summary>
    public static (string Key, string Label) ActiveDateChip(ILocalizedStrings strings, PeriodFilter period) =>
        (FilterKeys.ActiveDate, strings.Format("Filters_ChipLabel", strings.GetString("Filters_ActiveDate"), PeriodFieldViewModel.Text(strings, period)));

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
                { Date: { IsSet: true } date } => FilterText.Dates(strings, date),
                _ => string.Join(", ", filter.Options.Select(o => field?.Options.FirstOrDefault(fo => fo.Id == o)?.Label.Display ?? strings.GetString("Filters_Unknown"))),
            };
            yield return (FilterKeys.Custom(filter.FieldId), strings.Format("Filters_ChipLabel", label, value));
        }
    }

    private static string ChoiceId(string key, Guid id) =>
        "Filters_" + key + "_" + (id == Guid.Empty ? "General" : id.ToString("N", CultureInfo.InvariantCulture));
}

/// <summary>
/// The shared Board/Grid task scope (B-10: "Board/Grid share task search and advanced filters"), persisted as
/// <c>tasks</c>. Its predicate is the Application <see cref="TaskFilterMatcher"/>, the same one Dashboard metrics use.
/// </summary>
public sealed class TaskFilterScopeViewModel : FilterScopeViewModel<TaskFilterSpec>
{
    internal const string Key = "tasks";

    private readonly IDisposable _commits;
    private DateOnly _predicateDay;

    internal TaskFilterScopeViewModel(WorkspaceContext context)
        : base(context, Key)
    {
        // Rolling dates (Active date "Last 7 days", date presets such as "This week") move at midnight, on resume and
        // on a time-zone change (AC-21). The new day can arrive with any commit (a day tick, or a snapshot after a
        // resync that skipped the tick), so every commit whose day differs from the one the predicate was built for
        // makes Board and Grid rebuild it. This also corrects a predicate built on the startup fallback day. The
        // Calendar re-applies its CR predicate itself.
        _commits = context.Store.Committed
            .ObserveOn(context.UiScheduler)
            .Subscribe(commit =>
            {
                if (commit.Today != _predicateDay && Spec.IsRolling)
                {
                    // Recorded here too, so a day change raises once even before a page has built a predicate.
                    _predicateDay = commit.Today;
                    RaiseChanged();
                }
            });
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
        var store = Context.Store;
        var today = Today;
        _predicateDay = today;
        if (!spec.IsActive)
        {
            return _ => true;
        }

        var context = new TaskFilterContext(
            id => store.Tasks.Lookup(id) is { HasValue: true } task ? task.Value : null,
            id => store.ChangeRequests.Lookup(id) is { HasValue: true } cr ? cr.Value : null,
            today);
        return record => record.Task is { } task ? TaskFilterMatcher.Matches(spec, task, context) : TaskFilterMatcher.Matches(spec, record.Card!, context);
    }

    /// <summary>Rows in design §2.6 order: Priority, Status, Start date, End date, Active date, Squad, CR Owner, people, Tags, CR, Service, custom fields.</summary>
    public override FilterMenuViewModel CreateMenu()
    {
        var s = Context.Strings;
        var spec = Spec;
        var today = Today;
        var workflow = Workflow;
        var menu = OpenMenu();
        if (workflow is not null)
        {
            menu.Add(MenuFields.Choices(menu, FilterKeys.Priority, MenuFields.Priorities(workflow), spec.Priorities.Select(p => p.Value)), s.GetString("Filters_Priority"), MenuFields.PriorityGlyph);
            menu.Add(
                MenuFields.Choices(
                    menu,
                    FilterKeys.Status,
                    workflow.TaskStatuses.OrderBy(t => t.Order).Select(t => (t.Id.Value, t.Name.Display, WorkflowGlyphs.TaskStatus(t), MenuFields.StatusTone(t.IsCompleted))),
                    spec.Statuses.Select(t => t.Value)),
                s.GetString("Filters_Status"),
                MenuFields.StatusGlyph);
        }

        MenuFields.AddDates(menu, s, FilterKeys.StartDate, "Filters_StartDate", spec.StartDate, today);
        MenuFields.AddDates(menu, s, FilterKeys.EndDate, "Filters_EndDate", spec.EndDate, today);
        MenuFields.AddActiveDate(menu, s, spec.Period, today);
        if (workflow is not null)
        {
            menu.Add(MenuFields.Choices(menu, FilterKeys.Squad, MenuFields.Plain(Squads(workflow)), spec.Squads.Select(q => q?.Value ?? Guid.Empty)), s.GetString("Filters_Squad"), MenuFields.SquadGlyph);
            menu.Add(MenuFields.Choices(menu, FilterKeys.Owner, MenuFields.Plain(Owners(workflow, spec.Owners)), spec.Owners.Select(p => p.Value)), s.GetString("Filters_Owner"), MenuFields.PersonGlyph);
            menu.Add(MenuFields.Choices(menu, FilterKeys.Designers, MenuFields.Plain(People(workflow)), spec.Designers.Select(p => p.Value)), s.GetString("Filters_Designers"), MenuFields.PersonGlyph);
            menu.Add(MenuFields.Choices(menu, FilterKeys.Developers, MenuFields.Plain(People(workflow)), spec.Developers.Select(p => p.Value)), s.GetString("Filters_Developers"), MenuFields.PersonGlyph);
            menu.Add(
                MenuFields.Choices(
                    menu,
                    FilterKeys.Tags,
                    MenuFields.Plain(workflow.Tags.OrderBy(t => t.Name.Display, StringComparer.CurrentCultureIgnoreCase).Select(t => (t.Id.Value, t.Name.Display))),
                    spec.Tags.Select(t => t.Value),
                    showsMatchMode: true,
                    matchAll: spec.TagMode == TagMatchMode.All),
                s.GetString("Filters_Tags"),
                MenuFields.TagGlyph);
        }

        menu.Add(new FilterCrFieldViewModel(CrChoice(spec.Cr), s.GetString("Filters_AnyCr"), s.GetString("Filters_NoCr"), SuggestCrs, menu.Changed), s.GetString("Filters_Cr"), MenuFields.CrGlyph);
        menu.Add(new FilterTextFieldViewModel(FilterKeys.Service, spec.ServiceContains, menu.Changed), s.GetString("Filters_Service"), MenuFields.TextGlyph);
        if (workflow is not null)
        {
            MenuFields.AddCustom(menu, s, workflow.FieldsFor(FieldTarget.Task), spec.CustomFields, today);
        }

        menu.EndBuild();
        return menu;
    }

    private protected override TaskFilterSpec FromMenu(FilterMenuViewModel menu, TaskFilterSpec current)
    {
        var tags = menu.Find<FilterChoiceGroupViewModel>(FilterKeys.Tags);
        var service = menu.Find<FilterTextFieldViewModel>(FilterKeys.Service);
        return current with
        {
            Statuses = MenuFields.Checked(menu, FilterKeys.Status, current.Statuses, id => new TaskStatusId(id)),
            Priorities = MenuFields.Checked(menu, FilterKeys.Priority, current.Priorities, id => new PriorityId(id)),
            Squads = MenuFields.Checked(menu, FilterKeys.Squad, current.Squads, id => id == Guid.Empty ? (SquadId?)null : new SquadId(id)),
            Owners = MenuFields.Checked(menu, FilterKeys.Owner, current.Owners, id => new PersonId(id)),
            Designers = MenuFields.Checked(menu, FilterKeys.Designers, current.Designers, id => new PersonId(id)),
            Developers = MenuFields.Checked(menu, FilterKeys.Developers, current.Developers, id => new PersonId(id)),
            Tags = MenuFields.Checked(menu, FilterKeys.Tags, current.Tags, id => new TagId(id)),
            TagMode = tags is null ? current.TagMode : tags.IsSet && tags.MatchAll ? TagMatchMode.All : TagMatchMode.Any,
            Cr = menu.Find<FilterCrFieldViewModel>(FilterKeys.Cr)?.Selected.Filter ?? current.Cr,
            ServiceContains = service is null ? current.ServiceContains : service.IsSet ? service.CommittedText : null,
            StartDate = MenuFields.Date(menu, FilterKeys.StartDate, current.StartDate),
            EndDate = MenuFields.Date(menu, FilterKeys.EndDate, current.EndDate),
            Period = MenuFields.ActiveDate(menu, current.Period),
            CustomFields = MenuFields.ReadCustom(menu, current.CustomFields),
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
        FilterKeys.ActiveDate => spec with { Period = null },
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
            yield return (FilterKeys.StartDate, Chip("Filters_StartDate", FilterText.Dates(s, startDate)));
        }

        if (spec.EndDate is { IsSet: true } endDate)
        {
            yield return (FilterKeys.EndDate, Chip("Filters_EndDate", FilterText.Dates(s, endDate)));
        }

        if (spec.Period is { IsSet: true } period)
        {
            yield return MenuFields.ActiveDateChip(s, period);
        }

        foreach (var chip in MenuFields.CustomChips(s, w, spec.CustomFields))
        {
            yield return chip;
        }
    }

    public override void Dispose()
    {
        _commits.Dispose();
        base.Dispose();
    }

    private protected override (TaskFilterSpec Spec, IReadOnlyList<RemovedFilterKind> Removed) Clean(TaskFilterSpec spec, WorkflowSnapshot workflow) =>
        // The workflow can reach the UI before the same batch's CRs are in the cache; until the first batch has
        // completed (sequence 0) a CR is assumed to exist, so a saved CR filter is never cleared by that race.
        FilterValidation.Clean(spec, workflow, id => Context.Store.Sequence == 0 || Context.Store.ChangeRequests.Lookup(id).HasValue);

    private static IEnumerable<(Guid Id, string Label)> People(WorkflowSnapshot workflow) =>
        workflow.People.OrderBy(p => p.Name.Display, StringComparer.CurrentCultureIgnoreCase).Select(p => (p.Id.Value, p.Name.Display));

    private IEnumerable<(Guid Id, string Label)> Squads(WorkflowSnapshot workflow) =>
        workflow.Squads.OrderBy(q => q.Order).Select(q => (q.Id.Value, q.Name.Display)).Append((Guid.Empty, Context.Strings.GetString("Squad_General")));

    /// <summary>CR Owners are the people who own a CR now, plus any already selected (design §2.6).</summary>
    private IEnumerable<(Guid Id, string Label)> Owners(WorkflowSnapshot workflow, IReadOnlyList<PersonId> selected)
    {
        var owners = Context.Store.ChangeRequests.Items.Select(c => c.Owner.Id).Concat(selected).ToHashSet();
        return workflow.People.Where(p => owners.Contains(p.Id)).OrderBy(p => p.Name.Display, StringComparer.CurrentCultureIgnoreCase).Select(p => (p.Id.Value, p.Name.Display));
    }

    private FilterCrChoice CrChoice(CrLinkFilter filter) => filter.Mode switch
    {
        CrLinkMode.NoCr => new FilterCrChoice(filter, Context.Strings.GetString("Filters_NoCr")),
        CrLinkMode.Specific when filter.ChangeRequestId is { } id => new FilterCrChoice(
            filter, Context.Store.ChangeRequests.Lookup(id) is { HasValue: true } cr ? cr.Value.Number + " · " + cr.Value.Title : Context.Strings.GetString("Filters_Unknown")),
        _ => new FilterCrChoice(CrLinkFilter.Any, Context.Strings.GetString("Filters_AnyCr")),
    };

    /// <summary>The CR search box: matching CRs by number (Any CR and No CR are items above the box).</summary>
    private List<FilterCrChoice> SuggestCrs(string query)
    {
        var q = query.Trim();
        return [.. Context.Store.ChangeRequests.Items
            .Where(c => q.Length == 0 || CrFilterMatcher.Search(c, q))
            .OrderBy(c => c.Number, StringComparer.CurrentCultureIgnoreCase)
            .Take(20)
            .Select(c => new FilterCrChoice(CrLinkFilter.For(c.Id), c.Number + " · " + c.Title))];
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

    /// <summary>Rows in design §2.6 order: Priority, Status, Planned dates, Active date, Squad, CR Owner, custom fields.</summary>
    public override FilterMenuViewModel CreateMenu()
    {
        var s = Context.Strings;
        var spec = Spec;
        var today = Today;
        var workflow = Workflow;
        var menu = OpenMenu();
        if (workflow is not null)
        {
            menu.Add(MenuFields.Choices(menu, FilterKeys.Priority, MenuFields.Priorities(workflow), spec.Priorities.Select(p => p.Value)), s.GetString("Filters_Priority"), MenuFields.PriorityGlyph);
            menu.Add(
                MenuFields.Choices(
                    menu,
                    FilterKeys.Status,
                    workflow.CrStatuses.OrderBy(t => t.Order).Select(t => (t.Id.Value, t.Name.Display, WorkflowGlyphs.CrStatus(t), MenuFields.StatusTone(t.IsCompleted))),
                    spec.Statuses.Select(t => t.Value)),
                s.GetString("Filters_Status"),
                MenuFields.StatusGlyph);
        }

        MenuFields.AddDates(menu, s, FilterKeys.Planned, "Filters_Planned", spec.Planned, today);
        MenuFields.AddActiveDate(menu, s, spec.Period, today);
        if (workflow is not null)
        {
            menu.Add(
                MenuFields.Choices(
                    menu,
                    FilterKeys.Squad,
                    MenuFields.Plain(workflow.Squads.OrderBy(q => q.Order).Select(q => (q.Id.Value, q.Name.Display)).Append((Guid.Empty, s.GetString("Squad_General")))),
                    spec.Squads.Select(q => q?.Value ?? Guid.Empty)),
                s.GetString("Filters_Squad"),
                MenuFields.SquadGlyph);
            var owners = Context.Store.ChangeRequests.Items.Select(c => c.Owner.Id).Concat(spec.Owners).ToHashSet();
            menu.Add(
                MenuFields.Choices(
                    menu,
                    FilterKeys.Owner,
                    MenuFields.Plain(workflow.People.Where(p => owners.Contains(p.Id)).OrderBy(p => p.Name.Display, StringComparer.CurrentCultureIgnoreCase).Select(p => (p.Id.Value, p.Name.Display))),
                    spec.Owners.Select(p => p.Value)),
                s.GetString("Filters_Owner"),
                MenuFields.PersonGlyph);
            MenuFields.AddCustom(menu, s, workflow.FieldsFor(FieldTarget.ChangeRequest), spec.CustomFields, today);
        }

        menu.EndBuild();
        return menu;
    }

    private protected override CrFilterSpec FromMenu(FilterMenuViewModel menu, CrFilterSpec current) => current with
    {
        Statuses = MenuFields.Checked(menu, FilterKeys.Status, current.Statuses, id => new CrStatusId(id)),
        Priorities = MenuFields.Checked(menu, FilterKeys.Priority, current.Priorities, id => new PriorityId(id)),
        Squads = MenuFields.Checked(menu, FilterKeys.Squad, current.Squads, id => id == Guid.Empty ? (SquadId?)null : new SquadId(id)),
        Owners = MenuFields.Checked(menu, FilterKeys.Owner, current.Owners, id => new PersonId(id)),
        Planned = MenuFields.Date(menu, FilterKeys.Planned, current.Planned),
        Period = MenuFields.ActiveDate(menu, current.Period),
        CustomFields = MenuFields.ReadCustom(menu, current.CustomFields),
    };

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
        FilterKeys.ActiveDate => spec with { Period = null },
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
            yield return (FilterKeys.Planned, Chip("Filters_Planned", FilterText.Dates(s, planned)));
        }

        if (spec.Period is { IsSet: true } period)
        {
            yield return MenuFields.ActiveDateChip(s, period);
        }

        foreach (var chip in MenuFields.CustomChips(s, w, spec.CustomFields))
        {
            yield return chip;
        }
    }

    private protected override (CrFilterSpec Spec, IReadOnlyList<RemovedFilterKind> Removed) Clean(CrFilterSpec spec, WorkflowSnapshot workflow) =>
        FilterValidation.Clean(spec, workflow);
}
