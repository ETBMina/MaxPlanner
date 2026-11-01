using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Transfer.External;

/// <summary>The user's mapping of one source table (B-13.1): its entity type and the field of each column.</summary>
/// <param name="ColumnFields">Aligned with the table's columns.</param>
public sealed record ExternalTableMapping(string TableName, ExternalEntity Entity, IReadOnlyList<ExternalField> ColumnFields)
{
    /// <summary>ADR 0027: the custom field of each column (by index) mapped to <see cref="ExternalField.CustomField"/>.</summary>
    public IReadOnlyDictionary<int, ExternalCustomFieldChoice> CustomFields { get; init; } = new Dictionary<int, ExternalCustomFieldChoice>();

    /// <summary>
    /// ADR 0027: the unmapped columns (by index) whose values are discarded. Every other unmapped column is kept hidden
    /// in the record's preserved metadata (T-05).
    /// </summary>
    public IReadOnlySet<int> DiscardedColumns { get; init; } = new HashSet<int>();
}

/// <summary>
/// ADR 0027: the custom field a column maps to: an existing active field of the table's entity, or a new optional field
/// created in the same apply.
/// </summary>
/// <param name="ExistingFieldId">The existing field; null to create one.</param>
/// <param name="Label">A new field's label (its key is derived from it).</param>
/// <param name="Kind">A new field's kind, one of <see cref="ExternalValues.CreatableKinds"/>.</param>
public sealed record ExternalCustomFieldChoice(Guid? ExistingFieldId, string? Label = null, FieldKind Kind = FieldKind.Text)
{
    public static ExternalCustomFieldChoice Existing(Guid fieldId) => new(fieldId);

    public static ExternalCustomFieldChoice New(string label, FieldKind kind) => new(null, label, kind);
}

/// <summary>Everything the wizard decided for an external import (B-13).</summary>
public sealed record ExternalImportOptions
{
    /// <summary>B-13.5: the user-selected source namespace; repeated imports with the same namespace update or skip.</summary>
    public required string SourceNamespace { get; init; }

    public ImportMode Mode { get; init; } = ImportMode.CreateOnly;

    /// <summary>ADR 0027: the import time. A later Created value is an error; an imported Created value gets it as the updated time.</summary>
    public DateTimeOffset NowUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>ADR 0027: the zone of Created values without an offset, and of date-only ones (local midnight).</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    public IReadOnlyList<ExternalTableMapping> Tables { get; init; } = [];

    /// <summary>
    /// B-13.8 value mappings by <see cref="ExternalImportPlanner.ValueKey"/>: an existing lookup ID, or null to create the
    /// value as a new definition. A value without an entry maps to the existing value of the same normalized name, or
    /// is proposed as new; identity is never guessed from a similar name.
    /// </summary>
    public IReadOnlyDictionary<string, Guid?> ValueChoices { get; init; } = new Dictionary<string, Guid?>();

    /// <summary>B-13.7 CR-level value choices by <see cref="ExternalCrConflict.Key"/>.</summary>
    public IReadOnlyDictionary<string, string?> CrValueChoices { get; init; } = new Dictionary<string, string?>();

    /// <summary>B-13.6: for a task that refers to several CRs, the one CR number chosen (null for none) by <see cref="ExternalCrChoice.Key"/>.</summary>
    public IReadOnlyDictionary<string, string?> CrReferenceChoices { get; init; } = new Dictionary<string, string?>();

    /// <summary>B-13.7: the owner of CRs without a resolvable one; the wizard proposes the default CR Owner.</summary>
    public Guid? FallbackOwnerId { get; init; }

    /// <summary>
    /// B-13.7: a source person (by name) the import creates, used as the owner of CRs without one when
    /// <see cref="FallbackOwnerId"/> is null. The wizard offers it after people are mapped.
    /// </summary>
    public string? FallbackOwnerName { get; init; }
}

/// <summary>How one source lookup value is resolved (B-13.8).</summary>
/// <param name="ExistingId">The existing value of the same normalized name, if any.</param>
/// <param name="ChosenId">The value used; null when it is created as a new definition.</param>
public sealed record ExternalValueDecision(string Key, LookupKind Kind, string SourceName, Guid? ExistingId, Guid? ChosenId);

/// <summary>B-13.7: linked source tasks supply different values for one CR-level field; the user picks one.</summary>
public sealed record ExternalCrConflict(string Key, string CrNumber, string Field, IReadOnlyList<string> Candidates, string? Chosen);

/// <summary>B-13.6: a source task refers to several CRs; at most one may be linked, and the choice is permanent.</summary>
public sealed record ExternalCrChoice(string Key, string SourceKey, IReadOnlyList<string> Numbers, string? Chosen, bool IsChosen);

/// <summary>The mapped document of an external source plus the decisions the wizard shows (B-13.4–13.9).</summary>
public sealed record ExternalImportDraft(
    TransferDocument Document,
    IReadOnlyDictionary<Guid, (string EntityKind, string SourceKey)> SourceKeys,
    IReadOnlyList<ImportFinding> Findings,
    IReadOnlyList<ExternalValueDecision> Values,
    IReadOnlyList<ExternalCrConflict> CrConflicts,
    IReadOnlyList<ExternalCrChoice> CrChoices)
{
    /// <summary>B-13.7: new CRs whose source gives no owner, so they take the fallback owner.</summary>
    public int MissingOwnerCount { get; init; }
}

/// <summary>
/// Maps an external source into a transfer document with generated or ledger-supplied IDs (B-13). Pure; the result is
/// then previewed by <see cref="ImportPlanner"/> like any other import, so every route shares one rule set.
/// </summary>
/// <remarks>
/// <para>B-13.6: tasks that refer to one CR are all linked to it; a CR number found neither among the source CRs nor in the
/// dataset becomes a new CR whose values come from its tasks. A task referring to several CRs needs a choice; the
/// other references are preserved. B-13.7: missing CR-level values are proposed from linked tasks; differing values
/// need an explicit choice; a linked task's own title becomes its preserved pre-link title.</para>
/// <para>B-13.3/B-13.4: Markdown checkboxes become checklist items and the rest of the body becomes notes (or legacy
/// content when notes are mapped elsewhere); nothing absent from the source is invented.</para>
/// <para>ADR 0027: columns can map to Created, Checklist items, or a custom field; new custom fields join the
/// document's definitions, so the same apply creates them. An unmapped column is kept hidden or discarded.</para>
/// </remarks>
public static class ExternalImportPlanner
{
    private const string GeneralSquadName = "General";
    private const string GeneralNoSquadName = "General (no squad)";
    private static readonly char[] ListSeparators = [',', ';', '\n'];

    /// <summary>ADR 0027: a new Lookup field takes at most this many distinct values as options; beyond it Text fits better.</summary>
    public const int MaxNewLookupOptions = 200;

