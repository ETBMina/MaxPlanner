namespace MaxPlanner.Presentation.Common;

/// <summary>
/// The decisions of the "Copied" feedback a copy button gives (the task editor's CR and task number copy icons, the Board
/// card's Task chip), kept free of WinUI so tests can check them. Values may arrive through x:Bind, which turns a null
/// string into an empty one, so null and empty mean the same.
/// </summary>
public static class CopyFeedbackRules
{
    /// <summary>How long the "Copied" hint stays at the button before it closes by itself.</summary>
    public static readonly TimeSpan HintDuration = TimeSpan.FromSeconds(1.5);

    /// <summary>The hint and the announcement follow only a copy that put text on the clipboard.</summary>
    /// <param name="copiedText">The text the button copies.</param>
    /// <param name="reachedClipboard">The clipboard took the text.</param>
    public static bool ShowsFeedback(string? copiedText, bool reachedClipboard) =>
        reachedClipboard && !string.IsNullOrEmpty(copiedText);

    /// <summary>A record's Copy ID item is enabled only for a saved record whose ID text is not empty.</summary>
    /// <param name="isNew">The record is a new, unsaved draft.</param>
    /// <param name="idText">The record's Copy ID text.</param>
    public static bool CanCopyId(bool isNew, string? idText) => !isNew && !string.IsNullOrEmpty(idText);
}
