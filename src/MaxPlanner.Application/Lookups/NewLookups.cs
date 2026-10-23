using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Lookups;

/// <summary>A person typed into a picker that did not exist yet, with a preallocated ID (B-02.6).</summary>
public sealed record NewPerson(PersonId Id, string Name);

/// <summary>A tag typed into a picker that did not exist yet, with a preallocated ID (B-02.6).</summary>
public sealed record NewTag(TagId Id, string Name);

/// <summary>
/// People and tags a draft references but that are created only when the draft is saved (B-02.6: Cancel
/// must not create them). The draft's people and tag lists refer to them by these IDs.
/// </summary>
public sealed record NewLookups(IReadOnlyList<NewPerson> People, IReadOnlyList<NewTag> Tags)
{
    public static NewLookups None { get; } = new([], []);

    public bool IsEmpty => People.Count == 0 && Tags.Count == 0;
}

/// <summary>Creates the new people and tags of a draft inside the draft's own transaction.</summary>
internal static class LookupAdditions
{
    /// <summary>Validates the names before the transaction, so every name error is reported with the field errors.</summary>
    public static void Validate(NewLookups? additions, ValidationCollector validation)
    {
        foreach (var person in additions?.People ?? [])
        {
            validation.Check(LookupName.Create(person.Name, "People"));
        }

        foreach (var tag in additions?.Tags ?? [])
        {
            validation.Check(LookupName.Create(tag.Name, "Tags"));
        }
    }

    /// <summary>
    /// Inserts the new names and rewrites <paramref name="content"/> to the final IDs. A name that already
    /// exists (case-insensitively, for example added by another editor meanwhile) or repeats within the
    /// draft maps to that one record instead of creating a duplicate (B-02.6 trim and deduplicate).
    /// Publishes the new workflow when anything was inserted.
    /// </summary>
    public static TaskContent Apply(IWriteTransaction tx, NewLookups? additions, TaskContent content)
    {
        if (additions is null || additions.IsEmpty)
        {
            return content;
        }

        var workflow = tx.Workflow.Read();
        var people = Resolve(
            additions.People,
            p => p.Id,
            p => LookupName.Create(p.Name, "People").Value,
            name => workflow.People.FirstOrDefault(p => p.Name.Normalized == name.Normalized)?.Id,
            (id, name) => tx.Lookups.InsertPerson(new PersonDefinition(id, name)),
            out var insertedPeople);
        var tags = Resolve(
            additions.Tags,
            t => t.Id,
            t => LookupName.Create(t.Name, "Tags").Value,
            name => workflow.Tags.FirstOrDefault(t => t.Name.Normalized == name.Normalized)?.Id,
            (id, name) => tx.Lookups.InsertTag(new TagDefinition(id, name)),
            out var insertedTags);
        var inserted = insertedPeople || insertedTags;
        if (inserted)
        {
            tx.Changes.ReplaceWorkflow(tx.Workflow.Read());
        }

        return content with
        {
            DesignerIds = [.. content.DesignerIds.Select(id => people.GetValueOrDefault(id, id)).Distinct()],
            DeveloperIds = [.. content.DeveloperIds.Select(id => people.GetValueOrDefault(id, id)).Distinct()],
            TagIds = [.. content.TagIds.Select(id => tags.GetValueOrDefault(id, id)).Distinct()],
        };
    }

    /// <returns>Each requested ID mapped to the ID the draft must use: its own when inserted, otherwise the existing record's.</returns>
    private static Dictionary<TId, TId> Resolve<TAddition, TId>(
        IReadOnlyList<TAddition> additions,
        Func<TAddition, TId> idOf,
        Func<TAddition, LookupName> nameOf,
        Func<LookupName, TId?> findExisting,
        Action<TId, LookupName> insert,
        out bool inserted)
        where TId : struct
    {
        var map = new Dictionary<TId, TId>();
        var addedByName = new Dictionary<string, TId>(StringComparer.Ordinal);
        inserted = false;
        foreach (var addition in additions)
        {
            var id = idOf(addition);
            var name = nameOf(addition);
            if (findExisting(name) is { } existing)
            {
                map[id] = existing;
            }
            else if (addedByName.TryGetValue(name.Normalized, out var sameName))
            {
                map[id] = sameName;
            }
            else
            {
                insert(id, name);
                addedByName[name.Normalized] = id;
                map[id] = id;
                inserted = true;
            }
        }

        return map;
    }
}
