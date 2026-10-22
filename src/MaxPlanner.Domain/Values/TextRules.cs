using MaxPlanner.Domain.Common;

namespace MaxPlanner.Domain.Values;

internal static class TextRules
{
    /// <summary>Trims single-line input; blank is an error.</summary>
    public static DomainResult<string> RequiredLine(string? raw, int maxLength, string field)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return DomainResult.Failure<string>(DomainErrorCodes.Required, field);
        }

        return trimmed.Length > maxLength
            ? DomainResult.Failure<string>(DomainErrorCodes.TooLong, field, maxLength)
            : DomainResult.Success(trimmed);
    }

    /// <summary>Trims single-line input; blank means "no value".</summary>
    public static DomainResult<string?> OptionalLine(string? raw, int maxLength, string field)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return DomainResult.Success<string?>(null);
        }

        return trimmed.Length > maxLength
            ? DomainResult.Failure<string?>(DomainErrorCodes.TooLong, field, maxLength)
            : DomainResult.Success<string?>(trimmed);
    }

    /// <summary>
    /// Multiline text keeps its line breaks and inner whitespace (T-05: preserve multiline notes);
    /// only text that is entirely whitespace counts as "no value".
    /// </summary>
    public static DomainResult<string?> OptionalMultiline(string? raw, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DomainResult.Success<string?>(null);
        }

        return raw.Length > maxLength
            ? DomainResult.Failure<string?>(DomainErrorCodes.TooLong, field, maxLength)
            : DomainResult.Success<string?>(raw);
    }
}
