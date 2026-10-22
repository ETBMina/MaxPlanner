using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Tasks;

/// <summary>Raw editor values of the user-editable task fields (B-02.2). Blank text means "no value".</summary>
/// <param name="Title">The task's own title; must be blank for a linked task, whose title comes from its CR.</param>
public sealed record TaskContentDraft(
    string? Title,
    string? Service,
    IReadOnlyList<PersonId> DesignerIds,
    IReadOnlyList<PersonId> DeveloperIds,
    IReadOnlyList<TagId> TagIds,
    string? Notes,
    IReadOnlyDictionary<FieldId, CustomValue> CustomValues);

internal static class TaskContentParser
{
    /// <summary>
    /// Validates the draft text, collecting every error. When <paramref name="isLinked"/> is known, the
    /// title/service ownership rule is checked too, so it is reported together with text errors.
    /// </summary>
    /// <returns>The content, or null when any text is invalid.</returns>
    public static TaskContent? Parse(TaskContentDraft draft, bool? isLinked, ValidationCollector validation)
    {
        var title = validation.Check(string.IsNullOrWhiteSpace(draft.Title)
            ? DomainResult.Success<TitleText?>(null)
            : TitleText.Create(draft.Title).Map<TitleText?>(t => t));
        var service = validation.Check(ServiceText.CreateOptional(draft.Service));
        var notes = validation.Check(LongText.CreateOptional(draft.Notes, "Notes"));
        if (isLinked is { } linked)
        {
            validation.Add(InheritancePolicy.ValidateOwnFields(linked, !string.IsNullOrWhiteSpace(draft.Title), !string.IsNullOrWhiteSpace(draft.Service)));
        }

        return title.IsSuccess && service.IsSuccess && notes.IsSuccess
            ? new TaskContent(title.Value, service.Value, draft.DesignerIds, draft.DeveloperIds, draft.TagIds, notes.Value, draft.CustomValues)
            : null;
    }
}
