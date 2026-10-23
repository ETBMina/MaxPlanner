using System.IO.Compression;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Transfer;

namespace MaxPlanner.Infrastructure.Transfer;

/// <summary>
/// Writes transfer files (B-12, T-10). The file is assembled next to the destination under a hidden temporary name and
/// moved over the destination only after a complete, flushed write, so cancellation or failure never replaces a valid
/// existing export.
/// </summary>
internal sealed class TransferFileWriter : ITransferFileWriter
{
    public const string BundleMetadataEntry = "metadata.json";

    public Task<AppResult<string>> WriteAsync(TransferDocument document, TransferFileFormat format, TransferTable? table, string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return Task.Run(() => Write(document, format, table, destinationPath, cancellationToken), CancellationToken.None);
    }

    private static AppResult<string> Write(TransferDocument document, TransferFileFormat format, TransferTable? table, string destinationPath, CancellationToken cancellationToken)
    {
        string? temp = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(destinationPath);
            var directory = Path.GetDirectoryName(full) ?? throw new IOException("No destination folder.");
            temp = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                switch (format)
                {
                    case TransferFileFormat.Json:
                        stream.Write(TransferJson.Serialize(document));
                        break;
                    case TransferFileFormat.Xlsx:
                        XlsxWorkbook.Write(stream, Sheets(document), cancellationToken);
                        break;
                    case TransferFileFormat.CsvTable:
                        var single = table ?? TransferTable.Tasks;
                        CsvTable.Write(stream, TransferTables.Columns(single), TransferTables.Rows(document, single), cancellationToken);
                        break;
                    default:
                        WriteBundle(stream, document, cancellationToken);
                        break;
                }

                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, full, overwrite: true);
            temp = null;
            return AppResult.Success(full);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AppResult.Failure<string>(OperationCancelledError.Instance);
        }
        catch (TransferFormatException ex)
        {
            return AppResult.Failure<string>(new TransferFileError(ex.Failure, ex.Detail));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return AppResult.Failure<string>(new TransferFileError(TransferFailure.Io));
        }
        finally
        {
            if (temp is not null)
            {
                TryDelete(temp);
            }
        }
    }

    private static List<XlsxWorkbook.Sheet> Sheets(TransferDocument document)
    {
        var sheets = TransferTables.All
            .Select(t => new XlsxWorkbook.Sheet(TransferTables.NameOf(t), TransferTables.Columns(t), TransferTables.Rows(document, t)))
            .ToList();
        sheets.Add(new XlsxWorkbook.Sheet(TransferTables.MetadataSheet, TransferTables.MetadataColumns, TransferTables.MetadataRows(document)));
        sheets.Add(new XlsxWorkbook.Sheet(TransferTables.LookupsSheet, TransferTables.LookupColumns, TransferTables.LookupRows(document)));
        sheets.Add(new XlsxWorkbook.Sheet(TransferTables.FieldsSheet, TransferTables.FieldColumns, TransferTables.FieldRows(document)));
        return sheets;
    }

    private static void WriteBundle(Stream stream, TransferDocument document, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var table in TransferTables.All)
        {
            var entry = archive.CreateEntry(TransferTables.NameOf(table) + ".csv", CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            CsvTable.Write(entryStream, TransferTables.Columns(table), TransferTables.Rows(document, table), cancellationToken);
        }

        // B-12: the definitions the tables need (format, lookups, fields, defaults) as a record-free envelope.
        var metadata = document with { Tasks = [], SubtaskCards = [], ChangeRequests = [], Links = [], Reviews = [], GeneralTodos = [] };
        var metadataEntry = archive.CreateEntry(BundleMetadataEntry, CompressionLevel.Optimal);
        using var metadataStream = metadataEntry.Open();
        metadataStream.Write(TransferJson.Serialize(metadata));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover hidden temporary file never replaces the destination; the user can delete it.
        }
    }
}

