using System.Globalization;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MaxPlanner.Application.Transfer;

namespace MaxPlanner.Infrastructure.Transfer;

/// <summary>
/// XLSX through the Open XML SDK streaming reader and writer (ADR 0012): user text is written as inline strings so
/// leading zeros and multiline text survive; business dates are real date cells; formulas are never written,
/// evaluated, or accepted (T-10); both the 1900 and the 1904 date systems are recognized on read.
/// </summary>
internal static class XlsxWorkbook
{
    /// <summary>Excel's per-cell text limit; longer content fails the export clearly instead of truncating (B-12).</summary>
    public const int MaxCellCharacters = 32_767;

    private const uint DateStyleIndex = 1;
    private const uint DateFormatId = 164;

    // Built-in number formats that Excel renders as dates (ECMA-376 Part 1, 18.8.30).
    private static readonly HashSet<uint> BuiltInDateFormats = [14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47];

    /// <summary>A worksheet to write: its name, columns, and rows of invariant text.</summary>
    public sealed record Sheet(string Name, IReadOnlyList<TableColumn> Columns, IEnumerable<string?[]> Rows);

    public static void Write(Stream destination, IReadOnlyList<Sheet> sheets, CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Create(destination, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var styles = workbookPart.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = Stylesheet();
        styles.Stylesheet.Save();

        var sheetList = new Sheets();
        uint sheetId = 1;
        foreach (var sheet in sheets)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            using (var writer = OpenXmlWriter.Create(worksheetPart))
            {
                writer.WriteStartElement(new Worksheet());
                writer.WriteStartElement(new SheetData());
                WriteRow(writer, sheet.Columns.Select(c => ((string?)c.Name, CellKind.Text)).ToList(), sheet.Name);
                foreach (var row in sheet.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteRow(writer, [.. row.Select((value, i) => (value, sheet.Columns[i].Kind))], sheet.Name);
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            sheetList.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = sheetId++, Name = sheet.Name });
        }

        workbookPart.Workbook.AppendChild(sheetList);
        workbookPart.Workbook.Save();
    }

