using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;

namespace MaxPlanner.Application.Transfer;

/// <summary>B-12: an export of the whole dataset or of the current view's results.</summary>
public abstract record TransferExportScope;

/// <summary>Everything, archived tasks and every dependency included (B-12).</summary>
public sealed record EntireDatasetScope : TransferExportScope
{
    public static EntireDatasetScope Instance { get; } = new();
}

/// <summary>
/// The records a view showed (B-12 "Current view results"). The CRs of exported tasks, the parents of exported
/// Subtask cards, and every definition are included so the file stays resolvable; it is still not a backup.
/// </summary>
/// <param name="EntityType">The view's entity type, for example <c>tasks</c>.</param>
/// <param name="Description">The view's filters as shown to the user.</param>
public sealed record ViewExportScope(string EntityType, string Description, IReadOnlySet<Guid> TaskIds, IReadOnlySet<Guid> SubtaskCardIds) : TransferExportScope;

/// <summary>Limitations an export report states (B-12, T-10); Presentation shows <c>Export_Limitation_{Code}</c>.</summary>
public static class ExportLimitationCodes
{
    public const string ViewIsNotBackup = "view-not-backup";
    public const string ParentsIncluded = "parents-included";
    public const string CsvFormulaNeutralized = "csv-formula-neutralized";
    public const string CsvSingleTable = "csv-single-table";
    public const string SpreadsheetNotBackup = "spreadsheet-not-backup";
}

/// <param name="Counts">Exported records by <see cref="ImportEntity"/> name.</param>
public sealed record ExportReport(string Path, TransferFileFormat Format, bool IsViewExport, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<string> Limitations);

/// <summary>
/// Exports a read snapshot through <see cref="ITransferFileWriter"/> (B-12, T-10). Reads run on a pooled reader,
/// concurrently with edits; the export reflects one consistent commit.
/// </summary>
public sealed class ExportTransfer
{
    private readonly IReadSnapshotSource _snapshots;
    private readonly ITransferFileWriter _writer;
    private readonly Guid _datasetId;
    private readonly TimeProvider _time;

    public ExportTransfer(IReadSnapshotSource snapshots, ITransferFileWriter writer, Guid datasetId, TimeProvider time)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _datasetId = datasetId;
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <param name="table">The table of a single-CSV export.</param>
    /// <returns>The report, or the snapshot/writer failure. A failure leaves an existing destination file unchanged.</returns>
    public async Task<AppResult<ExportReport>> ExportAsync(TransferExportScope scope, TransferFileFormat format, TransferTable? table, string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var snapshot = await _snapshots.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsSuccess)
        {
            return AppResult.Failure<ExportReport>(snapshot.Error!);
        }

        var document = TransferDocumentBuilder.Build(snapshot.Value, _datasetId, _time.GetUtcNow(), scope);
        var written = await _writer.WriteAsync(document, format, table, destinationPath, cancellationToken).ConfigureAwait(false);
        if (!written.IsSuccess)
        {
            return AppResult.Failure<ExportReport>(written.Error!);
        }

        var counts = new Dictionary<string, int>
        {
            [ImportEntity.Task] = document.Tasks.Count,
            [ImportEntity.SubtaskCard] = document.SubtaskCards.Count,
            [ImportEntity.ChangeRequest] = document.ChangeRequests.Count,
            [ImportEntity.Link] = document.Links.Count,
            [ImportEntity.Review] = document.Reviews.Count,
            [ImportEntity.GeneralTodo] = document.GeneralTodos.Count,
        };
        if (format == TransferFileFormat.CsvTable && table is { } single)
        {
            counts = counts.Where(c => c.Key == EntityOf(single)).ToDictionary();
        }

        var limitations = new List<string>();
        if (scope is ViewExportScope)
        {
            limitations.Add(ExportLimitationCodes.ViewIsNotBackup);
            limitations.Add(ExportLimitationCodes.ParentsIncluded);
        }

        if (format is TransferFileFormat.CsvTable or TransferFileFormat.CsvBundle)
        {
            limitations.Add(ExportLimitationCodes.CsvFormulaNeutralized);
        }

        if (format == TransferFileFormat.CsvTable)
        {
            limitations.Add(ExportLimitationCodes.CsvSingleTable);
        }

        if (format != TransferFileFormat.Json)
        {
            limitations.Add(ExportLimitationCodes.SpreadsheetNotBackup);
        }

