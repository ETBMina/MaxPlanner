using System.Globalization;
using System.Text.Json;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Application.Transfer.External;

namespace MaxPlanner.Infrastructure.Transfer.External;

/// <summary>
/// Reads user-selected external sources (B-13): CSV and XLSX tables, the simple JSON envelope, and folders of Markdown
/// records. Sources are opened read-only and never modified; every T-10 limit is checked before parsing.
/// </summary>
internal sealed class ExternalSourceReader : IExternalSourceReader
{
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = TransferLimits.MaxJsonDepth };

    public Task<AppResult<ExternalSource>> ReadAsync(IReadOnlyList<string> paths, string? generalChecklistPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Task.Run(() => Read(paths, generalChecklistPath, cancellationToken), CancellationToken.None);
    }

    private static AppResult<ExternalSource> Read(IReadOnlyList<string> paths, string? generalChecklistPath, CancellationToken cancellationToken)
    {
        try
        {
            var tables = new List<ExternalTable>();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(path))
                {
                    tables.AddRange(MarkdownRecords.ReadFolder(path, cancellationToken));
                    continue;
                }

                var name = Path.GetFileName(path);
                var bytes = TransferFileReader.ReadLimited(path);
                switch (Path.GetExtension(path).ToUpperInvariant())
                {
                    case ".CSV":
                        tables.Add(FromRaw(CsvTable.Read(name, CsvTable.Decode(bytes, name), separator: null, cancellationToken), ExternalSourceKind.Csv, Suggest(name)));
                        break;
                    case ".XLSX":
                        using (var stream = new MemoryStream(bytes, writable: false))
                        {
                            foreach (var (sheet, raw) in XlsxWorkbook.Read(stream, cancellationToken))
                            {
                                tables.Add(FromRaw(raw with { Name = name + " › " + sheet }, ExternalSourceKind.Xlsx, Suggest(sheet)));
                            }
                        }

                        break;
                    case ".JSON":
                        tables.AddRange(ReadEnvelope(name, bytes));
                        break;
                    case ".MD" or ".MARKDOWN":
                        tables.AddRange(MarkdownRecords.ReadFiles([path], Path.GetDirectoryName(path) ?? string.Empty, cancellationToken));
                        break;
                    default:
                        throw new TransferFormatException(TransferFailure.Malformed, "extension");
                }
            }

            var todos = generalChecklistPath is null ? [] : MarkdownRecords.ReadGeneralChecklist(generalChecklistPath);
            var records = tables.Sum(t => t.Rows.Count) + todos.Count;
            return records > TransferLimits.MaxTopLevelRecords
                ? AppResult.Failure<ExternalSource>(new TransferFileError(TransferFailure.TooLarge, "records"))
                : AppResult.Success(new ExternalSource(tables, todos));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AppResult.Failure<ExternalSource>(OperationCancelledError.Instance);
        }
        catch (TransferFormatException ex)
        {
            return AppResult.Failure<ExternalSource>(new TransferFileError(ex.Failure, ex.Detail));
        }
        catch (JsonException ex)
        {
            return AppResult.Failure<ExternalSource>(new TransferFileError(TransferFailure.Malformed, ex.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return AppResult.Failure<ExternalSource>(new TransferFileError(ex is InvalidDataException ? TransferFailure.Malformed : TransferFailure.Io));
        }
    }

    private static ExternalTable FromRaw(RawTable raw, ExternalSourceKind kind, ExternalEntity? suggested) => new(
        raw.Name,
        kind,
        suggested,
        raw.Headers,
        [.. raw.Rows.Select((row, i) => new ExternalRow(
            raw.Name + "#" + (i + 1).ToString(CultureInfo.InvariantCulture),
            i + 1,
            [.. raw.Headers.Select((_, c) => c < row.Length && row[c] is { } text ? new ExternalValue(text) : null)]))]);

    /// <summary>The entity a table name suggests; the user confirms or changes it in the wizard (B-13.1).</summary>
    internal static ExternalEntity? Suggest(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("review", StringComparison.Ordinal) => ExternalEntity.Review,
            _ when lower.Contains("todo", StringComparison.Ordinal) || lower.Contains("to-do", StringComparison.Ordinal) => ExternalEntity.GeneralTodo,
            _ when lower.Contains("change", StringComparison.Ordinal) || lower.StartsWith("cr", StringComparison.Ordinal) => ExternalEntity.ChangeRequest,
            _ when lower.Contains("task", StringComparison.Ordinal) => ExternalEntity.Task,
            _ => null,
        };
    }

    /// <summary>B-13.1: <c>{ "tasks": [...], "crs": [...], "reviews": [...] }</c> with object records; one table per collection.</summary>
    private static List<ExternalTable> ReadEnvelope(string name, byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, JsonOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("format", out _))
        {
            // A Max Planner export is imported through the app's own format, which keeps its IDs (B-12).
            throw new TransferFormatException(TransferFailure.Malformed, "envelope");
        }

        var tables = new List<ExternalTable>();
        foreach (var collection in root.EnumerateObject())
        {
            ExternalEntity? entity = collection.Name.ToUpperInvariant() switch
            {
                "TASKS" => ExternalEntity.Task,
                "CRS" or "CHANGEREQUESTS" => ExternalEntity.ChangeRequest,
                "REVIEWS" => ExternalEntity.Review,
                "TODOS" or "GENERALTODOS" => ExternalEntity.GeneralTodo,
                _ => null,
            };
            if (entity is null || collection.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var columns = new List<string>();
            var objects = collection.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
            foreach (var property in objects.SelectMany(o => o.EnumerateObject()))
            {
                if (!columns.Contains(property.Name, StringComparer.Ordinal))
                {
                    columns.Add(property.Name);
                }
            }

            var tableName = name + " › " + collection.Name;
            var rows = objects.Select((record, index) => new ExternalRow(
                tableName + "#" + (index + 1).ToString(CultureInfo.InvariantCulture),
                index + 1,
                [.. columns.Select(c => record.TryGetProperty(c, out var value) ? Value(value) : null)])).ToList();
            tables.Add(new ExternalTable(tableName, ExternalSourceKind.Json, entity, columns, rows));
        }

        return tables;
    }

    /// <summary>Scalars become text, arrays of scalars become lists (people, tags, services), objects stay raw JSON text.</summary>
    private static ExternalValue? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => new ExternalValue(value.GetString()),
        JsonValueKind.True => new ExternalValue("true"),
        JsonValueKind.False => new ExternalValue("false"),
        JsonValueKind.Number => new ExternalValue(value.GetRawText()),
        JsonValueKind.Array => new ExternalValue(null, [.. value.EnumerateArray()
            .Where(i => i.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            .Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() ?? string.Empty : i.GetRawText())]),
        _ => new ExternalValue(value.GetRawText()),
    };
}
