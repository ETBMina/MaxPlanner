namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// The display decisions of <see cref="TokenPicker"/>, kept free of WinUI so tests can check them. Names arrive through
/// <c>x:Bind</c>, which turns a null string into an empty one, so null and empty mean the same.
/// </summary>
public static class TokenPickerRules
{
    /// <summary>A person dropdown shows the coloured avatar only for a person it shows by name.</summary>
    public static bool ShowsPersonAvatar(bool isSingleSelection, string? shownPersonName) =>
        isSingleSelection && !string.IsNullOrEmpty(shownPersonName);

    /// <summary>A person dropdown that shows nobody ("Choose a person", or while typing) shows a neutral person outline.</summary>
    public static bool ShowsEmptyPersonIcon(bool isSingleSelection, string? shownPersonName) =>
        isSingleSelection && string.IsNullOrEmpty(shownPersonName);
}
