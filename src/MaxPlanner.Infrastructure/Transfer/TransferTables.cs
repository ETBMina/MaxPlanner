using System.Globalization;
using MaxPlanner.Application.Transfer;

namespace MaxPlanner.Infrastructure.Transfer;

/// <summary>How a tabular cell is written: XLSX uses real dates, numbers, and booleans; CSV writes the same invariant text.</summary>
internal enum CellKind
{
    Text,

    /// <summary>A business date: an XLSX date cell, ISO <c>yyyy-MM-dd</c> text in CSV.</summary>
    Date,
    Number,
    Boolean,

    /// <summary>A UTC timestamp, always ISO 8601 text (B-12).</summary>
    Timestamp,

    /// <summary>A JSON-cell encoding of a collection or custom values (B-12).</summary>
    Json,
}

/// <param name="IsInformational">A readable, derived value (labels, CR-owned values of a linked task, T-13); ignored on import.</param>
internal sealed record TableColumn(string Name, CellKind Kind, bool IsInformational = false);

/// <summary>One table as read: header names and the rows' invariant text values, aligned with the headers.</summary>
internal sealed record RawTable(string Name, IReadOnlyList<string> Headers, IReadOnlyList<string?[]> Rows);

/// <summary>
/// The B-12 tabular layout shared by XLSX and CSV: one table per entity with internal IDs, explicit relationship IDs,
/// and informational label columns. Collections and custom values use a documented JSON-cell encoding; checklist items
/// and Subtask cards are one row per record with their <c>ParentTaskId</c>.
/// </summary>
internal static class TransferTables
{
    public const string MetadataSheet = "Metadata";
    public const string LookupsSheet = "Lookups";
    public const string FieldsSheet = "Fields";

    private const string Iso = "yyyy-MM-dd";

    public static IReadOnlyList<TransferTable> All { get; } =
        [TransferTable.Tasks, TransferTable.ChangeRequests, TransferTable.ChecklistItems, TransferTable.SubtaskCards, TransferTable.Reviews, TransferTable.GeneralTodos];

    /// <summary>The worksheet names of B-12, also the CSV file names (with <c>.csv</c>).</summary>
    public static string NameOf(TransferTable table) => table switch
    {
        TransferTable.Tasks => "Tasks",
        TransferTable.ChangeRequests => "Change Requests",
        TransferTable.ChecklistItems => "Checklist Items",
        TransferTable.SubtaskCards => "Subtask Cards",
        TransferTable.Reviews => "Reviews",
        _ => "General To-dos",
    };

