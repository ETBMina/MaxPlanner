using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
public static partial class ExternalImportPlanner
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

    private sealed partial class Builder(
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
    }
}