    public static string ValueKey(LookupKind kind, string normalizedName) => kind + "|" + normalizedName;

    /// <param name="existingCrs">Every CR of the dataset by normalized number.</param>
    /// <param name="ledger">The namespace's import ledger by (entity kind, source key).</param>
    public static ExternalImportDraft Build(
        ExternalSource source,
        ExternalImportOptions options,
        WorkflowSnapshot workflow,
        IReadOnlyDictionary<string, Guid> existingCrs,
        IReadOnlyDictionary<(string EntityKind, string SourceKey), Guid> ledger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(existingCrs);
        ArgumentNullException.ThrowIfNull(ledger);
        return new Builder(source, options, workflow, existingCrs, ledger).Build();
    }

    private sealed class CrDraft
    {
        public required Guid Id { get; init; }

        public required string Number { get; init; }

        public required string Key { get; init; }

        public bool IsFromSource { get; init; }

        public bool IsExisting { get; init; }

        public string? Title { get; set; }

        public string? TaskNumber { get; set; }

        public string? SquadName { get; set; }

        /// <summary>The CR's own source row maps a Squad column, so an empty cell means "General" (no squad).</summary>
        public bool HasSquadColumn { get; set; }

        public string? OwnerName { get; set; }

        public TransferChangeRequest? Record { get; set; }

        public HashSet<string> Supplied { get; } = new(StringComparer.Ordinal);

        public List<(string Field, string Value)> TaskValues { get; } = [];
    }

    /// <summary>A table's mapped columns: built-in fields, custom fields, and the columns kept out of preserved metadata.</summary>
    private sealed record TableColumns(Dictionary<ExternalField, int> Fields, IReadOnlyList<CustomColumn> Custom, IReadOnlySet<int> NotPreserved);

    /// <param name="Options">A Lookup field's options by normalized label.</param>
    private sealed record CustomColumn(int Index, Guid FieldId, FieldKind Kind, string Label, IReadOnlyDictionary<string, Guid> Options);

    /// <summary>A custom field the import creates (ADR 0027); the columns that map to it share its options.</summary>
    private sealed class NewField(Guid id, FieldTarget target, string key, LookupName label, FieldKind kind)
    {
        private readonly Dictionary<string, Guid> _options = new(StringComparer.Ordinal);
        private readonly List<TransferFieldOption> _ordered = [];

        public Guid Id { get; } = id;

        public FieldTarget Target { get; } = target;

        public string Key { get; } = key;

        public LookupName Label { get; } = label;

        public FieldKind Kind { get; } = kind;

        /// <summary>Option IDs by normalized label.</summary>
        public Dictionary<string, Guid> Options => _options;

        public void AddOption(LookupName label)
        {
            var optionId = Guid.CreateVersion7();
            _options[label.Normalized] = optionId;
            _ordered.Add(new TransferFieldOption(optionId, label.Display, _ordered.Count));
        }

        public TransferFieldDefinition ToTransfer() =>
            new(Id, Key, Target.ToString(), Label.Display, Kind.ToString(), IsRequired: false, DefaultValue: null, DisplayOrder: 0, [.. _ordered], IsActive: true);
    }

    private sealed class Builder(
        ExternalSource source,
        ExternalImportOptions options,
        WorkflowSnapshot workflow,
        IReadOnlyDictionary<string, Guid> existingCrs,
        IReadOnlyDictionary<(string EntityKind, string SourceKey), Guid> ledger)
    {
        private readonly List<ImportFinding> _findings = [];
        private readonly Dictionary<string, ExternalValueDecision> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, (string, string)> _sourceKeys = [];
        private readonly Dictionary<string, CrDraft> _crs = new(StringComparer.Ordinal);
        private readonly List<ExternalCrConflict> _conflicts = [];
        private readonly List<ExternalCrChoice> _choices = [];
        private readonly List<TransferTaskStatus> _newTaskStatuses = [];
        private readonly List<TransferCrStatus> _newCrStatuses = [];
        private readonly List<TransferOrderedLookup> _newPriorities = [];
        private readonly List<TransferOrderedLookup> _newSquads = [];
        private readonly List<TransferNamedLookup> _newPeople = [];
        private readonly List<TransferNamedLookup> _newTags = [];
        private readonly HashSet<(string, string)> _usedKeys = [];
        private int _missingOwners;
        private readonly List<NewField> _newFields = [];

        public ExternalImportDraft Build()
        {
            var tasks = new List<TransferTask>();
            var links = new List<TransferLink>();
            var reviews = new List<TransferReview>();
            var todos = new List<TransferGeneralTodo>();
            var mappings = options.Tables.ToDictionary(m => m.TableName, StringComparer.Ordinal);

            // CRs first, so task references resolve to the source CRs before the dataset's.
            foreach (var table in source.Tables)
            {
                if (Mapping(table, mappings, ExternalEntity.ChangeRequest) is { } columns)
                {
                    foreach (var row in table.Rows)
                    {
                        ReadChangeRequest(table, row, columns);
                    }
                }
            }

            foreach (var table in source.Tables)
            {
                if (Mapping(table, mappings, ExternalEntity.Task) is { } columns)
                {
                    foreach (var row in table.Rows)
                    {
                        if (ReadTask(table, row, columns, links) is { } task)
                        {
                            tasks.Add(task);
                        }
                    }
                }

                if (Mapping(table, mappings, ExternalEntity.Review) is { } reviewColumns)
                {
                    reviews.AddRange(table.Rows.Select(row => ReadReview(table, row, reviewColumns)).OfType<TransferReview>());
                }

                if (Mapping(table, mappings, ExternalEntity.GeneralTodo) is { } todoColumns)
                {
                    todos.AddRange(table.Rows.Select(row => ReadTodo(table, row, todoColumns)).OfType<TransferGeneralTodo>());
                }
            }

            foreach (var todo in source.GeneralTodos)
            {
                if (ChecklistText.Create(todo.Text).IsSuccess && RecordId(ImportEntity.GeneralTodo, todo.SourceKey, todo.SourceKey) is { } id)
                {
                    todos.Add(new TransferGeneralTodo { Id = id, Text = todo.Text.Trim(), IsCompleted = todo.IsDone, SuppliedFields = Members("Text", "IsCompleted") });
                }
            }

            var changeRequests = ResolveChangeRequests();
            var document = new TransferDocument
            {
                Scope = new TransferScope("external", null, options.SourceNamespace),
                Lookups = new TransferLookups
                {
                    TaskStatuses = _newTaskStatuses,
                    CrStatuses = _newCrStatuses,
                    Priorities = _newPriorities,
                    Squads = _newSquads,
                    People = _newPeople,
                    Tags = _newTags,
                },
                FieldDefinitions = [.. _newFields.Select(f => f.ToTransfer())],
                Tasks = tasks,
                ChangeRequests = changeRequests,
                Links = links,
                Reviews = reviews,
                GeneralTodos = todos,
            };
            return new ExternalImportDraft(document, _sourceKeys, _findings, [.. _values.Values], _conflicts, _choices) { MissingOwnerCount = _missingOwners };
        }

        // ----- Mapping -----
        private TableColumns? Mapping(ExternalTable table, Dictionary<string, ExternalTableMapping> mappings, ExternalEntity entity)
        {
            if (!mappings.TryGetValue(table.Name, out var mapping) || mapping.Entity != entity)
            {
                return null;
            }

            var columns = new Dictionary<ExternalField, int>();
            var custom = new List<CustomColumn>();
            var notPreserved = new HashSet<int>();
            var ambiguous = false;
            var target = ExternalFieldAliases.CustomFieldTarget(entity);
            for (var i = 0; i < mapping.ColumnFields.Count && i < table.Columns.Count; i++)
            {
                var field = mapping.ColumnFields[i];
                if (field == ExternalField.Ignore)
                {
                    if (mapping.DiscardedColumns.Contains(i))
                    {
                        // ADR 0027: a discarded column is stored nowhere.
                        notPreserved.Add(i);
                    }

                    continue;
                }

                if (field == ExternalField.CustomField)
                {
                    notPreserved.Add(i);
                    if (target is not { } fieldTarget || !mapping.CustomFields.TryGetValue(i, out var choice))
                    {
                        // Reviews and General to-dos have no custom fields, and a custom field column needs its field.
                        _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.File, table.Name, ImportFindingCodes.CustomFieldInvalid, null, table.Columns[i]));
                        continue;
                    }

                    if (CustomColumnFor(table, i, choice, fieldTarget) is not { } column)
                    {
                        continue;
                    }

                    if (custom.Any(c => c.FieldId == column.FieldId))
                    {
                        ambiguous = true;
                        _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.File, table.Name, ImportFindingCodes.AmbiguousColumn, column.Label, table.Columns[i]));
                        continue;
                    }

