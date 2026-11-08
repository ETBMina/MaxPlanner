using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using MaxPlanner.Infrastructure.Tests.Persistence;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>
/// A populated dataset written through the real use cases: linked and unlinked tasks, a CR with several tasks, owners,
/// checklists, a Subtask card, reviews, General to-dos, an archived task, custom fields, and preserved metadata.
/// </summary>
/// <param name="LinkedOwnTitle">Created with its own title, then linked: its title is preserved as PreLinkTitle (ADR 0005).</param>
/// <param name="LinkedAtCreation">Created under <paramref name="Cr"/>, so <paramref name="Cr"/> has two tasks.</param>
/// <param name="Unlinked">Unlinked, with preserved legacy content and a Subtask card.</param>
internal sealed record TransferFixture(
    PersonId Alice,
    PersonId Bob,
    TagId Mobile,
    FieldId Estimate,
    FieldId Risk,
    ChangeRequestId Cr,
    ChangeRequestId OtherCr,
    TaskId LinkedOwnTitle,
    TaskId LinkedAtCreation,
    TaskId Unlinked,
    TaskId Archived,
    TaskId OtherCrTask,
    SubtaskCardId Card,
    ReviewItemId Review,
    GeneralTodoId Todo)
{
    /// <summary>Notes as a WinUI text box (CR) and a Windows file (CR LF) supply them.</summary>
    public const string Notes = "Line 1\r\nLïne 2 — ✓ 🚀\rLine 3";

    /// <summary><see cref="Notes"/> as stored: LF line breaks only (ADR 0025).</summary>
    /// <summary>A non-default Copy ID format with a value containing a separator character.</summary>
    public const string CopyIdFormat = "{Squad} / {CRNum}-{TaskNum}";

    public const string StoredNotes = "Line 1\nLïne 2 — ✓ 🚀\nLine 3";

    public static async Task<TransferFixture> PopulateAsync(TestDataset db)
    {
        var alice = db.AddPerson("Alice");
        var bob = db.AddPerson("Bob");
        var mobile = db.AddTag("Mobile");
        var estimate = db.AddField("estimate", FieldKind.Number);
        var highRisk = Guid.NewGuid();
        var risk = db.AddField("risk", FieldKind.Lookup, $$"""[{"Id":"{{highRisk}}","Label":"High","Order":0}]""");
        var workflow = (await db.SnapshotAsync()).Workflow;
        var cards = workflow.Squads.Single(s => s.Name.Display == "Retail Cards").Id;
        var blocked = workflow.TaskStatuses.Single(s => s.Name.Display == "Blocked").Id;

        var cr = await CreateCrAsync(db, new ChangeRequestDraft(
            "CR-0105", "Instant card freeze", alice, cards, null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5), "Re: freeze",
            "https://example.test/design?id=7", "Scope\nOut of scope: ✓", "0042", CustomValue.NoValues));
        var otherCr = await CreateCrAsync(db, new ChangeRequestDraft(
            "CR-2", "Statement redesign", bob, null, null, null, null, null, null, null, null, CustomValue.NoValues));

        var custom = new Dictionary<FieldId, CustomValue>
        {
            [estimate] = new NumberCustomValue(3.25m),
            [risk] = new LookupCustomValue(new FieldOptionId(highRisk)),
        };
        var linkedOwnTitle = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(
            new CreateTaskCommand(
                OperationId.New(),
                new TaskId(Guid.NewGuid()),
                new TaskContentDraft("Own title", "Mobile Banking", alice, bob, [mobile], Notes, custom),
                Start: DateIntent.Set(new DateOnly(2026, 10, 1)),
                End: DateIntent.Set(new DateOnly(2026, 10, 9))),
            Ct)).Value;
        var revision = Succeeded(await new AddChecklistItem(db.Coordinator).ExecuteAsync(
            new AddChecklistItemCommand(OperationId.New(), linkedOwnTitle, 1, new ChecklistItemId(Guid.NewGuid()), "First"), Ct)).Value;
        var second = new ChecklistItemId(Guid.NewGuid());
        revision = Succeeded(await new AddChecklistItem(db.Coordinator).ExecuteAsync(
            new AddChecklistItemCommand(OperationId.New(), linkedOwnTitle, revision, second, "Second; with 007"), Ct)).Value;
        revision = Succeeded(await new SetChecklistItemCompleted(db.Coordinator).ExecuteAsync(
            new SetChecklistItemCompletedCommand(OperationId.New(), linkedOwnTitle, revision, second, true), Ct)).Value;
        Succeeded(await new LinkTaskToChangeRequest(db.Coordinator).ExecuteAsync(
            new LinkTaskToChangeRequestCommand(OperationId.New(), linkedOwnTitle, revision, cr, 1), Ct));

        var linkedAtCreation = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft(title: null, service: "Cards API"), LinkTo: new CrLinkTarget(cr, 1)), Ct)).Value;
        var unlinked = await db.AddTaskAsync("007 Unlinked", blocked, service: "Core");
        var card = Succeeded(await new CreateSubtaskCard(db.Coordinator).ExecuteAsync(
            new CreateSubtaskCardCommand(OperationId.New(), unlinked, new SubtaskCardId(Guid.NewGuid()), "Card subtitle", "Card notes\nline 2",
                Start: DateIntent.Set(new DateOnly(2026, 10, 3))), Ct)).Value;
        var archived = await db.AddTaskAsync("Archived task");
        Succeeded(await new ArchiveTask(db.Coordinator).ExecuteAsync(new ArchiveTaskCommand(OperationId.New(), archived, 1), Ct));
        var otherCrTask = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft(title: null, service: "Statements"), LinkTo: new CrLinkTarget(otherCr, 1)), Ct)).Value;

        var review = new ReviewItemId(Guid.NewGuid());
        Succeeded(await new CreateReview(db.Coordinator).ExecuteAsync(new CreateReviewCommand(
            OperationId.New(), review, new ReviewDraft("Review login", "CR-0105", "0042", ["Cards API", "Mobile, Web"], bob, "Looks good\nShip it"), null, true), Ct));
        Succeeded(await new CreateReview(db.Coordinator).ExecuteAsync(new CreateReviewCommand(
            OperationId.New(), new ReviewItemId(Guid.NewGuid()), new ReviewDraft("Open review", null, null, [], null, null), null, false), Ct));
        var todo = new GeneralTodoId(Guid.NewGuid());
        Succeeded(await new AddGeneralTodo(db.Coordinator).ExecuteAsync(new AddGeneralTodoCommand(OperationId.New(), todo, "Call bank"), Ct));
        var done = new GeneralTodoId(Guid.NewGuid());
        Succeeded(await new AddGeneralTodo(db.Coordinator).ExecuteAsync(new AddGeneralTodoCommand(OperationId.New(), done, "Renew card"), Ct));
        Succeeded(await new SetGeneralTodoCompleted(db.Coordinator).ExecuteAsync(new SetGeneralTodoCompletedCommand(OperationId.New(), done, 1, true), Ct));

        // Design §4.6: a non-default Copy ID format, so every round trip carries it (ADR 0026).
        var defaults = (await db.SnapshotAsync()).Workflow;
        Succeeded(await new UpdateWorkflowDefaults(db.Coordinator).ExecuteAsync(new UpdateWorkflowDefaultsCommand(
            OperationId.New(), WorkflowStamps.Defaults(defaults), defaults.Defaults.DefaultTaskStatusId, defaults.Defaults.DefaultCrStatusId,
            defaults.Defaults.DefaultPriorityId, defaults.Defaults.DefaultCrOwnerId, defaults.Defaults.CalendarSchedulingStatusId,
            defaults.Defaults.CrBacklogStatusId, CopyIdFormat), Ct));

        // No use case writes legacy content; an external import would. Stored directly, as the storage tests do.
        db.Execute("UPDATE tasks SET preserved_json = $p WHERE id = $id;",
            ("$p", StorageJson.WritePreserved(new PreservedMetadata(null, ["OLD-77"], """{"jira":{"key":"MP-77"}}"""))), ("$id", unlinked.Value));
        db.Execute("UPDATE change_requests SET preserved_json = $p WHERE id = $id;",
            ("$p", StorageJson.WritePreserved(new PreservedMetadata(null, [], """{"origin":"sheet"}"""))), ("$id", otherCr.Value));

        return new TransferFixture(alice, bob, mobile, estimate, risk, cr, otherCr, linkedOwnTitle, linkedAtCreation, unlinked, archived, otherCrTask, card, review, todo);
    }

    /// <summary>The business content of a snapshot with workflow IDs replaced by names, so two datasets can be compared.</summary>
    public static object Shape(ReadSnapshot s)
    {
        var w = s.Workflow;
        var names = new Dictionary<Guid, string>();
        foreach (var x in w.TaskStatuses)
        {
            names[x.Id.Value] = "task:" + x.Name.Display;
        }

        foreach (var x in w.CrStatuses)
        {
            names[x.Id.Value] = "cr:" + x.Name.Display;
        }

        foreach (var x in w.Priorities)
        {
            names[x.Id.Value] = "priority:" + x.Name.Display;
        }

        foreach (var x in w.Squads)
        {
            names[x.Id.Value] = "squad:" + x.Name.Display;
        }

        string? Name(Guid? id) => id is { } value ? names[value] : null;
        return new
        {
            Tasks = s.Tasks.OrderBy(t => t.Id.Value).Select(t => new
            {
                t.Id,
                OwnTitle = t.OwnTitle?.Value,
                Service = t.Service?.Value,
                Status = Name(t.StatusId.Value),
                Priority = Name(t.PriorityId.Value),
                t.DesignerId,
                t.DeveloperId,
                Tags = t.TagIds.ToList(),
                Notes = t.Notes?.Value,
                t.Dates.Start,
                t.Dates.End,
                t.IsArchived,
                t.Rank,
                Checklist = t.ChecklistItems.Select(i => new { i.Id, Text = i.Text.Value, i.IsCompleted, i.Rank }).ToList(),
                Custom = TransferValues.ToTransfer(t.CustomValues),
                Preserved = Preserved(t.Preserved),
                t.ChangeRequestId,
                t.CreatedAtUtc,
                t.UpdatedAtUtc,
                t.Revision,
            }).ToList(),
            ChangeRequests = s.ChangeRequests.OrderBy(c => c.Id.Value).Select(c => new
            {
                c.Id,
                Number = c.Number.Display,
                Title = c.Title.Value,
                c.OwnerId,
                Squad = Name(c.SquadId?.Value),
                Status = Name(c.StatusId.Value),
                Priority = Name(c.PriorityId.Value),
                PlannedStart = c.Planned?.Start,
                PlannedEnd = c.Planned?.End,
                Email = c.EmailSubject?.Value,
                Url = c.SolutionDesignUrl?.ToString(),
                Description = c.Description?.Value,
                TaskNumber = c.TaskNumber?.Value,
                Custom = TransferValues.ToTransfer(c.CustomValues),
                Preserved = Preserved(c.Preserved),
                c.CreatedAtUtc,
                c.UpdatedAtUtc,
                c.Revision,
            }).ToList(),
            Links = s.Links.OrderBy(l => l.TaskId.Value).Select(l => new { l.TaskId, l.ChangeRequestId }).ToList(),
            Cards = s.SubtaskCards.OrderBy(c => c.Id.Value).Select(c => new
            {
                c.Id,
                c.ParentTaskId,
                Subtitle = c.Subtitle.Value,
                Status = Name(c.StatusId.Value),
                c.Dates.Start,
                c.Dates.End,
                Notes = c.Notes?.Value,
                c.Rank,
                c.CreatedAtUtc,
                c.UpdatedAtUtc,
                c.Revision,
            }).ToList(),
            Reviews = s.ReviewItems.OrderBy(r => r.Id.Value).Select(r => new
            {
                r.Id,
                Title = r.Content.Title.Value,
                Cr = r.Content.CrNumberText?.Value,
                Task = r.Content.TaskNumberText?.Value,
                Services = r.Content.Services.Select(v => v.Value).ToList(),
                r.Content.DeveloperId,
                Comments = r.Content.Comments?.Value,
                r.IsDone,
                r.CompletedOn,
                r.Rank,
                r.CreatedAtUtc,
                r.UpdatedAtUtc,
                r.Revision,
            }).ToList(),
            Todos = s.GeneralTodos.OrderBy(t => t.Id.Value).Select(t => new { t.Id, Text = t.Text.Value, t.IsCompleted, t.Rank, t.CreatedAtUtc, t.UpdatedAtUtc, t.Revision }).ToList(),
            People = w.People.OrderBy(p => p.Id.Value).Select(p => new { p.Id, Name = p.Name.Display }).ToList(),
            Tags = w.Tags.OrderBy(t => t.Id.Value).Select(t => new { t.Id, Name = t.Name.Display }).ToList(),
            Fields = w.Fields.OrderBy(f => f.Id.Value).Select(f => new
            {
                f.Id,
                Key = f.Key.Display,
                f.Target,
                f.Kind,
                Label = f.Label.Display,
                f.IsRequired,
                f.IsActive,
                Options = f.Options.Select(o => new { o.Id, Label = o.Label.Display }).ToList(),
            }).ToList(),
            Defaults = new
            {
                Task = Name(w.Defaults.DefaultTaskStatusId.Value),
                Cr = Name(w.Defaults.DefaultCrStatusId.Value),
                Priority = Name(w.Defaults.DefaultPriorityId.Value),
                Owner = w.Defaults.DefaultCrOwnerId,
                Scheduling = Name(w.Defaults.CalendarSchedulingStatusId.Value),
                Backlog = Name(w.Defaults.CrBacklogStatusId.Value),
                w.Defaults.CopyIdFormat,
            },
        };
    }

    private static object Preserved(PreservedMetadata p) => new { p.PreLinkTitle, References = p.UnselectedCrReferences.ToList(), p.LegacyJson };

    private static async Task<ChangeRequestId> CreateCrAsync(TestDataset db, ChangeRequestDraft draft)
    {
        var id = new ChangeRequestId(Guid.NewGuid());
        return Succeeded(await new CreateChangeRequest(db.Coordinator).ExecuteAsync(new CreateChangeRequestCommand(OperationId.New(), id, draft), Ct)).Value;
    }
}
