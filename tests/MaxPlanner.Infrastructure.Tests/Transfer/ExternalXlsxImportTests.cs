using System.Security.Cryptography;
using DocumentFormat.OpenXml.Spreadsheet;
using AwesomeAssertions;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Application.Transfer.External;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
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

    [Fact]
    [Trait("Adr", "0027")]
    public async Task Created_checklist_custom_field_and_unmapped_columns_import_into_a_dataset_and_merge_again()
    {
        await using var db = await CreateAsync();
        var points = db.AddField("points", FieldKind.Number);
        var first = await ReadAsync(WriteTracker(
            Row(Text("T-1"), Text("Card freeze"), Number("45000.5", 1), Text("[ ] Draft\r\n- [x] Review\r\n\r\nPlain step"), Number("5"), Text("High"), Text("2026-12-01"), Text("x1"), Text("hidden-1")),
            Row(Text("T-2"), Text("Card unfreeze"), Text("2025-06-01T08:30:00Z"), Text("One"), Text(""), Text("low"), Text(""), Text(""), Text("hidden-2")),
            Row(Text("T-3"), Text("Bad dates"), Text("2099-01-01"), Text(""), Text("many"), Text("Low"), Text("tomorrow"), Text(""), Text(""))));
        first.Tables[0].Rows[0].Values[2]!.Text.Should().Be("2023-03-15T12:00:00", "an external date-time cell arrives as local ISO text");

        var invalid = await BuildMappedAsync(db, first, ImportMode.CreateOnly, ExternalCustomFieldChoice.New("Risk", FieldKind.Lookup));
        invalid.Findings.Where(f => f.Severity == ImportSeverity.Error).Select(f => (f.RecordKey, f.Code, f.Field)).Should().BeEquivalentTo(
        [
            ("tracker.xlsx › Tasks#3", ImportFindingCodes.CreatedInFuture, "Created"),
            ("tracker.xlsx › Tasks#3", ImportFindingCodes.InvalidCustomValue, "points"),
            ("tracker.xlsx › Tasks#3", ImportFindingCodes.InvalidCustomValue, "Due"),
        ]);

        // Without the invalid row, the import applies: the new fields are created in the same transaction as the tasks.
        var source = first with { Tables = [first.Tables[0] with { Rows = [.. first.Tables[0].Rows.Take(2)] }] };
        var draft = await BuildMappedAsync(db, source, ImportMode.CreateOnly, ExternalCustomFieldChoice.New("Risk", FieldKind.Lookup));
        var plan = await PreviewDraftAsync(db, draft, ImportMode.CreateOnly);
        draft.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error);
        plan.Findings.Where(f => f.Code == ImportFindingCodes.FieldCreated).Select(f => f.Detail).Should().BeEquivalentTo(["risk", "due"]);
        await Transfers.ApplyAsync(db, plan);

        var snapshot = await db.SnapshotAsync();
        var fields = snapshot.Workflow.Fields.ToDictionary(f => f.Key.Display);
        (fields["risk"].Kind, fields["risk"].IsRequired, fields["due"].Kind).Should().Be((FieldKind.Lookup, false, FieldKind.Date));
        fields["risk"].Options.Select(o => o.Label.Display).Should().Equal("High", "low");
        var t1 = snapshot.Tasks.Single(t => t.OwnTitle!.Value == "Card freeze");
        var t2 = snapshot.Tasks.Single(t => t.OwnTitle!.Value == "Card unfreeze");
        (t1.CreatedAtUtc, t1.UpdatedAtUtc).Should().Be((new DateTimeOffset(2023, 3, 15, 12, 0, 0, TimeSpan.Zero), db.Clock.UtcNow));
        t2.CreatedAtUtc.Should().Be(new DateTimeOffset(2025, 6, 1, 8, 30, 0, TimeSpan.Zero));
        t1.ChecklistItems.Select(i => (i.Text.Value, i.IsCompleted)).Should().Equal(("Draft", false), ("Review", true), ("Plain step", false));
        t1.CustomValues.Should().BeEquivalentTo(new Dictionary<FieldId, CustomValue>
        {
            [points] = new NumberCustomValue(5m),
            [fields["risk"].Id] = new LookupCustomValue(fields["risk"].Options[0].Id),
            [fields["due"].Id] = new DateCustomValue(new DateOnly(2026, 12, 1)),
        });
        t1.Preserved.LegacyJson.Should().Be("""{"Extra":"x1"}""", "a column kept hidden is preserved (T-05)");
        t2.Preserved.LegacyJson.Should().BeNull();
        snapshot.Tasks.Should().NotContain(t => (t.Preserved.LegacyJson ?? string.Empty).Contains("hidden", StringComparison.Ordinal), "a discarded column is stored nowhere");
        db.Scalar("SELECT COUNT(*) FROM tasks WHERE preserved_json LIKE '%hidden%';").Should().Be(0);

        // Merge again: Risk now exists, so it is mapped instead of created; existing records keep their created time.
        var again = await ReadAsync(WriteTracker(
            Row(Text("T-1"), Text("Card freeze"), Text("2020-01-01"), Text("[x] Ship"), Text(""), Text("low"), Text("2026-12-01"), Text("x1"), Text("hidden-1")),
            Row(Text("T-2"), Text("Card unfreeze"), Text("2025-06-01T08:30:00Z"), Text(""), Text("2"), Text("low"), Text(""), Text(""), Text("hidden-2"))));
        db.Clock.UtcNow = db.Clock.UtcNow.AddHours(1);
        var merge = await BuildMappedAsync(db, again, ImportMode.Merge, ExternalCustomFieldChoice.Existing(fields["risk"].Id.Value), ExternalCustomFieldChoice.Existing(fields["due"].Id.Value));
        var mergePlan = await PreviewDraftAsync(db, merge, ImportMode.Merge);
        merge.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error);
        mergePlan.Counts[ImportEntity.Task].Should().Be(new ImportEntityCounts(0, 2, 0, 0, 0));
        await Transfers.ApplyAsync(db, mergePlan);

        var merged = await db.SnapshotAsync();
        var m1 = merged.Tasks.Single(t => t.Id == t1.Id);
        var m2 = merged.Tasks.Single(t => t.Id == t2.Id);
        m1.CreatedAtUtc.Should().Be(t1.CreatedAtUtc, "T-10: an existing record keeps its own created time");
        m1.ChecklistItems.Select(i => (i.Text.Value, i.IsCompleted)).Should().Equal(("Ship", true));
        m2.ChecklistItems.Should().BeEmpty("an empty checklist cell clears the checklist");
        m1.CustomValues.Should().BeEquivalentTo(new Dictionary<FieldId, CustomValue>
        {
            [fields["risk"].Id] = new LookupCustomValue(fields["risk"].Options[1].Id),
            [fields["due"].Id] = new DateCustomValue(new DateOnly(2026, 12, 1)),
        }, "an empty cell clears its field");
        m2.CustomValues[points].Should().Be(new NumberCustomValue(2m));
        merged.Workflow.Fields.Should().HaveCount(3, "no field is created twice");
    }

    [Fact]
    [Trait("Adr", "0027")]
    public async Task A_new_field_whose_proposed_key_is_taken_gets_a_generated_key_and_keeps_its_label()
    {
        await using var db = await CreateAsync();
        db.AddField("points", FieldKind.Number);
        db.AddField("risk", FieldKind.Text);
        var source = await ReadAsync(WriteTracker(Row(Text("T-1"), Text("Card freeze"), Text(""), Text(""), Text(""), Text("High"))));

        var draft = await BuildMappedAsync(db, source, ImportMode.CreateOnly, ExternalCustomFieldChoice.New("Risk", FieldKind.Lookup));
        var plan = await PreviewDraftAsync(db, draft, ImportMode.CreateOnly);
        await Transfers.ApplyAsync(db, plan);

        draft.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error);
        var fields = (await db.SnapshotAsync()).Workflow.Fields;
        fields.Select(f => (f.Key.Display, f.Label.Display, f.Kind)).Should().Contain([("risk", "risk", FieldKind.Text), ("field", "Risk", FieldKind.Lookup), ("due", "Due", FieldKind.Date)]);
    }

    [Fact]
    [Trait("Adr", "0027")]
    public async Task A_json_checklist_array_with_non_text_entries_is_flagged_by_the_reader_and_reported()
    {
        await using var db = await CreateAsync();
        var path = _folder.File("tracker.json");
        await File.WriteAllTextAsync(path, """{ "tasks": [ { "id": "T-1", "title": "One", "subtasks": ["Draft", { "text": "Review" }] } ] }""", Ct);
        var source = await ReadAsync(path);
        var table = source.Tables.Single();
        var subtasks = table.Rows.Single().Values[table.Columns.ToList().IndexOf("subtasks")]!;
        subtasks.Should().Be(new ExternalValue(null, subtasks.Items, HasNonTextItems: true));
        subtasks.Items.Should().Equal("Draft");

        var snapshot = await db.SnapshotAsync();
        var options = new ExternalImportOptions
        {
            SourceNamespace = Namespace,
            Tables = [new ExternalTableMapping(table.Name, ExternalEntity.Task, [.. ExternalFieldAliases.Propose(ExternalEntity.Task, table.Columns, table.Kind).Select(p => p.Field)])],
        };
        var draft = ExternalImportPlanner.Build(source, options, snapshot.Workflow, new Dictionary<string, Guid>(), new Dictionary<(string, string), Guid>());

        draft.Findings.Should().ContainSingle(f => f.Severity == ImportSeverity.Error).Which.Code.Should().Be(ImportFindingCodes.InvalidChecklist);
        draft.Document.Tasks.Single().ChecklistItems.Should().BeNull("an invalid list never becomes a supplied, empty checklist");
    }

    private string WriteTracker(params Row[] rows) => _folder.Write("tracker.xlsx", Create(
        [Sheet("Tasks", [Header("Key", "Title", "createdAt", "subtasks", "Points", "Risk", "Due", "Extra", "Secret"), .. rows])],
        stylesheet: Styles(22)));

    /// <summary>
    /// The tracker mapping: aliases for Key, Title, createdAt and subtasks; Points to the existing <c>points</c> field;
    /// Risk and Due as given (Due is a new Date field unless <paramref name="due"/> names one); Extra kept; Secret discarded.
    /// </summary>
    private static async Task<ExternalImportDraft> BuildMappedAsync(TestDataset db, ExternalSource source, ImportMode mode, ExternalCustomFieldChoice risk, ExternalCustomFieldChoice? due = null)
    {
        var snapshot = await db.SnapshotAsync();
        var ledger = await db.Dataset.ImportState.LoadLedgerAsync(Namespace, Ct);
        ledger.Error.Should().BeNull();
        var table = source.Tables[0];
        var fields = ExternalFieldAliases.Propose(ExternalEntity.Task, table.Columns).Select(p => p.Field).ToArray();
        fields[..4].Should().Equal(ExternalField.SourceId, ExternalField.Title, ExternalField.Created, ExternalField.ChecklistItems);
        fields[4] = fields[5] = fields[6] = ExternalField.CustomField;
        var options = new ExternalImportOptions
        {
            SourceNamespace = Namespace,
            Mode = mode,
            NowUtc = db.Clock.UtcNow,
            TimeZone = TimeZoneInfo.Utc,
            Tables =
            [
                new ExternalTableMapping(table.Name, ExternalEntity.Task, fields)
                {
                    CustomFields = new Dictionary<int, ExternalCustomFieldChoice>
                    {
                        [4] = ExternalCustomFieldChoice.Existing(snapshot.Workflow.Fields.Single(f => f.Key.Display == "points").Id.Value),
                        [5] = risk,
                        [6] = due ?? ExternalCustomFieldChoice.New("Due", FieldKind.Date),
                    },
                    DiscardedColumns = new HashSet<int> { 8 },
                },
            ],
        };
        return ExternalImportPlanner.Build(source, options, snapshot.Workflow, snapshot.ChangeRequests.ToDictionary(c => c.Number.Normalized, c => c.Id.Value), ledger.Value);
    }

    private static async Task<ImportPlan> PreviewDraftAsync(TestDataset db, ExternalImportDraft draft, ImportMode mode)
    {
        var result = await Transfers.Importer(db).PreviewAsync(draft.Document, mode, Ct, Namespace, draft.SourceKeys);
        result.Error.Should().BeNull();
        return result.Value;
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
