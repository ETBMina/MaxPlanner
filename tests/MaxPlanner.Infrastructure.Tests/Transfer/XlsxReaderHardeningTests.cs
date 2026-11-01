using System.Text;
using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Infrastructure.Transfer;
using static MaxPlanner.Infrastructure.Tests.Transfer.TestWorkbook;
using TableColumn = MaxPlanner.Infrastructure.Transfer.TableColumn;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>
/// T-10 / B-12 hardening of the XLSX reader: formulas, both date systems, ambiguous dates, cell types, sheet and column
/// handling, malformed and oversized input, and cancellation. Workbooks are built in the test with the Open XML SDK.
/// </summary>
[Trait("Req", "T-10")]
[Trait("Req", "B-12")]
public sealed class XlsxReaderHardeningTests : IDisposable
{
    private const uint DateStyle = 1;
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    // ----- Formulas -----
    [Fact]
    public void A_formula_is_rejected_with_its_sheet_and_cell_and_never_evaluated()
    {
        var bytes = Create([Sheet("Tasks", Header("Id", "Title"), Row(Text("x").At("A2"), Formula("1+1", "2").At("B2")))]);

        var read = () => Read(bytes);

        read.Should().Throw<TransferFormatException>().Which.Should().Match<TransferFormatException>(e => e.Failure == TransferFailure.FormulaRejected && e.Detail == "Tasks!B2");
    }

    [Fact]
    public async Task A_formula_in_an_app_workbook_fails_the_import_before_anything_is_planned()
    {
        var path = _folder.Write("formula.xlsx", Create([Metadata(), Sheet("General To-dos", Header("Id", "Text"), Row(Text(Guid.NewGuid().ToString()).At("A2"), Formula("HYPERLINK(\"http://x\")", "x").At("B2")))]));

        (await Transfers.ReadErrorAsync(path)).Should().Be(new TransferFileError(TransferFailure.FormulaRejected, "General To-dos!B2"));
    }

    // ----- Dates -----
    [Theory]
    [InlineData(false, "2023-03-15")]
    [InlineData(true, "2027-03-16")]
    public void A_date_cell_is_read_in_the_workbook_date_system(bool date1904, string expected)
    {
        var bytes = Create([Sheet("Tasks", Header("StartDate"), Row(Number("45000", DateStyle)))], date1904, Styles(14));

        Read(bytes)["Tasks"].Rows.Single()[0].Should().Be(expected, "serial 45000 is 2023-03-15 in the 1900 system and 1,462 days later in the 1904 system");
    }

    [Theory]
    [InlineData(164u, "yyyy-mm-dd", true)]
    [InlineData(165u, "dd/mm/yyyy;@", true)]
    [InlineData(166u, "\"Day\" 0", false)]
    [InlineData(167u, "[Red]0.00", false)]
    [InlineData(2u, null, false)]
    [InlineData(22u, null, true)]
    public void Only_date_number_formats_turn_a_serial_into_a_date(uint formatId, string? code, bool isDate)
    {
        var bytes = Create([Sheet("Tasks", Header("Value"), Row(Number("45000", DateStyle)))], stylesheet: Styles(formatId, code));

        Read(bytes)["Tasks"].Rows.Single()[0].Should().Be(isDate ? "2023-03-15" : "45000");
    }

    [Fact]
    public void A_date_cell_with_a_time_of_day_is_rejected_as_ambiguous()
    {
        var bytes = Create([Sheet("Tasks", Header("Id", "StartDate"), Row(Text("x").At("A2"), Number("45000.5", DateStyle).At("B2")))], stylesheet: Styles(14));

        var read = () => Read(bytes);

        read.Should().Throw<TransferFormatException>().Which.Should().Match<TransferFormatException>(e => e.Failure == TransferFailure.Malformed && e.Detail == "Tasks!B2");
    }