    private static void WriteRow(OpenXmlWriter writer, List<(string? Value, CellKind Kind)> cells, string sheet)
    {
        writer.WriteStartElement(new Row());
        foreach (var (value, kind) in cells)
        {
            if (value is null)
            {
                writer.WriteElement(new Cell());
                continue;
            }

            switch (kind)
            {
                case CellKind.Date when DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                    writer.WriteElement(new Cell(new CellValue(SerialOf(date).ToString(CultureInfo.InvariantCulture))) { StyleIndex = DateStyleIndex });
                    break;
                case CellKind.Number:
                    writer.WriteElement(new Cell(new CellValue(value)) { DataType = CellValues.Number });
                    break;
                case CellKind.Boolean:
                    writer.WriteElement(new Cell(new CellValue(value == "true" ? "1" : "0")) { DataType = CellValues.Boolean });
                    break;
                default:
                    if (value.Length > MaxCellCharacters)
                    {
                        throw new TransferFormatException(TransferFailure.CellLimitExceeded, sheet);
                    }

                    // Inline strings keep identifiers (leading zeros) and user text exactly; they are never formulas.
                    writer.WriteElement(new Cell(new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })) { DataType = CellValues.InlineString });
                    break;
            }
        }

        writer.WriteEndElement();
    }

    private static Stylesheet Stylesheet() => new(
        new NumberingFormats(new NumberingFormat { NumberFormatId = DateFormatId, FormatCode = "yyyy-mm-dd" }) { Count = 1 },
        new Fonts(new Font()) { Count = 1 },
        new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
        new Borders(new Border()) { Count = 1 },
        new CellStyleFormats(new CellFormat()) { Count = 1 },
        new CellFormats(new CellFormat(), new CellFormat { NumberFormatId = DateFormatId, ApplyNumberFormat = true }) { Count = 2 });

    /// <summary>The 1900 date system serial (valid for dates from March 1900, where the 1900 leap-year quirk no longer applies).</summary>
    private static int SerialOf(DateOnly date) => date.DayNumber - new DateOnly(1899, 12, 30).DayNumber;

    /// <summary>
    /// Reads every worksheet into raw tables (first row = headers). Date-formatted numeric cells become ISO dates in
    /// the workbook's date system; booleans become <c>true</c>/<c>false</c>. A formula anywhere fails the read.
    /// </summary>
    /// <exception cref="TransferFormatException">TooLarge, UnsafeArchive, FormulaRejected, or Malformed.</exception>
    public static IReadOnlyDictionary<string, RawTable> Read(Stream source, CancellationToken cancellationToken)
    {
        using (var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
        {
            ArchiveGuard.Check(archive, null);
        }

        source.Position = 0;
        var tables = new Dictionary<string, RawTable>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = SpreadsheetDocument.Open(source, false, new OpenSettings { MaxCharactersInPart = TransferLimits.MaxExpandedBytes });
            var workbookPart = document.WorkbookPart ?? throw new TransferFormatException(TransferFailure.Malformed, "workbook");
            var is1904 = workbookPart.Workbook?.WorkbookProperties?.Date1904?.Value ?? false;
            var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToList() ?? [];
            var dateStyles = DateStyles(workbookPart.WorkbookStylesPart?.Stylesheet);
            foreach (var sheet in workbookPart.Workbook?.Sheets?.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>() ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = sheet.Name?.Value ?? string.Empty;
                if (sheet.Id?.Value is not { } relationship || workbookPart.GetPartById(relationship) is not WorksheetPart part)
                {
                    continue;
                }

                tables[name] = ReadSheet(name, part, sharedStrings, dateStyles, is1904, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or System.Xml.XmlException or FormatException)
        {
            throw new TransferFormatException(TransferFailure.Malformed, "workbook", ex);
        }

        return tables;
    }

    private static RawTable ReadSheet(string name, WorksheetPart part, List<string> sharedStrings, HashSet<uint> dateStyles, bool is1904, CancellationToken cancellationToken)
    {
        var rows = new List<string?[]>();
        string[]? headers = null;
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var row = (Row)reader.LoadCurrentElement()!;
            var values = new List<string?>();
            var position = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                var column = cell.CellReference?.Value is { } reference ? ColumnIndex(reference) : position;
                while (values.Count < column)
                {
                    values.Add(null);
                }

                if (cell.CellFormula is not null)
                {
                    // T-10: formulas are never evaluated; a workbook containing one is rejected with its location.
                    throw new TransferFormatException(TransferFailure.FormulaRejected, name + "!" + (cell.CellReference?.Value ?? "?"));
                }

                values.Add(Value(cell, sharedStrings, dateStyles, is1904, name));
                position = column + 1;
            }

            if (headers is null)
            {
                headers = [.. values.Select(v => v ?? string.Empty)];
                continue;
            }

            if (values.Any(v => !string.IsNullOrEmpty(v)))
            {
                rows.Add([.. values]);
            }
        }

        return new RawTable(name, headers ?? [], rows);
    }

    private static string? Value(Cell cell, List<string> sharedStrings, HashSet<uint> dateStyles, bool is1904, string sheet)
    {
        var raw = cell.CellValue?.Text;
        var type = cell.DataType?.Value;
        if (type == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText;
        }

        if (type == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < sharedStrings.Count
                ? sharedStrings[index]
                : throw new TransferFormatException(TransferFailure.Malformed, sheet + "!" + cell.CellReference?.Value);
        }

        if (type == CellValues.Boolean)
        {
            return raw == "1" ? "true" : "false";
        }

        if (type == CellValues.Error)
        {
            throw new TransferFormatException(TransferFailure.Malformed, sheet + "!" + cell.CellReference?.Value);
        }

        if (raw is null)
        {
            return null;
        }

        if (type is null || type == CellValues.Number || type == CellValues.Date)
        {
            if (cell.StyleIndex?.Value is { } style && dateStyles.Contains(style)
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
            {
                if (serial != Math.Floor(serial))
                {
                    // A date with a time of day is ambiguous for a date-only field (T-10).
                    throw new TransferFormatException(TransferFailure.Malformed, sheet + "!" + cell.CellReference?.Value);
                }

                var origin = is1904 ? new DateOnly(1904, 1, 1) : new DateOnly(1899, 12, 30);
                return origin.AddDays((int)serial).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        return raw;
    }

    private static HashSet<uint> DateStyles(Stylesheet? stylesheet)
    {
        var result = new HashSet<uint>();
        if (stylesheet?.CellFormats is not { } formats)
        {
            return result;
        }

        var custom = stylesheet.NumberingFormats?.Elements<NumberingFormat>()
            .Where(f => f.NumberFormatId?.Value is not null && IsDateFormat(f.FormatCode?.Value))
            .Select(f => f.NumberFormatId!.Value)
            .ToHashSet() ?? [];
        uint index = 0;
        foreach (var format in formats.Elements<CellFormat>())
        {
            var id = format.NumberFormatId?.Value ?? 0;
            if (BuiltInDateFormats.Contains(id) || custom.Contains(id))
            {
                result.Add(index);
            }

            index++;
        }

        return result;
    }

    /// <summary>A custom format is a date format when it uses day or year tokens outside quoted text and brackets.</summary>
    private static bool IsDateFormat(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        var inQuotes = false;
        var inBrackets = false;
        foreach (var c in code)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case '[' when !inQuotes:
                    inBrackets = true;
                    break;
                case ']' when !inQuotes:
                    inBrackets = false;
                    break;
                case 'd' or 'D' or 'y' or 'Y' when !inQuotes && !inBrackets:
                    return true;
            }
        }

        return false;
    }

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var c in reference)
        {
            if (c is < 'A' or > 'Z')
            {
                break;
            }

            index = (index * 26) + (c - 'A' + 1);
        }

        return Math.Max(0, index - 1);
    }
}
