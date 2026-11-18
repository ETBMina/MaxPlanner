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

    /// <summary>
    /// Whether the CR and Subtask card editors' More button is enabled (design §5.11): never for a record not saved yet;
    /// otherwise while Delete… can run, or while Create pull request… is offered, which needs no write and so also works
    /// on a read-only record.
    /// </summary>
    public static bool IsMoreMenuEnabled(bool isNew, bool canDelete, bool isPullRequestAvailable) =>
        !isNew && (canDelete || isPullRequestAvailable);
}