        return AppResult.Success(new ExportReport(written.Value, format, scope is ViewExportScope, counts, limitations));
    }

    private static string EntityOf(TransferTable table) => table switch
    {
        TransferTable.Tasks or TransferTable.ChecklistItems => ImportEntity.Task,
        TransferTable.ChangeRequests => ImportEntity.ChangeRequest,
        TransferTable.SubtaskCards => ImportEntity.SubtaskCard,
        TransferTable.Reviews => ImportEntity.Review,
        _ => ImportEntity.GeneralTodo,
    };
}

/// <summary>
/// Previews and applies imports (T-10, B-12, B-13). A preview reads one snapshot and writes nothing; Apply commits the
/// reviewed plan as one transaction with a stable operation ID and publishes <c>RequiresFullReload</c> (ADR 0010).
/// </summary>
public sealed class ImportTransfer
{
    private readonly IMutationCoordinator _coordinator;
    private readonly IReadSnapshotSource _snapshots;
    private readonly IImportStateReader _state;
    private readonly TimeProvider _time;

    public ImportTransfer(IMutationCoordinator coordinator, IReadSnapshotSource snapshots, IImportStateReader state, TimeProvider time)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Validates <paramref name="document"/> against the current data and returns the full report (T-10).</summary>
    /// <param name="sourceNamespace">For external imports: the B-13.5 namespace whose ledger rows the plan adds.</param>
    /// <param name="sourceKeys">For external imports: the source identity of each document record ID.</param>
    public async Task<AppResult<ImportPlan>> PreviewAsync(
        TransferDocument document,
        ImportMode mode,
        CancellationToken cancellationToken,
        string? sourceNamespace = null,
        IReadOnlyDictionary<Guid, (string EntityKind, string SourceKey)>? sourceKeys = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var snapshot = await _snapshots.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsSuccess)
        {
            return AppResult.Failure<ImportPlan>(snapshot.Error!);
        }

        var ids = DocumentIds(document);
        var deleted = await _state.FindDeletedAsync(ids, cancellationToken).ConfigureAwait(false);
        if (!deleted.IsSuccess)
        {
            return AppResult.Failure<ImportPlan>(deleted.Error!);
        }

        var context = new ImportContext(deleted.Value, sourceNamespace, sourceKeys);
        var now = _time.GetUtcNow();

