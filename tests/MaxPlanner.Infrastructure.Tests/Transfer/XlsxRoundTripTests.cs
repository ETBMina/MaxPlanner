using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Infrastructure.Tests.Persistence;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>
/// B-12 / AC-12 over XLSX with real SQLite files: an entire-dataset export imported into a new empty dataset reproduces
/// the business content and relationships; a view export includes what it needs and says it is not a backup.
/// </summary>
[Trait("Req", "B-12")]
[Trait("Req", "AC-12")]
public sealed class XlsxRoundTripTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task A_full_xlsx_export_imported_into_an_empty_dataset_reproduces_business_content_and_relationships()
    {
        await using var source = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(source);
        var path = _folder.File("full.xlsx");
        await Transfers.ExportAsync(source, path);
        await using var target = await CreateAsync();

        var plan = await Transfers.PreviewAsync(target, path);
        plan.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error || f.Severity == ImportSeverity.Conflict);
        plan.IsTargetEmpty.Should().BeTrue();
        await Transfers.ApplyAsync(target, plan);

        var expected = await source.SnapshotAsync();
        var actual = await target.SnapshotAsync();
        TransferFixture.Shape(actual).Should().BeEquivalentTo(TransferFixture.Shape(expected), o => o.WithStrictOrdering());
        var linked = actual.Tasks.Single(t => t.Id == fixture.LinkedOwnTitle);
        (linked.OwnTitle, linked.Preserved.PreLinkTitle).Should().Be((null, "Own title"));
        actual.Tasks.Single(t => t.Id == fixture.Archived).IsArchived.Should().BeTrue("entire dataset includes archived tasks");
        actual.Tasks.Single(t => t.Id == fixture.Unlinked).Preserved.LegacyJson.Should().Be("""{"jira":{"key":"MP-77"}}""");
        actual.ChangeRequests.Single(c => c.Id == fixture.Cr).TaskNumber!.Value.Should().Be("0042");
        actual.Links.Count(l => l.ChangeRequestId == fixture.Cr).Should().Be(2, "many tasks may share one CR");
        linked.Notes!.Value.Should().Be(TransferFixture.StoredNotes, "notes typed with CR and CR LF are stored, exported, and imported with LF");
    }

    [Fact]
    public async Task The_import_preview_counts_every_record_as_a_create_and_writes_nothing()
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var path = _folder.File("full.xlsx");
        var report = await Transfers.ExportAsync(source, path);
        await using var target = await CreateAsync();
        var before = await target.SnapshotAsync();

        var plan = await Transfers.PreviewAsync(target, path);

        plan.Counts[ImportEntity.Task].Should().Be(new ImportEntityCounts(report.Counts[ImportEntity.Task], 0, 0, 0, 0));
        plan.Counts[ImportEntity.ChangeRequest].Create.Should().Be(2);
        plan.Counts[ImportEntity.SubtaskCard].Create.Should().Be(1);
        plan.Counts[ImportEntity.Review].Create.Should().Be(2);
        plan.Counts[ImportEntity.GeneralTodo].Create.Should().Be(2);
        plan.Links.Should().HaveCount(report.Counts[ImportEntity.Link]);
        plan.Presence.Should().Be(new ImportPresence(true, true, true, true, true, true, true, true));
        var after = await target.SnapshotAsync();
        (after.CommitSequence, after.Tasks.Count).Should().Be((before.CommitSequence, 0), "a preview never writes (T-10)");
    }

    [Fact]
    public async Task An_entire_dataset_export_reports_its_counts_and_that_a_spreadsheet_is_not_a_backup()
    {
        await using var db = await CreateAsync();
        await TransferFixture.PopulateAsync(db);

        var report = await Transfers.ExportAsync(db, _folder.File("full.xlsx"));

        report.IsViewExport.Should().BeFalse();
        report.Format.Should().Be(TransferFileFormat.Xlsx);
        report.Limitations.Should().Equal(ExportLimitationCodes.SpreadsheetNotBackup);
        report.Counts.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [ImportEntity.Task] = 5,
            [ImportEntity.SubtaskCard] = 1,
            [ImportEntity.ChangeRequest] = 2,
            [ImportEntity.Link] = 3,
            [ImportEntity.Review] = 2,
            [ImportEntity.GeneralTodo] = 2,
        });
        report.Path.Should().Be(_folder.File("full.xlsx"));
        Directory.GetFiles(_folder.Path).Should().ContainSingle("the temporary file was moved over the destination");
    }

    [Fact]
    [Trait("Req", "T-09")]
    public async Task A_view_export_includes_the_parents_and_crs_it_needs_and_states_its_scope_and_limitations()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var path = _folder.File("view.xlsx");
        var scope = new ViewExportScope("tasks", "Status: Blocked", new HashSet<Guid> { fixture.LinkedAtCreation.Value }, new HashSet<Guid> { fixture.Card.Value });

        var report = await Transfers.ExportAsync(db, path, scope);

        report.IsViewExport.Should().BeTrue();
        report.Limitations.Should().BeEquivalentTo([ExportLimitationCodes.DataOnly, ExportLimitationCodes.ViewIsNotBackup, ExportLimitationCodes.ParentsIncluded, ExportLimitationCodes.SpreadsheetNotBackup]);
        var document = await Transfers.ReadAsync(path);
        document.Scope.Should().Be(new TransferScope(TransferFormat.ScopeCurrentView, "tasks", "Status: Blocked"));
        document.Tasks.Select(t => t.Id).Should().BeEquivalentTo([fixture.LinkedAtCreation.Value, fixture.Unlinked.Value], "the card's parent is included");
        document.SubtaskCards.Should().ContainSingle().Which.Id.Should().Be(fixture.Card.Value);
        document.ChangeRequests.Should().ContainSingle().Which.Id.Should().Be(fixture.Cr.Value, "the CR of an exported task is included");
        document.Links.Should().ContainSingle().Which.Should().Match<TransferLink>(l => l.TaskId == fixture.LinkedAtCreation.Value && l.ChangeRequestId == fixture.Cr.Value);
        (document.Reviews.Count, document.GeneralTodos.Count).Should().Be((0, 0));
        // ADR 0026: a view export is data only, with the configuration its records use and no defaults.
        document.Lookups.People.Select(p => p.Id).Should().Equal([fixture.Alice.Value], "only the owner of the exported CR is used");
        document.FieldDefinitions.Should().BeEmpty("no exported record has a custom value");
        document.Defaults.Should().BeNull();
    }

    [Fact]
    public async Task A_view_export_imports_into_an_empty_dataset_with_its_relationships()
    {
        await using var source = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(source);
        var path = _folder.File("view.xlsx");
        await Transfers.ExportAsync(source, path, new ViewExportScope("tasks", "Linked", new HashSet<Guid> { fixture.LinkedOwnTitle.Value }, new HashSet<Guid>()));
        await using var target = await CreateAsync();

        var plan = await Transfers.PreviewAsync(target, path);
        await Transfers.ApplyAsync(target, plan);

        var snapshot = await target.SnapshotAsync();
        snapshot.Tasks.Should().ContainSingle().Which.ChangeRequestId.Should().Be(fixture.Cr);
        snapshot.ChangeRequests.Should().ContainSingle().Which.OwnerId.Should().Be(fixture.Alice);
        snapshot.Tasks[0].ChecklistItems.Select(i => i.Text.Value).Should().Equal("First", "Second; with 007");
    }

    [Fact]
    [Trait("Req", "T-10")]
    public async Task Notes_typed_with_carriage_returns_are_stored_with_line_feeds_and_merge_back_as_no_change()
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        await source.AddTaskAsync("Typed in the app");
        var task = (await source.SnapshotAsync()).Tasks.Single(t => t.OwnTitle?.Value == "Typed in the app");
        const string notes = "Typed in a WinUI TextBox\rsecond line\r\nthird line";
        Succeeded(await new UpdateTaskContent(source.Coordinator).ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), task.Id, task.Revision, Draft("Typed in the app", notes: notes)), Ct));
        var path = _folder.File("cr.xlsx");
        await Transfers.ExportAsync(source, path);
        await using var target = await CreateAsync();

        var merge = await Transfers.PreviewAsync(source, path, ImportMode.Merge);
        await Transfers.ApplyAsync(target, await Transfers.PreviewAsync(target, path));

        merge.RecordWriteCount.Should().Be(0, "an unchanged export merged back changes nothing");
        (await source.SnapshotAsync()).Tasks.Single(t => t.Id == task.Id).Notes!.Value.Should().Be("Typed in a WinUI TextBox\nsecond line\nthird line");
        (await target.SnapshotAsync()).Tasks.Single(t => t.Id == task.Id).Notes!.Value.Should().Be("Typed in a WinUI TextBox\nsecond line\nthird line");
        TransferFixture.Shape(await target.SnapshotAsync()).Should().BeEquivalentTo(TransferFixture.Shape(await source.SnapshotAsync()), o => o.WithStrictOrdering());
    }

    [Fact]
    [Trait("Req", "T-10")]
    public async Task Reimporting_an_export_into_its_own_dataset_skips_every_record_and_a_merge_changes_nothing()
    {
        await using var db = await CreateAsync();
        await TransferFixture.PopulateAsync(db);
        var path = _folder.File("full.xlsx");
        await Transfers.ExportAsync(db, path);

        var createOnly = await Transfers.PreviewAsync(db, path);
        var merge = await Transfers.PreviewAsync(db, path, ImportMode.Merge);

        createOnly.Counts.Values.Should().AllSatisfy(c => (c.Create, c.Update, c.Conflict, c.Error).Should().Be((0, 0, 0, 0)));
        createOnly.Findings.Where(f => f.Entity == ImportEntity.Task).Should().AllSatisfy(f => f.Code.Should().Be(ImportFindingCodes.Exists));
        merge.HasErrors.Should().BeFalse();
        merge.RecordWriteCount.Should().Be(0, "an unchanged export merged back is all skips");
        merge.CanApply.Should().BeFalse();
    }
}
