namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>An input that reaches an editor dialog while it opens.</summary>
public enum FirstFocusInput
{
    PointerPressed,
    PointerReleased,
    KeyDown,
    KeyUp,
}

/// <summary>
/// The time in which an editor dialog's first field takes over the focus the dialog places by itself as it opens
/// (<see cref="EditorDialogShell"/>), kept free of WinUI so tests can check it. The input device a focus change reports
/// is never part of the decision: the dialog's own first focus can report the mouse, touch or the keyboard that opened
/// it. The window closes at the first new user input on the dialog (a press, or a key that is not the auto-repeat of a
/// key held since before), when the editor asks for a field, or once the open has settled.
/// </summary>
public sealed class FirstFocusWindow
{
    /// <summary>Whether the dialog's focus changes still go to the first field.</summary>
    public bool IsOpen { get; private set; } = true;

    /// <summary>
    /// Whether an input ends the window. The release of the click, tap or key that opened the dialog arrives after it
    /// opened and is not new input; neither is an auto-repeated key down.
    /// </summary>
    public static bool EndsWindow(FirstFocusInput input, bool isKeyRepeat) => input switch
    {
        FirstFocusInput.PointerPressed => true,
        FirstFocusInput.KeyDown => !isKeyRepeat,
        _ => false,
    };

    /// <summary>Whether a focus change goes to the first field instead: while the window is open and it is not there yet.</summary>
    public bool ShouldRedirect(bool isFirstField) => IsOpen && !isFirstField;

    /// <summary>Records an input on the dialog; returns whether the window is now closed.</summary>
    public bool OnInput(FirstFocusInput input, bool isKeyRepeat)
    {
        if (EndsWindow(input, isKeyRepeat))
        {
            IsOpen = false;
        }

        return !IsOpen;
    }

    /// <summary>Closes the window: the open has settled, or the editor asked for a field.</summary>
    public void Close() => IsOpen = false;
}