        // Planning is CPU work over up to 50,000 records; keep it off the caller's (UI) thread (T-10).
        var plan = await Task.Run(() => ImportPlanner.Plan(document, snapshot.Value, mode, context, now), cancellationToken).ConfigureAwait(false);
        return AppResult.Success(plan);
    }

    /// <summary>
    /// Commits <paramref name="plan"/> all-or-nothing (B-12, AC-13). Everything the preview relied on is checked again
    /// inside the transaction; any difference returns <see cref="ImportPlanStaleError"/> with nothing written.
    /// </summary>
    /// <returns>The number of records written, or a typed failure. Reuse <paramref name="operationId"/> only to retry the same plan.</returns>
    public Task<AppResult<CommandOutcome<long>>> ApplyAsync(ImportPlan plan, OperationId operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
        {
            return Task.FromResult(AppResult.Failure<CommandOutcome<long>>(ImportNotApplicableError.Instance));
        }

        return _coordinator.ExecuteAsync(operationId, "transfer.import", tx => Apply(tx, plan), cancellationToken);
    }

    private static List<Guid> DocumentIds(TransferDocument document) =>
    [
        .. document.Tasks.Select(t => t.Id),
        .. document.SubtaskCards.Select(c => c.Id),
        .. document.ChangeRequests.Select(c => c.Id),
        .. document.Reviews.Select(r => r.Id),
        .. document.GeneralTodos.Select(t => t.Id),
    ];

    // T-06.2: synchronous from BEGIN IMMEDIATE to commit; every check reads inside the same transaction.
    private static TxResult<long> Apply(IWriteTransaction tx, ImportPlan plan)
    {
        if (ImportPlanner.WorkflowStamp(tx.Workflow.Read()) != plan.WorkflowStamp)
        {
            return TxResult.Abort<long>(new ImportPlanStaleError("workflow"));
        }

        foreach (var planned in plan.ChangeRequests)
        {
            var current = tx.ChangeRequests.Find(planned.Record.Id);
            var holder = tx.ChangeRequests.FindByNormalizedNumber(planned.Record.Number.Normalized);
            if (!Matches(current?.Revision, planned.ExpectedRevision, EntityKind.ChangeRequest, planned.Record.Id.Value, tx)
                || (holder is not null && holder.Id != planned.Record.Id))
            {
                return TxResult.Abort<long>(new ImportPlanStaleError("changeRequest"));
            }
        }

        foreach (var planned in plan.Tasks)
        {
            if (!Matches(tx.Tasks.Find(planned.Record.Id)?.Revision, planned.ExpectedRevision, EntityKind.Task, planned.Record.Id.Value, tx))
            {
                return TxResult.Abort<long>(new ImportPlanStaleError("task"));
            }
        }

        foreach (var link in plan.Links.Where(l => l.TaskExists))
        {
            // ADR 0006: an existing task may be linked only while it is still unlinked.
            if (tx.Links.FindByTask(link.Link.TaskId) is not null)
            {
                return TxResult.Abort<long>(new ImportPlanStaleError("link"));
            }
        }

        foreach (var planned in plan.SubtaskCards)
        {
            if (!Matches(tx.SubtaskCards.Find(planned.Record.Id)?.Revision, planned.ExpectedRevision, EntityKind.SubtaskCard, planned.Record.Id.Value, tx))
            {
                return TxResult.Abort<long>(new ImportPlanStaleError("subtaskCard"));
            }
        }

        foreach (var planned in plan.Reviews)
        {
            if (!Matches(tx.Reviews.Find(planned.Record.Id)?.Revision, planned.ExpectedRevision, EntityKind.ReviewItem, planned.Record.Id.Value, tx))
            {
                return TxResult.Abort<long>(new ImportPlanStaleError("review"));
            }
        }

        foreach (var planned in plan.GeneralTodos)
        {
            if (!Matches(tx.GeneralTodos.Find(planned.Record.Id)?.Revision, planned.ExpectedRevision, EntityKind.GeneralTodo, planned.Record.Id.Value, tx))
            {
                return TxResult.Abort<long>(new ImportPlanStaleError("generalTodo"));
            }
        }

        foreach (var entry in plan.LedgerEntries)
        {
            if (tx.ImportLedger.Find(entry.SourceNamespace, entry.EntityKind, entry.SourceKey) is not null)
            {
                return TxResult.Abort<long>(new ImportPlanStaleError("ledger"));
            }
        }

        // Writes in foreign-key order: lookups and fields, CRs, tasks, links, Subtask cards, then the rest.
        if (plan.NewWorkflow is { } workflow)
        {
            tx.Lookups.SaveWorkflow(workflow);
        }

        foreach (var planned in plan.ChangeRequests)
        {
            if (planned.IsCreate)
            {
                tx.ChangeRequests.Insert(planned.Record);
            }
            else
            {
                tx.ChangeRequests.Update(planned.Record);
            }
        }

        foreach (var planned in plan.Tasks)
        {
            if (planned.IsCreate)
            {
                tx.Tasks.Insert(planned.Record);
            }
            else
            {
                tx.Tasks.Update(planned.Record);
            }
        }

        foreach (var link in plan.Links)
        {
            tx.Links.Insert(link.Link);
        }

        foreach (var planned in plan.SubtaskCards)
        {
            if (planned.IsCreate)
            {
                tx.SubtaskCards.Insert(planned.Record);
            }
            else
            {
                tx.SubtaskCards.Update(planned.Record);
            }
        }

        foreach (var planned in plan.Reviews)
        {
            if (planned.IsCreate)
            {
                tx.Reviews.Insert(planned.Record);
            }
            else
            {
                tx.Reviews.Update(planned.Record);
            }
        }

        foreach (var planned in plan.GeneralTodos)
        {
            if (planned.IsCreate)
            {
                tx.GeneralTodos.Insert(planned.Record);
            }
            else
            {
                tx.GeneralTodos.Update(planned.Record);
            }
        }

        foreach (var entry in plan.LedgerEntries)
        {
            tx.ImportLedger.Insert(entry.SourceNamespace, entry.EntityKind, entry.SourceKey, entry.RecordId);
        }

        // ADR 0010: a bulk import asks subscribers to reload one snapshot instead of applying records one by one.
        tx.Changes.RequireFullReload();
        return TxResult.Commit((long)plan.RecordWriteCount);
    }

    /// <summary>A create needs an unused, never-deleted ID; an update needs the previewed revision.</summary>
    private static bool Matches(long? currentRevision, long? expectedRevision, EntityKind kind, Guid id, IWriteTransaction tx) =>
        expectedRevision is null
            ? currentRevision is null && !tx.Tombstones.Contains(kind, id)
            : currentRevision == expectedRevision;
}

