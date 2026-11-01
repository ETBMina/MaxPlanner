using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Infrastructure.Tests.Persistence;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>
/// The B-12 "for every format" import rules driven by edited XLSX workbooks against real SQLite datasets: merge keeps
/// omitted fields, CR-derived columns never override the CR, permanent links (AC-29), duplicate CR numbers, unknown
/// references, all-or-nothing apply (AC-13), stale plans, and no automatic dates.
/// </summary>
[Trait("Req", "B-12")]
[Trait("Req", "T-10")]
public sealed class XlsxImportPlanningTests : IDisposable
{
    private const string Tasks = "Tasks";
    private const string Crs = "Change Requests";
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task A_merge_updates_the_supplied_columns_and_keeps_the_fields_of_omitted_columns()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        workbook.Set(Tasks, fixture.Unlinked.Value, "OwnTitle", "Renamed in Excel").RemoveColumn(Tasks, "Notes").RemoveColumn(Tasks, "Service");
        var before = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.Unlinked);

        var plan = await PreviewAsync(db, workbook, ImportMode.Merge);
        await Transfers.ApplyAsync(db, plan);

        plan.Counts[ImportEntity.Task].Should().Be(new ImportEntityCounts(0, 1, 4, 0, 0));
        var after = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.Unlinked);
        after.OwnTitle!.Value.Should().Be("Renamed in Excel");
        (after.Service, after.Notes).Should().Be((before.Service, before.Notes), "absent columns mean unchanged");
        after.Preserved.Should().BeEquivalentTo(before.Preserved);
        after.Revision.Should().Be(before.Revision + 1);
    }

    [Fact]
    public async Task A_blank_cell_in_a_supplied_column_clears_that_field_in_a_merge()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        workbook.Set(Tasks, fixture.Unlinked.Value, "Service", null);

        await Transfers.ApplyAsync(db, await PreviewAsync(db, workbook, ImportMode.Merge));

        (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.Unlinked).Service.Should().BeNull("explicit null clears a nullable field (T-10)");
    }

    [Fact]
    public async Task Create_only_is_the_default_and_never_updates_existing_records()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        workbook.Set(Tasks, fixture.Unlinked.Value, "OwnTitle", "Changed");

        var plan = await PreviewAsync(db, workbook);

        plan.Mode.Should().Be(ImportMode.CreateOnly);
        plan.Counts[ImportEntity.Task].Should().Be(new ImportEntityCounts(0, 0, 5, 0, 0));
        plan.Findings.Should().Contain(new ImportFinding(ImportSeverity.Info, ImportEntity.Task, fixture.Unlinked.Value.ToString(), ImportFindingCodes.Exists));
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "T-13")]
    public async Task Cr_derived_informational_columns_of_a_task_row_never_override_the_cr_or_the_task()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        var id = fixture.LinkedAtCreation.Value;
        workbook.Set(Tasks, id, "Title", "Hacked title").Set(Tasks, id, "CrNumber", "CR-999").Set(Tasks, id, "TaskNumber", "9999")
            .Set(Tasks, id, "Squad", "Retail Finance").Set(Tasks, id, "Owner", "Bob").Set(Tasks, id, "Status", "Completed").Set(Tasks, id, "Priority", "Low")
            .Set(Tasks, fixture.Unlinked.Value, "OwnTitle", "A real change so the plan can be applied");
        var before = await db.SnapshotAsync();

        var plan = await PreviewAsync(db, workbook, ImportMode.Merge);
        await Transfers.ApplyAsync(db, plan);

        plan.Counts[ImportEntity.Task].Should().Be(new ImportEntityCounts(0, 1, 4, 0, 0), "the edited linked row has only informational changes and is skipped");
        plan.Counts[ImportEntity.ChangeRequest].Should().Be(new ImportEntityCounts(0, 0, 2, 0, 0));
        plan.Links.Should().BeEmpty();
        var after = await db.SnapshotAsync();
        after.ChangeRequests.Should().BeEquivalentTo(before.ChangeRequests);
        after.Tasks.Single(t => t.Id == fixture.LinkedAtCreation).Should().BeEquivalentTo(before.Tasks.Single(t => t.Id == fixture.LinkedAtCreation));
    }

    [Fact]
    [Trait("Req", "AC-29")]
    [Trait("Req", "AC-13")]
    public async Task Reassigning_a_linked_task_to_another_cr_is_a_conflict_that_changes_nothing_for_that_task()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        workbook.Set(Tasks, fixture.LinkedAtCreation.Value, "ChangeRequestId", fixture.OtherCr.Value.ToString())
            .Set(Tasks, fixture.LinkedAtCreation.Value, "Service", "Changed with the reassignment")
            .Set(Tasks, fixture.Unlinked.Value, "OwnTitle", "Valid change");
        var before = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.LinkedAtCreation);

        var plan = await PreviewAsync(db, workbook, ImportMode.Merge);
        await Transfers.ApplyAsync(db, plan);

        plan.Findings.Should().ContainSingle(f => f.Severity == ImportSeverity.Conflict).Which.Should().Be(
            new ImportFinding(ImportSeverity.Conflict, ImportEntity.Task, fixture.LinkedAtCreation.Value.ToString(), ImportFindingCodes.LinkChangeRefused, "ChangeRequestId"));
        plan.Counts[ImportEntity.Task].Should().Be(new ImportEntityCounts(0, 1, 3, 1, 0));
        var snapshot = await db.SnapshotAsync();
        snapshot.Tasks.Single(t => t.Id == fixture.LinkedAtCreation).Should().BeEquivalentTo(before, "the conflicting row is not applied");
        snapshot.Links.Single(l => l.TaskId == fixture.LinkedAtCreation).ChangeRequestId.Should().Be(fixture.Cr);
        snapshot.Tasks.Single(t => t.Id == fixture.Unlinked).OwnTitle!.Value.Should().Be("Valid change");
    }

    [Fact]
    [Trait("Req", "AC-29")]
    public async Task Clearing_the_cr_of_a_linked_task_never_removes_the_link()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        workbook.Set(Tasks, fixture.LinkedAtCreation.Value, "ChangeRequestId", null)
            .Set(Tasks, fixture.Unlinked.Value, "OwnTitle", "A real change so the plan can be applied");
        var before = await db.SnapshotAsync();

        var plan = await PreviewAsync(db, workbook, ImportMode.Merge);
        await Transfers.ApplyAsync(db, plan);

        plan.Counts[ImportEntity.Task].Should().Be(new ImportEntityCounts(0, 1, 3, 1, 0), "the unlink attempt is a conflict; the other row applies");
        plan.Links.Should().BeEmpty();
        var after = await db.SnapshotAsync();
        after.Links.Should().BeEquivalentTo(before.Links);
        after.Tasks.Single(t => t.Id == fixture.LinkedAtCreation).Should().BeEquivalentTo(before.Tasks.Single(t => t.Id == fixture.LinkedAtCreation));
    }

    [Fact]
    [Trait("Req", "AC-29")]
    public async Task Clearing_the_cr_of_a_linked_task_is_reported_as_a_conflict()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        workbook.Set(Tasks, fixture.LinkedAtCreation.Value, "ChangeRequestId", null).Set(Tasks, fixture.LinkedAtCreation.Value, "Service", "Changed with the unlink");

        var plan = await PreviewAsync(db, workbook, ImportMode.Merge);

        plan.Findings.Should().Contain(new ImportFinding(
            ImportSeverity.Conflict, ImportEntity.Task, fixture.LinkedAtCreation.Value.ToString(), ImportFindingCodes.UnlinkRefused, "ChangeRequestId"));
        plan.Tasks.Should().NotContain(t => t.Record.Id == fixture.LinkedAtCreation, "the conflicting row is not applied");
    }

    [Fact]
    [Trait("Req", "AC-29")]
    public async Task A_single_csv_tasks_table_that_blanks_the_cr_of_a_linked_task_is_also_an_unlink_conflict()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var id = fixture.LinkedAtCreation.Value;
        var path = _folder.Write("tasks.csv", System.Text.Encoding.UTF8.GetBytes($"Id,OwnTitle,ChangeRequestId,Notes\r\n{id},,,\"Changed\r\nwith the unlink\"\r\n"));

        var plan = await Transfers.PreviewAsync(db, path, ImportMode.Merge);

        plan.Findings.Should().ContainSingle().Which.Should().Be(
            new ImportFinding(ImportSeverity.Conflict, ImportEntity.Task, id.ToString(), ImportFindingCodes.UnlinkRefused, "ChangeRequestId"));
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "B-03.7")]
    public async Task Linking_a_currently_unlinked_task_is_previewed_as_permanent_and_moves_its_title_to_the_pre_link_title()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        workbook.Set(Tasks, fixture.Unlinked.Value, "ChangeRequestId", fixture.OtherCr.Value.ToString());

        var plan = await PreviewAsync(db, workbook, ImportMode.Merge);
        await Transfers.ApplyAsync(db, plan);

        plan.Findings.Should().Contain(new ImportFinding(ImportSeverity.Warning, ImportEntity.Task, fixture.Unlinked.Value.ToString(), ImportFindingCodes.PermanentLink, "ChangeRequestId"));
        plan.Links.Should().ContainSingle().Which.TaskExists.Should().BeTrue();
        var task = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.Unlinked);
        (task.ChangeRequestId, task.OwnTitle, task.Preserved.PreLinkTitle).Should().Be((fixture.OtherCr, null, "007 Unlinked"));
    }

    [Fact]
    [Trait("Req", "B-03.2")]
    [Trait("Req", "AC-13")]
    public async Task A_new_cr_whose_number_already_exists_in_any_spelling_is_a_conflict_in_create_only()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        var copy = workbook.CopyRow(Crs, fixture.Cr.Value);
        var newId = Guid.NewGuid();
        copy[workbook.Column(Crs, "Id")] = newId.ToString();
        copy[workbook.Column(Crs, "Number")] = " cr-0105 ";

        var plan = await PreviewAsync(db, workbook);

        plan.Findings.Should().Contain(new ImportFinding(ImportSeverity.Conflict, ImportEntity.ChangeRequest, newId.ToString(), ImportFindingCodes.DuplicateCrNumber, "Number", "cr-0105"));
        plan.ChangeRequests.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "B-03.2")]
    public async Task A_merge_matches_a_cr_by_its_normalized_number_after_review()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        var copy = workbook.CopyRow(Crs, fixture.Cr.Value);
        workbook.KeepRows(Crs, r => ReferenceEquals(r, copy)).KeepRows(Tasks, _ => false).KeepRows("Checklist Items", _ => false);
        var newId = Guid.NewGuid();
        copy[workbook.Column(Crs, "Id")] = newId.ToString();
        copy[workbook.Column(Crs, "Number")] = "cr-0105";
        copy[workbook.Column(Crs, "Title")] = "Retitled through the number";

        var plan = await PreviewAsync(db, workbook, ImportMode.Merge);
        await Transfers.ApplyAsync(db, plan);

        plan.Findings.Should().Contain(f => f.Code == ImportFindingCodes.MatchedByNumber && f.RecordKey == newId.ToString());
        var crs = (await db.SnapshotAsync()).ChangeRequests;
        crs.Should().HaveCount(2, "no second CR with the same number is created");
        crs.Single(c => c.Id == fixture.Cr).Title.Value.Should().Be("Retitled through the number");
    }

    [Fact]
    [Trait("Req", "AC-13")]
    public async Task Two_new_crs_with_one_number_in_the_same_file_are_an_error_and_nothing_applies()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var path = _folder.Write("dup.xlsx", TestWorkbook.Create(
        [
            TestWorkbook.Metadata(),
            TestWorkbook.Sheet(Crs, TestWorkbook.Header("Id", "Number", "Title", "OwnerId"), CrRow(Guid.NewGuid(), "CR-9", "First", fixture.Alice.Value), CrRow(Guid.NewGuid(), "cr-9", "Second", fixture.Alice.Value)),
        ]));
        var before = await db.SnapshotAsync();

        var plan = await Transfers.PreviewAsync(db, path);

        plan.HasErrors.Should().BeTrue();
        plan.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.Entity == ImportEntity.ChangeRequest && f.Field == "Number");
        (await Transfers.Importer(db).ApplyAsync(plan, OperationId.New(), Ct)).Error.Should().Be(ImportNotApplicableError.Instance);
        (await db.SnapshotAsync()).CommitSequence.Should().Be(before.CommitSequence);
    }

    [Fact]
    public async Task A_new_cr_row_without_a_status_column_gets_the_default_cr_status()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var id = Guid.NewGuid();
        var path = _folder.Write("nostatus.xlsx", TestWorkbook.Create(
            [TestWorkbook.Metadata(), TestWorkbook.Sheet(Crs, TestWorkbook.Header("Id", "Number", "Title", "OwnerId"), CrRow(id, "CR-77", "Partial", fixture.Alice.Value))]));

        var plan = await Transfers.PreviewAsync(db, path);

        plan.ChangeRequests.Should().ContainSingle().Which.Record.StatusId.Should().Be((await db.SnapshotAsync()).Workflow.Defaults.DefaultCrStatusId);
    }

    [Fact]
    [Trait("Req", "AC-13")]
    public async Task A_task_row_repeated_with_a_second_cr_is_an_error_because_a_task_has_at_most_one_cr()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        workbook.CopyRow(Tasks, fixture.LinkedAtCreation.Value)[workbook.Column(Tasks, "ChangeRequestId")] = fixture.OtherCr.Value.ToString();
        await using var target = await CreateAsync();

        var plan = await PreviewAsync(target, workbook);

        plan.HasErrors.Should().BeTrue();
        plan.Findings.Where(f => f.Severity == ImportSeverity.Error).Should().Equal(
            new ImportFinding(ImportSeverity.Error, ImportEntity.Task, fixture.LinkedAtCreation.Value.ToString(), ImportFindingCodes.MultipleCrReferences, "ChangeRequestId"),
            new ImportFinding(ImportSeverity.Error, ImportEntity.Task, fixture.LinkedAtCreation.Value.ToString(), ImportFindingCodes.DuplicateInSource));
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "AC-13")]
    public async Task Unknown_references_are_reported_per_record_with_their_field()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        var badStatus = Guid.NewGuid();
        workbook.Set(Tasks, fixture.Unlinked.Value, "StatusId", badStatus.ToString())
            .Set(Tasks, fixture.Archived.Value, "ChangeRequestId", Guid.NewGuid().ToString())
            .Set(Tasks, fixture.LinkedOwnTitle.Value, "DesignerId", Guid.NewGuid().ToString())
            .Set("Subtask Cards", fixture.Card.Value, "ParentTaskId", Guid.NewGuid().ToString());
        await using var target = await CreateAsync();

        var plan = await PreviewAsync(target, workbook);

        var errors = plan.Findings.Where(f => f.Severity == ImportSeverity.Error).ToList();
        errors.Should().Contain(new ImportFinding(ImportSeverity.Error, ImportEntity.Task, fixture.Unlinked.Value.ToString(), ImportFindingCodes.UnknownReference, "StatusId", "unknown-reference"));
        errors.Should().Contain(new ImportFinding(ImportSeverity.Error, ImportEntity.Task, fixture.Archived.Value.ToString(), ImportFindingCodes.UnknownReference, "ChangeRequestId", "unknown-reference"));
        errors.Should().Contain(new ImportFinding(ImportSeverity.Error, ImportEntity.Task, fixture.LinkedOwnTitle.Value.ToString(), ImportFindingCodes.UnknownReference, "DesignerId", "unknown-reference"));
        errors.Should().Contain(f => f.Entity == ImportEntity.SubtaskCard && f.RecordKey == fixture.Card.Value.ToString() && f.Code == ImportFindingCodes.MissingParent);
        string[] content =
        [
            "Own title", "Mobile Banking", TransferFixture.Notes, "First", "Second; with 007", "Cards API", "007 Unlinked", "Core", "Card subtitle",
            "Card notes", "Archived task", "Instant card freeze", "Scope", "Re: freeze", "Statement redesign", "Review login", "Looks good", "Call bank", "Renew card",
        ];
        foreach (var finding in plan.Findings)
        {
            foreach (var text in new[] { finding.RecordKey, finding.Field, finding.Detail }.OfType<string>())
            {
                content.Should().NotContain(c => text.Contains(c, StringComparison.Ordinal), "findings never carry record content ({0})", finding.Code);
            }
        }

        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "AC-13")]
    public async Task One_invalid_row_blocks_the_whole_import_so_nothing_is_partially_applied()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        workbook.Set(Tasks, fixture.Unlinked.Value, "OwnTitle", new string('x', 1_000));
        await using var target = await CreateAsync();
        var before = await target.SnapshotAsync();

        var plan = await PreviewAsync(target, workbook);
        var applied = await Transfers.Importer(target).ApplyAsync(plan, OperationId.New(), Ct);

        plan.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.RecordKey == fixture.Unlinked.Value.ToString() && f.Code == ImportFindingCodes.Invalid);
        plan.Tasks.Should().NotBeEmpty("the other rows are valid");
        applied.Error.Should().Be(ImportNotApplicableError.Instance);
        var after = await target.SnapshotAsync();
        (after.CommitSequence, after.Tasks.Count, after.ChangeRequests.Count, after.Workflow.People.Count).Should().Be((before.CommitSequence, 0, 0, before.Workflow.People.Count));
    }

    [Fact]
    [Trait("Req", "AC-13")]
    public async Task A_plan_whose_records_changed_after_the_preview_is_stale_and_writes_nothing()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        workbook.Set(Tasks, fixture.Unlinked.Value, "OwnTitle", "From Excel").Set(Tasks, fixture.Archived.Value, "Service", "Also from Excel");
        var plan = await PreviewAsync(db, workbook, ImportMode.Merge);
        plan.Tasks.Should().HaveCount(2);
        var current = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.Unlinked);
        Succeeded(await new UpdateTaskContent(db.Coordinator).ExecuteAsync(new UpdateTaskContentCommand(
            OperationId.New(), fixture.Unlinked, current.Revision, Draft("Edited in the app", "Core")), Ct));
        var before = await db.SnapshotAsync();

        var applied = await Transfers.Importer(db).ApplyAsync(plan, OperationId.New(), Ct);

        applied.Error.Should().Be(new ImportPlanStaleError("task"));
        var after = await db.SnapshotAsync();
        after.CommitSequence.Should().Be(before.CommitSequence);
        after.Tasks.Single(t => t.Id == fixture.Unlinked).OwnTitle!.Value.Should().Be("Edited in the app");
        after.Tasks.Single(t => t.Id == fixture.Archived).Service.Should().BeNull("the other planned update was not applied either");
    }

    [Fact]
    [Trait("Req", "AC-13")]
    public async Task A_cancelled_apply_writes_nothing()
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var path = _folder.File("full.xlsx");
        await Transfers.ExportAsync(source, path);
        await using var target = await CreateAsync();
        var plan = await Transfers.PreviewAsync(target, path);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var applied = await Transfers.Importer(target).ApplyAsync(plan, OperationId.New(), cancellation.Token);

        applied.Error.Should().Be(OperationCancelledError.Instance);
        (await target.SnapshotAsync()).Tasks.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "B-04")]
    public async Task Imported_statuses_and_dates_are_kept_as_supplied_and_no_automatic_today_dates_are_applied()
    {
        await using var db = await CreateAsync();
        var inProgress = await db.TaskStatusAsync("In Progress");
        inProgress.SetsStartDate.Should().BeTrue();
        var task = Guid.NewGuid();
        var dated = Guid.NewGuid();
        var path = _folder.Write("dates.xlsx", TestWorkbook.Create(
        [
            TestWorkbook.Metadata(),
            TestWorkbook.Sheet(Tasks, TestWorkbook.Header("Id", "OwnTitle", "StatusId", "StartDate"),
                TestWorkbook.Row(TestWorkbook.Text(task.ToString()), TestWorkbook.Text("No dates"), TestWorkbook.Text(inProgress.Id.Value.ToString())),
                TestWorkbook.Row(TestWorkbook.Text(dated.ToString()), TestWorkbook.Text("Historical"), TestWorkbook.Text(inProgress.Id.Value.ToString()), TestWorkbook.Text("2019-02-03"))),
        ]));

        await Transfers.ApplyAsync(db, await Transfers.PreviewAsync(db, path));

        var tasks = (await db.SnapshotAsync()).Tasks.ToDictionary(t => t.Id.Value);
        (tasks[task].StatusId, tasks[task].Dates.Start, tasks[task].Dates.End).Should().Be((inProgress.Id, null, null));
        tasks[dated].Dates.Start.Should().Be(new DateOnly(2019, 2, 3));
    }

    [Fact]
    [Trait("Req", "B-04")]
    public async Task A_done_review_without_its_completion_date_is_an_error_instead_of_being_stamped_today()
    {
        await using var db = await CreateAsync();
        var id = Guid.NewGuid();
        var path = _folder.Write("review.xlsx", TestWorkbook.Create(
        [
            TestWorkbook.Metadata(),
            TestWorkbook.Sheet("Reviews", TestWorkbook.Header("Id", "Title", "IsDone"), TestWorkbook.Row(TestWorkbook.Text(id.ToString()), TestWorkbook.Text("Done"), TestWorkbook.Bool(true))),
        ]));

        var plan = await Transfers.PreviewAsync(db, path);

        plan.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.RecordKey == id.ToString() && f.Field == "CompletedOn");
    }

    [Fact]
    [Trait("Req", "B-05")]
    public async Task A_deleted_task_is_not_recreated_by_reimporting_an_older_export()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        var archived = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.Archived);
        Succeeded(await new DeleteTask(db.Coordinator).ExecuteAsync(new DeleteTaskCommand(OperationId.New(), fixture.Archived, archived.Revision), Ct));

        var plan = await PreviewAsync(db, workbook);

        plan.Findings.Should().Contain(new ImportFinding(ImportSeverity.Conflict, ImportEntity.Task, fixture.Archived.Value.ToString(), ImportFindingCodes.Deleted));
        plan.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_checklist_items_sheet_replaces_the_checklist_of_a_merged_task()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var workbook = await ExportAsync(db);
        var items = workbook.Rows("Checklist Items");
        items.RemoveAt(0);
        items[0][workbook.Column("Checklist Items", "Text")] = "Second, renamed";

        await Transfers.ApplyAsync(db, await PreviewAsync(db, workbook, ImportMode.Merge));

        var task = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.LinkedOwnTitle);
        task.ChecklistItems.Select(i => (i.Text.Value, i.IsCompleted)).Should().Equal(("Second, renamed", true));
    }

    private async Task<EditableWorkbook> ExportAsync(TestDataset db)
    {
        var path = _folder.File(Guid.NewGuid().ToString("N") + ".xlsx");
        await Transfers.ExportAsync(db, path);
        return EditableWorkbook.Load(path);
    }

    private async Task<ImportPlan> PreviewAsync(TestDataset db, EditableWorkbook workbook, ImportMode mode = ImportMode.CreateOnly) =>
        await Transfers.PreviewAsync(db, workbook.Save(_folder.File(Guid.NewGuid().ToString("N") + ".xlsx")), mode);

    private static DocumentFormat.OpenXml.Spreadsheet.Row CrRow(Guid id, string number, string title, Guid owner) => TestWorkbook.Row(
        TestWorkbook.Text(id.ToString()), TestWorkbook.Text(number), TestWorkbook.Text(title), TestWorkbook.Text(owner.ToString()));
}