    public static TransferTable? TableNamed(string name) =>
        All.Cast<TransferTable?>().FirstOrDefault(t => string.Equals(NameOf(t!.Value), name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<TableColumn> Columns(TransferTable table) => table switch
    {
        TransferTable.Tasks =>
        [
            new("Id", CellKind.Text), new("Title", CellKind.Text, true), new("OwnTitle", CellKind.Text), new("ChangeRequestId", CellKind.Text),
            new("CrNumber", CellKind.Text, true), new("TaskNumber", CellKind.Text, true), new("Squad", CellKind.Text, true), new("Owner", CellKind.Text, true),
            new("Service", CellKind.Text), new("StatusId", CellKind.Text), new("Status", CellKind.Text, true), new("PriorityId", CellKind.Text),
            new("Priority", CellKind.Text, true), new("DesignerIds", CellKind.Json), new("DeveloperIds", CellKind.Json), new("TagIds", CellKind.Json),
            new("Notes", CellKind.Text), new("StartDate", CellKind.Date), new("EndDate", CellKind.Date), new("IsArchived", CellKind.Boolean),
            new("Order", CellKind.Number), new("CustomValues", CellKind.Json), new("Preserved", CellKind.Json), new("CreatedAtUtc", CellKind.Timestamp),
            new("UpdatedAtUtc", CellKind.Timestamp), new("Revision", CellKind.Number),
        ],
        TransferTable.ChangeRequests =>
        [
            new("Id", CellKind.Text), new("Number", CellKind.Text), new("Title", CellKind.Text), new("OwnerId", CellKind.Text), new("Owner", CellKind.Text, true),
            new("SquadId", CellKind.Text), new("Squad", CellKind.Text, true), new("StatusId", CellKind.Text), new("Status", CellKind.Text, true),
            new("PriorityId", CellKind.Text), new("Priority", CellKind.Text, true), new("PlannedStart", CellKind.Date), new("PlannedEnd", CellKind.Date),
            new("EmailSubject", CellKind.Text), new("SolutionDesignUrl", CellKind.Text), new("Description", CellKind.Text), new("TaskNumber", CellKind.Text),
            new("CustomValues", CellKind.Json), new("Preserved", CellKind.Json), new("CreatedAtUtc", CellKind.Timestamp), new("UpdatedAtUtc", CellKind.Timestamp),
            new("Revision", CellKind.Number),
        ],
        TransferTable.ChecklistItems =>
        [
            new("ParentTaskId", CellKind.Text), new("Id", CellKind.Text), new("Text", CellKind.Text), new("IsCompleted", CellKind.Boolean), new("Order", CellKind.Number),
        ],
        TransferTable.SubtaskCards =>
        [
            new("Id", CellKind.Text), new("ParentTaskId", CellKind.Text), new("Subtitle", CellKind.Text), new("StatusId", CellKind.Text),
            new("Status", CellKind.Text, true), new("StartDate", CellKind.Date), new("EndDate", CellKind.Date), new("Notes", CellKind.Text),
            new("Order", CellKind.Number), new("CreatedAtUtc", CellKind.Timestamp), new("UpdatedAtUtc", CellKind.Timestamp), new("Revision", CellKind.Number),
        ],
        TransferTable.Reviews =>
        [
            new("Id", CellKind.Text), new("Title", CellKind.Text), new("CrNumberText", CellKind.Text), new("TaskNumberText", CellKind.Text),
            new("Services", CellKind.Json), new("DeveloperId", CellKind.Text), new("Developer", CellKind.Text, true), new("Comments", CellKind.Text),
            new("IsDone", CellKind.Boolean), new("CompletedOn", CellKind.Date), new("Order", CellKind.Number), new("CreatedAtUtc", CellKind.Timestamp),
            new("UpdatedAtUtc", CellKind.Timestamp), new("Revision", CellKind.Number),
        ],
        _ =>
        [
            new("Id", CellKind.Text), new("Text", CellKind.Text), new("IsCompleted", CellKind.Boolean), new("Order", CellKind.Number),
            new("CreatedAtUtc", CellKind.Timestamp), new("UpdatedAtUtc", CellKind.Timestamp), new("Revision", CellKind.Number),
        ],
    };

    public static IReadOnlyList<TableColumn> LookupColumns { get; } =
    [
        new("Kind", CellKind.Text), new("Id", CellKind.Text), new("Name", CellKind.Text), new("Order", CellKind.Number),
        new("IsCompleted", CellKind.Boolean), new("SetsStartDate", CellKind.Boolean), new("IsBacklog", CellKind.Boolean),
    ];

    public static IReadOnlyList<TableColumn> FieldColumns { get; } =
    [
        new("Id", CellKind.Text), new("Target", CellKind.Text), new("Key", CellKind.Text), new("Label", CellKind.Text), new("Kind", CellKind.Text),
        new("IsRequired", CellKind.Boolean), new("DefaultValue", CellKind.Json), new("DisplayOrder", CellKind.Number), new("Options", CellKind.Json),
        new("IsActive", CellKind.Boolean),
    ];

    public static IReadOnlyList<TableColumn> MetadataColumns { get; } = [new("Key", CellKind.Text), new("Value", CellKind.Text)];

    // ----- Writing -----
    public static IEnumerable<string?[]> Rows(TransferDocument doc, TransferTable table)
    {
        var labels = Labels(doc);
        string? Label(Guid? id) => id is { } value && labels.TryGetValue(value, out var name) ? name : null;
        var crs = doc.ChangeRequests.ToDictionary(c => c.Id);
        var links = doc.Links.ToDictionary(l => l.TaskId, l => l.ChangeRequestId);
        switch (table)
        {
            case TransferTable.Tasks:
                foreach (var t in doc.Tasks)
                {
                    var cr = links.TryGetValue(t.Id, out var crId) && crs.TryGetValue(crId, out var found) ? found : null;
                    yield return
                    [
                        G(t.Id), cr?.Title ?? t.OwnTitle, t.OwnTitle, links.TryGetValue(t.Id, out var linked) ? G(linked) : null, cr?.Number, cr?.TaskNumber,
                        cr is null ? null : Label(cr.SquadId) ?? "General", Label(cr?.OwnerId), t.Service, G(t.StatusId), Label(t.StatusId), G(t.PriorityId),
                        Label(t.PriorityId), TransferJson.SerializeList(t.DesignerIds ?? []), TransferJson.SerializeList(t.DeveloperIds ?? []),
                        TransferJson.SerializeList(t.TagIds ?? []), t.Notes, D(t.StartDate), D(t.EndDate), B(t.IsArchived), N(t.Order),
                        TransferJson.SerializeCustomValues(t.CustomValues), TransferJson.SerializePreserved(t.Preserved), Ts(t.CreatedAtUtc), Ts(t.UpdatedAtUtc), N(t.Revision),
                    ];
                }

                break;
            case TransferTable.ChangeRequests:
                foreach (var c in doc.ChangeRequests)
                {
                    yield return
                    [
                        G(c.Id), c.Number, c.Title, G(c.OwnerId), Label(c.OwnerId), G(c.SquadId), Label(c.SquadId) ?? "General", G(c.StatusId), Label(c.StatusId),
                        G(c.PriorityId), Label(c.PriorityId), D(c.PlannedStart), D(c.PlannedEnd), c.EmailSubject, c.SolutionDesignUrl, c.Description, c.TaskNumber,
                        TransferJson.SerializeCustomValues(c.CustomValues), TransferJson.SerializePreserved(c.Preserved), Ts(c.CreatedAtUtc), Ts(c.UpdatedAtUtc), N(c.Revision),
                    ];
                }

                break;
            case TransferTable.ChecklistItems:
                foreach (var t in doc.Tasks)
                {
                    foreach (var item in t.ChecklistItems ?? [])
                    {
                        yield return [G(t.Id), G(item.Id), item.Text, B(item.IsCompleted), N(item.Order)];
                    }
                }

                break;
            case TransferTable.SubtaskCards:
                foreach (var c in doc.SubtaskCards)
                {
                    yield return
                    [
                        G(c.Id), G(c.ParentTaskId), c.Subtitle, G(c.StatusId), Label(c.StatusId), D(c.StartDate), D(c.EndDate), c.Notes, N(c.Order),
                        Ts(c.CreatedAtUtc), Ts(c.UpdatedAtUtc), N(c.Revision),
                    ];
                }

                break;
            case TransferTable.Reviews:
                foreach (var r in doc.Reviews)
                {
                    yield return
                    [
                        G(r.Id), r.Title, r.CrNumberText, r.TaskNumberText, TransferJson.SerializeList(r.Services ?? []), G(r.DeveloperId), Label(r.DeveloperId),
                        r.Comments, B(r.IsDone), D(r.CompletedOn), N(r.Order), Ts(r.CreatedAtUtc), Ts(r.UpdatedAtUtc), N(r.Revision),
                    ];
                }

                break;
            default:
                foreach (var t in doc.GeneralTodos)
                {
                    yield return [G(t.Id), t.Text, B(t.IsCompleted), N(t.Order), Ts(t.CreatedAtUtc), Ts(t.UpdatedAtUtc), N(t.Revision)];
                }

                break;
        }
    }

    public static IEnumerable<string?[]> LookupRows(TransferDocument doc)
    {
        var l = doc.Lookups;
        foreach (var s in l.TaskStatuses)
        {
            yield return ["TaskStatus", G(s.Id), s.Name, N(s.Order), B(s.IsCompleted), B(s.SetsStartDate), B(s.IsBacklog)];
        }

        foreach (var s in l.CrStatuses)
        {
            yield return ["CrStatus", G(s.Id), s.Name, N(s.Order), B(s.IsCompleted), null, B(s.IsBacklog)];
        }

        foreach (var p in l.Priorities)
        {
            yield return ["Priority", G(p.Id), p.Name, N(p.Order), null, null, null];
        }

        foreach (var s in l.Squads)
        {
            yield return ["Squad", G(s.Id), s.Name, N(s.Order), null, null, null];
        }

        foreach (var p in l.People)
        {
            yield return ["Person", G(p.Id), p.Name, null, null, null, null];
        }

        foreach (var t in l.Tags)
        {
            yield return ["Tag", G(t.Id), t.Name, null, null, null, null];
        }
    }

    public static IEnumerable<string?[]> FieldRows(TransferDocument doc) =>
        doc.FieldDefinitions.Select(f => new string?[]
        {
            G(f.Id), f.Target, f.Key, f.Label, f.Kind, B(f.IsRequired), TransferJson.SerializeValue(f.DefaultValue), N(f.DisplayOrder),
            TransferJson.SerializeOptions(f.Options), B(f.IsActive),
        });

    public static IEnumerable<string?[]> MetadataRows(TransferDocument doc)
    {
        yield return ["format", doc.Format];
        yield return ["formatVersion", N(doc.FormatVersion)];
        yield return ["datasetId", G(doc.DatasetId)];
        yield return ["exportedAtUtc", Ts(doc.ExportedAtUtc)];
        yield return ["scope", doc.Scope?.Kind];
        yield return ["scopeEntityType", doc.Scope?.EntityType];
        yield return ["scopeDescription", doc.Scope?.Description];
        if (doc.Defaults is { } d)
        {
            yield return ["defaultTaskStatusId", G(d.DefaultTaskStatusId)];
            yield return ["defaultCrStatusId", G(d.DefaultCrStatusId)];
            yield return ["defaultPriorityId", G(d.DefaultPriorityId)];
            yield return ["defaultCrOwnerId", G(d.DefaultCrOwnerId)];
            yield return ["calendarSchedulingStatusId", G(d.CalendarSchedulingStatusId)];
            yield return ["crBacklogStatusId", G(d.CrBacklogStatusId)];
        }
    }

    private static Dictionary<Guid, string> Labels(TransferDocument doc)
    {
        var labels = new Dictionary<Guid, string>();
        var l = doc.Lookups;
        foreach (var (id, name) in l.TaskStatuses.Select(s => (s.Id, s.Name)).Concat(l.CrStatuses.Select(s => (s.Id, s.Name)))
                     .Concat(l.Priorities.Select(p => (p.Id, p.Name))).Concat(l.Squads.Select(s => (s.Id, s.Name)))
                     .Concat(l.People.Select(p => (p.Id, p.Name))).Concat(l.Tags.Select(t => (t.Id, t.Name))))
        {
            labels.TryAdd(id, name);
        }

        return labels;
    }

    private static string? G(Guid? id) => id?.ToString("D");

    private static string? D(DateOnly? date) => date?.ToString(Iso, CultureInfo.InvariantCulture);

    private static string? B(bool? value) => value is { } flag ? (flag ? "true" : "false") : null;

    private static string? N(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Ts(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    // ----- Reading -----

    /// <summary>
    /// Rebuilds a document from the tables of a tabular file. <paramref name="definitions"/> carries the format,
    /// version, lookups, fields, and defaults (the XLSX definition sheets or the CSV bundle's <c>metadata.json</c>).
    /// </summary>
    /// <exception cref="TransferFormatException">A required column is missing or a value cannot be parsed.</exception>
    public static TransferDocument Read(IReadOnlyDictionary<TransferTable, RawTable> tables, TransferDocument definitions)
    {
        var checklist = new Dictionary<Guid, List<TransferChecklistItem>>();
        if (tables.TryGetValue(TransferTable.ChecklistItems, out var itemsTable))
        {
            var r = new Reader(itemsTable);
            foreach (var row in itemsTable.Rows)
            {
                var parent = r.RequiredGuid(row, "ParentTaskId");
                (checklist.TryGetValue(parent, out var list) ? list : checklist[parent] = []).Add(new TransferChecklistItem(
                    r.RequiredGuid(row, "Id"), r.Text(row, "Text") ?? string.Empty, r.Bool(row, "IsCompleted") ?? false, r.Long(row, "Order") ?? 0));
            }
        }

        var tasks = new List<TransferTask>();
        var links = new List<TransferLink>();
        if (tables.TryGetValue(TransferTable.Tasks, out var tasksTable))
        {
            var r = new Reader(tasksTable);
            var supplied = r.Supplied(Columns(TransferTable.Tasks));
            if (itemsTable is not null)
            {
                supplied.Add(nameof(TransferTask.ChecklistItems));
            }

            foreach (var row in tasksTable.Rows)
            {
                var id = r.RequiredGuid(row, "Id");
                if (r.Guid(row, "ChangeRequestId") is { } cr)
                {
                    links.Add(new TransferLink(id, cr, null));
                }

                tasks.Add(new TransferTask
                {
                    Id = id,
                    OwnTitle = r.Text(row, "OwnTitle"),
                    Service = r.Text(row, "Service"),
                    StatusId = r.Guid(row, "StatusId"),
                    PriorityId = r.Guid(row, "PriorityId"),
                    DesignerIds = r.Json(row, "DesignerIds", TransferJsonContext.Default.ListGuid),
                    DeveloperIds = r.Json(row, "DeveloperIds", TransferJsonContext.Default.ListGuid),
                    TagIds = r.Json(row, "TagIds", TransferJsonContext.Default.ListGuid),
                    Notes = r.Text(row, "Notes"),
                    StartDate = r.Date(row, "StartDate"),
                    EndDate = r.Date(row, "EndDate"),
                    IsArchived = r.Bool(row, "IsArchived"),
                    Order = r.Long(row, "Order"),
                    ChecklistItems = itemsTable is null ? null : checklist.GetValueOrDefault(id) ?? [],
                    CustomValues = r.Json(row, "CustomValues", TransferJsonContext.Default.DictionaryStringTransferValue),
                    Preserved = r.Json(row, "Preserved", TransferJsonContext.Default.TransferPreserved),
                    CreatedAtUtc = r.Timestamp(row, "CreatedAtUtc"),
                    UpdatedAtUtc = r.Timestamp(row, "UpdatedAtUtc"),
                    Revision = r.Long(row, "Revision"),
                    SuppliedFields = supplied,
                });
            }
        }
        else if (itemsTable is not null)
        {
            // A Checklist Items table alone replaces the checklists of tasks that already exist (merge), never invents them.
            tasks.AddRange(checklist.Select(pair => new TransferTask
            {
                Id = pair.Key,
                ChecklistItems = pair.Value,
                SuppliedFields = new HashSet<string>(StringComparer.Ordinal) { nameof(TransferTask.ChecklistItems) },
            }));
        }

        return definitions with
        {
            Tasks = tasks,
            Links = links,
            ChangeRequests = ReadChangeRequests(tables),
            SubtaskCards = ReadCards(tables),
            Reviews = ReadReviews(tables),
            GeneralTodos = ReadTodos(tables),
        };
    }

    private static List<TransferChangeRequest> ReadChangeRequests(IReadOnlyDictionary<TransferTable, RawTable> tables)
    {
        if (!tables.TryGetValue(TransferTable.ChangeRequests, out var table))
        {
            return [];
        }

        var r = new Reader(table);
        var supplied = r.Supplied(Columns(TransferTable.ChangeRequests));
        return [.. table.Rows.Select(row => new TransferChangeRequest
        {
            Id = r.RequiredGuid(row, "Id"),
            Number = r.Text(row, "Number"),
            Title = r.Text(row, "Title"),
            OwnerId = r.Guid(row, "OwnerId"),
            SquadId = r.Guid(row, "SquadId"),
            StatusId = r.Guid(row, "StatusId"),
            PriorityId = r.Guid(row, "PriorityId"),
            PlannedStart = r.Date(row, "PlannedStart"),
            PlannedEnd = r.Date(row, "PlannedEnd"),
            EmailSubject = r.Text(row, "EmailSubject"),
            SolutionDesignUrl = r.Text(row, "SolutionDesignUrl"),
            Description = r.Text(row, "Description"),
            TaskNumber = r.Text(row, "TaskNumber"),
            CustomValues = r.Json(row, "CustomValues", TransferJsonContext.Default.DictionaryStringTransferValue),
            Preserved = r.Json(row, "Preserved", TransferJsonContext.Default.TransferPreserved),
            CreatedAtUtc = r.Timestamp(row, "CreatedAtUtc"),
            UpdatedAtUtc = r.Timestamp(row, "UpdatedAtUtc"),
            Revision = r.Long(row, "Revision"),
            SuppliedFields = supplied,
        })];
    }

    private static List<TransferSubtaskCard> ReadCards(IReadOnlyDictionary<TransferTable, RawTable> tables)
    {
        if (!tables.TryGetValue(TransferTable.SubtaskCards, out var table))
        {
            return [];
        }

        var r = new Reader(table);
        var supplied = r.Supplied(Columns(TransferTable.SubtaskCards));
        return [.. table.Rows.Select(row => new TransferSubtaskCard
        {
            Id = r.RequiredGuid(row, "Id"),
            ParentTaskId = r.RequiredGuid(row, "ParentTaskId"),
            Subtitle = r.Text(row, "Subtitle"),
            StatusId = r.Guid(row, "StatusId"),
            StartDate = r.Date(row, "StartDate"),
            EndDate = r.Date(row, "EndDate"),
            Notes = r.Text(row, "Notes"),
            Order = r.Long(row, "Order"),
            CreatedAtUtc = r.Timestamp(row, "CreatedAtUtc"),
            UpdatedAtUtc = r.Timestamp(row, "UpdatedAtUtc"),
            Revision = r.Long(row, "Revision"),
            SuppliedFields = supplied,
        })];
    }

    private static List<TransferReview> ReadReviews(IReadOnlyDictionary<TransferTable, RawTable> tables)
    {
        if (!tables.TryGetValue(TransferTable.Reviews, out var table))
        {
            return [];
        }

        var r = new Reader(table);
        var supplied = r.Supplied(Columns(TransferTable.Reviews));
        return [.. table.Rows.Select(row => new TransferReview
        {
            Id = r.RequiredGuid(row, "Id"),
            Title = r.Text(row, "Title"),
            CrNumberText = r.Text(row, "CrNumberText"),
            TaskNumberText = r.Text(row, "TaskNumberText"),
            Services = r.Json(row, "Services", TransferJsonContext.Default.ListString),
            DeveloperId = r.Guid(row, "DeveloperId"),
            Comments = r.Text(row, "Comments"),
            IsDone = r.Bool(row, "IsDone"),
            CompletedOn = r.Date(row, "CompletedOn"),
            Order = r.Long(row, "Order"),
            CreatedAtUtc = r.Timestamp(row, "CreatedAtUtc"),
            UpdatedAtUtc = r.Timestamp(row, "UpdatedAtUtc"),
            Revision = r.Long(row, "Revision"),
            SuppliedFields = supplied,
        })];
    }

    private static List<TransferGeneralTodo> ReadTodos(IReadOnlyDictionary<TransferTable, RawTable> tables)
    {
        if (!tables.TryGetValue(TransferTable.GeneralTodos, out var table))
        {
            return [];
        }

        var r = new Reader(table);
        var supplied = r.Supplied(Columns(TransferTable.GeneralTodos));
        return [.. table.Rows.Select(row => new TransferGeneralTodo
        {
            Id = r.RequiredGuid(row, "Id"),
            Text = r.Text(row, "Text"),
            IsCompleted = r.Bool(row, "IsCompleted"),
            Order = r.Long(row, "Order"),
            CreatedAtUtc = r.Timestamp(row, "CreatedAtUtc"),
            UpdatedAtUtc = r.Timestamp(row, "UpdatedAtUtc"),
            Revision = r.Long(row, "Revision"),
            SuppliedFields = supplied,
        })];
    }

    /// <summary>Lookups, fields, and defaults from the XLSX definition sheets.</summary>
    public static TransferDocument ReadDefinitions(RawTable? metadata, RawTable? lookups, RawTable? fields)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (metadata is not null)
        {
            var m = new Reader(metadata);
            foreach (var row in metadata.Rows)
            {
                if (m.Text(row, "Key") is { } key)
                {
                    values[key] = m.Text(row, "Value");
                }
            }
        }

        if (values.GetValueOrDefault("format") != TransferFormat.Marker)
        {
            throw new TransferFormatException(TransferFailure.Malformed, MetadataSheet);
        }

        if (!int.TryParse(values.GetValueOrDefault("formatVersion"), NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw new TransferFormatException(TransferFailure.Malformed, MetadataSheet + "!formatVersion");
        }

        if (version > TransferFormat.CurrentVersion)
        {
            throw new TransferFormatException(TransferFailure.UnsupportedVersion, version.ToString(CultureInfo.InvariantCulture));
        }

        var taskStatuses = new List<TransferTaskStatus>();
        var crStatuses = new List<TransferCrStatus>();
        var priorities = new List<TransferOrderedLookup>();
        var squads = new List<TransferOrderedLookup>();
        var people = new List<TransferNamedLookup>();
        var tags = new List<TransferNamedLookup>();
        if (lookups is not null)
        {
            var r = new Reader(lookups);
            foreach (var row in lookups.Rows)
            {
                var id = r.RequiredGuid(row, "Id");
                var name = r.Text(row, "Name") ?? string.Empty;
                var order = (int)(r.Long(row, "Order") ?? 0);
                switch (r.Text(row, "Kind"))
                {
                    case "TaskStatus":
                        taskStatuses.Add(new TransferTaskStatus(id, name, order, r.Bool(row, "IsCompleted") ?? false, r.Bool(row, "SetsStartDate") ?? false, r.Bool(row, "IsBacklog") ?? false));
                        break;
                    case "CrStatus":
                        crStatuses.Add(new TransferCrStatus(id, name, order, r.Bool(row, "IsCompleted") ?? false, r.Bool(row, "IsBacklog") ?? false));
                        break;
                    case "Priority":
                        priorities.Add(new TransferOrderedLookup(id, name, order));
                        break;
                    case "Squad":
                        squads.Add(new TransferOrderedLookup(id, name, order));
                        break;
                    case "Person":
                        people.Add(new TransferNamedLookup(id, name));
                        break;
                    case "Tag":
                        tags.Add(new TransferNamedLookup(id, name));
                        break;
                    default:
                        throw new TransferFormatException(TransferFailure.Malformed, LookupsSheet + "!Kind");
                }
            }
        }

        var definitions = new List<TransferFieldDefinition>();
        if (fields is not null)
        {
            var r = new Reader(fields);
            definitions.AddRange(fields.Rows.Select(row => new TransferFieldDefinition(
                r.RequiredGuid(row, "Id"), r.Text(row, "Key") ?? string.Empty, r.Text(row, "Target") ?? string.Empty, r.Text(row, "Label") ?? string.Empty,
                r.Text(row, "Kind") ?? string.Empty, r.Bool(row, "IsRequired") ?? false, r.Json(row, "DefaultValue", TransferJsonContext.Default.TransferValue),
                (int)(r.Long(row, "DisplayOrder") ?? 0), r.Json(row, "Options", TransferJsonContext.Default.ListTransferFieldOption) ?? [], r.Bool(row, "IsActive") ?? true)));
        }

        TransferDefaults? defaults = null;
        if (Parse(values, "defaultTaskStatusId") is { } taskStatus && Parse(values, "defaultCrStatusId") is { } crStatus
            && Parse(values, "defaultPriorityId") is { } priority && Parse(values, "calendarSchedulingStatusId") is { } scheduling
            && Parse(values, "crBacklogStatusId") is { } backlog)
        {
            defaults = new TransferDefaults(taskStatus, crStatus, priority, Parse(values, "defaultCrOwnerId"), scheduling, backlog);
        }

        return new TransferDocument
        {
            FormatVersion = version,
            DatasetId = Parse(values, "datasetId"),
            ExportedAtUtc = DateTimeOffset.TryParse(values.GetValueOrDefault("exportedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exported) ? exported : null,
            Scope = values.GetValueOrDefault("scope") is { } scope ? new TransferScope(scope, values.GetValueOrDefault("scopeEntityType"), values.GetValueOrDefault("scopeDescription")) : null,
            Lookups = new TransferLookups { TaskStatuses = taskStatuses, CrStatuses = crStatuses, Priorities = priorities, Squads = squads, People = people, Tags = tags },
            FieldDefinitions = definitions,
            Defaults = defaults,
        };
    }

    private static Guid? Parse(Dictionary<string, string?> values, string key) =>
        Guid.TryParse(values.GetValueOrDefault(key), out var id) ? id : null;

    /// <summary>Typed access to one raw table's cells with locations in every error.</summary>
    private sealed class Reader(RawTable table)
    {
        private readonly Dictionary<string, int> _index = table.Headers
            .Select((name, i) => (name: name.Trim(), i))
            .GroupBy(h => h.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);

        /// <summary>T-10 merge: the authoritative members this table supplies (informational columns never count).</summary>
        public HashSet<string> Supplied(IReadOnlyList<TableColumn> columns) =>
            columns.Where(c => !c.IsInformational && _index.ContainsKey(c.Name)).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        public string? Text(string?[] row, string column) => _index.TryGetValue(column, out var i) && i < row.Length && !string.IsNullOrEmpty(row[i]) ? row[i] : null;

        public Guid RequiredGuid(string?[] row, string column) =>
            Guid(row, column) ?? throw new TransferFormatException(TransferFailure.Malformed, Where(column));

        public Guid? Guid(string?[] row, string column) =>
            Text(row, column) is { } text ? System.Guid.TryParse(text, out var id) ? id : throw new TransferFormatException(TransferFailure.Malformed, Where(column)) : null;

        public DateOnly? Date(string?[] row, string column) =>
            Text(row, column) is { } text
                ? DateOnly.TryParseExact(text, Iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw new TransferFormatException(TransferFailure.Malformed, Where(column))
                : null;

        public DateTimeOffset? Timestamp(string?[] row, string column) =>
            Text(row, column) is { } text
                ? DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.ToUniversalTime() : throw new TransferFormatException(TransferFailure.Malformed, Where(column))
                : null;

        public long? Long(string?[] row, string column) =>
            Text(row, column) is { } text
                ? long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == decimal.Truncate(number)
                        ? (long)number
                        : throw new TransferFormatException(TransferFailure.Malformed, Where(column))
                : null;

        /// <summary>The documented boolean spellings only (T-10): true/false, plus 1/0 from XLSX boolean cells.</summary>
        public bool? Bool(string?[] row, string column) =>
            Text(row, column)?.Trim().ToUpperInvariant() switch
            {
                null => null,
                "TRUE" or "1" => true,
                "FALSE" or "0" => false,
                _ => throw new TransferFormatException(TransferFailure.Malformed, Where(column)),
            };

        public T? Json<T>(string?[] row, string column, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
            where T : class =>
            TransferJson.ParseCell(Text(row, column), type, Where(column));

        private string Where(string column) => $"{table.Name}!{column}";
    }
}
