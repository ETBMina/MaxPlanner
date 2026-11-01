using System.Globalization;
using System.IO.Compression;
using AwesomeAssertions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Infrastructure.Tests.Persistence;
using MaxPlanner.Infrastructure.Transfer;
using TableColumn = MaxPlanner.Infrastructure.Transfer.TableColumn;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>A unique temp folder (path with a space and non-ASCII text) for transfer files, deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MaxPlanner Tëst Transfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, byte[] bytes)
    {
        var path = File(name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: temp folders are disposable.
        }
    }
}

/// <summary>
/// Builds small workbooks cell by cell with the Open XML SDK, so reader tests describe exactly the XML they feed in
/// (formulas, shared strings, date styles, the 1904 date system) without binary fixtures.
/// </summary>
internal static class TestWorkbook
{
    public static byte[] Create(IEnumerable<(string Name, Row[] Rows)> sheets, bool date1904 = false, Stylesheet? stylesheet = null, IReadOnlyList<string>? sharedStrings = null)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            if (date1904)
            {
                workbookPart.Workbook.AppendChild(new WorkbookProperties { Date1904 = true });
            }

            if (stylesheet is not null)
            {
                workbookPart.AddNewPart<WorkbookStylesPart>().Stylesheet = stylesheet;
            }

            if (sharedStrings is not null)
            {
                workbookPart.AddNewPart<SharedStringTablePart>().SharedStringTable =
                    new SharedStringTable(sharedStrings.Select(s => new SharedStringItem(new Text(s) { Space = SpaceProcessingModeValues.Preserve })));
            }