                    custom.Add(column);
                    continue;
                }

                if (!columns.TryAdd(field, i))
                {
                    // B-13.2: two columns for one field is ambiguous and needs review.
                    ambiguous = true;
                    _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.File, table.Name, ImportFindingCodes.AmbiguousColumn, field.ToString(), table.Columns[i]));
                }
            }

            if (ambiguous)
            {
                return null;
            }

            if (entity is ExternalEntity.Task or ExternalEntity.Review or ExternalEntity.GeneralTodo && !columns.ContainsKey(ExternalField.Title) && table.Kind != ExternalSourceKind.Markdown)
            {
                _findings.Add(new ImportFinding(ImportSeverity.Warning, ImportEntity.File, table.Name, ImportFindingCodes.MissingColumn, nameof(ExternalField.Title)));
            }

            for (var i = 0; i < table.Columns.Count; i++)
            {
                if (i >= mapping.ColumnFields.Count || mapping.ColumnFields[i] == ExternalField.Ignore)
                {
                    // ADR 0027: reviews and General to-dos have no preserved metadata, so their unmapped columns are never kept.
                    var code = entity is ExternalEntity.Review or ExternalEntity.GeneralTodo ? ImportFindingCodes.ColumnNotStored
                        : mapping.DiscardedColumns.Contains(i) ? ImportFindingCodes.ColumnDiscarded
                        : ImportFindingCodes.UnknownColumn;
                    _findings.Add(new ImportFinding(ImportSeverity.Info, ImportEntity.File, table.Name, code, null, table.Columns[i]));
                }
            }

            foreach (var index in columns.Values)
            {
                notPreserved.Add(index);
            }

            return new TableColumns(columns, custom, notPreserved);
        }

        /// <summary>
        /// ADR 0027: the custom field of a column: an existing active field of the target, or a new optional field (B-11).
        /// Columns of several tables that create a field with the same target and label share one new field.
        /// </summary>
        private CustomColumn? CustomColumnFor(ExternalTable table, int index, ExternalCustomFieldChoice choice, FieldTarget target)
        {
            if (choice.ExistingFieldId is { } existingId)
            {
                if (workflow.FieldsFor(target).FirstOrDefault(f => f.Id.Value == existingId && f.IsActive) is not { } existing)
                {
                    _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.File, table.Name, ImportFindingCodes.CustomFieldInvalid, null, table.Columns[index]));
                    return null;
                }

                return new CustomColumn(index, existingId, existing.Kind, existing.Label.Display,
                    existing.Options.ToDictionary(o => o.Label.Normalized, o => o.Id.Value, StringComparer.Ordinal));
            }

            var labelText = (string.IsNullOrWhiteSpace(choice.Label) ? table.Columns[index] : choice.Label).Trim();
            var label = LookupName.Create(labelText, "Label");
            if (!label.IsSuccess || !ExternalValues.CreatableKinds.Contains(choice.Kind))
            {
                _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.Field, table.Name, ImportFindingCodes.CustomFieldInvalid, null, labelText));
                return null;
            }

            // A new Lookup field gets one option per distinct value, in source order; an invalid value stays a record error.
            var values = new List<LookupName>();
            if (choice.Kind == FieldKind.Lookup)
            {
                foreach (var text in table.Rows.Select(r => index < r.Values.Count ? ExternalValues.TextOf(r.Values[index]) : null).OfType<string>())
                {
                    if (LookupName.Create(text, "Options") is { IsSuccess: true } option && values.All(v => v.Normalized != option.Value.Normalized))
                    {
                        values.Add(option.Value);
                    }
                }
            }

            var field = _newFields.FirstOrDefault(f => f.Target == target && f.Label.Normalized == label.Value.Normalized);
            if (field is not null && field.Kind != choice.Kind)
            {
                _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.Field, table.Name, ImportFindingCodes.FieldKindMismatch, "Kind", labelText));
                return null;
            }

            var added = values.Where(v => field is null || !field.Options.ContainsKey(v.Normalized)).ToList();
            var optionCount = (field?.Options.Count ?? 0) + added.Count;
            if (choice.Kind == FieldKind.Lookup && optionCount is 0 or > MaxNewLookupOptions)
            {
                _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.Field, table.Name, ImportFindingCodes.LookupFieldValues, null, labelText));
                return null;
            }

            if (field is null)
            {
                field = new NewField(Guid.CreateVersion7(), target, UniqueKey(target, labelText), label.Value, choice.Kind);
                _newFields.Add(field);
            }

            foreach (var value in added)
            {
                field.AddOption(value);
            }

            return new CustomColumn(index, field.Id, field.Kind, field.Label.Display, field.Options);
        }

        /// <summary>
        /// ADR 0027: the key proposed from the label, or a generated one (<c>field</c>, <c>field_2</c>, …) when that is
        /// empty, reserved, or taken. Keys are unique per target, deactivated fields included (B-11).
        /// </summary>
        private string UniqueKey(FieldTarget target, string label)
        {
            bool IsFree(string key) => FieldKey.Create(key).IsSuccess
                && !workflow.FieldsFor(target).Any(f => string.Equals(f.Key.Normalized, key, StringComparison.OrdinalIgnoreCase))
                && !_newFields.Any(f => f.Target == target && string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

            var proposed = FieldKey.Propose(label);
            if (IsFree(proposed))
            {
                return proposed;
            }

            for (var n = 1; ; n++)
            {
                var key = n == 1 ? "field" : "field_" + n.ToString(CultureInfo.InvariantCulture);
                if (IsFree(key))
                {
                    return key;
                }
            }
        }

        private static ExternalValue? Cell(ExternalRow row, Dictionary<ExternalField, int> columns, ExternalField field) =>
            columns.TryGetValue(field, out var index) && index < row.Values.Count ? row.Values[index] : null;

        private static string? Text(ExternalRow row, Dictionary<ExternalField, int> columns, ExternalField field) => ExternalValues.TextOf(Cell(row, columns, field));

        private static List<string> List(ExternalValue? value)
        {
            if (value is null)
            {
                return [];
            }

            var raw = value.Items ?? (value.Text is { } text ? text.Split(ListSeparators) : []);
            return [.. raw.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        private static HashSet<string> Members(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);

        private Guid? RecordId(string entity, string sourceKey, string displayKey)
        {
            if (!_usedKeys.Add((entity, sourceKey)))
            {
                _findings.Add(new ImportFinding(ImportSeverity.Error, entity, displayKey, ImportFindingCodes.DuplicateInSource));
                return null;
            }

            var id = ledger.TryGetValue((entity, sourceKey), out var known) ? known : Guid.CreateVersion7();
            _sourceKeys[id] = (entity, sourceKey);
            return id;
        }

        private static string SourceKey(ExternalRow row, Dictionary<ExternalField, int> columns) => Text(row, columns, ExternalField.SourceId) ?? row.SourceKey;

        private DateOnly? Date(ExternalRow row, Dictionary<ExternalField, int> columns, ExternalField field, string entity, string key, HashSet<string> supplied)
        {
            if (!columns.ContainsKey(field))
            {
                return null;
            }

            supplied.Add(MemberOf(field));
            if (Text(row, columns, field) is not { } text)
            {
                return null;
            }

            // T-10: only unambiguous ISO dates (spreadsheet date cells arrive converted); anything else is rejected.
            if (ExternalValues.TryParseDate(text, out var date))
            {
                return date;
            }

            _findings.Add(new ImportFinding(ImportSeverity.Error, entity, key, ImportFindingCodes.InvalidDate, field.ToString()));
            return null;
        }

        /// <summary>ADR 0027: the record's created time; an unreadable or future value is an error.</summary>
        private DateTimeOffset? Created(ExternalRow row, Dictionary<ExternalField, int> columns, string entity, string key)
        {
            if (Text(row, columns, ExternalField.Created) is not { } text)
            {
                return null;
            }

            if (!ExternalValues.TryParseCreated(text, options.TimeZone, out var created))
            {
                _findings.Add(new ImportFinding(ImportSeverity.Error, entity, key, ImportFindingCodes.InvalidCreated, nameof(ExternalField.Created), text));
                return null;
            }

            if (created > options.NowUtc)
            {
                _findings.Add(new ImportFinding(ImportSeverity.Error, entity, key, ImportFindingCodes.CreatedInFuture, nameof(ExternalField.Created), text));
                return null;
            }

            return created.ToUniversalTime();
        }

        /// <summary>ADR 0027: the updated time of a record whose created time the source supplies is the import time.</summary>
        private DateTimeOffset? UpdatedFor(DateTimeOffset? created) => created is null ? null : options.NowUtc.ToUniversalTime();

        /// <summary>
        /// ADR 0027: the custom values of a row. Each mapped field is supplied on its own, so a merge changes only these
        /// fields; an empty cell clears the field. A value invalid for the field's kind is an error, never coerced.
        /// </summary>
        private Dictionary<string, TransferValue>? CustomValues(ExternalRow row, TableColumns map, string entity, string key, HashSet<string> supplied)
        {
            if (map.Custom.Count == 0)
            {
                return null;
            }

            var values = new Dictionary<string, TransferValue>(StringComparer.Ordinal);
            foreach (var column in map.Custom)
            {
                supplied.Add(ImportPlanner.CustomValueMember(column.FieldId));
                var cell = column.Index < row.Values.Count ? row.Values[column.Index] : null;
                if (ExternalValues.TextOf(cell) is not { } text)
                {
                    continue;
                }

                if (CustomValue(column, cell!, text, entity, key) is { } value)
                {
                    values[column.FieldId.ToString("D")] = value;
                }
                else
                {
                    _findings.Add(new ImportFinding(ImportSeverity.Error, entity, key, ImportFindingCodes.InvalidCustomValue, column.Label, text));
                }
            }

            return values;
        }

        private TransferValue? CustomValue(CustomColumn column, ExternalValue cell, string text, string entity, string key)
        {
            var kind = column.Kind.ToString();
            switch (column.Kind)
            {
                case FieldKind.Text when !text.Contains('\n', StringComparison.Ordinal) && !text.Contains('\r', StringComparison.Ordinal):
                    return new TransferValue(kind, Text: text);
                case FieldKind.MultilineText:
                    return new TransferValue(kind, Text: LineBreaks.Normalize(text));
                case FieldKind.Number when ExternalValues.TryParseNumber(text, out var number):
                    return new TransferValue(kind, Number: number);
                case FieldKind.Date when ExternalValues.TryParseDate(text, out var date):
                    return new TransferValue(kind, Date: date);
                case FieldKind.Url when ExternalValues.TryParseUrl(text, out var url):
                    return new TransferValue(kind, Text: url.ToString());
                case FieldKind.Lookup when LookupName.Create(text, "Options") is { IsSuccess: true } name && column.Options.TryGetValue(name.Value.Normalized, out var option):
                    return new TransferValue(kind, Option: option);
                case FieldKind.People:
                    // People and Tags values resolve like the built-in people and tags (B-13.8): matched or proposed.
                    return new TransferValue(kind, Ids: Values(LookupKind.Person, cell, entity, key));
                case FieldKind.Tags:
                    return new TransferValue(kind, Ids: Values(LookupKind.Tag, cell, entity, key));
                default:
                    return null;
            }
        }

        private bool? Boolean(ExternalRow row, Dictionary<ExternalField, int> columns, ExternalField field, string entity, string key)
        {
            if (Text(row, columns, field) is not { } text)
            {
                return null;
            }

            // Documented boolean spellings (T-10); anything else is an error rather than a guess.
            switch (text.ToUpperInvariant())
            {
                case "TRUE" or "YES" or "Y" or "1" or "X":
                    return true;
                case "FALSE" or "NO" or "N" or "0":
                    return false;
                default:
                    _findings.Add(new ImportFinding(ImportSeverity.Error, entity, key, ImportFindingCodes.InvalidBoolean, field.ToString()));
                    return null;
            }
        }

        private static string MemberOf(ExternalField field) => field switch
        {
            ExternalField.Title => "Title",
            ExternalField.Service => "Service",
            ExternalField.Status => "StatusId",
            ExternalField.Priority => "PriorityId",
            ExternalField.Designers => "DesignerId",
            ExternalField.Developers => "DeveloperId",
            ExternalField.Tags => "TagIds",
            ExternalField.Notes => "Notes",
            ExternalField.StartDate => "StartDate",
            ExternalField.EndDate => "EndDate",
            ExternalField.Archived => "IsArchived",
            ExternalField.TaskNumber => "TaskNumber",
            ExternalField.Squad => "SquadId",
            ExternalField.Owner => "OwnerId",
            ExternalField.PlannedStart => "PlannedStart",
            ExternalField.PlannedEnd => "PlannedEnd",
            ExternalField.EmailSubject => "EmailSubject",
            ExternalField.SolutionDesignUrl => "SolutionDesignUrl",
            ExternalField.Developer => "DeveloperId",
            ExternalField.Done => "IsDone",
            ExternalField.CompletedOn => "CompletedOn",
            ExternalField.CrNumber => "Number",
            _ => field.ToString(),
        };

        /// <summary>
        /// The unmapped, non-empty values of a row as legacy JSON (T-05: unknown fields are preserved), except the columns
        /// the user discarded (ADR 0027).
        /// </summary>
        private static string? Legacy(ExternalTable table, ExternalRow row, TableColumns map, string? body)
        {
            var mapped = map.NotPreserved;
            var buffer = new ArrayBufferWriter<byte>();
            var written = false;
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                for (var i = 0; i < table.Columns.Count && i < row.Values.Count; i++)
                {
                    if (mapped.Contains(i) || row.Values[i] is not { IsEmpty: false } value)
                    {
                        continue;
                    }

                    written = true;
                    if (value.Items is { } items)
                    {
                        writer.WriteStartArray(table.Columns[i]);
                        foreach (var item in items)
                        {
                            writer.WriteStringValue(item);
                        }

                        writer.WriteEndArray();
                    }
                    else
                    {
                        writer.WriteString(table.Columns[i], value.Text);
                    }
                }

                if (body is not null)
                {
                    written = true;
                    writer.WriteString("body", body);
                }

                writer.WriteEndObject();
            }

            return written ? Encoding.UTF8.GetString(buffer.WrittenSpan) : null;
        }

        // ----- Lookup values (B-13.8) -----
        private Guid? Value(LookupKind kind, string? raw, string entity, string key)
        {
            if (raw is null || raw.Trim().Length == 0)
            {
                return null;
            }

            var name = LookupName.Create(raw);
            if (!name.IsSuccess)
            {
                _findings.Add(new ImportFinding(ImportSeverity.Error, entity, key, ImportFindingCodes.Invalid, kind.ToString(), name.Error!.Code));
                return null;
            }

            var valueKey = ValueKey(kind, name.Value.Normalized);
            if (_values.TryGetValue(valueKey, out var decided))
            {
                return decided.ChosenId ?? NewIdOf(kind, name.Value.Normalized);
            }

            var existing = WorkflowLookups.Entries(workflow, kind).FirstOrDefault(e => e.Name.Normalized == name.Value.Normalized)?.Id;
            var chosen = options.ValueChoices.TryGetValue(valueKey, out var choice) ? choice : existing;
            if (chosen is { } id && WorkflowLookups.Find(workflow, kind, id) is null)
            {
                chosen = null;
            }

            _values[valueKey] = new ExternalValueDecision(valueKey, kind, name.Value.Display, existing, chosen);
            if (chosen is { } resolved)
            {
                return resolved;
            }

            var newId = Guid.CreateVersion7();
            var order = WorkflowLookups.Entries(workflow, kind).Count + NewCount(kind);
            switch (kind)
            {
                case LookupKind.TaskStatus:
                    // Proposed statuses get no semantic flags; the user sets them in Settings (B-04: flags, never labels).
                    _newTaskStatuses.Add(new TransferTaskStatus(newId, name.Value.Display, order, false, false, false));
                    break;
                case LookupKind.CrStatus:
                    _newCrStatuses.Add(new TransferCrStatus(newId, name.Value.Display, order, false, false));
                    break;
                case LookupKind.Priority:
                    _newPriorities.Add(new TransferOrderedLookup(newId, name.Value.Display, order));
                    break;
                case LookupKind.Squad:
                    _newSquads.Add(new TransferOrderedLookup(newId, name.Value.Display, order));
                    break;
                case LookupKind.Person:
                    _newPeople.Add(new TransferNamedLookup(newId, name.Value.Display));
                    break;
                default:
                    _newTags.Add(new TransferNamedLookup(newId, name.Value.Display));
                    break;
            }

            return newId;
        }

        private int NewCount(LookupKind kind) => kind switch
        {
            LookupKind.TaskStatus => _newTaskStatuses.Count,
            LookupKind.CrStatus => _newCrStatuses.Count,
            LookupKind.Priority => _newPriorities.Count,
            LookupKind.Squad => _newSquads.Count,
            LookupKind.Person => _newPeople.Count,
            _ => _newTags.Count,
        };

        private Guid NewIdOf(LookupKind kind, string normalized) => kind switch
        {
            LookupKind.TaskStatus => _newTaskStatuses.First(s => Norm(s.Name) == normalized).Id,
            LookupKind.CrStatus => _newCrStatuses.First(s => Norm(s.Name) == normalized).Id,
            LookupKind.Priority => _newPriorities.First(s => Norm(s.Name) == normalized).Id,
            LookupKind.Squad => _newSquads.First(s => Norm(s.Name) == normalized).Id,
            LookupKind.Person => _newPeople.First(s => Norm(s.Name) == normalized).Id,
            _ => _newTags.First(s => Norm(s.Name) == normalized).Id,
        };

        private static string Norm(string name) => name.Trim().ToUpperInvariant();

        /// <summary>
        /// ADR 0024: a task has at most one Designer and one Developer. The first name that resolves is kept; a name before
        /// it that does not resolve keeps its own error. Names after it are not resolved (so no person is created for a
        /// dropped one) and are reported in a warning.
        /// </summary>
        private Guid? FirstPerson(ExternalValue? value, string field, string entity, string key)
        {
            var names = List(value);
            for (var i = 0; i < names.Count; i++)
            {
                if (Value(LookupKind.Person, names[i], entity, key) is not { } id)
                {
                    continue;
                }

                if (i + 1 < names.Count)
                {
                    _findings.Add(new ImportFinding(ImportSeverity.Warning, entity, key, ImportFindingCodes.PeopleDropped, field, string.Join(", ", names.Skip(i + 1))));
                }

                return id;
            }

            return null;
        }

        private List<Guid> Values(LookupKind kind, ExternalValue? value, string entity, string key) =>
            [.. List(value).Select(v => Value(kind, v, entity, key)).OfType<Guid>().Distinct()];

        // ----- Change Requests -----
        private void ReadChangeRequest(ExternalTable table, ExternalRow row, TableColumns map)
        {
            const string Entity = ImportEntity.ChangeRequest;
            var columns = map.Fields;
            var displayKey = table.Name + "#" + row.RowNumber.ToString(CultureInfo.InvariantCulture);
            var number = Text(row, columns, ExternalField.CrNumber);
            if (number is null)
            {
                _findings.Add(new ImportFinding(ImportSeverity.Error, Entity, displayKey, ImportFindingCodes.Invalid, "Number", "required"));
                return;
            }

            var normalized = Norm(number);
            if (_crs.ContainsKey(normalized))
            {
                // B-13.8: duplicate CR numbers in the source are reported, never merged silently.
                _findings.Add(new ImportFinding(ImportSeverity.Error, Entity, displayKey, ImportFindingCodes.DuplicateCrNumber, "Number", number));
                return;
            }

            var sourceKey = SourceKey(row, columns);

            // A CR number already in the dataset is the same CR (its unique key, B-03.2): the row updates or skips it.
            Guid? id = existingCrs.TryGetValue(normalized, out var existingId) && !ledger.ContainsKey((Entity, sourceKey))
                ? existingId
                : RecordId(Entity, sourceKey, displayKey);
            if (id is not { } recordId)
            {
                return;
            }

            if (existingCrs.ContainsKey(normalized))
            {
                _usedKeys.Add((Entity, sourceKey));
            }

            var draft = new CrDraft { Id = recordId, Number = number, Key = normalized, IsFromSource = true, IsExisting = existingCrs.ContainsKey(normalized) };
            draft.Supplied.Add("Number");
            var supplied = draft.Supplied;
            void Map(ExternalField field)
            {
                if (columns.ContainsKey(field))
                {
                    supplied.Add(MemberOf(field));
                }
            }

            foreach (var field in new[] { ExternalField.Status, ExternalField.Priority, ExternalField.EmailSubject, ExternalField.SolutionDesignUrl, ExternalField.Notes })
            {
                Map(field);
            }

            draft.Title = Text(row, columns, ExternalField.Title);
            draft.TaskNumber = Text(row, columns, ExternalField.TaskNumber);
            draft.SquadName = Text(row, columns, ExternalField.Squad);
            draft.HasSquadColumn |= columns.ContainsKey(ExternalField.Squad);
            draft.OwnerName = Text(row, columns, ExternalField.Owner);
            var legacy = Legacy(table, row, map, null);
            var created = Created(row, columns, Entity, displayKey);
            draft.Record = new TransferChangeRequest
            {
                Id = recordId,
                Number = number,
                StatusId = Value(LookupKind.CrStatus, Text(row, columns, ExternalField.Status), Entity, displayKey),
                PriorityId = Value(LookupKind.Priority, Text(row, columns, ExternalField.Priority), Entity, displayKey),
                PlannedStart = Date(row, columns, ExternalField.PlannedStart, Entity, displayKey, supplied),
                PlannedEnd = Date(row, columns, ExternalField.PlannedEnd, Entity, displayKey, supplied),
                EmailSubject = Text(row, columns, ExternalField.EmailSubject),
                SolutionDesignUrl = Text(row, columns, ExternalField.SolutionDesignUrl),
                Description = Text(row, columns, ExternalField.Notes) is { } description ? description : row.Body,
                CustomValues = CustomValues(row, map, Entity, displayKey, supplied),
                Preserved = legacy is null ? null : new TransferPreserved(null, null, legacy),
                CreatedAtUtc = created,
                UpdatedAtUtc = UpdatedFor(created),
            };
            if (draft.Record.Description is not null)
            {
                supplied.Add("Description");
            }

            if (legacy is not null)
            {
                supplied.Add("Preserved");
            }

            _crs[normalized] = draft;
        }

        private CrDraft? CrFor(string number)
        {
            var normalized = Norm(number);
            if (_crs.TryGetValue(normalized, out var draft))
            {
                return draft;
            }

            if (existingCrs.TryGetValue(normalized, out var existing))
            {
                draft = new CrDraft { Id = existing, Number = number, Key = normalized, IsExisting = true };
                _crs[normalized] = draft;
                return draft;
            }

            // B-13.6: a CR referred to only by tasks becomes a new CR whose values come from those tasks.
            if (RecordId(ImportEntity.ChangeRequest, "cr-number:" + normalized, number) is not { } id)
            {
                return null;
            }

            draft = new CrDraft { Id = id, Number = number, Key = normalized };
            draft.Supplied.Add("Number");
            _crs[normalized] = draft;
            return draft;
        }

        private List<TransferChangeRequest> ResolveChangeRequests()
        {
            var result = new List<TransferChangeRequest>();
            foreach (var cr in _crs.Values)
            {
                if (cr.IsExisting && !cr.IsFromSource)
                {
                    // The dataset's CR owns its values (T-13); linked task values were preserved on the tasks.
                    continue;
                }

                var record = cr.Record ?? new TransferChangeRequest { Id = cr.Id, Number = cr.Number };
                var title = Propose(cr, "Title", cr.Title);
                var taskNumber = Propose(cr, "TaskNumber", cr.TaskNumber);
                var squad = Propose(cr, "Squad", cr.SquadName);
                var isGeneralSquad = squad is null ? cr.HasSquadColumn : IsGeneralSquad(squad);
                if (isGeneralSquad)
                {
                    squad = null;
                }

                var owner = Propose(cr, "Owner", cr.OwnerName);
                var key = cr.Number;
                Guid? ownerId = owner is not null ? Value(LookupKind.Person, owner, ImportEntity.ChangeRequest, key) : null;
                if (ownerId is null && !cr.IsExisting)
                {
                    _missingOwners++;
                    if (FallbackOwner(key) is { } fallback)
                    {
                        ownerId = fallback;
                        _findings.Add(new ImportFinding(ImportSeverity.Warning, ImportEntity.ChangeRequest, key, ImportFindingCodes.DefaultUsed, "OwnerId"));
                    }
                    else
                    {
                        _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.ChangeRequest, key, ImportFindingCodes.OwnerRequired, "OwnerId", cr.Number));
                    }
                }

                if (title is null && !cr.IsExisting)
                {
                    _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.ChangeRequest, key, ImportFindingCodes.NoTitle, "Title", cr.Number));
                }

                var supplied = new HashSet<string>(cr.Supplied, StringComparer.Ordinal);
                if (title is not null)
                {
                    supplied.Add("Title");
                }

                if (taskNumber is not null)
                {
                    supplied.Add("TaskNumber");
                }

                if (squad is not null || isGeneralSquad)
                {
                    supplied.Add("SquadId");
                }

                if (ownerId is not null)
                {
                    supplied.Add("OwnerId");
                }

                result.Add(record with
                {
                    Title = title,
                    TaskNumber = taskNumber,
                    SquadId = squad is null ? null : Value(LookupKind.Squad, squad, ImportEntity.ChangeRequest, key),
                    OwnerId = ownerId,
                    SuppliedFields = supplied,
                });
            }

            return result;
        }

        // B-13.7: an existing person, or a source person the import creates (offered after people are mapped).
        private Guid? FallbackOwner(string key) =>
            options.FallbackOwnerId is { } id && workflow.FindPerson(new Domain.Identifiers.PersonId(id)) is not null ? id
            : options.FallbackOwnerName is { } name ? Value(LookupKind.Person, name, ImportEntity.ChangeRequest, key)
            : null;

        // ADR 0020: no squad displays as "General"; an exported "General" label or an empty cell is no squad, never a new squad.
        private static bool IsGeneralSquad(string name) =>
            name.Equals(GeneralSquadName, StringComparison.OrdinalIgnoreCase) || name.Equals(GeneralNoSquadName, StringComparison.OrdinalIgnoreCase);

        /// <summary>B-13.7: the CR's own value, else the single value its linked tasks agree on, else the user's choice.</summary>
        private string? Propose(CrDraft cr, string field, string? own)
        {
            if (own is not null)
            {
                return own;
            }

            var candidates = cr.TaskValues.Where(v => v.Field == field).Select(v => v.Value)
                .Distinct(field == "Title" ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                _findings.Add(new ImportFinding(ImportSeverity.Warning, ImportEntity.ChangeRequest, cr.Number, ImportFindingCodes.CrValueProposed, field, cr.Number));
                return candidates[0];
            }

            var key = cr.Key + "|" + field;
            var chosen = options.CrValueChoices.TryGetValue(key, out var choice) && choice is not null && candidates.Contains(choice) ? choice : null;
            _conflicts.Add(new ExternalCrConflict(key, cr.Number, field, candidates, chosen));
            if (chosen is null)
            {
                _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.ChangeRequest, cr.Number, ImportFindingCodes.ConflictingCrValues, field, cr.Number));
            }

            return chosen;
        }

        // ----- Tasks -----
        private TransferTask? ReadTask(ExternalTable table, ExternalRow row, TableColumns map, List<TransferLink> links)
        {
            const string Entity = ImportEntity.Task;
            var columns = map.Fields;
            var displayKey = table.Name + "#" + row.RowNumber.ToString(CultureInfo.InvariantCulture);
            var sourceKey = SourceKey(row, columns);
            if (RecordId(Entity, sourceKey, displayKey) is not { } id)
            {
                return null;
            }

            var supplied = columns.Keys.Where(f => f is not (ExternalField.SourceId or ExternalField.CrNumber or ExternalField.Squad or ExternalField.Owner or ExternalField.TaskNumber or ExternalField.Title or ExternalField.Created or ExternalField.ChecklistItems))
                .Select(MemberOf).ToHashSet(StringComparer.Ordinal);
            var title = Text(row, columns, ExternalField.Title);
            var references = List(Cell(row, columns, ExternalField.CrNumber)).Select(n => n.Trim()).DistinctBy(Norm).ToList();
            string? chosenNumber = null;
            var unselected = new List<string>();
            if (references.Count == 1)
            {
                chosenNumber = references[0];
            }
            else if (references.Count > 1)
            {
                var hasChoice = options.CrReferenceChoices.TryGetValue(sourceKey, out var choice);
                chosenNumber = hasChoice && choice is not null && references.Any(r => Norm(r) == Norm(choice)) ? references.First(r => Norm(r) == Norm(choice!)) : null;
                _choices.Add(new ExternalCrChoice(sourceKey, displayKey, references, chosenNumber, hasChoice));
                if (!hasChoice)
                {
                    _findings.Add(new ImportFinding(ImportSeverity.Error, Entity, displayKey, ImportFindingCodes.MultipleCrReferences, "CrNumber"));
                }

                // B-13.6: the references not chosen are kept in legacy metadata.
                unselected.AddRange(references.Where(r => chosenNumber is null || Norm(r) != Norm(chosenNumber)));
            }

            var taskNumber = Text(row, columns, ExternalField.TaskNumber);
            var squad = Text(row, columns, ExternalField.Squad);
            var owner = Text(row, columns, ExternalField.Owner);
            string? preLinkTitle = null;
            string? ownTitle = title;
            var legacyBody = (string?)null;
            var notes = Text(row, columns, ExternalField.Notes);
            if (row.Body is { } body)
            {
                if (notes is null)
                {
                    notes = body;
                    supplied.Add("Notes");
                }
                else
                {
                    legacyBody = body;
                    _findings.Add(new ImportFinding(ImportSeverity.Info, Entity, displayKey, ImportFindingCodes.PreservedText, "Notes"));
                }
            }

            if (chosenNumber is not null && CrFor(chosenNumber) is { } cr)
            {
                links.Add(new TransferLink(id, cr.Id, null));

                // B-13.7: a linked task's own title is kept as PreLinkTitle; its CR-level values are proposals for the CR.
                preLinkTitle = title;
                ownTitle = null;
                AddTaskValue(cr, "Title", title);
                AddTaskValue(cr, "TaskNumber", taskNumber);
                AddTaskValue(cr, "Squad", squad);
                AddTaskValue(cr, "Owner", owner);
            }
            else
            {
                supplied.Add("OwnTitle");
                if (title is null)
                {
                    _findings.Add(new ImportFinding(ImportSeverity.Error, Entity, displayKey, ImportFindingCodes.NoTitle, "Title"));
                }
            }

            var legacy = Legacy(table, row, map, legacyBody);
            var unlinkedValues = chosenNumber is null && (taskNumber ?? squad ?? owner) is not null
                ? MergeLegacy(legacy, ("taskNumber", taskNumber), ("squad", squad), ("owner", owner))
                : legacy;
            var preserved = preLinkTitle is not null || unselected.Count > 0 || unlinkedValues is not null
                ? new TransferPreserved(preLinkTitle, unselected, unlinkedValues)
                : null;
            if (preserved is not null)
            {
                supplied.Add("Preserved");
            }

            // ADR 0027: a mapped checklist column is a supplied collection: in a merge it replaces the checklist, and an
            // empty cell clears it (B-12). Otherwise a Markdown record's checkboxes are its checklist (B-13.3).
            var items = row.Checklist ?? [];
            var suppliesChecklist = items.Count > 0;
            if (columns.ContainsKey(ExternalField.ChecklistItems))
            {
                var cell = Cell(row, columns, ExternalField.ChecklistItems);
                if (cell is { HasNonTextItems: true })
                {
                    // A list with objects or nested lists is reported, never read as a shorter (or empty) checklist.
                    _findings.Add(new ImportFinding(ImportSeverity.Error, Entity, displayKey, ImportFindingCodes.InvalidChecklist, nameof(ExternalField.ChecklistItems)));
                    suppliesChecklist = false;
                }
                else if (items.Count > 0)
                {
                    // Two checklists for one record: neither is dropped silently.
                    _findings.Add(new ImportFinding(ImportSeverity.Error, Entity, displayKey, ImportFindingCodes.ChecklistConflict, nameof(ExternalField.ChecklistItems)));
                }
                else
                {
                    items = ExternalValues.ParseChecklist(cell);
                    suppliesChecklist = true;
                }
            }

            List<TransferChecklistItem>? checklist = null;
            if (suppliesChecklist)
            {
                checklist = [.. items.Select((item, index) => new TransferChecklistItem(DerivedId(id, index), item.Text.Trim(), item.IsDone, (index + 1) * 1024L))];
                supplied.Add("ChecklistItems");
            }

            var created = Created(row, columns, Entity, displayKey);
            return new TransferTask
            {
                Id = id,
                OwnTitle = ownTitle,
                Service = Text(row, columns, ExternalField.Service),
                StatusId = Value(LookupKind.TaskStatus, Text(row, columns, ExternalField.Status), Entity, displayKey),
                PriorityId = Value(LookupKind.Priority, Text(row, columns, ExternalField.Priority), Entity, displayKey),
                DesignerId = FirstPerson(Cell(row, columns, ExternalField.Designers), "DesignerId", Entity, displayKey),
                DeveloperId = FirstPerson(Cell(row, columns, ExternalField.Developers), "DeveloperId", Entity, displayKey),
                TagIds = Values(LookupKind.Tag, Cell(row, columns, ExternalField.Tags), Entity, displayKey),
                Notes = notes,
                StartDate = Date(row, columns, ExternalField.StartDate, Entity, displayKey, supplied),
                EndDate = Date(row, columns, ExternalField.EndDate, Entity, displayKey, supplied),
                IsArchived = Boolean(row, columns, ExternalField.Archived, Entity, displayKey),
                ChecklistItems = checklist,
                CustomValues = CustomValues(row, map, Entity, displayKey, supplied),
                Preserved = preserved,
                CreatedAtUtc = created,
                UpdatedAtUtc = UpdatedFor(created),
                SuppliedFields = supplied,
            };
        }

        private static void AddTaskValue(CrDraft cr, string field, string? value)
        {
            if (value is not null)
            {
                cr.TaskValues.Add((field, value));
            }
        }

        private static string? MergeLegacy(string? legacy, params (string Name, string? Value)[] values)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (legacy is not null)
                {
                    using var existing = JsonDocument.Parse(legacy);
                    foreach (var property in existing.RootElement.EnumerateObject())
                    {
                        property.WriteTo(writer);
                    }
                }

                foreach (var (name, value) in values)
                {
                    if (value is not null)
                    {
                        writer.WriteString(name, value);
                    }
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        /// <summary>
        /// A stable checklist item ID derived from its task and position, so importing the same source again matches
        /// the same items instead of replacing them with new identities.
        /// </summary>
        private static Guid DerivedId(Guid parent, int index)
        {
            Span<byte> input = stackalloc byte[20];
            parent.TryWriteBytes(input);
            BitConverter.TryWriteBytes(input[16..], index);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(input, hash);
            return new Guid(hash[..16]);
        }

        // ----- Reviews and General to-dos -----
        private TransferReview? ReadReview(ExternalTable table, ExternalRow row, TableColumns map)
        {
            const string Entity = ImportEntity.Review;
            var columns = map.Fields;
            var displayKey = table.Name + "#" + row.RowNumber.ToString(CultureInfo.InvariantCulture);
            if (RecordId(Entity, SourceKey(row, columns), displayKey) is not { } id)
            {
                return null;
            }

            var supplied = columns.Keys.Where(f => f is not (ExternalField.SourceId or ExternalField.Created)).Select(f => f switch
            {
                ExternalField.CrNumber => "CrNumberText",
                ExternalField.TaskNumber => "TaskNumberText",
                ExternalField.Service => "Services",
                ExternalField.Notes => "Comments",
                _ => MemberOf(f),
            }).ToHashSet(StringComparer.Ordinal);
            var done = Boolean(row, columns, ExternalField.Done, Entity, displayKey);
            var completedOn = Date(row, columns, ExternalField.CompletedOn, Entity, displayKey, supplied);
            var developer = Value(LookupKind.Person, Text(row, columns, ExternalField.Developer), Entity, displayKey);
            var created = Created(row, columns, Entity, displayKey);
            return new TransferReview
            {
                Id = id,
                Title = Text(row, columns, ExternalField.Title),
                CrNumberText = Text(row, columns, ExternalField.CrNumber),
                TaskNumberText = Text(row, columns, ExternalField.TaskNumber),
                Services = List(Cell(row, columns, ExternalField.Service)),
                DeveloperId = developer,
                Comments = Text(row, columns, ExternalField.Notes) ?? row.Body,
                IsDone = done ?? completedOn is not null,
                CompletedOn = completedOn,
                CreatedAtUtc = created,
                UpdatedAtUtc = UpdatedFor(created),
                SuppliedFields = supplied,
            };
        }

        private TransferGeneralTodo? ReadTodo(ExternalTable table, ExternalRow row, TableColumns map)
        {
            const string Entity = ImportEntity.GeneralTodo;
            var columns = map.Fields;
            var displayKey = table.Name + "#" + row.RowNumber.ToString(CultureInfo.InvariantCulture);
            if (RecordId(Entity, SourceKey(row, columns), displayKey) is not { } id)
            {
                return null;
            }

            var created = Created(row, columns, Entity, displayKey);
            return new TransferGeneralTodo
            {
                Id = id,
                Text = Text(row, columns, ExternalField.Title),
                IsCompleted = Boolean(row, columns, ExternalField.Done, Entity, displayKey),
                CreatedAtUtc = created,
                UpdatedAtUtc = UpdatedFor(created),
                SuppliedFields = columns.ContainsKey(ExternalField.Done) ? Members("Text", "IsCompleted") : Members("Text"),
            };
        }
    }
}
