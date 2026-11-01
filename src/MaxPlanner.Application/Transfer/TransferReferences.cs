namespace MaxPlanner.Application.Transfer;

/// <summary>
/// The configuration a document's records use (ADR 0026): lookup values, custom fields, and Lookup options. A data-only
/// export writes only these definitions, and a data-only import requires only these to exist.
/// </summary>
/// <param name="Lookups">Status, priority, squad, person, and tag IDs, including People and Tags custom values.</param>
/// <param name="Fields">Custom field IDs that hold a value.</param>
/// <param name="Options">Lookup option IDs that hold a value.</param>
public sealed record TransferReferences(IReadOnlySet<Guid> Lookups, IReadOnlySet<Guid> Fields, IReadOnlySet<Guid> Options)
{
    public static TransferReferences Of(TransferDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var lookups = new HashSet<Guid>();
        var fields = new HashSet<Guid>();
        var options = new HashSet<Guid>();

        void One(Guid? id)
        {
            if (id is { } value)
            {
                lookups.Add(value);
            }
        }

        void Many(IReadOnlyList<Guid>? ids) => lookups.UnionWith(ids ?? []);

        void Custom(IReadOnlyDictionary<string, TransferValue>? values)
        {
            foreach (var (key, value) in values ?? new Dictionary<string, TransferValue>())
            {
                if (Guid.TryParse(key, out var field))
                {
                    fields.Add(field);
                }

                Value(value);
            }
        }

        void Value(TransferValue? value)
        {
            if (value?.Option is { } option)
            {
                options.Add(option);
            }

            Many(value?.Ids);
        }

        foreach (var task in document.Tasks)
        {
            One(task.StatusId);
            One(task.PriorityId);
            Many(task.DesignerIds);
            Many(task.DeveloperIds);
            Many(task.TagIds);
            Custom(task.CustomValues);
        }

        foreach (var card in document.SubtaskCards)
        {
            One(card.StatusId);
        }

        foreach (var changeRequest in document.ChangeRequests)
        {
            One(changeRequest.OwnerId);
            One(changeRequest.SquadId);
            One(changeRequest.StatusId);
            One(changeRequest.PriorityId);
            Custom(changeRequest.CustomValues);
        }

        foreach (var review in document.Reviews)
        {
            One(review.DeveloperId);
        }

        return new TransferReferences(lookups, fields, options);
    }

    /// <summary>
    /// ADR 0026 data-only export: the document with only the lookups, fields, and options its records use, and no
    /// defaults. The default value of a kept field is kept too, with what it names, so each definition stays valid.
    /// </summary>
    public static TransferDocument KeepReferencedConfiguration(TransferDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var references = Of(document);
        var fields = document.FieldDefinitions.Where(f => references.Fields.Contains(f.Id)).ToList();
        var lookups = references.Lookups.ToHashSet();
        var options = references.Options.ToHashSet();
        foreach (var field in fields)
        {
            lookups.UnionWith(field.DefaultValue?.Ids ?? []);
            if (field.DefaultValue?.Option is { } option)
            {
                options.Add(option);
            }
        }

        var l = document.Lookups;
        return document with
        {
            Lookups = new TransferLookups
            {
                TaskStatuses = [.. l.TaskStatuses.Where(s => lookups.Contains(s.Id))],
                CrStatuses = [.. l.CrStatuses.Where(s => lookups.Contains(s.Id))],
                Priorities = [.. l.Priorities.Where(p => lookups.Contains(p.Id))],
                Squads = [.. l.Squads.Where(s => lookups.Contains(s.Id))],
                People = [.. l.People.Where(p => lookups.Contains(p.Id))],
                Tags = [.. l.Tags.Where(t => lookups.Contains(t.Id))],
            },
            FieldDefinitions = [.. fields.Select(f => f with { Options = [.. f.Options.Where(o => options.Contains(o.Id))] })],
            Defaults = null,
        };
    }
}
