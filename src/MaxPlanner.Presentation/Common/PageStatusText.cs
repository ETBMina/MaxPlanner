namespace MaxPlanner.Presentation.Common;

/// <summary>
/// The status bar's left text (design §2.8, U-10): only transient or page-specific status that the page subtitle does
/// not already say, such as "Filtered: 12 of 40 cards". A status that only repeats the subtitle shows nothing.
/// </summary>
public static class PageStatusText
{
    /// <summary>The text to show: <paramref name="status"/>, or empty when it is blank or equal to <paramref name="subtitle"/>.</summary>
    public static string ForStatusBar(string? status, string? subtitle) =>
        string.IsNullOrEmpty(status) || string.Equals(status, subtitle, StringComparison.Ordinal) ? string.Empty : status;
}
