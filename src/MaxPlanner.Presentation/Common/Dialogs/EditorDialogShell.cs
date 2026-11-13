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
    // every focus change the dialog makes by itself while it opens is redirected to the first field in GettingFocus,
    // before any element shows focus; the redirect keeps the dialog's programmatic focus state. It ends once the open
    // has settled (the first queued low-priority item after Opened), at the first focus the user moves (a pointer, or a
    // key that navigates), or when the editor asks for a field.
    private static FirstFocus AttachFirstFocus(ContentDialog dialog, Func<Control?> firstField)
    {
        var state = new FirstFocus(dialog, firstField);
        dialog.GettingFocus += state.OnGettingFocus;
        dialog.Opened += (_, _) => dialog.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, state.End);
        return state;
    }

    private sealed class FirstFocus(ContentDialog dialog, Func<Control?> firstField)
    {
        private bool _isEnded;

        public void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            if (IsUserMove(args))
            {
                End();
                return;
            }

            if (firstField() is { } target && !ReferenceEquals(args.NewFocusedElement, target))
            {
                args.TrySetNewFocusedElement(target);
            }
        }

        // A pointer, or a key that navigates (Tab, arrows); the dialog's own programmatic focus is neither.
        private static bool IsUserMove(GettingFocusEventArgs args) => args.InputDevice switch
        {
            FocusInputDeviceKind.Mouse or FocusInputDeviceKind.Touch or FocusInputDeviceKind.Pen => true,
            FocusInputDeviceKind.Keyboard or FocusInputDeviceKind.GameController => args.Direction != FocusNavigationDirection.None,
            _ => false,
        };

        public void End()
        {
            if (!_isEnded)
            {
                _isEnded = true;
                dialog.GettingFocus -= OnGettingFocus;
            }
        }
    }
}
