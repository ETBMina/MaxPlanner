using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>
/// Wires the parts every draft editor dialog shares (design §5.0, §5.8, §7.2): <see cref="EditorDialogLayout"/>, the
/// primary button and Ctrl+Enter running Save, the close guard, Alt+Left on an inline step, and the editor's focus
/// requests. A dialog keeps only what is its own: its field-to-control focus map, its selection sync, and its special
/// handlers.
/// </summary>
public static class EditorDialogShell
{
    /// <summary>Call once, after InitializeComponent.</summary>
    /// <param name="dialog">The editor dialog.</param>
    /// <param name="editor">Its view model. Alt+Left is added only when it implements <see cref="IInlineStepEditor"/>.</param>
    /// <param name="keyScope">The element that holds the dialog's content; it owns the Ctrl+Enter and Alt+Left accelerators.</param>
    /// <param name="focusField">Moves focus to the control of a field key the editor asks for.</param>
    /// <param name="onLayout">Runs after each resize with the window size (see <see cref="EditorDialogLayout.Attach"/>).</param>
    /// <param name="saveFromFormStepOnly">
    /// Ctrl+Enter saves only on the form step and is left unhandled on an inline step; otherwise it is always handled.
    /// </param>
    public static void Attach(
        ContentDialog dialog,
        IEditorDialogViewModel editor,
        UIElement keyScope,
        Action<string> focusField,
        Action<Windows.Foundation.Size>? onLayout = null,
        bool saveFromFormStepOnly = false)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(keyScope);
        ArgumentNullException.ThrowIfNull(focusField);

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

        void OnFocusRequested(object? sender, string field) => focusField(field);

        editor.FocusRequested += OnFocusRequested;
        dialog.Unloaded += (_, _) => editor.FocusRequested -= OnFocusRequested;
    }
}
