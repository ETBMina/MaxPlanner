namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>An input that reaches an editor dialog while it opens.</summary>
public enum FirstFocusInput
{
    PointerPressed,
    PointerReleased,
    KeyDown,
    KeyUp,
}

/// <summary>How focus leaves the dialog's neutral first focus.</summary>
public enum FirstFocusMove
{
    /// <summary>No direction: a click, a tap, or code (a field-error focus request).</summary>
    None,

    /// <summary>Tab.</summary>
    Next,

    /// <summary>Shift+Tab.</summary>
    Previous,

    /// <summary>An arrow key or a game controller direction.</summary>
    Directional,
}

/// <summary>
/// The first focus of an editor dialog (<see cref="EditorDialogShell"/>), kept free of WinUI so tests can check it. The
/// dialog opens with no visible focus: focus sits on the dialog itself (no caret, no highlighted field, its name is what
/// a screen reader announces), and the first Tab goes to the editor's first field.
/// </summary>
/// <remarks>
/// While the dialog opens, every focus change it makes by itself goes to that neutral first focus instead, so WinUI's
/// own first focus never lands on the tag field, More or Close. The device a focus change reports is never part of that
/// decision: the dialog's own first focus can report the mouse, touch or the keyboard that opened it. The opening ends at
/// the first new user input on the dialog (a press, or a key that is not the auto-repeat of a key held since before),
/// when the editor asks for a field, or once the open has settled.
/// </remarks>
public sealed class FirstFocusWindow
{
    /// <summary>Whether the dialog is still opening, so its own focus changes go to the neutral first focus.</summary>
    public bool IsOpen { get; private set; } = true;

    /// <summary>
    /// Whether an input ends the opening. The release of the click, tap or key that opened the dialog arrives after it
    /// opened and is not new input; neither is an auto-repeated key down.
    /// </summary>
    public static bool EndsWindow(FirstFocusInput input, bool isKeyRepeat) => input switch
    {
        FirstFocusInput.PointerPressed => true,
        FirstFocusInput.KeyDown => !isKeyRepeat,
        _ => false,
    };

    /// <summary>
    /// Whether focus leaving the neutral first focus goes to the editor's first field: only the first Tab. Shift+Tab,
    /// arrows, a click and a field-error focus request keep their own target.
    /// </summary>
    public static bool GoesToFirstField(FirstFocusMove move) => move == FirstFocusMove.Next;

    /// <summary>Whether a focus change goes to the neutral first focus instead: while opening, and it is not there yet.</summary>
    public bool ShouldRedirect(bool isNeutral) => IsOpen && !isNeutral;

    /// <summary>Records an input on the dialog; returns whether the opening is now over.</summary>
    public bool OnInput(FirstFocusInput input, bool isKeyRepeat)
    {
        if (EndsWindow(input, isKeyRepeat))
        {
            IsOpen = false;
        }

        return !IsOpen;
    }

    /// <summary>Ends the opening: the open has settled, or the editor asked for a field.</summary>
    public void Close() => IsOpen = false;
}
