using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>
/// Wires the parts every draft editor dialog shares (design §5.0, §5.8, §7.2): <see cref="EditorDialogLayout"/>, the
/// primary button and Ctrl+Enter running Save, the close guard, Alt+Left on an inline step, the first focus, and the
/// editor's focus requests. A dialog keeps only what is its own: its first field, its field-to-control focus map, its
/// selection sync, and its special handlers.
/// </summary>
public static class EditorDialogShell
{
    /// <summary>Call once, after InitializeComponent.</summary>
    /// <param name="dialog">The editor dialog.</param>
    /// <param name="editor">Its view model. Alt+Left is added only when it implements <see cref="IInlineStepEditor"/>.</param>
    /// <param name="keyScope">The element that holds the dialog's content; it owns the Ctrl+Enter and Alt+Left accelerators.</param>
    /// <param name="focusField">Moves focus to the control of a field key the editor asks for.</param>
    /// <param name="firstField">
    /// The control that takes the dialog's first focus, read when the dialog places it; null leaves the dialog's own choice.
    /// </param>
    /// <param name="onLayout">Runs after each resize with the window size (see <see cref="EditorDialogLayout.Attach"/>).</param>
    /// <param name="saveFromFormStepOnly">
    /// Ctrl+Enter saves only on the form step and is left unhandled on an inline step; otherwise it is always handled.
    /// </param>
    public static void Attach(
        ContentDialog dialog,
        IEditorDialogViewModel editor,
        UIElement keyScope,
        Action<string> focusField,
        Func<Control?> firstField,
        Action<Windows.Foundation.Size>? onLayout = null,
        bool saveFromFormStepOnly = false)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(keyScope);
        ArgumentNullException.ThrowIfNull(focusField);
        ArgumentNullException.ThrowIfNull(firstField);

        EditorDialogLayout.Attach(dialog, onLayout);

        // The dialog closes only after the save committed (the ViewModel asks for it); a failure keeps it open.
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            editor.SaveCommand.Execute(null);
        };

        dialog.Closing += (_, args) =>
        {
            if (args.Result != ContentDialogResult.None)
            {
                return;
            }

            // Design §5.8: while the dirty-close confirmation is open it owns the decision, so the editor stays open.
            args.Cancel = editor.IsDirtyClosePromptOpen || !editor.TryClose();
        };

        var steps = editor as IInlineStepEditor;

        // Design §7.2: Ctrl+Enter saves from anywhere; Alt+Left goes back on an inline step.
        var save = new KeyboardAccelerator { Key = VirtualKey.Enter, Modifiers = VirtualKeyModifiers.Control };
        save.Invoked += (_, args) =>
        {
            if (saveFromFormStepOnly && steps is { IsFormStep: false })
            {
                return;
            }

            args.Handled = true;
            editor.SaveCommand.Execute(null);
        };
        keyScope.KeyboardAccelerators.Add(save);

        if (steps is not null)
        {
            var back = new KeyboardAccelerator { Key = VirtualKey.Left, Modifiers = VirtualKeyModifiers.Menu };
            back.Invoked += (_, args) =>
            {
                if (!steps.IsFormStep)
                {
                    args.Handled = true;
                    steps.StepBack();
                }
            };
            keyScope.KeyboardAccelerators.Add(back);
        }

        var initialFocus = AttachFirstFocus(dialog, firstField);

        void OnFocusRequested(object? sender, string field)
        {
            // A field the editor asks for wins over the first field from then on.
            initialFocus.End();
            focusField(field);
        }

        editor.FocusRequested += OnFocusRequested;
        dialog.Unloaded += (_, _) => editor.FocusRequested -= OnFocusRequested;
    }

    // ContentDialog places its own first focus (its first focusable element: a tag field or a chrome button) as it opens,
    // after Opened has run, so focusing the first field in Opened is undone and a later Focus call flickers. Instead,
    // every focus change while the dialog opens is redirected to the first field in GettingFocus, before any element
    // shows focus; the redirect keeps the dialog's programmatic focus state. The device a focus change reports is ignored
    // (the dialog's own first focus can report the mouse, touch or keyboard that opened it): FirstFocusWindow ends the
    // redirect at the first new user input on the dialog, when the editor asks for a field, or once the open has settled
    // (the first queued low-priority item after Opened). A press is seen after the pressed control took focus, so the
    // first press during that short time still lands on the first field.
    private static FirstFocus AttachFirstFocus(ContentDialog dialog, Func<Control?> firstField)
    {
        var state = new FirstFocus(dialog, firstField);
        dialog.GettingFocus += state.OnGettingFocus;
        dialog.AddHandler(UIElement.PointerPressedEvent, state.PointerPressedHandler, true);
        dialog.AddHandler(UIElement.PointerReleasedEvent, state.PointerReleasedHandler, true);
        dialog.AddHandler(UIElement.KeyDownEvent, state.KeyDownHandler, true);
        dialog.AddHandler(UIElement.KeyUpEvent, state.KeyUpHandler, true);
        dialog.Opened += (_, _) => dialog.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, state.End);
        return state;
    }

    private sealed class FirstFocus
    {
        private readonly ContentDialog _dialog;
        private readonly Func<Control?> _firstField;
        private readonly FirstFocusWindow _window = new();
        private bool _isDetached;

        public FirstFocus(ContentDialog dialog, Func<Control?> firstField)
        {
            _dialog = dialog;
            _firstField = firstField;
            PointerPressedHandler = new PointerEventHandler((_, _) => OnInput(FirstFocusInput.PointerPressed, false));
            PointerReleasedHandler = new PointerEventHandler((_, _) => OnInput(FirstFocusInput.PointerReleased, false));
            KeyDownHandler = new KeyEventHandler((_, e) => OnInput(FirstFocusInput.KeyDown, e.KeyStatus.WasKeyDown));
            KeyUpHandler = new KeyEventHandler((_, _) => OnInput(FirstFocusInput.KeyUp, false));
        }

        public PointerEventHandler PointerPressedHandler { get; }

        public PointerEventHandler PointerReleasedHandler { get; }

        public KeyEventHandler KeyDownHandler { get; }

        public KeyEventHandler KeyUpHandler { get; }

        public void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            if (_firstField() is { } target && _window.ShouldRedirect(ReferenceEquals(args.NewFocusedElement, target)))
            {
                args.TrySetNewFocusedElement(target);
            }
        }

        public void End()
        {
            _window.Close();
            Detach();
        }

        private void OnInput(FirstFocusInput input, bool isKeyRepeat)
        {
            if (_window.OnInput(input, isKeyRepeat))
            {
                Detach();
            }
        }

        private void Detach()
        {
            if (_isDetached)
            {
                return;
            }

            _isDetached = true;
            _dialog.GettingFocus -= OnGettingFocus;
            _dialog.RemoveHandler(UIElement.PointerPressedEvent, PointerPressedHandler);
            _dialog.RemoveHandler(UIElement.PointerReleasedEvent, PointerReleasedHandler);
            _dialog.RemoveHandler(UIElement.KeyDownEvent, KeyDownHandler);
            _dialog.RemoveHandler(UIElement.KeyUpEvent, KeyUpHandler);
        }
    }
}