    [Fact]
    public async Task Date_text_that_is_not_iso_is_rejected_with_its_location()
    {
        var path = _folder.Write("dates.xlsx", Create([Metadata(), Sheet("Tasks", Header("Id", "OwnTitle", "StartDate"), Row(Text(Guid.NewGuid().ToString()), Text("T"), Text("22/10/2026")))]));

        (await Transfers.ReadErrorAsync(path)).Should().Be(new TransferFileError(TransferFailure.Malformed, "Tasks!StartDate"));
    }

    // ----- Cell types -----
    [Fact]
    public void Shared_and_inline_strings_read_the_same_and_keep_leading_zeros_and_line_breaks()
    {
        var bytes = Create(
            [Sheet("Tasks", Header("A", "B", "C", "D"), Row(Shared(0), Text("0042"), Shared(1), Bool(true)), Row(Text("x"), Text("y"), Text("z"), Bool(false)))],
            sharedStrings: ["007", "a\nb"]);

        var rows = Read(bytes)["Tasks"].Rows;

        rows[0].Should().Equal("007", "0042", "a\nb", "true");
        rows[1][3].Should().Be("false");
    }

    [Fact]
    public void Ooxml_character_escapes_written_by_excel_are_decoded_in_shared_and_inline_strings()
    {
        var bytes = Create(
            [Sheet("Tasks", Header("A", "B", "C", "D"), Row(Shared(0), Text("Line 1_x000D_\nLine 2"), Text("_x005F_x0041_ stays literal"), Text("_xZZZZ_ _x0041")))],
            sharedStrings: ["Excel_x000D_\nnote _x00e9_"]);

        Read(bytes)["Tasks"].Rows.Single().Should().Equal("Excel\r\nnote é", "Line 1\r\nLine 2", "_x0041_ stays literal", "_xZZZZ_ _x0041");
    }

    [Theory]
    [InlineData("_xD83D__xDE00_", true)]
    [InlineData("_xD83D_", false)]
    [InlineData("_xDE00_", false)]
    [InlineData("_xD83D_x_xDE00_", false)]
    [InlineData("_x0000_", false)]
    [InlineData("_xFFFE_", false)]
    [InlineData("_xFFFF_", false)]
    public void Only_escapes_that_form_valid_text_are_decoded(string cell, bool decodes)
    {
        var bytes = Create([Sheet("Tasks", Header("A"), Row(Text(cell)))]);

        Read(bytes)["Tasks"].Rows.Single()[0].Should().Be(decodes ? char.ConvertFromUtf32(0x1F600) : cell);
    }

    [Fact]
    public async Task An_excel_authored_carriage_return_imports_as_a_line_feed()
    {
        await using var db = await Persistence.TestDataset.CreateAsync();
        var id = Guid.NewGuid();
        var path = _folder.Write("excel.xlsx", Create([Metadata(), Sheet("Tasks", Header("Id", "OwnTitle", "Notes"), Row(Text(id.ToString()), Text("From Excel"), Text("Line 1_x000D_\nLine 2_x000D_")))]));

        await Transfers.ApplyAsync(db, await Transfers.PreviewAsync(db, path));

        (await db.SnapshotAsync()).Tasks.Single().Notes!.Value.Should().Be("Line 1\nLine 2\n");
    }

    [Fact]
    public void A_shared_string_index_outside_the_table_is_malformed()
    {
        var bytes = Create([Sheet("Tasks", Header("A"), Row(Shared(5).At("A2")))], sharedStrings: ["only"]);

        var read = () => Read(bytes);

        read.Should().Throw<TransferFormatException>().Which.Detail.Should().Be("Tasks!A2");
    }

    [Fact]
    public void An_error_cell_is_malformed()
    {
        var bytes = Create([Sheet("Tasks", Header("A"), Row(Error("#REF!").At("A2")))]);

        var read = () => Read(bytes);

        read.Should().Throw<TransferFormatException>().Which.Should().Match<TransferFormatException>(e => e.Failure == TransferFailure.Malformed && e.Detail == "Tasks!A2");
    }

