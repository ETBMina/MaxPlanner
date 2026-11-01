using System.Globalization;
using System.Text.RegularExpressions;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Transfer.External;

/// <summary>
/// ADR 0027: how external cell text is read for the Created, Checklist items, and custom-field targets. Every rule is
/// strict: a value that does not match is reported, never coerced.
/// </summary>
public static partial class ExternalValues
{
    /// <summary>The kinds a custom field created by an import can have, in the wizard's order.</summary>
    public static IReadOnlyList<FieldKind> CreatableKinds { get; } =
        [FieldKind.Text, FieldKind.MultilineText, FieldKind.Number, FieldKind.Date, FieldKind.Url, FieldKind.Lookup];

    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF",
    ];

    private static readonly string[] OffsetFormats = [.. LocalFormats.Select(f => f + "zzz")];

    private static readonly string[] UtcFormats = [.. LocalFormats.Select(f => f + "'Z'")];

    /// <summary>The cell's text: its text, or a list's items joined with ", " (JSON and YAML arrays); null when blank.</summary>
    public static string? TextOf(ExternalValue? value) =>
        value is null ? null
        : value.Text is { } text ? string.IsNullOrWhiteSpace(text) ? null : text.Trim()
        : value.Items is { Count: > 0 } items ? string.Join(", ", items) : null;

    /// <summary>
    /// A Created value: an ISO date-time with an offset or <c>Z</c>; one without an offset, read as local time in
    /// <paramref name="zone"/>; or an ISO date, read as local midnight. Spreadsheet date cells arrive in these forms.
    /// </summary>
    public static bool TryParseCreated(string text, TimeZoneInfo zone, out DateTimeOffset created)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(zone);
        text = text.Trim();
        var invariant = CultureInfo.InvariantCulture;
        if (DateTimeOffset.TryParseExact(text, OffsetFormats, invariant, DateTimeStyles.None, out created)
            || DateTimeOffset.TryParseExact(text, UtcFormats, invariant, DateTimeStyles.AssumeUniversal, out created))
        {
            return true;
        }

        DateTime local;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", invariant, DateTimeStyles.None, out var date))
        {
            local = date.ToDateTime(TimeOnly.MinValue);
        }
        else if (!DateTime.TryParseExact(text, LocalFormats, invariant, DateTimeStyles.None, out local))
        {
            created = default;
            return false;
        }

        // ADR 0027: a local time in a daylight-saving gap or overlap takes the standard offset (GetUtcOffset's rule), and a
        // value whose UTC instant is outside the DateTimeOffset range is invalid rather than an exception.
        var offset = zone.GetUtcOffset(local);
        var utcTicks = local.Ticks - offset.Ticks;
        if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
        {
            created = default;
            return false;
        }

        created = new DateTimeOffset(local, offset);
        return true;
    }

    /// <summary>
    /// Checklist items from a cell: each non-empty line is one item, in order. <c>[ ]</c>, <c>[x]</c> or <c>[X]</c>
    /// (optionally after <c>-</c> or <c>*</c>) sets the done state; any other line is an item that is not done, without a
    /// leading <c>- </c> or <c>* </c> bullet. Line
    /// breaks are normalized first (ADR 0025); a list (JSON or YAML array) gives one line per entry.
    /// </summary>
    public static IReadOnlyList<ExternalChecklistItem> ParseChecklist(ExternalValue? value)
    {
        if (value is null)
        {
            return [];
        }

        var lines = value.Items ?? (value.Text is { } text ? LineBreaks.Normalize(text).Split('\n') : []);
        var items = new List<ExternalChecklistItem>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = Checkbox().Match(line);
            var itemText = match.Success ? match.Groups["text"].Value.Trim() : Bullet().Replace(line, string.Empty);
            if (itemText.Length > 0)
            {
                items.Add(new ExternalChecklistItem(itemText, match.Success && match.Groups["mark"].Value is "x" or "X"));
            }
        }

        return items;
    }

    /// <summary>
    /// The kind proposed for a new custom field from its column's values (ADR 0027): MultilineText if any value spans
    /// lines; otherwise Date, Number, or Url when every value is one; otherwise Text. Blank values are not considered.
    /// </summary>
    public static FieldKind SuggestKind(IEnumerable<ExternalValue?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var texts = values.Select(TextOf).OfType<string>().ToList();
        if (texts.Count == 0)
        {
            return FieldKind.Text;
        }

        if (texts.Any(t => t.Contains('\n', StringComparison.Ordinal) || t.Contains('\r', StringComparison.Ordinal)))
        {
            return FieldKind.MultilineText;
        }

        // An identifier such as 00123 parses as a number but would lose its leading zeros, so it suggests Text.
        return texts.All(t => TryParseDate(t, out _)) ? FieldKind.Date
            : texts.All(t => TryParseNumber(t, out _) && !HasLeadingZero(t)) ? FieldKind.Number
            : texts.All(t => TryParseUrl(t, out _)) ? FieldKind.Url
            : FieldKind.Text;
    }

    private static bool HasLeadingZero(string text)
    {
        var digits = text.TrimStart('+', '-');
        return digits.Length > 1 && digits[0] == '0' && char.IsAsciiDigit(digits[1]);
    }

    /// <summary>An ISO <c>yyyy-MM-dd</c> date (T-10); spreadsheet date cells arrive in this form.</summary>
    public static bool TryParseDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>An invariant decimal number (sign, decimal point, exponent; no thousands separators).</summary>
    public static bool TryParseNumber(string text, out decimal number) =>
        decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    /// <summary>An absolute http(s) URL, as the Solution design URL and Url fields accept.</summary>
    public static bool TryParseUrl(string text, out HttpUrl url)
    {
        var result = HttpUrl.CreateOptional(text.Trim(), "Custom");
        url = result.IsSuccess ? result.Value! : null!;
        return result is { IsSuccess: true, Value: not null };
    }

    [GeneratedRegex(@"^(?:[-*]\s*)?\[(?<mark>[ xX])\]\s*(?<text>.*)$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Checkbox();

    [GeneratedRegex(@"^[-*]\s+", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Bullet();
}
