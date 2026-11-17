namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>What Esc does in the on-demand tag input (<see cref="TokenPickerRules.TagInputEscape"/>).</summary>
public enum TagInputEscapeStep
{
    /// <summary>Close the open suggestion list.</summary>
    CloseList,

    /// <summary>Clear the typed text.</summary>
    ClearText,

    /// <summary>Leave the empty input for the field itself.</summary>
    LeaveInput,
}

/// <summary>What a reveal of an on-demand input does after one try to focus its text box.</summary>
public enum InputRevealStep
{
    /// <summary>The text box has keyboard focus (a caret): the reveal is done.</summary>
    Focused,

    /// <summary>The text box is not loaded yet: try again when its Loaded event arrives.</summary>
    WaitForLoad,

    /// <summary>Focus still did not move after every try: hide the input again.</summary>
    GiveUp,
}

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

    /// <summary>
    /// The order of a reveal: the input is made visible and laid out first, then its text box is focused. A text box that
    /// was collapsed until now may not be loaded on the first try; that try waits for its Loaded event and tries once
    /// more. A loaded text box that refuses focus, or a second failed try, gives up, so the input never waits forever.
    /// </summary>
    /// <param name="focused">This try gave the text box focus.</param>
    /// <param name="isTextBoxLoaded">The text box was loaded when this try ran.</param>
    /// <param name="attempt">The zero-based number of this try.</param>
    public static InputRevealStep NextRevealStep(bool focused, bool isTextBoxLoaded, int attempt) =>
        focused ? InputRevealStep.Focused
        : !isTextBoxLoaded && attempt == 0 ? InputRevealStep.WaitForLoad
        : InputRevealStep.GiveUp;

    /// <summary>
    /// A line of the inline field is as tall as its tallest child and never less than <paramref name="minLineHeight"/>,
    /// so chips on a line without the input are centred in the input's height and the field does not change height
    /// when the input shows.
    /// </summary>
    public static double FieldLineHeight(double tallestChild, double minLineHeight) => Math.Max(tallestChild, minLineHeight);

    /// <summary>
    /// What Esc does in the on-demand tag input: it closes an open suggestion list first, then clears typed text, and with
    /// nothing left to undo it leaves the input for the field itself (which hides the empty input), so a further Esc
    /// closes the dialog. It never restores text typed before a pick.
    /// </summary>
    public static TagInputEscapeStep TagInputEscape(bool isListOpen, string? query) =>
        isListOpen ? TagInputEscapeStep.CloseList
        : !string.IsNullOrEmpty(query) ? TagInputEscapeStep.ClearText
        : TagInputEscapeStep.LeaveInput;

    /// <summary>A printable character typed on the focused field (Space included) reveals the input; control keys do not.</summary>
    public static bool RevealsInput(char character) => !char.IsControl(character);

    /// <summary>The text a revealing character starts the query with: the character itself, or nothing for white space.</summary>
    public static string QueryStart(char character) =>
        char.IsWhiteSpace(character) || char.IsControl(character) ? string.Empty : character.ToString();
}