    [Fact]
    public void Cell_references_place_values_in_their_columns_across_gaps()
    {
        var bytes = Create([Sheet("Tasks", Header("A", "B", "C"), Row(Text("first").At("A2"), Text("third").At("C2")))]);

        Read(bytes)["Tasks"].Rows.Single().Should().Equal("first", null, "third");
    }

    // ----- Sheets and columns -----
    [Fact]
    public void Empty_sheets_and_blank_rows_produce_no_records()
    {
        var bytes = Create([Sheet("Empty"), Sheet("Headers only", Header("Id")), Sheet("Blank row", Header("Id"), Row(Text("")), Row(Text("x")))]);

        var tables = Read(bytes);

        (tables["Empty"].Headers.Count, tables["Empty"].Rows.Count).Should().Be((0, 0));
        tables["Headers only"].Rows.Should().BeEmpty();
        tables["Blank row"].Rows.Should().ContainSingle().Which.Should().Equal("x");
    }

    [Fact]
    public async Task A_workbook_without_the_metadata_sheet_is_not_an_app_workbook()
    {
        var path = _folder.Write("nometa.xlsx", Create([Sheet("Tasks", Header("Id", "OwnTitle"), Row(Text(Guid.NewGuid().ToString()), Text("T")))]));

        (await Transfers.ReadErrorAsync(path)).Should().Be(new TransferFileError(TransferFailure.Malformed, "Metadata"));
    }

    [Fact]
    public async Task A_newer_format_version_is_rejected_and_an_invalid_one_is_malformed()
    {
        var newer = _folder.Write("v2.xlsx", Create([Metadata("2")]));
        var invalid = _folder.Write("vx.xlsx", Create([Metadata("one")]));

        (await Transfers.ReadErrorAsync(newer)).Should().Be(new TransferFileError(TransferFailure.UnsupportedVersion, "2"));
        (await Transfers.ReadErrorAsync(invalid)).Should().Be(new TransferFileError(TransferFailure.Malformed, "Metadata!formatVersion"));
    }

    [Fact]
    public async Task A_workbook_with_only_metadata_reads_as_an_empty_document()
    {
        var document = await Transfers.ReadAsync(_folder.Write("meta.xlsx", Create([Metadata()])));

        document.TopLevelRecordCount.Should().Be(0);
        document.Links.Should().BeEmpty();
    }

