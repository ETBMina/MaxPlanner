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

    /// <summary>
    /// Whether an inline tag field shows its search input. A read-only field never does. An always-on input shows while
    /// editable; an on-demand one (the task editor's tags) only once the user revealed it and focus is still in it, or
    /// while it holds typed text, so leaving it empty hides it again.
    /// </summary>
    /// <param name="isEditable">The field can be edited.</param>
    /// <param name="isInputOnDemand">The input hides behind an Add button until it is asked for.</param>
    /// <param name="isRevealed">The input was revealed (Add, a press on the field, a focus request) and has not been left.</param>
    /// <param name="query">The text in the input.</param>
    public static bool ShowsInlineInput(bool isEditable, bool isInputOnDemand, bool isRevealed, string? query) =>
        isEditable && (!isInputOnDemand || isRevealed || !string.IsNullOrEmpty(query));

    /// <summary>An on-demand inline field shows its Add button in place of the hidden input, and only while editable.</summary>
    public static bool ShowsAddButton(bool isEditable, bool isInputOnDemand, bool isInputShown) =>
        isEditable && isInputOnDemand && !isInputShown;
}
