namespace MaxPlanner.Domain.Values;

/// <summary>
/// The one line-break form the app stores (ADR 0025): LF. WinUI text boxes return CR, Windows files CR LF, and both
/// must compare equal to the stored text so an unchanged note is never a change.
/// </summary>
public static class LineBreaks
{
    /// <summary>Replaces CR LF and bare CR with LF; other characters, including other Unicode separators, are kept.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains('\r', StringComparison.Ordinal) ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;
    }
}