    [Fact]
    public async Task Sheets_are_matched_by_name_ignoring_case_and_unknown_sheets_and_columns_do_not_break_the_read()
    {
        var id = Guid.NewGuid();
        var path = _folder.Write("names.xlsx", Create(
        [
            Metadata(),
            Sheet("general to-dos", Header("Id", "Text", "Mood"), Row(Text(id.ToString()), Text("Call"), Text("happy"))),
            Sheet("Notes to self", Header("Anything"), Row(Text("ignored"))),
        ]));

        var document = await Transfers.ReadAsync(path);

        document.GeneralTodos.Should().ContainSingle().Which.Should().Match<TransferGeneralTodo>(t => t.Id == id && t.Text == "Call");
        document.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_optional_columns_are_not_supplied_so_a_merge_leaves_those_fields_unchanged()
    {
        var path = _folder.Write("partial.xlsx", Create([Metadata(), Sheet("Tasks", Header("Id", "Notes", "Title"), Row(Text(Guid.NewGuid().ToString()), Text("n"), Text("info")))]));

        var task = (await Transfers.ReadAsync(path)).Tasks.Single();

        task.SuppliedFields.Should().BeEquivalentTo(["Id", "Notes"], "absent columns and the informational Title column never count as supplied (T-10)");
        task.ChecklistItems.Should().BeNull("no Checklist Items sheet means checklists are unchanged");
    }

    [Fact]
    public async Task Rows_without_their_required_id_column_are_malformed_with_the_location()
    {
        var path = _folder.Write("noid.xlsx", Create([Metadata(), Sheet("Tasks", Header("OwnTitle"), Row(Text("No id")))]));

        (await Transfers.ReadErrorAsync(path)).Should().Be(new TransferFileError(TransferFailure.Malformed, "Tasks!Id"));
    }

    [Theory]
    [InlineData("StatusId", "not-a-guid", "Tasks!StatusId")]
    [InlineData("IsArchived", "yes", "Tasks!IsArchived")]
    [InlineData("Order", "1.5", "Tasks!Order")]
    [InlineData("DesignerIds", "Alice; Bob", "Tasks!DesignerIds")]
    [InlineData("CreatedAtUtc", "yesterday", "Tasks!CreatedAtUtc")]
    public async Task An_unparsable_cell_is_malformed_with_its_sheet_and_column(string column, string value, string location)
    {
        var path = _folder.Write("bad.xlsx", Create([Metadata(), Sheet("Tasks", Header("Id", "OwnTitle", column), Row(Text(Guid.NewGuid().ToString()), Text("T"), Text(value)))]));

        (await Transfers.ReadErrorAsync(path)).Should().Be(new TransferFileError(TransferFailure.Malformed, location));
    }

    [Fact]
    public async Task An_unknown_lookup_kind_is_malformed()
    {
        var path = _folder.Write("kind.xlsx", Create([Metadata(), Sheet("Lookups", Header("Kind", "Id", "Name"), Row(Text("Colour"), Text(Guid.NewGuid().ToString()), Text("Red")))]));

        (await Transfers.ReadErrorAsync(path)).Should().Be(new TransferFileError(TransferFailure.Malformed, "Lookups!Kind"));
    }

    // ----- Not a workbook -----
    [Fact]
    public async Task A_file_that_is_not_a_zip_is_malformed()
    {
        var path = _folder.Write("text.xlsx", Encoding.UTF8.GetBytes("Id,Title\n1,Not a workbook\n"));

        (await Transfers.ReadErrorAsync(path)).Should().Be(new TransferFileError(TransferFailure.Malformed));
    }

    [Fact]
    public async Task A_truncated_workbook_is_malformed()
    {
        var bytes = Create([Metadata()]);
        var path = _folder.Write("cut.xlsx", bytes[..(bytes.Length / 2)]);

        (await Transfers.ReadErrorAsync(path)).Should().BeOfType<TransferFileError>().Which.Failure.Should().Be(TransferFailure.Malformed);
    }

    [Fact]
    public async Task A_zip_that_is_not_a_spreadsheet_package_is_malformed()
    {
        var path = _folder.Write("zip.xlsx", Zip(("readme.txt", "hello")));

        (await Transfers.ReadErrorAsync(path)).Should().Be(new TransferFileError(TransferFailure.Malformed, "workbook"));
    }

    [Fact]
    public async Task A_worksheet_with_broken_xml_is_malformed()
    {
        var bytes = Create([Metadata(), Sheet("Tasks", Header("Id"))]);
        var broken = Rewrite(bytes, "xl/worksheets/", xml => xml.Replace("</x:sheetData>", "<x:row><x:c></x:sheetData>", StringComparison.Ordinal).Replace("</sheetData>", "<row><c></sheetData>", StringComparison.Ordinal));
        var path = _folder.Write("broken.xlsx", broken);

        (await Transfers.ReadErrorAsync(path)).Should().Be(new TransferFileError(TransferFailure.Malformed, "workbook"));
    }

    // ----- Limits -----
    [Fact]
    public async Task An_input_over_the_size_limit_is_rejected_before_it_is_parsed()
    {
        var path = _folder.File("huge.xlsx");
        using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            stream.SetLength(TransferLimits.MaxInputBytes + 1);
        }

        (await Transfers.ReadErrorAsync(path)).Should().Be(new TransferFileError(TransferFailure.TooLarge, "file size"));
    }