            var list = workbookPart.Workbook.AppendChild(new Sheets());
            uint sheetId = 1;
            foreach (var (name, rows) in sheets)
            {
                var part = workbookPart.AddNewPart<WorksheetPart>();
                part.Worksheet = new Worksheet(new SheetData(rows));
                list.Append(new Sheet { Id = workbookPart.GetIdOfPart(part), SheetId = sheetId++, Name = name });
            }
        }

        return stream.ToArray();
    }

    /// <summary>A plain ZIP of text entries.</summary>
    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Adds an entry to an existing ZIP, for example a name the writer would never produce.</summary>
    public static byte[] AddEntry(byte[] zip, string name, string content)
    {
        using var stream = new MemoryStream();
        stream.Write(zip);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        return stream.ToArray();
    }

    /// <summary>Rewrites the text of every entry under <paramref name="prefix"/>.</summary>
    public static byte[] Rewrite(byte[] zip, string prefix, Func<string, string> change)
    {
        using var stream = new MemoryStream();
        stream.Write(zip);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                string xml;
                using (var reader = new StreamReader(entry.Open()))
                {
                    xml = reader.ReadToEnd();
                }

                var name = entry.FullName;
                entry.Delete();
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(change(xml));
            }
        }

        return stream.ToArray();
    }

    public static (string Name, Row[] Rows) Sheet(string name, params Row[] rows) => (name, rows);

    /// <summary>The Metadata sheet an app workbook needs before any table is read.</summary>
    public static (string Name, Row[] Rows) Metadata(string version = "1") => Sheet(
        TransferTables.MetadataSheet,
        Header("Key", "Value"),
        Row(Text("format"), Text(TransferFormat.Marker)),
        Row(Text("formatVersion"), Text(version)));

    public static Row Row(params Cell[] cells) => new(cells);

    public static Row Header(params string[] names) => new(names.Select(Text));

    public static Cell Text(string value) =>
        new(new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })) { DataType = CellValues.InlineString };

    public static Cell Shared(int index) => new(new CellValue(index.ToString(CultureInfo.InvariantCulture))) { DataType = CellValues.SharedString };

    public static Cell Number(string value, uint? style = null)
    {
        var cell = new Cell(new CellValue(value));
        if (style is { } index)
        {
            cell.StyleIndex = index;
        }

        return cell;
    }

    public static Cell Bool(bool value) => new(new CellValue(value ? "1" : "0")) { DataType = CellValues.Boolean };

    public static Cell Error(string value) => new(new CellValue(value)) { DataType = CellValues.Error };

    public static Cell Formula(string formula, string cachedValue) => new(new CellFormula(formula), new CellValue(cachedValue));

    public static Cell At(this Cell cell, string reference)
    {
        cell.CellReference = reference;
        return cell;
    }

    /// <summary>A stylesheet whose cell format 1 uses <paramref name="numberFormatId"/>, optionally a custom format code.</summary>
    public static Stylesheet Styles(uint numberFormatId, string? formatCode = null)
    {
        var stylesheet = new Stylesheet();
        if (formatCode is not null)
        {
            stylesheet.Append(new NumberingFormats(new NumberingFormat { NumberFormatId = numberFormatId, FormatCode = formatCode }) { Count = 1 });
        }

        stylesheet.Append(
            new Fonts(new Font()) { Count = 1 },
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None })) { Count = 1 },
            new Borders(new Border()) { Count = 1 },
            new CellFormats(new CellFormat(), new CellFormat { NumberFormatId = numberFormatId, ApplyNumberFormat = true }) { Count = 2 });
        return stylesheet;
    }

    /// <summary>Reads a workbook with the production reader.</summary>
    public static IReadOnlyDictionary<string, RawTable> Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return XlsxWorkbook.Read(stream, allowDateTimes: false, TestContext.Current.CancellationToken);
    }

    /// <summary>Reads a workbook as the external-source reader does (ADR 0027: date-time cells become ISO text).</summary>
    public static IReadOnlyDictionary<string, RawTable> ReadExternal(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return XlsxWorkbook.Read(stream, allowDateTimes: true, TestContext.Current.CancellationToken);
    }

    /// <summary>The sheets of a workbook in order, each as its rows of cells, for assertions on the written XML.</summary>
    public static List<(string Name, List<List<Cell>> Rows)> Cells(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart!;
        return [.. workbookPart.Workbook!.Sheets!.Elements<Sheet>().Select(sheet =>
        {
            var part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
            var rows = part.Worksheet!.Descendants<Row>().Select(r => r.Elements<Cell>().Select(c => (Cell)c.CloneNode(true)).ToList()).ToList();
            return (sheet.Name!.Value!, rows);
        })];
    }

    public static string? CellText(Cell cell) => cell.DataType?.Value == CellValues.InlineString ? cell.InlineString?.InnerText : cell.CellValue?.Text;

    /// <summary>The cell text as a reader sees it, with OOXML <c>_xHHHH_</c> escapes decoded.</summary>
    public static string? DecodedText(Cell cell) => CellText(cell) is { } text ? XlsxWorkbook.UnescapeText(text) : null;

    /// <summary>The number format code of a cell's style, read from the workbook's stylesheet.</summary>
    public static string? FormatCodeOf(string path, Cell cell)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var stylesheet = document.WorkbookPart!.WorkbookStylesPart!.Stylesheet!;
        var format = stylesheet.CellFormats!.Elements<CellFormat>().ElementAt((int)(cell.StyleIndex?.Value ?? 0));
        return stylesheet.NumberingFormats?.Elements<NumberingFormat>().FirstOrDefault(f => f.NumberFormatId?.Value == format.NumberFormatId?.Value)?.FormatCode?.Value;
    }
}

/// <summary>
/// An app workbook loaded through the production reader so a test can change cells, columns, or sheets by header name
/// and write it back with the production writer (date, number, and boolean cells keep their native types).
/// </summary>
internal sealed class EditableWorkbook
{
    private readonly List<(string Name, List<string> Headers, List<string?[]> Rows)> _sheets;

    private EditableWorkbook(List<(string, List<string>, List<string?[]>)> sheets) => _sheets = sheets;

    public static EditableWorkbook Load(string path)
    {
        var sheets = TestWorkbook.Read(File.ReadAllBytes(path));
        return new EditableWorkbook([.. sheets.Select(pair =>
        {
            var headers = pair.Value.Headers.ToList();
            var rows = pair.Value.Rows.Select(r => { var row = new string?[headers.Count]; Array.Copy(r, row, Math.Min(r.Length, row.Length)); return row; }).ToList();
            return (pair.Key, headers, rows);
        })]);
    }

    public List<string?[]> Rows(string sheet) => Find(sheet).Rows;

    public EditableWorkbook Set(string sheet, Guid id, string column, string? value)
    {
        RowOf(sheet, id)[Column(sheet, column)] = value;
        return this;
    }

