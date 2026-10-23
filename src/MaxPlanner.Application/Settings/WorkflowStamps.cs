using System.Globalization;
using System.Text;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Settings;

/// <summary>
/// Concurrency stamps for workflow settings (ADR 0022). Lookups, field definitions, and defaults have no row
/// revisions, so a Settings command carries the stamp of the part it was prepared against; the use case
/// recomputes it inside the transaction and refuses the command with <see cref="Common.WorkflowChangedError"/>
/// when it differs. Stamps are opaque, culture-independent strings compared for equality only.
/// </summary>
/// <remarks>
/// Each stamp covers only what its command depends on, so an unrelated change (for example a person added
/// by a task editor) never makes a rename of another value fail.
/// </remarks>
public static class WorkflowStamps
{
    /// <summary>One lookup value: name, order, and semantic flags. Empty when it does not exist.</summary>
    public static string Item(WorkflowSnapshot workflow, LookupKind kind, Guid id)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return kind switch
        {
            LookupKind.TaskStatus => workflow.FindTaskStatus(new TaskStatusId(id)) is { } s
                ? Join(s.Id.Value, s.Name.Display, s.Order, s.IsCompleted, s.SetsStartDate, s.IsBacklog) : string.Empty,
            LookupKind.CrStatus => workflow.FindCrStatus(new CrStatusId(id)) is { } c
                ? Join(c.Id.Value, c.Name.Display, c.Order, c.IsCompleted, c.IsBacklog) : string.Empty,
            _ => WorkflowLookups.Find(workflow, kind, id) is { } entry ? Join(entry.Id, entry.Name.Display, entry.Order) : string.Empty,
        };
    }

    /// <summary>The configured order of one lookup kind.</summary>
    public static string Order(WorkflowSnapshot workflow, LookupKind kind) =>
        string.Join(',', WorkflowLookups.Entries(workflow, kind).Select(e => e.Id.ToString("N")));

    /// <summary>One field definition, including its options and default. Empty when it does not exist.</summary>
    public static string Field(WorkflowSnapshot workflow, FieldId id)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.Fields.FirstOrDefault(f => f.Id == id) is not { } field)
        {
            return string.Empty;
        }

        var options = string.Join(';', field.Options.Select(o => Join(o.Id.Value, o.Label.Display, o.Order)));
        return Join(field.Id.Value, field.Key.Display, field.Label.Display, field.Kind, field.IsRequired, field.DisplayOrder, field.IsActive, Describe(field.DefaultValue), options);
    }

    /// <summary>The display order of the active fields of one target.</summary>
    public static string FieldOrder(WorkflowSnapshot workflow, FieldTarget target)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return string.Join(',', workflow.FieldsFor(target).Where(f => f.IsActive).OrderBy(f => f.DisplayOrder).ThenBy(f => f.Id.Value).Select(f => f.Id.Value.ToString("N")));
    }

    /// <summary>The workspace defaults (B-04, B-11).</summary>
    public static string Defaults(WorkflowSnapshot workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var d = workflow.Defaults;
        return Join(d.DefaultTaskStatusId.Value, d.DefaultCrStatusId.Value, d.DefaultPriorityId.Value, d.DefaultCrOwnerId?.Value, d.CalendarSchedulingStatusId.Value, d.CrBacklogStatusId.Value);
    }

    private static string Describe(CustomValue? value) => value switch
    {
        null => string.Empty,
        TextCustomValue text => "T:" + text.Value,
        MultilineTextCustomValue text => "M:" + text.Value,
        NumberCustomValue number => "N:" + number.Value.ToString(CultureInfo.InvariantCulture),
        DateCustomValue date => "D:" + date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        LookupCustomValue lookup => "L:" + lookup.OptionId.Value.ToString("N"),
        PeopleCustomValue people => "P:" + string.Join(',', people.PersonIds.Select(p => p.Value.ToString("N"))),
        TagsCustomValue tags => "G:" + string.Join(',', tags.TagIds.Select(t => t.Value.ToString("N"))),
        UrlCustomValue url => "U:" + url.Url,
        _ => value.Kind.ToString(),
    };

    private static string Join(params object?[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            // Lengths make the encoding unambiguous even when a label contains the separator.
            var text = part switch
            {
                null => string.Empty,
                Guid id => id.ToString("N"),
                bool flag => flag ? "1" : "0",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => part.ToString() ?? string.Empty,
            };
            builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append('|');
        }

        return builder.ToString();
    }
}
