using System.Text;
using MaxPlanner.Application.Transfer;
using nietras.SeparatedValues;

namespace MaxPlanner.Infrastructure.Transfer;

/// <summary>
/// One CSV table through Sep (ADR 0012): UTF-8 with a BOM, standard quoting of separators, quotes, and line breaks,
/// explicit headers, ISO dates, and <c>true</c>/<c>false</c> booleans (B-12).
/// </summary>
/// <remarks>
/// T-10 spreadsheet safety: a text cell that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab, or carriage return
/// is written with a leading apostrophe so a spreadsheet shows it as text instead of running it as a formula. That is a
/// documented transfer-text change; reading never strips characters, and lossless exchange uses JSON.
/// </remarks>
internal static class CsvTable
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static void Write(Stream destination, IReadOnlyList<TableColumn> columns, IEnumerable<string?[]> rows, CancellationToken cancellationToken)
    {
        using var text = new StreamWriter(destination, Utf8WithBom, bufferSize: 65536, leaveOpen: true);
        using (var writer = Sep.New(',').Writer(o => o with { Escape = true }).To(text))
        {
            writer.Header.Add([.. columns.Select(c => c.Name)]);
            writer.Header.Write();
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var line = writer.NewRow(cancellationToken);
                for (var i = 0; i < columns.Count; i++)
                {
                    line[columns[i].Name].Set(Neutralize(i < row.Length ? row[i] : null, columns[i].Kind));
                }
            }
        }

        text.Flush();
    }

    public static string Neutralize(string? value, CellKind kind) =>
        kind is CellKind.Text or CellKind.Json && value is { Length: > 0 } && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value
            : value ?? string.Empty;

    /// <summary>Decodes UTF-8 (a BOM is optional); invalid bytes are a malformed file, never silently replaced.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..];
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new TransferFormatException(TransferFailure.Malformed, name, ex);
        }
    }

    /// <param name="separator">The separator of a Max Planner file; null detects it from the header (external files).</param>
    public static RawTable Read(string name, string text, char? separator, CancellationToken cancellationToken)
    {
        try
        {
            var options = separator is { } sep ? Sep.New(sep).Reader(o => o with { Unescape = true, DisableColCountCheck = true })
                : Sep.Reader(o => o with { Unescape = true, DisableColCountCheck = true });
            using var reader = options.FromText(text);
            var headers = reader.Header.ColNames.ToList();
            var rows = new List<string?[]>();
            foreach (var row in reader)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new string?[Math.Max(headers.Count, row.ColCount)];
                for (var i = 0; i < row.ColCount; i++)
                {
                    values[i] = row[i].ToString();
                }

                if (values.Any(v => !string.IsNullOrEmpty(v)))
                {
                    rows.Add(values);
                }
            }

            return new RawTable(name, headers, rows);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException)
        {
            throw new TransferFormatException(TransferFailure.Malformed, name, ex);
        }
    }
}
