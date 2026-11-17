namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>
/// The display decisions of the shared editor controls, kept free of WinUI so tests can check them. Values arrive
/// through <c>x:Bind</c>, which turns a null string into an empty one, so null and empty must always mean the same.
/// </summary>
public static class EditorControlRules
{
    /// <summary>The reload message most editors use.</summary>
    public const string DefaultReloadMessageKey = "Conflict_ReloadMessage";

    /// <summary>Whether a message bar is open: it has text to show.</summary>
    public static bool IsMessageShown(string? message) => !string.IsNullOrEmpty(message);

    /// <summary>The reload message's x:Uid; none given means <see cref="DefaultReloadMessageKey"/>.</summary>
    public static string ReloadMessageKeyOrDefault(string? key) => string.IsNullOrEmpty(key) ? DefaultReloadMessageKey : key;
}