    [Fact]
    public async Task A_workbook_over_the_record_limit_is_rejected_after_reading_and_before_planning()
    {
        var over = WriteTodos("over.xlsx", TransferLimits.MaxTopLevelRecords + 1);

        (await Transfers.ReadErrorAsync(over)).Should().Be(new TransferFileError(TransferFailure.TooLarge, "records"));
    }

    [Fact]
    public async Task A_workbook_with_an_unsafe_entry_name_is_rejected_before_parsing()
    {
        var bytes = AddEntry(Create([Metadata()]), "../escape.xml", "<x/>");
        var path = _folder.Write("unsafe.xlsx", bytes);

        (await Transfers.ReadErrorAsync(path)).Should().Be(new TransferFileError(TransferFailure.UnsafeArchive, "entry name"));
    }

    // ----- Cancellation -----
    [Fact]
    public async Task A_cancelled_read_reports_cancellation()
    {
        var path = _folder.Write("meta.xlsx", Create([Metadata()]));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        (await Transfers.ReadErrorAsync(path, cancellation.Token)).Should().Be(OperationCancelledError.Instance);
    }

    [Fact]
    public void A_cancelled_token_stops_the_reader_before_the_first_sheet()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new MemoryStream(Create([Sheet("Tasks", Header("Id"), Row(Text("a")))]), writable: false);
        cancellation.Cancel();

        var read = () => XlsxWorkbook.Read(stream, cancellation.Token);

        read.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Cancellation_while_rows_are_read_stops_the_reader_between_rows()
    {
        var path = WriteTodos("many.xlsx", 20_000, randomIds: true);
        using var cancellation = new CancellationTokenSource();
        var bytes = File.ReadAllBytes(path);

        // The second pass over the stream is the package read (the first is the ZIP check); once it has consumed half of
        // the file, which is almost all worksheet XML, the token is cancelled while rows are still being parsed.
        using var stream = new CancelAfterStream(bytes, bytes.Length / 2, cancellation);
        var read = () => XlsxWorkbook.Read(stream, cancellation.Token);

        read.Should().Throw<OperationCanceledException>();
        stream.CancelledAt.Should().NotBeNull().And.BeLessThan(bytes.Length, "cancellation happened before the workbook was fully read");
    }

    private string WriteTodos(string name, int count, bool randomIds = false)
    {
        var path = _folder.File(name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        XlsxWorkbook.Write(
            stream,
            [
                new XlsxWorkbook.Sheet("General To-dos", [new TableColumn("Id", CellKind.Text)], Enumerable.Range(0, count).Select(i => new string?[] { (randomIds ? Guid.NewGuid() : new Guid(i, 0, 0, new byte[8])).ToString("D") })),
                new XlsxWorkbook.Sheet("Metadata", TransferTables.MetadataColumns, [["format", TransferFormat.Marker], ["formatVersion", "1"]]),
            ],
            CancellationToken.None);
        return path;
    }

    /// <summary>A read-only stream that cancels <paramref name="source"/> once the second pass has read <paramref name="threshold"/> bytes.</summary>
    private sealed class CancelAfterStream(byte[] bytes, long threshold, CancellationTokenSource source) : MemoryStream(bytes, writable: false)
    {
        private int _passes;
        private long _readThisPass;

        public long? CancelledAt { get; private set; }

        public override long Position
        {
            get => base.Position;
            set
            {
                if (value == 0)
                {
                    _passes++;
                    _readThisPass = 0;
                }

                base.Position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(base.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(base.Read(buffer));

        private int Count(int read)
        {
            _readThisPass += read;
            if (_passes >= 1 && CancelledAt is null && _readThisPass >= threshold)
            {
                CancelledAt = Position;
                source.Cancel();
            }

            return read;
        }
    }
}
