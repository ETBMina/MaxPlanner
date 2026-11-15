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

    /// <summary>
    /// While an editable on-demand field hides its input, the field itself is its one tab stop (the input is the tab stop
    /// once shown; a read-only field has none).
    /// </summary>
    public static bool IsFieldTabStop(bool isEditable, bool isInputOnDemand, bool isInputShown) =>
        isEditable && isInputOnDemand && !isInputShown;

    /// <summary>A printable character typed on the focused field (Space included) reveals the input; control keys do not.</summary>
    public static bool RevealsInput(char character) => !char.IsControl(character);

    /// <summary>The text a revealing character starts the query with: the character itself, or nothing for white space.</summary>
    public static string QueryStart(char character) =>
        char.IsWhiteSpace(character) || char.IsControl(character) ? string.Empty : character.ToString();
}
