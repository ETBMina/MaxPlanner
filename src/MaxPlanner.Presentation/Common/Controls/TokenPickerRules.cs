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

/// <summary>What a press elsewhere in the picker's window or popup does (<see cref="TokenPickerRules.OutsidePress"/>).</summary>
public enum OutsidePressStep
{
    /// <summary>Nothing: the press is in the box itself, or under another root (another popup).</summary>
    None,

    /// <summary>A press on a suggestion: the pick runs, and an on-demand input takes focus back afterwards.</summary>
    Pick,

    /// <summary>Close the suggestion list; focus stays where the press leaves it.</summary>
    CloseList,

    /// <summary>
    /// Close the list and take focus out of the field: a focusable control that was pressed keeps the focus it took,
    /// and a press anywhere else leaves focus on the editor's content with no visible focus.
    /// </summary>
    LeaveField,
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

    /// <summary>
    /// Whether the picker watches presses elsewhere in its window or popup: while its suggestion list is open, and for an
    /// inline field while focus is in it, so a single press outside takes focus out of the field (WinUI moves no focus
    /// on a press on content that takes none, such as an editor's empty space or its labels).
    /// </summary>
    public static bool WatchesOutsidePress(bool isListOpen, bool isInline, bool hasFocusInField) =>
        isListOpen || (isInline && hasFocusInField);

    /// <summary>
    /// What a press does while the picker watches presses. A press on a suggestion is a pick. A press in the box, or
    /// under another root, is left alone. A press elsewhere in an inline field (a chip, its icon, its empty space) only
    /// closes the list, as the field keeps focus. A press outside an inline field leaves it; outside any other picker
    /// it only closes the list.
    /// </summary>
    /// <param name="isInline">The picker is an inline tag field.</param>
    /// <param name="isInSuggestions">The press is in the suggestion list.</param>
    /// <param name="isInBox">The press is in the search box.</param>
    /// <param name="isInField">The press is in the inline field (always false for a picker that is not inline).</param>
    /// <param name="isSameRoot">The press is under the picker's own window or popup root.</param>
    public static OutsidePressStep OutsidePress(bool isInline, bool isInSuggestions, bool isInBox, bool isInField, bool isSameRoot) =>
        isInSuggestions ? OutsidePressStep.Pick
        : isInBox || !isSameRoot ? OutsidePressStep.None
        : isInline && !isInField ? OutsidePressStep.LeaveField
        : OutsidePressStep.CloseList;

    /// <summary>A printable character typed on the focused field (Space included) reveals the input; control keys do not.</summary>
    public static bool RevealsInput(char character) => !char.IsControl(character);

    /// <summary>The text a revealing character starts the query with: the character itself, or nothing for white space.</summary>
    public static string QueryStart(char character) =>
        char.IsWhiteSpace(character) || char.IsControl(character) ? string.Empty : character.ToString();
}
