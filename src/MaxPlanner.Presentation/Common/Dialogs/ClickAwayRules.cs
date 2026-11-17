namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>
/// Click-away in an editor dialog (<see cref="EditorDialogShell"/>, design §5.0), kept free of WinUI so tests can check
/// it. WinUI moves no focus on a press on content that takes none (empty space, labels, panels, fact boxes, the scroll
/// area), so a focused field would otherwise keep its caret and focused look until a press lands on another control.
/// </summary>
public static class ClickAwayRules
{
    /// <summary>
    /// Whether a finished press moves focus to the dialog's neutral focus (as on open: no caret, no focus visual, the
    /// next Tab goes to the first field). Only a press on the dialog's own content that left focus on the field that had
    /// it, and was not inside that field, does. A press on a control that takes focus has moved it; a press in the
    /// focused field (moving the caret, selecting text) keeps it there; a press in a popup (a drop-down, a calendar, a
    /// suggestion list, a menu, the unsaved-changes confirmation) is not on the dialog's content.
    /// </summary>
    /// <param name="isPressOnDialogContent">The press is under the dialog's own root, not in a popup.</param>
    /// <param name="wasFieldFocused">Before the press, focus was on an element of the dialog other than the dialog itself.</param>
    /// <param name="isPressInFocusedField">The press is inside the element that had focus, or its focus area.</param>
    /// <param name="isFocusUnchanged">After the press, focus is still on that element.</param>
    public static bool RestsFocusAfterPress(bool isPressOnDialogContent, bool wasFieldFocused, bool isPressInFocusedField, bool isFocusUnchanged) =>
        isPressOnDialogContent && wasFieldFocused && !isPressInFocusedField && isFocusUnchanged;
}
