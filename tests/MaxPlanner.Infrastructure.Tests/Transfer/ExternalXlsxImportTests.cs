using System.Security.Cryptography;
using AwesomeAssertions;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Application.Transfer.External;
using MaxPlanner.Infrastructure.Tests.Persistence;
using MaxPlanner.Infrastructure.Transfer;
using MaxPlanner.Infrastructure.Transfer.External;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;
using static MaxPlanner.Infrastructure.Tests.Transfer.TestWorkbook;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>
/// B-13 import of user-mapped external XLSX tables into real SQLite datasets: the reader, entity detection, column
/// aliases, generated IDs, the import ledger for repeated imports, CR resolution from tasks, and a read-only source.
/// </summary>
[Trait("Req", "B-13")]
public sealed class ExternalXlsxImportTests : IDisposable
{
    private const string Namespace = "jira-export";
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task The_reader_turns_each_worksheet_into_a_table_with_a_suggested_entity_and_row_keys()
    {
        var path = WriteSource(standaloneTitle: "Standalone 007");

        var source = await ReadAsync(path);

        source.Tables.Select(t => (t.Name, t.Kind, t.SuggestedEntity)).Should().Equal(
            ("jira.xlsx › Jira Tasks", ExternalSourceKind.Xlsx, ExternalEntity.Task),
            ("jira.xlsx › Change Requests", ExternalSourceKind.Xlsx, ExternalEntity.ChangeRequest),
            ("jira.xlsx › Code reviews", ExternalSourceKind.Xlsx, ExternalEntity.Review),
            ("jira.xlsx › Sheet4", ExternalSourceKind.Xlsx, null));
        var tasks = source.Tables[0];
        tasks.Columns.Should().Equal("Key", "Summary", "Status", "Assignee", "CR #", "Start", "Labels", "Random", "Service");
        tasks.Rows.Select(r => (r.SourceKey, r.RowNumber)).Should().Equal(("jira.xlsx › Jira Tasks#1", 1), ("jira.xlsx › Jira Tasks#2", 2), ("jira.xlsx › Jira Tasks#3", 3));
        tasks.Rows[0].Values[5]!.Text.Should().Be("2023-03-15", "spreadsheet date cells arrive as ISO text");
        tasks.Rows[2].Values[1]!.Text.Should().Be("Standalone 007");
        tasks.Rows[2].Values.Skip(3).Should().OnlyContain(v => v == null, "missing trailing cells are empty");
    }

    [Theory]
    [InlineData("Jira Tasks", ExternalEntity.Task)]
    [InlineData("Change Requests", ExternalEntity.ChangeRequest)]
    [InlineData("CRs", ExternalEntity.ChangeRequest)]
    [InlineData("Code reviews", ExternalEntity.Review)]
    [InlineData("My To-dos", ExternalEntity.GeneralTodo)]
    [InlineData("todo list", ExternalEntity.GeneralTodo)]
    [InlineData("Sheet1", null)]
    public void A_sheet_name_suggests_the_entity_the_user_then_confirms(string name, ExternalEntity? expected) =>
        ExternalSourceReader.Suggest(name).Should().Be(expected);

    [Fact]
    public async Task An_external_workbook_with_a_formula_is_rejected_with_its_location()
    {
        var path = _folder.Write("formula.xlsx", Create([Sheet("Tasks", Header("Title"), Row(Formula("A1&\"x\"", "x").At("A2")))]));

        var result = await TransferAdapters.CreateExternalReader().ReadAsync([path], null, TestContext.Current.CancellationToken);

        result.Error.Should().Be(new TransferFileError(TransferFailure.FormulaRejected, "Tasks!A2"));
    }