    /// <summary>Appends a copy of the row with <paramref name="id"/> and returns it for editing.</summary>
    public string?[] CopyRow(string sheet, Guid id)
    {
        var copy = (string?[])RowOf(sheet, id).Clone();
        Rows(sheet).Add(copy);
        return copy;
    }

    public EditableWorkbook RemoveColumn(string sheet, string column)
    {
        var (_, headers, rows) = Find(sheet);
        var index = Column(sheet, column);
        headers.RemoveAt(index);
        for (var i = 0; i < rows.Count; i++)
        {
            var list = rows[i].ToList();
            list.RemoveAt(index);
            rows[i] = [.. list];
        }

        return this;
    }

    /// <summary>Keeps only the rows of <paramref name="sheet"/> that match.</summary>
    public EditableWorkbook KeepRows(string sheet, Func<string?[], bool> keep)
    {
        Rows(sheet).RemoveAll(r => !keep(r));
        return this;
    }

    public int Column(string sheet, string column)
    {
        var index = Find(sheet).Headers.IndexOf(column);
        index.Should().BeGreaterThanOrEqualTo(0, $"{sheet} has a {column} column");
        return index;
    }

    public string Save(string path)
    {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        {
            XlsxWorkbook.Write(stream, [.. _sheets.Select(s => new XlsxWorkbook.Sheet(s.Name, [.. s.Headers.Select(h => new TableColumn(h, KindOf(s.Name, h)))], s.Rows))], CancellationToken.None);
        }

        return path;
    }

    private static CellKind KindOf(string sheet, string header)
    {
        var columns = TransferTables.TableNamed(sheet) is { } table
            ? TransferTables.Columns(table)
            : sheet switch
            {
                TransferTables.LookupsSheet => TransferTables.LookupColumns,
                TransferTables.FieldsSheet => TransferTables.FieldColumns,
                _ => TransferTables.MetadataColumns,
            };
        return columns.FirstOrDefault(c => c.Name == header)?.Kind ?? CellKind.Text;
    }

    private string?[] RowOf(string sheet, Guid id)
    {
        var column = Column(sheet, "Id");
        return Rows(sheet).Single(r => r[column] == id.ToString("D"));
    }

    private (string Name, List<string> Headers, List<string?[]> Rows) Find(string sheet) => _sheets.Single(s => s.Name == sheet);
}

/// <summary>The real export and import use cases over a <see cref="TestDataset"/>.</summary>
internal static class Transfers
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ExportTransfer Exporter(TestDataset db) => new(db.Dataset.Snapshots, TransferAdapters.CreateWriter(), db.Dataset.Id.Value, db.Clock);

    public static ImportTransfer Importer(TestDataset db) => new(db.Coordinator, db.Dataset.Snapshots, db.Dataset.ImportState, db.Clock);

    public static async Task<ExportReport> ExportAsync(TestDataset db, string path, TransferExportScope? scope = null)
    {
        var result = await Exporter(db).ExportAsync(scope ?? EntireDatasetScope.Instance, TransferFileFormat.Xlsx, null, path, Ct);
        result.Error.Should().BeNull();
        return result.Value;
    }

    public static async Task<TransferDocument> ReadAsync(string path)
    {
        var result = await TransferAdapters.CreateReader().ReadAsync(path, null, Ct);
        result.Error.Should().BeNull();
        return result.Value;
    }

    public static async Task<AppError?> ReadErrorAsync(string path, CancellationToken? cancellationToken = null)
    {
        var result = await TransferAdapters.CreateReader().ReadAsync(path, null, cancellationToken ?? Ct);
        result.IsSuccess.Should().BeFalse();
        return result.Error;
    }

    public static async Task<ImportPlan> PreviewAsync(TestDataset db, string path, ImportMode mode = ImportMode.CreateOnly)
    {
        var result = await Importer(db).PreviewAsync(await ReadAsync(path), mode, Ct);
        result.Error.Should().BeNull();
        return result.Value;
    }

    public static async Task<long> ApplyAsync(TestDataset db, ImportPlan plan)
    {
        plan.Findings.Where(f => f.Severity == ImportSeverity.Error).Should().BeEmpty();
        var result = await Importer(db).ApplyAsync(plan, OperationId.New(), Ct);
        result.Error.Should().BeNull();
        return result.Value.Value;
    }
}