/// <summary>Builds the transfer document of a read snapshot (B-12 export).</summary>
public static class TransferDocumentBuilder
{
    public static TransferDocument Build(ReadSnapshot snapshot, Guid datasetId, DateTimeOffset exportedAtUtc, TransferExportScope scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scope);
        var tasks = snapshot.Tasks.AsEnumerable();
        var cards = snapshot.SubtaskCards.AsEnumerable();
        var changeRequests = snapshot.ChangeRequests.AsEnumerable();
        var reviews = snapshot.ReviewItems.AsEnumerable();
        var todos = snapshot.GeneralTodos.AsEnumerable();
        TransferScope transferScope = new(TransferFormat.ScopeEntireDataset);
        if (scope is ViewExportScope view)
        {
            cards = snapshot.SubtaskCards.Where(c => view.SubtaskCardIds.Contains(c.Id.Value)).ToList();
            var taskIds = view.TaskIds.Concat(cards.Select(c => c.ParentTaskId.Value)).ToHashSet();
            tasks = snapshot.Tasks.Where(t => taskIds.Contains(t.Id.Value)).ToList();
            var crIds = tasks.Where(t => t.ChangeRequestId is not null).Select(t => t.ChangeRequestId!.Value).ToHashSet();
            changeRequests = snapshot.ChangeRequests.Where(c => crIds.Contains(c.Id)).ToList();
            reviews = [];
            todos = [];
            transferScope = new TransferScope(TransferFormat.ScopeCurrentView, view.EntityType, view.Description);
        }

