using MaxPlanner.Domain.Common;

namespace MaxPlanner.Domain.Values;

// Validated text values. Each type can only be created through its factory, so an entity holding
// one never holds blank or oversized text (B-02.10).

public sealed record TitleText
{
    private TitleText(string value) => Value = value;

    public string Value { get; }

    public static DomainResult<TitleText> Create(string? raw, string field = "Title") =>
        TextRules.RequiredLine(raw, DomainLimits.Title, field).Map(v => new TitleText(v));

    public override string ToString() => Value;
}

public sealed record SubtitleText
{
    private const string Ellipsis = "…";

    private SubtitleText(string value) => Value = value;

    public string Value { get; }

    public static DomainResult<SubtitleText> Create(string? raw) =>
        TextRules.RequiredLine(raw, DomainLimits.Subtitle, "Subtitle").Map(v => new SubtitleText(v));

    /// <summary>
    /// The subtitle for a checklist item popped out to a card (B-16.3). Text that fits on one line is
    /// used as is. Longer or multiline text yields its first line, cut at a word boundary and ending in
    /// "…" within the limit; the caller keeps the full text in the card's notes, so nothing is lost.
    /// </summary>
    /// <remarks>
    /// A single word longer than the limit is cut mid-word, never between the halves of a surrogate pair.
    /// </remarks>
    public static SubtitleText FromChecklistText(ChecklistText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var value = text.Value;
        var lineBreak = value.AsSpan().IndexOfAny('\r', '\n');
        if (lineBreak < 0 && value.Length <= DomainLimits.Subtitle)
        {
            return new SubtitleText(value);
        }

        var firstLine = (lineBreak < 0 ? value : value[..lineBreak]).TrimEnd();
        var budget = DomainLimits.Subtitle - Ellipsis.Length;
        if (firstLine.Length > budget)
        {
            firstLine = CutAtWordBoundary(firstLine, budget);
        }

        return new SubtitleText(firstLine + Ellipsis);
    }

    private static string CutAtWordBoundary(string line, int maxLength)
    {
        // Cut at the last whitespace that keeps the prefix within the limit. The character just past
        // the limit counts too: a word ending exactly at the limit is kept whole.
        var cut = maxLength;
        while (cut > 0 && !char.IsWhiteSpace(line[cut]))
        {
            cut--;
        }

        if (string.IsNullOrWhiteSpace(line[..cut]))
        {
            cut = char.IsHighSurrogate(line[maxLength - 1]) ? maxLength - 1 : maxLength;
        }

        return line[..cut].TrimEnd();
    }

    public override string ToString() => Value;
}

/// <summary>A CR number as entered, plus its normalized form used for uniqueness (B-03.2).</summary>
public sealed record CrNumber
{
    private CrNumber(string display)
    {
        Display = display;
        Normalized = display.ToUpperInvariant();
    }

    public string Display { get; }

    /// <summary>Trimmed, ordinal upper-case; two CRs may not share it, whatever their spelling.</summary>
    public string Normalized { get; }

    public static DomainResult<CrNumber> Create(string? raw) =>
        TextRules.RequiredLine(raw, DomainLimits.CrNumber, "Number").Map(v => new CrNumber(v));

    public override string ToString() => Display;
}

/// <summary>An external reference kept exactly as text, including leading zeros (B-03.3). Not unique.</summary>
public sealed record TaskNumber
{
    private TaskNumber(string value) => Value = value;

    public string Value { get; }

    public static DomainResult<TaskNumber?> CreateOptional(string? raw, string field = "TaskNumber") =>
        TextRules.OptionalLine(raw, DomainLimits.TaskNumber, field).Map(v => v is null ? null : new TaskNumber(v));

    public override string ToString() => Value;
}

public sealed record ServiceText
{
    private ServiceText(string value) => Value = value;

    public string Value { get; }

    public static DomainResult<ServiceText?> CreateOptional(string? raw, string field = "Service") =>
        TextRules.OptionalLine(raw, DomainLimits.Service, field).Map(v => v is null ? null : new ServiceText(v));

    public override string ToString() => Value;
}

/// <summary>A lookup display name plus the normalized form used for per-type uniqueness (B-04).</summary>
public sealed record LookupName
{
    private LookupName(string display)
    {
        Display = display;
        Normalized = display.ToUpperInvariant();
    }

    public string Display { get; }

    public string Normalized { get; }

    public static DomainResult<LookupName> Create(string? raw, string field = "Name") =>
        TextRules.RequiredLine(raw, DomainLimits.LookupName, field).Map(v => new LookupName(v));

    public override string ToString() => Display;
}

/// <summary>Notes, descriptions, and review comments: multiline, Unicode, never sanitized.</summary>
public sealed record LongText
{
    private LongText(string value) => Value = value;

    public string Value { get; }

    public static DomainResult<LongText?> CreateOptional(string? raw, string field) =>
        TextRules.OptionalMultiline(raw, DomainLimits.LongText, field).Map(v => v is null ? null : new LongText(v));

    public override string ToString() => Value;
}

public sealed record ChecklistText
{
    private ChecklistText(string value) => Value = value;

    public string Value { get; }

    /// <summary>Blank text is a validation error; deleting an item is a separate explicit command (B-05).</summary>
    public static DomainResult<ChecklistText> Create(string? raw) =>
        TextRules.RequiredLine(raw, DomainLimits.ChecklistText, "Text").Map(v => new ChecklistText(v));

    public override string ToString() => Value;
}

public sealed record EmailSubject
{
    private EmailSubject(string value) => Value = value;

    public string Value { get; }

    public static DomainResult<EmailSubject?> CreateOptional(string? raw) =>
        TextRules.OptionalLine(raw, DomainLimits.EmailSubject, "EmailSubject").Map(v => v is null ? null : new EmailSubject(v));

    public override string ToString() => Value;
}

/// <summary>An absolute http/https link. Other schemes are rejected so they can never be launched (B-02.11).</summary>
public sealed record HttpUrl
{
    private HttpUrl(Uri value) => Value = value;

    public Uri Value { get; }

    public static DomainResult<HttpUrl?> CreateOptional(string? raw, string field = "SolutionDesignUrl")
    {
        var text = TextRules.OptionalLine(raw, DomainLimits.Url, field);
        if (!text.IsSuccess || text.Value is null)
        {
            return text.Map<HttpUrl?>(_ => null);
        }

        return Uri.TryCreate(text.Value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? DomainResult.Success<HttpUrl?>(new HttpUrl(uri))
            : DomainResult.Failure<HttpUrl?>(DomainErrorCodes.InvalidUrl, field);
    }

    public override string ToString() => Value.OriginalString;
}
