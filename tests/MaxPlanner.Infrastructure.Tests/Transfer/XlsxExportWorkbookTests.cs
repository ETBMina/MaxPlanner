using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using DocumentFormat.OpenXml.Spreadsheet;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Infrastructure.Transfer;
using TableColumn = MaxPlanner.Infrastructure.Transfer.TableColumn;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>
/// B-12 Excel export at the workbook level: sheet names, headers, identifier text cells, native date/number/boolean
/// cells, UTC timestamp text, JSON-cell encodings, and the Excel cell limit (ADR 0012).
/// </summary>
[Trait("Req", "B-12")]
public sealed class XlsxExportWorkbookTests : IDisposable
{
    private static readonly Guid TaskStatus = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid CrStatus = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Priority = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid Squad = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid Alice = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid Mobile = Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly Guid Field = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid LinkedTask = Guid.Parse("80000000-0000-0000-0000-000000000001");
    private static readonly Guid OwnTask = Guid.Parse("80000000-0000-0000-0000-000000000002");
    private static readonly Guid Cr = Guid.Parse("90000000-0000-0000-0000-000000000001");
    private static readonly Guid Item = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Card = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid ReviewId = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Todo = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Created = new(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2));
    private const string Multiline = "Line 1\nLïne 2 — ✓ 🚀\n  indented  ";

    private readonly TempFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task An_export_has_the_six_table_sheets_then_metadata_and_supporting_definition_sheets()
    {
        var path = await WriteAsync(Document());

        TestWorkbook.Cells(path).Select(s => s.Name).Should().Equal(
            "Tasks", "Change Requests", "Checklist Items", "Subtask Cards", "Reviews", "General To-dos", "Metadata", "Lookups", "Fields");
    }

    [Fact]
    public async Task Every_sheet_starts_with_its_documented_header_row()
    {
        var sheets = TestWorkbook.Cells(await WriteAsync(Document())).ToDictionary(s => s.Name, s => s.Rows[0].Select(TestWorkbook.CellText).ToList());

        sheets["Tasks"].Should().Equal(
            "Id", "Title", "OwnTitle", "ChangeRequestId", "CrNumber", "TaskNumber", "Squad", "Owner", "Service", "StatusId", "Status", "PriorityId",
            "Priority", "DesignerId", "Designer", "DeveloperId", "Developer", "TagIds", "Tags", "Notes", "StartDate", "EndDate", "IsArchived", "Order", "CustomValues", "Preserved",
            "CreatedAtUtc", "UpdatedAtUtc", "Revision");
        sheets["Change Requests"].Should().Equal(
            "Id", "Number", "Title", "OwnerId", "Owner", "SquadId", "Squad", "StatusId", "Status", "PriorityId", "Priority", "PlannedStart", "PlannedEnd",
            "EmailSubject", "SolutionDesignUrl", "Description", "TaskNumber", "CustomValues", "Preserved", "CreatedAtUtc", "UpdatedAtUtc", "Revision");
        sheets["Checklist Items"].Should().Equal("ParentTaskId", "Id", "Text", "IsCompleted", "Order");
        sheets["Subtask Cards"].Should().Equal(
            "Id", "ParentTaskId", "Subtitle", "StatusId", "Status", "StartDate", "EndDate", "Notes", "Order", "CreatedAtUtc", "UpdatedAtUtc", "Revision");
        sheets["Reviews"].Should().Equal(
            "Id", "Title", "CrNumberText", "TaskNumberText", "Services", "DeveloperId", "Developer", "Comments", "IsDone", "CompletedOn", "Order",
            "CreatedAtUtc", "UpdatedAtUtc", "Revision");
        sheets["General To-dos"].Should().Equal("Id", "Text", "IsCompleted", "Order", "CreatedAtUtc", "UpdatedAtUtc", "Revision");
        sheets["Metadata"].Should().Equal("Key", "Value");
        sheets["Lookups"].Should().Equal("Kind", "Id", "Name", "Order", "IsCompleted", "SetsStartDate", "IsBacklog");
        sheets["Fields"].Should().Equal("Id", "Target", "Key", "Label", "Kind", "IsRequired", "DefaultValue", "DisplayOrder", "Options", "IsActive");
    }

    [Fact]
    public async Task Identifiers_and_user_text_are_inline_text_cells_that_keep_leading_zeros_and_multiline_unicode()
    {
        var path = await WriteAsync(Document());
        var sheets = TestWorkbook.Cells(path).ToDictionary(s => s.Name, s => s.Rows);
        var task = Row(sheets["Tasks"], OwnTask);
        var cr = Row(sheets["Change Requests"], Cr);

        foreach (var cell in new[] { task["Id"], task["OwnTitle"], task["StatusId"], task["Notes"], cr["Id"], cr["Number"], cr["TaskNumber"] })
        {
            cell.DataType!.Value.Should().Be(CellValues.InlineString, "identifiers and user strings are written as text (T-10)");
            cell.CellFormula.Should().BeNull();
        }

        TestWorkbook.CellText(task["OwnTitle"]).Should().Be("007 Own task");
        TestWorkbook.CellText(task["Notes"]).Should().Be(Multiline);
        TestWorkbook.CellText(cr["Number"]).Should().Be("CR-0105");
        TestWorkbook.CellText(cr["TaskNumber"]).Should().Be("0042");
        var read = await Transfers.ReadAsync(path);
        read.Tasks.Single(t => t.Id == OwnTask).Notes.Should().Be(Multiline);
        read.ChangeRequests.Single().TaskNumber.Should().Be("0042");
    }

    [Fact]
    public async Task Line_feeds_and_surrounding_spaces_survive_an_export_and_read()
    {
        const string notes = "  First line\nSecond line\n\nLast  ";

        var path = await WriteAsync(Document(notes: notes));

        (await Transfers.ReadAsync(path)).Tasks.Single(t => t.Id == OwnTask).Notes.Should().Be(notes);
    }

    [Theory]
    [InlineData("_x0041_", "_x005F_x0041_")]
    [InlineData("a_x000D_b", "a_x005F_x000D_b")]
    [InlineData("_x005F_", "_x005F_x005F_")]
    [InlineData("_xZZZZ_ and _x12_", "_xZZZZ_ and _x12_")]
    [InlineData("plain_x text", "plain_x text")]
    [InlineData("_x0041_x0042_", "_x005F_x0041_x005F_x0042_")]
    [InlineData("_x005F_x0041_", "_x005F_x005F_x005F_x0041_")]
    [InlineData("a_x0041_x0042_x0043_x0044_b", "a_x005F_x0041_x005F_x0042_x005F_x0043_x005F_x0044_b")]
    [InlineData("__x0041__", "__x005F_x0041__")]
    public async Task Text_that_looks_like_an_ooxml_escape_is_written_escaped_and_reads_back_unchanged(string text, string written)
    {
        var path = await WriteAsync(Document(notes: text));

        TestWorkbook.CellText(Row(TestWorkbook.Cells(path).Single(s => s.Name == "Tasks").Rows, OwnTask)["Notes"]).Should().Be(written, "Excel shows the escaped form as the literal text");
        (await Transfers.ReadAsync(path)).Tasks.Single(t => t.Id == OwnTask).Notes.Should().Be(text);
    }

    [Fact]
    public async Task Characters_xml_cannot_hold_are_written_as_escapes_and_control_characters_read_back_exactly()
    {
        var notes = "bell" + (char)0x07 + " unit" + (char)0x1F + " tab\t lf\n emoji " + char.ConvertFromUtf32(0x1F600);

        var path = await WriteAsync(Document(notes: notes));

        TestWorkbook.CellText(Row(TestWorkbook.Cells(path).Single(s => s.Name == "Tasks").Rows, OwnTask)["Notes"])
            .Should().Be("bell_x0007_ unit_x001F_ tab\t lf\n emoji " + char.ConvertFromUtf32(0x1F600), "Excel writes these characters as escapes too");
        (await Transfers.ReadAsync(path)).Tasks.Single(t => t.Id == OwnTask).Notes.Should().Be(notes);
    }

    [Fact]
    public async Task Characters_that_cannot_be_decoded_safely_still_export_and_read_back_as_their_escape_text()
    {
        var notes = "nul" + (char)0 + " lone" + (char)0xD800 + " nonchar" + (char)0xFFFF;

        var path = await WriteAsync(Document(notes: notes));

        (await Transfers.ReadAsync(path)).Tasks.Single(t => t.Id == OwnTask).Notes.Should().Be("nul_x0000_ lone_xD800_ nonchar_xFFFF_",
            "a valid workbook is written; the reader never decodes NUL, U+FFFF, or an unpaired surrogate");
    }

    [Fact]
    public async Task Text_that_looks_like_a_formula_is_exported_as_plain_text()
    {
        var document = Document() with { GeneralTodos = [new TransferGeneralTodo { Id = Todo, Text = "=1+2", IsCompleted = false, Order = 0 }] };

        var path = await WriteAsync(document);

        var cell = Row(TestWorkbook.Cells(path).Single(s => s.Name == "General To-dos").Rows, Todo)["Text"];
        cell.CellFormula.Should().BeNull();
        TestWorkbook.CellText(cell).Should().Be("=1+2");
        (await Transfers.ReadAsync(path)).GeneralTodos.Single().Text.Should().Be("=1+2", "XLSX never neutralizes or evaluates text");
    }

    [Fact]
    public async Task Business_dates_are_native_date_cells_with_an_iso_style()
    {
        var path = await WriteAsync(Document());
        var task = Row(TestWorkbook.Cells(path).Single(s => s.Name == "Tasks").Rows, OwnTask);
        var start = task["StartDate"];

        start.DataType.Should().BeNull("a date is a numeric serial with a date style");
        start.StyleIndex!.Value.Should().NotBe(0u);
        TestWorkbook.FormatCodeOf(path, start).Should().Be("yyyy-mm-dd");
        var serial = int.Parse(start.CellValue!.Text, CultureInfo.InvariantCulture);
        new DateOnly(1899, 12, 30).AddDays(serial).Should().Be(new DateOnly(2026, 10, 1), "the 1900 date system serial");
        task["EndDate"].CellValue.Should().BeNull("an absent end date is an empty cell");
    }

    [Fact]
    public async Task Timestamps_are_utc_iso_text_and_numbers_and_booleans_are_native_cells()
    {
        var path = await WriteAsync(Document());
        var task = Row(TestWorkbook.Cells(path).Single(s => s.Name == "Tasks").Rows, OwnTask);

        task["CreatedAtUtc"].DataType!.Value.Should().Be(CellValues.InlineString);
        TestWorkbook.CellText(task["CreatedAtUtc"]).Should().Be("2026-03-04T03:06:07.0000000+00:00", "timestamps are exported in UTC (B-12)");
        task["IsArchived"].DataType!.Value.Should().Be(CellValues.Boolean);
        task["IsArchived"].CellValue!.Text.Should().Be("1");
        task["Order"].DataType!.Value.Should().Be(CellValues.Number);
        task["Order"].CellValue!.Text.Should().Be("2048");
        task["Revision"].CellValue!.Text.Should().Be("7");
        var read = (await Transfers.ReadAsync(path)).Tasks.Single(t => t.Id == OwnTask);
        (read.IsArchived, read.Order, read.Revision, read.CreatedAtUtc).Should().Be((true, 2048L, 7L, Created));
        read.CreatedAtUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task Collections_custom_values_and_preserved_metadata_use_json_cells()
    {
        var path = await WriteAsync(Document());
        var sheets = TestWorkbook.Cells(path).ToDictionary(s => s.Name, s => s.Rows);
        var task = Row(sheets["Tasks"], OwnTask);

        TestWorkbook.CellText(task["DesignerId"]).Should().Be(Alice.ToString("D"), "a task has one Designer, written as plain ID text (ADR 0028)");
        TestWorkbook.CellText(task["DeveloperId"]).Should().BeNull();
        JsonSerializer.Deserialize<List<Guid>>(TestWorkbook.DecodedText(task["TagIds"])!).Should().Equal(Mobile);
        using (var custom = JsonDocument.Parse(TestWorkbook.DecodedText(task["CustomValues"])!))
        {
            var value = custom.RootElement.GetProperty(Field.ToString("D"));
            value.GetProperty("kind").GetString().Should().Be("Number");
            value.GetProperty("number").GetDecimal().Should().Be(3.25m);
        }

        using (var preserved = JsonDocument.Parse(TestWorkbook.DecodedText(task["Preserved"])!))
        {
            preserved.RootElement.GetProperty("legacyJson").GetString().Should().Be("""{"jira":"MP-7"}""");
        }

        JsonSerializer.Deserialize<List<string>>(TestWorkbook.DecodedText(Row(sheets["Reviews"], ReviewId)["Services"])!).Should().Equal("Cards API", "Mobile, Web");
        var read = await Transfers.ReadAsync(path);
        read.Reviews.Single().Services.Should().Equal("Cards API", "Mobile, Web");
        read.Tasks.Single(t => t.Id == OwnTask).CustomValues.Should().ContainKey(Field.ToString("D")).WhoseValue.Number.Should().Be(3.25m);
    }

    [Fact]
    public async Task A_linked_task_row_carries_the_cr_derived_informational_columns_and_no_own_title()
    {
        var path = await WriteAsync(Document());
        var task = Row(TestWorkbook.Cells(path).Single(s => s.Name == "Tasks").Rows, LinkedTask).ToDictionary(c => c.Key, c => TestWorkbook.CellText(c.Value));

        task["Title"].Should().Be("Instant card freeze", "Title shows the CR's title");
        task["OwnTitle"].Should().BeNull("a linked task has no own title (ADR 0005)");
        task["ChangeRequestId"].Should().Be(Cr.ToString("D"));
        (task["CrNumber"], task["TaskNumber"], task["Squad"], task["Owner"]).Should().Be(("CR-0105", "0042", "Retail Cards", "Alice"));
        (task["Status"], task["Priority"]).Should().Be(("In Progress", "High"), "labels accompany the IDs");
    }

    [Fact]
    [Trait("Req", "B-02")]
    public async Task A_task_row_names_its_designer_developer_and_tags_beside_the_ids()
    {
        var bob = Guid.Parse("50000000-0000-0000-0000-000000000002");
        var web = Guid.Parse("60000000-0000-0000-0000-000000000002");
        var document = Document();
        document = document with
        {
            Lookups = document.Lookups with
            {
                People = [.. document.Lookups.People, new TransferNamedLookup(bob, "Bob")],
                Tags = [.. document.Lookups.Tags, new TransferNamedLookup(web, "Web")],
            },
            Tasks = [.. document.Tasks.Select(t => t.Id == OwnTask ? t with { DeveloperId = bob, TagIds = [web, Guid.NewGuid(), Mobile] } : t)],
        };

        var path = await WriteAsync(document);
        var rows = TestWorkbook.Cells(path).Single(s => s.Name == "Tasks").Rows;
        var own = Row(rows, OwnTask).ToDictionary(c => c.Key, c => TestWorkbook.CellText(c.Value));
        var linked = Row(rows, LinkedTask).ToDictionary(c => c.Key, c => TestWorkbook.CellText(c.Value));

        (own["DesignerId"], own["Designer"]).Should().Be((Alice.ToString("D"), "Alice"));
        (own["DeveloperId"], own["Developer"]).Should().Be((bob.ToString("D"), "Bob"));
        own["Tags"].Should().Be("Web, Mobile", "tag names follow the task's order and an unknown ID is skipped");
        (linked["DesignerId"], linked["Designer"], linked["DeveloperId"], linked["Developer"], linked["Tags"]).Should().Be((null, null, null, null, null));
        var read = (await Transfers.ReadAsync(path)).Tasks.Single(t => t.Id == OwnTask);
        (read.DesignerId, read.DeveloperId, read.DesignerIds, read.DeveloperIds).Should().Be((Alice, bob, null, null), "the name columns are informational");
    }

    [Fact]
    public async Task Checklist_items_are_one_row_per_item_with_parent_id_text_completion_and_order()
    {
        var rows = TestWorkbook.Cells(await WriteAsync(Document())).Single(s => s.Name == "Checklist Items").Rows;

        rows.Skip(1).Select(r => r.Select(TestWorkbook.CellText).ToList()).Should().ContainSingle()
            .Which.Should().Equal(OwnTask.ToString("D"), Item.ToString("D"), "Check; with semicolon", "1", "1024");
    }

    [Fact]
    public async Task Metadata_states_the_format_version_dataset_scope_and_defaults()
    {
        var metadata = TestWorkbook.Cells(await WriteAsync(Document())).Single(s => s.Name == "Metadata").Rows
            .Skip(1).ToDictionary(r => TestWorkbook.CellText(r[0])!, r => TestWorkbook.CellText(r[1]));

        metadata["format"].Should().Be(TransferFormat.Marker);
        metadata["formatVersion"].Should().Be("2", "ADR 0028");
        metadata["datasetId"].Should().Be("e0000000-0000-0000-0000-000000000001");
        metadata["exportedAtUtc"].Should().Be("2026-10-22T09:00:00.0000000+00:00");
        metadata["scope"].Should().Be(TransferFormat.ScopeEntireDataset);
        metadata["defaultCrOwnerId"].Should().Be(Alice.ToString("D"));
    }

    [Fact]
    public async Task A_cell_at_the_excel_limit_is_written_whole()
    {
        var notes = new string('x', XlsxWorkbook.MaxCellCharacters);

        var path = await WriteAsync(Document(notes: notes));

        (await Transfers.ReadAsync(path)).Tasks.Single(t => t.Id == OwnTask).Notes.Should().HaveLength(XlsxWorkbook.MaxCellCharacters);
    }

    [Fact]
    public async Task Text_over_the_excel_cell_limit_fails_clearly_and_leaves_the_existing_file_unchanged()
    {
        var destination = _folder.Write("export.xlsx", [1, 2, 3]);

        var result = await TransferAdapters.CreateWriter().WriteAsync(
            Document(notes: new string('x', XlsxWorkbook.MaxCellCharacters + 1)), TransferFileFormat.Xlsx, null, destination, Ct);

        result.Error.Should().Be(new TransferFileError(TransferFailure.CellLimitExceeded, "Tasks"), "B-12: fail clearly, never truncate");
        File.ReadAllBytes(destination).Should().Equal(1, 2, 3);
        Directory.GetFiles(_folder.Path).Should().ContainSingle("the hidden temporary file is removed");
    }

    [Fact]
    public async Task A_json_cell_over_the_excel_cell_limit_also_fails_instead_of_truncating()
    {
        var legacy = JsonSerializer.Serialize(new { blob = new string('y', XlsxWorkbook.MaxCellCharacters) });
        var document = Document() with
        {
            Tasks = [.. Document().Tasks.Select(t => t.Id == OwnTask ? t with { Preserved = new TransferPreserved(null, [], legacy) } : t)],
        };

        var result = await TransferAdapters.CreateWriter().WriteAsync(document, TransferFileFormat.Xlsx, null, _folder.File("big.xlsx"), Ct);

        result.Error.Should().Be(new TransferFileError(TransferFailure.CellLimitExceeded, "Tasks"));
        File.Exists(_folder.File("big.xlsx")).Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "T-10")]
    public async Task A_cancelled_export_reports_cancellation_and_never_replaces_the_existing_file()
    {
        var destination = _folder.Write("export.xlsx", [9, 9]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await TransferAdapters.CreateWriter().WriteAsync(Document(), TransferFileFormat.Xlsx, null, destination, cancellation.Token);

        result.Error.Should().Be(OperationCancelledError.Instance);
        File.ReadAllBytes(destination).Should().Equal(9, 9);
        Directory.GetFiles(_folder.Path).Should().ContainSingle();
    }

    [Fact]
    [Trait("Req", "T-10")]
    public void Cancellation_while_rows_are_written_stops_the_workbook_writer()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<string?[]> Rows()
        {
            yield return ["first"];
            cancellation.Cancel();
            yield return ["second"];
        }

        using var stream = new MemoryStream();
        var write = () => XlsxWorkbook.Write(stream, [new XlsxWorkbook.Sheet("Tasks", [new TableColumn("Id", CellKind.Text)], Rows())], cancellation.Token);

        write.Should().Throw<OperationCanceledException>();
    }

    private async Task<string> WriteAsync(TransferDocument document)
    {
        var path = _folder.File(Guid.NewGuid().ToString("N") + ".xlsx");
        var result = await TransferAdapters.CreateWriter().WriteAsync(document, TransferFileFormat.Xlsx, null, path, Ct);
        result.Error.Should().BeNull();
        return path;
    }

    /// <summary>The cells of the row whose first column is <paramref name="id"/>, keyed by the sheet's header names.</summary>
    private static Dictionary<string, Cell> Row(List<List<Cell>> rows, Guid id)
    {
        var headers = rows[0].Select(c => TestWorkbook.CellText(c)!).ToList();
        var row = rows.Single(r => TestWorkbook.CellText(r[0]) == id.ToString("D"));
        return headers.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => row[x.i]);
    }

    private static TransferDocument Document(string notes = Multiline) => new()
    {
        DatasetId = Guid.Parse("e0000000-0000-0000-0000-000000000001"),
        ExportedAtUtc = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero),
        Scope = new TransferScope(TransferFormat.ScopeEntireDataset),
        Lookups = new TransferLookups
        {
            TaskStatuses = [new TransferTaskStatus(TaskStatus, "In Progress", 0, false, true, false)],
            CrStatuses = [new TransferCrStatus(CrStatus, "Backlog", 0, false, true)],
            Priorities = [new TransferOrderedLookup(Priority, "High", 0)],
            Squads = [new TransferOrderedLookup(Squad, "Retail Cards", 0)],
            People = [new TransferNamedLookup(Alice, "Alice")],
            Tags = [new TransferNamedLookup(Mobile, "Mobile")],
        },
        FieldDefinitions = [new TransferFieldDefinition(Field, "estimate", "Task", "Estimate", "Number", false, null, 0, [], true)],
        Defaults = new TransferDefaults(TaskStatus, CrStatus, Priority, Alice, CrStatus, CrStatus),
        ChangeRequests =
        [
            new TransferChangeRequest
            {
                Id = Cr, Number = "CR-0105", Title = "Instant card freeze", OwnerId = Alice, SquadId = Squad, StatusId = CrStatus, PriorityId = Priority,
                PlannedStart = new DateOnly(2026, 11, 1), TaskNumber = "0042", Description = Multiline, CreatedAtUtc = Created, UpdatedAtUtc = Created, Revision = 1,
            },
        ],
        Tasks =
        [
            new TransferTask
            {
                Id = LinkedTask, StatusId = TaskStatus, PriorityId = Priority, TagIds = [], IsArchived = false, Order = 1024,
                ChecklistItems = [], CreatedAtUtc = Created, UpdatedAtUtc = Created, Revision = 1,
            },
            new TransferTask
            {
                Id = OwnTask, OwnTitle = "007 Own task", Service = "Cards API", StatusId = TaskStatus, PriorityId = Priority, DesignerId = Alice,
                TagIds = [Mobile], Notes = notes, StartDate = new DateOnly(2026, 10, 1), IsArchived = true, Order = 2048,
                ChecklistItems = [new TransferChecklistItem(Item, "Check; with semicolon", true, 1024)],
                CustomValues = new Dictionary<string, TransferValue> { [Field.ToString("D")] = new("Number", Number: 3.25m) },
                Preserved = new TransferPreserved(null, [], """{"jira":"MP-7"}"""), CreatedAtUtc = Created, UpdatedAtUtc = Created, Revision = 7,
            },
        ],
        Links = [new TransferLink(LinkedTask, Cr, Created)],
        SubtaskCards = [new TransferSubtaskCard { Id = Card, ParentTaskId = OwnTask, Subtitle = "Card", StatusId = TaskStatus, Order = 4096, Revision = 1 }],
        Reviews =
        [
            new TransferReview
            {
                Id = ReviewId, Title = "Review", CrNumberText = "CR-0105", Services = ["Cards API", "Mobile, Web"], DeveloperId = Alice, IsDone = true,
                CompletedOn = new DateOnly(2026, 10, 2), Order = 1024, Revision = 1,
            },
        ],
        GeneralTodos = [new TransferGeneralTodo { Id = Todo, Text = "Call bank", IsCompleted = false, Order = 1024, Revision = 1 }],
    };
}