    [Fact]
    public async Task Mapped_tables_import_with_generated_ids_proposed_definitions_and_one_cr_for_its_tasks()
    {
        await using var db = await CreateAsync();
        var alice = db.AddPerson("Alice");
        var path = WriteSource(standaloneTitle: "Standalone 007");

        var (draft, plan) = await PreviewAsync(db, path, ImportMode.CreateOnly);
        await Transfers.ApplyAsync(db, plan);

        draft.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error);
        draft.Findings.Should().Contain(new ImportFinding(ImportSeverity.Info, ImportEntity.File, "jira.xlsx › Jira Tasks", ImportFindingCodes.UnknownColumn, null, "Random"));
        draft.Values.Should().Contain(v => v.SourceName == "Waiting" && v.ExistingId == null, "an unknown status is proposed as a new definition");
        draft.Values.Should().Contain(v => v.SourceName == "Alice" && v.ExistingId == alice.Value, "an existing person matches by normalized name");
        plan.Presence.Should().Be(new ImportPresence(true, false, false, true, true, false, true, false), "the source has no checklists, cards, to-dos, or notes");
        plan.LedgerEntries.Should().HaveCount(draft.SourceKeys.Count);

        var snapshot = await db.SnapshotAsync();
        snapshot.Tasks.Should().HaveCount(3);
        snapshot.Tasks.Select(t => t.Id.Value).Should().OnlyHaveUniqueItems().And.NotContain(Guid.Empty);
        var cr = snapshot.ChangeRequests.Should().ContainSingle().Subject;
        (cr.Number.Display, cr.Title.Value, cr.OwnerId).Should().Be(("CR-500", "Freeze cards", alice));
        (cr.Planned!.Start, cr.Planned.End).Should().Be((new DateOnly(2026, 11, 20), new DateOnly(2026, 12, 1)), "\"Due\" is an alias of the planned end");
        snapshot.Workflow.Squads.Single(s => s.Id == cr.SquadId).Name.Display.Should().Be("Retail Cards");
        snapshot.Links.Should().HaveCount(2).And.OnlyContain(l => l.ChangeRequestId == cr.Id, "several source tasks referring to one CR are all linked to it");
        var linked = snapshot.Tasks.Where(t => t.IsLinked).ToList();
        linked.Select(t => t.Preserved.PreLinkTitle).Should().BeEquivalentTo(["Card freeze UI", "Card freeze API"]);
        linked.Should().OnlyContain(t => t.OwnTitle == null);
        var first = linked.Single(t => t.Preserved.PreLinkTitle == "Card freeze UI");
        first.Dates.Start.Should().Be(new DateOnly(2023, 3, 15));
        snapshot.Workflow.People.Single(p => p.Id == first.DeveloperId).Name.Display.Should().Be("Alice");
        snapshot.Workflow.Tags.Select(t => t.Name.Display).Should().BeEquivalentTo(["mobile", "web"]);
        first.Preserved.LegacyJson.Should().Be("""{"Random":"x"}""", "an unmapped column is preserved, not dropped (T-05)");
        var standalone = snapshot.Tasks.Single(t => !t.IsLinked);
        standalone.OwnTitle!.Value.Should().Be("Standalone 007");
        snapshot.Workflow.TaskStatuses.Single(s => s.Id == standalone.StatusId).Name.Display.Should().Be("Waiting");
        var review = snapshot.ReviewItems.Should().ContainSingle().Subject;
        (review.Content.Title.Value, review.Content.CrNumberText?.Value, review.IsDone, review.CompletedOn).Should().Be(("Check freeze", "CR-500", true, new DateOnly(2026, 10, 20)));
        snapshot.Workflow.People.Select(p => p.Name.Display).Should().BeEquivalentTo(["Alice", "Dana"]);
    }

    [Fact]
    public async Task Repeating_an_import_with_the_same_namespace_skips_the_records_instead_of_duplicating_them()
    {
        await using var db = await CreateAsync();
        db.AddPerson("Alice");
        var path = WriteSource(standaloneTitle: "Standalone 007");
        var (firstDraft, first) = await PreviewAsync(db, path, ImportMode.CreateOnly);
        await Transfers.ApplyAsync(db, first);

        var (againDraft, again) = await PreviewAsync(db, path, ImportMode.CreateOnly);

        againDraft.SourceKeys.Keys.Should().BeEquivalentTo(firstDraft.SourceKeys.Keys, "the ledger gives each source record the ID it got the first time");
        again.Counts[ImportEntity.Task].Should().Be(new ImportEntityCounts(0, 0, 3, 0, 0));
        again.Findings.Where(f => f.Entity == ImportEntity.Task).Should().OnlyContain(f => f.Code == ImportFindingCodes.AlreadyImported);
        again.CanApply.Should().BeFalse();
        (await db.SnapshotAsync()).Tasks.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_repeated_merge_import_updates_the_changed_record_and_skips_the_rest()
    {
        await using var db = await CreateAsync();
        db.AddPerson("Alice");
        await Transfers.ApplyAsync(db, (await PreviewAsync(db, WriteSource(standaloneTitle: "Standalone 007"), ImportMode.CreateOnly)).Plan);
        var changed = WriteSource(standaloneTitle: "Standalone, renamed");

        var (_, plan) = await PreviewAsync(db, changed, ImportMode.Merge);
        await Transfers.ApplyAsync(db, plan);

        plan.Counts[ImportEntity.Task].Should().Be(new ImportEntityCounts(0, 1, 2, 0, 0));
        var snapshot = await db.SnapshotAsync();
        snapshot.Tasks.Should().HaveCount(3);
        snapshot.Tasks.Single(t => !t.IsLinked).OwnTitle!.Value.Should().Be("Standalone, renamed");
        (snapshot.ChangeRequests.Count, snapshot.ReviewItems.Count).Should().Be((1, 1), "rows of the same file and namespace are matched through the ledger");
    }

    [Fact]
    public async Task A_task_referring_to_several_crs_needs_a_choice_and_keeps_the_other_references()
    {
        await using var db = await CreateAsync();
        db.AddPerson("Alice");
        var path = _folder.Write("multi.xlsx", Create(
        [
            Sheet("Tasks", Header("Key", "Title", "CR", "Owner"), Row(Text("T-1"), Text("Shared work"), Text("CR-1; CR-2"), Text("Alice"))),
        ]));
        var source = await ReadAsync(path);

        var unresolved = await BuildAsync(db, source, ImportMode.CreateOnly, new Dictionary<string, string?>());
        var chosen = await BuildAsync(db, source, ImportMode.CreateOnly, new Dictionary<string, string?> { ["T-1"] = "cr-2" });

        unresolved.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.Code == ImportFindingCodes.MultipleCrReferences);
        unresolved.CrChoices.Should().ContainSingle().Which.Numbers.Should().Equal("CR-1", "CR-2");
        chosen.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error);
        var task = chosen.Document.Tasks.Should().ContainSingle().Subject;
        task.Preserved!.UnselectedCrReferences.Should().Equal("CR-1");
        chosen.Document.Links.Should().ContainSingle().Which.ChangeRequestId.Should().Be(chosen.Document.ChangeRequests.Single(c => c.Number == "CR-2").Id);
    }

    [Fact]
    public async Task An_external_cr_table_without_a_status_column_previews_with_the_default_cr_status()
    {
        await using var db = await CreateAsync();
        db.AddPerson("Alice");
        var path = _folder.Write("crs.xlsx", Create([Sheet("Change Requests", Header("Number", "Title", "Owner"), Row(Text("CR-8"), Text("Plain"), Text("Alice")))]));

        var (_, plan) = await PreviewAsync(db, path, ImportMode.CreateOnly);

        plan.ChangeRequests.Should().ContainSingle().Which.Record.StatusId.Should().Be((await db.SnapshotAsync()).Workflow.Defaults.DefaultCrStatusId);
    }

    [Fact]
    public async Task Duplicate_cr_numbers_and_invalid_dates_in_the_source_are_reported()
    {
        await using var db = await CreateAsync();
        db.AddPerson("Alice");
        var path = _folder.Write("bad.xlsx", Create(
        [
            Sheet("Change Requests", Header("Number", "Title", "Owner", "Planned start"),
                Row(Text("CR-7"), Text("One"), Text("Alice"), Text("01/02/2026")),
                Row(Text("cr-7"), Text("Two"), Text("Alice"))),
        ]));

        var draft = await BuildAsync(db, await ReadAsync(path), ImportMode.CreateOnly, new Dictionary<string, string?>());

        draft.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.Code == ImportFindingCodes.DuplicateCrNumber && f.Detail == "cr-7");
        draft.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.Code == ImportFindingCodes.InvalidDate && f.Field == nameof(ExternalField.PlannedStart));
    }

    [Fact]
    public async Task The_source_file_is_left_byte_for_byte_unchanged_by_reading_previewing_and_applying()
    {
        await using var db = await CreateAsync();
        db.AddPerson("Alice");
        var path = WriteSource(standaloneTitle: "Standalone 007");
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        var written = File.GetLastWriteTimeUtc(path);

        await using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Another program holding the file open for reading does not stop the import from reading it.
            var (_, plan) = await PreviewAsync(db, path, ImportMode.CreateOnly);
            await Transfers.ApplyAsync(db, plan);
        }

        SHA256.HashData(File.ReadAllBytes(path)).Should().Equal(hash);
        File.GetLastWriteTimeUtc(path).Should().Be(written);
        Directory.GetFiles(_folder.Path).Should().ContainSingle("nothing is written next to the source");
    }

    private string WriteSource(string standaloneTitle) => _folder.Write("jira.xlsx", Create(
        [
            Sheet("Jira Tasks",
                Header("Key", "Summary", "Status", "Assignee", "CR #", "Start", "Labels", "Random", "Service"),
                Row(Text("MP-1"), Text("Card freeze UI"), Text("In Progress"), Text("Alice"), Text("CR-500"), Number("45000", 1), Text("mobile, web"), Text("x"), Text("Cards UI")),
                Row(Text("MP-2"), Text("Card freeze API"), Text("Backlog"), Text("Dana"), Text("cr-500"), Text(""), Text(""), Text(""), Text("Cards API")),
                Row(Text("MP-3"), Text(standaloneTitle), Text("Waiting"))),
            Sheet("Change Requests",
                Header("Number", "Title", "Owner", "Squad", "State", "Planned start", "Due"),
                Row(Text("CR-500"), Text("Freeze cards"), Text("Alice"), Text("Retail Cards"), Text("In Progress"), Text("2026-11-20"), Text("2026-12-01"))),
            Sheet("Code reviews",
                Header("Title", "CR", "Developer", "Done", "Completed on"),
                Row(Text("Check freeze"), Text("CR-500"), Text("Dana"), Text("yes"), Number("46315", 1))),
            Sheet("Sheet4", Header("Anything"), Row(Text("ignored"))),
        ],
        stylesheet: Styles(14)));

    private static async Task<ExternalSource> ReadAsync(string path)
    {
        var result = await TransferAdapters.CreateExternalReader().ReadAsync([path], null, TestContext.Current.CancellationToken);
        result.Error.Should().BeNull();
        return result.Value;
    }

    /// <summary>The wizard's default mapping: each table's suggested entity and the alias proposal for its columns.</summary>
    private static async Task<ExternalImportDraft> BuildAsync(TestDataset db, ExternalSource source, ImportMode mode, IReadOnlyDictionary<string, string?> crChoices)
    {
        var snapshot = await db.SnapshotAsync();
        var ledger = await db.Dataset.ImportState.LoadLedgerAsync(Namespace, Ct);
        ledger.Error.Should().BeNull();
        var options = new ExternalImportOptions
        {
            SourceNamespace = Namespace,
            Mode = mode,
            Tables = [.. source.Tables.Select(t =>
            {
                var entity = t.SuggestedEntity ?? ExternalEntity.Ignore;
                return new ExternalTableMapping(t.Name, entity, [.. ExternalFieldAliases.Propose(entity, t.Columns).Select(p => p.Field)]);
            })],
            CrReferenceChoices = crChoices,
        };
        return ExternalImportPlanner.Build(source, options, snapshot.Workflow, snapshot.ChangeRequests.ToDictionary(c => c.Number.Normalized, c => c.Id.Value), ledger.Value);
    }

    private static async Task<(ExternalImportDraft Draft, ImportPlan Plan)> PreviewAsync(TestDataset db, string path, ImportMode mode)
    {
        var source = await ReadAsync(path);
        var draft = await BuildAsync(db, source, mode, new Dictionary<string, string?>());
        var result = await Transfers.Importer(db).PreviewAsync(draft.Document, mode, Ct, Namespace, draft.SourceKeys);
        result.Error.Should().BeNull();
        return (draft, result.Value);
    }
}