        var taskList = tasks.OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.Id.Value).ToList();
        var exportedTaskIds = taskList.Select(t => t.Id).ToHashSet();
        var w = snapshot.Workflow;
        return new TransferDocument
        {
            DatasetId = datasetId,
            ExportedAtUtc = exportedAtUtc,
            Scope = transferScope,
            Lookups = new TransferLookups
            {
                TaskStatuses = [.. w.TaskStatuses.OrderBy(s => s.Order).Select(s => new TransferTaskStatus(s.Id.Value, s.Name.Display, s.Order, s.IsCompleted, s.SetsStartDate, s.IsBacklog))],
                CrStatuses = [.. w.CrStatuses.OrderBy(s => s.Order).Select(s => new TransferCrStatus(s.Id.Value, s.Name.Display, s.Order, s.IsCompleted, s.IsBacklog))],
                Priorities = [.. w.Priorities.OrderBy(p => p.Order).Select(p => new TransferOrderedLookup(p.Id.Value, p.Name.Display, p.Order))],
                Squads = [.. w.Squads.OrderBy(s => s.Order).Select(s => new TransferOrderedLookup(s.Id.Value, s.Name.Display, s.Order))],
                People = [.. w.People.OrderBy(p => p.Name.Normalized, StringComparer.Ordinal).Select(p => new TransferNamedLookup(p.Id.Value, p.Name.Display))],
                Tags = [.. w.Tags.OrderBy(t => t.Name.Normalized, StringComparer.Ordinal).Select(t => new TransferNamedLookup(t.Id.Value, t.Name.Display))],
            },
            FieldDefinitions = [.. w.Fields.OrderBy(f => f.Target).ThenBy(f => f.DisplayOrder).Select(f => new TransferFieldDefinition(
                f.Id.Value, f.Key.Display, f.Target.ToString(), f.Label.Display, f.Kind.ToString(), f.IsRequired,
                f.DefaultValue is { } d ? TransferValues.ToTransfer(d) : null, f.DisplayOrder,
                [.. f.Options.Select(o => new TransferFieldOption(o.Id.Value, o.Label.Display, o.Order))], f.IsActive))],
            Defaults = new TransferDefaults(
                w.Defaults.DefaultTaskStatusId.Value, w.Defaults.DefaultCrStatusId.Value, w.Defaults.DefaultPriorityId.Value,
                w.Defaults.DefaultCrOwnerId?.Value, w.Defaults.CalendarSchedulingStatusId.Value, w.Defaults.CrBacklogStatusId.Value),
            Tasks = [.. taskList.Select(t => new TransferTask
            {
                Id = t.Id.Value,
                OwnTitle = t.OwnTitle?.Value,
                Service = t.Service?.Value,
                StatusId = t.StatusId.Value,
                PriorityId = t.PriorityId.Value,
                DesignerIds = [.. t.DesignerIds.Select(p => p.Value)],
                DeveloperIds = [.. t.DeveloperIds.Select(p => p.Value)],
                TagIds = [.. t.TagIds.Select(p => p.Value)],
                Notes = t.Notes?.Value,
                StartDate = t.Dates.Start,
                EndDate = t.Dates.End,
                IsArchived = t.IsArchived,
                Order = t.Rank,
                ChecklistItems = [.. t.ChecklistItems.OrderBy(i => i.Rank).ThenBy(i => i.Id.Value).Select(i => new TransferChecklistItem(i.Id.Value, i.Text.Value, i.IsCompleted, i.Rank))],
                CustomValues = TransferValues.ToTransfer(t.CustomValues),
                Preserved = TransferValues.ToTransfer(t.Preserved),
                CreatedAtUtc = t.CreatedAtUtc,
                UpdatedAtUtc = t.UpdatedAtUtc,
                Revision = t.Revision,
            })],
            SubtaskCards = [.. cards.OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.Id.Value).Select(c => new TransferSubtaskCard
            {
                Id = c.Id.Value,
                ParentTaskId = c.ParentTaskId.Value,
                Subtitle = c.Subtitle.Value,
                StatusId = c.StatusId.Value,
                StartDate = c.Dates.Start,
                EndDate = c.Dates.End,
                Notes = c.Notes?.Value,
                Order = c.Rank,
                CreatedAtUtc = c.CreatedAtUtc,
                UpdatedAtUtc = c.UpdatedAtUtc,
                Revision = c.Revision,
            })],
            ChangeRequests = [.. changeRequests.OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.Id.Value).Select(c => new TransferChangeRequest
            {
                Id = c.Id.Value,
                Number = c.Number.Display,
                Title = c.Title.Value,
                OwnerId = c.OwnerId.Value,
                SquadId = c.SquadId?.Value,
                StatusId = c.StatusId.Value,
                PriorityId = c.PriorityId.Value,
                PlannedStart = c.Planned?.Start,
                PlannedEnd = c.Planned?.End,
                EmailSubject = c.EmailSubject?.Value,
                SolutionDesignUrl = c.SolutionDesignUrl?.ToString(),
                Description = c.Description?.Value,
                TaskNumber = c.TaskNumber?.Value,
                CustomValues = TransferValues.ToTransfer(c.CustomValues),
                Preserved = TransferValues.ToTransfer(c.Preserved),
                CreatedAtUtc = c.CreatedAtUtc,
                UpdatedAtUtc = c.UpdatedAtUtc,
                Revision = c.Revision,
            })],
            Links = [.. snapshot.Links.Where(l => exportedTaskIds.Contains(l.TaskId)).OrderBy(l => l.TaskId.Value)
                .Select(l => new TransferLink(l.TaskId.Value, l.ChangeRequestId.Value, l.LinkedAtUtc))],
            Reviews = [.. reviews.OrderBy(r => r.Rank).ThenBy(r => r.Id.Value).Select(r => new TransferReview
            {
                Id = r.Id.Value,
                Title = r.Content.Title.Value,
                CrNumberText = r.Content.CrNumberText?.Value,
                TaskNumberText = r.Content.TaskNumberText?.Value,
                Services = [.. r.Content.Services.Select(s => s.Value)],
                DeveloperId = r.Content.DeveloperId?.Value,
                Comments = r.Content.Comments?.Value,
                IsDone = r.IsDone,
                CompletedOn = r.CompletedOn,
                Order = r.Rank,
                CreatedAtUtc = r.CreatedAtUtc,
                UpdatedAtUtc = r.UpdatedAtUtc,
                Revision = r.Revision,
            })],
            GeneralTodos = [.. todos.OrderBy(t => t.Rank).ThenBy(t => t.Id.Value).Select(t => new TransferGeneralTodo
            {
                Id = t.Id.Value,
                Text = t.Text.Value,
                IsCompleted = t.IsCompleted,
                Order = t.Rank,
                CreatedAtUtc = t.CreatedAtUtc,
                UpdatedAtUtc = t.UpdatedAtUtc,
                Revision = t.Revision,
            })],
        };
    }
}