/// <summary>Reads the app's own transfer files (B-12) read-only, enforcing the T-10 limits before parsing.</summary>
internal sealed class TransferFileReader : ITransferFileReader
{
    public Task<AppResult<TransferDocument>> ReadAsync(string path, TransferTable? table, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() => Read(path, table, cancellationToken), CancellationToken.None);
    }

    /// <summary>Reads a whole input file after checking the T-10 size limit.</summary>
    /// <exception cref="TransferFormatException">The file is larger than the limit.</exception>
    public static byte[] ReadLimited(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Input file not found.");
        }

        if (info.Length > TransferLimits.MaxInputBytes)
        {
            throw new TransferFormatException(TransferFailure.TooLarge, "file size");
        }

        return File.ReadAllBytes(path);
    }

    private static AppResult<TransferDocument> Read(string path, TransferTable? table, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = ReadLimited(path);
            var document = Path.GetExtension(path).ToUpperInvariant() switch
            {
                ".JSON" => TransferJson.Deserialize(bytes),
                ".XLSX" => ReadWorkbook(bytes, cancellationToken),
                ".ZIP" => ReadBundle(bytes, cancellationToken),
                ".CSV" => ReadSingleCsv(Path.GetFileName(path), bytes, table, cancellationToken),
                _ => throw new TransferFormatException(TransferFailure.Malformed, "extension"),
            };
            return document.TopLevelRecordCount > TransferLimits.MaxTopLevelRecords
                ? AppResult.Failure<TransferDocument>(new TransferFileError(TransferFailure.TooLarge, "records"))
                : AppResult.Success(document);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AppResult.Failure<TransferDocument>(OperationCancelledError.Instance);
        }
        catch (TransferFormatException ex)
        {
            return AppResult.Failure<TransferDocument>(new TransferFileError(ex.Failure, ex.Detail));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return AppResult.Failure<TransferDocument>(new TransferFileError(ex is InvalidDataException ? TransferFailure.Malformed : TransferFailure.Io));
        }
    }

    private static TransferDocument ReadWorkbook(byte[] bytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var sheets = XlsxWorkbook.Read(stream, cancellationToken);
        var definitions = TransferTables.ReadDefinitions(
            sheets.GetValueOrDefault(TransferTables.MetadataSheet),
            sheets.GetValueOrDefault(TransferTables.LookupsSheet),
            sheets.GetValueOrDefault(TransferTables.FieldsSheet));
        var tables = new Dictionary<TransferTable, RawTable>();
        foreach (var (name, sheet) in sheets)
        {
            if (TransferTables.TableNamed(name) is { } table)
            {
                tables[table] = sheet;
            }
        }

        return TransferTables.Read(tables, definitions);
    }

    private static TransferDocument ReadBundle(byte[] bytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        // B-12: only the documented entries are accepted, and every name is checked before anything is read.
        ArchiveGuard.Check(archive, name => name == TransferFileWriter.BundleMetadataEntry || (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            && TransferTables.TableNamed(name[..^4]) is not null));
        var remaining = TransferLimits.MaxExpandedBytes;
        var metadataEntry = archive.GetEntry(TransferFileWriter.BundleMetadataEntry) ?? throw new TransferFormatException(TransferFailure.Malformed, TransferFileWriter.BundleMetadataEntry);
        var definitions = TransferJson.Deserialize(ArchiveGuard.ReadBounded(metadataEntry, ref remaining));
        var tables = new Dictionary<TransferTable, RawTable>();
        foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = TransferTables.TableNamed(entry.FullName[..^4])!.Value;
            tables[table] = CsvTable.Read(entry.FullName, CsvTable.Decode(ArchiveGuard.ReadBounded(entry, ref remaining), entry.FullName), ',', cancellationToken)
                with { Name = TransferTables.NameOf(table) };
        }

        return TransferTables.Read(tables, definitions with { Tasks = [], SubtaskCards = [], ChangeRequests = [], Links = [], Reviews = [], GeneralTodos = [] });
    }

    private static TransferDocument ReadSingleCsv(string name, byte[] bytes, TransferTable? table, CancellationToken cancellationToken)
    {
        var raw = CsvTable.Read(name, CsvTable.Decode(bytes, name), ',', cancellationToken);
        var detected = table ?? Detect(raw.Headers) ?? throw new TransferFormatException(TransferFailure.Malformed, "headers");

        // B-12: a single table carries no definitions; its references must resolve against the existing dataset.
        return TransferTables.Read(new Dictionary<TransferTable, RawTable> { [detected] = raw with { Name = TransferTables.NameOf(detected) } }, new TransferDocument());
    }

    private static TransferTable? Detect(IReadOnlyList<string> headers)
    {
        var set = headers.Select(h => h.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set switch
        {
            _ when set.Contains("OwnTitle") => TransferTable.Tasks,
            _ when set.Contains("Number") && set.Contains("OwnerId") => TransferTable.ChangeRequests,
            _ when set.Contains("Subtitle") => TransferTable.SubtaskCards,
            _ when set.Contains("ParentTaskId") && set.Contains("Text") => TransferTable.ChecklistItems,
            _ when set.Contains("CrNumberText") => TransferTable.Reviews,
            _ when set.Contains("Text") && set.Contains("IsCompleted") => TransferTable.GeneralTodos,
            _ => null,
        };
    }
}

/// <summary>The transfer adapters for the composition root (the implementations stay internal to Infrastructure).</summary>
public static class TransferAdapters
{
    public static ITransferFileWriter CreateWriter() => new TransferFileWriter();

    public static ITransferFileReader CreateReader() => new TransferFileReader();

    public static IExternalSourceReader CreateExternalReader() => new External.ExternalSourceReader();
}
