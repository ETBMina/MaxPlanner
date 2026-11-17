using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    // Each attached dialog's neutral focus, for RestFocus; it goes with the dialog.
    private static readonly ConditionalWeakTable<ContentDialog, FirstFocus> NeutralFocus = new();

    /// <summary>
    /// Moves focus back to the neutral focus of the editor dialog that holds <paramref name="element"/>, as when it
    /// opened (design §5.0, Focus on open): on the dialog's content, with no caret and no highlighted field; the next Tab
    /// goes to the editor's first field. For a press that took focus out of a field without landing on a control.
    /// </summary>
    /// <returns>False when the element is not inside an editor dialog, or the dialog could not take focus.</returns>
    public static bool RestFocus(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ContentDialog dialog && NeutralFocus.TryGetValue(dialog, out var state))
            {
                return state.Rest();
            }
        }

        return false;
    }

    /// <summary>Call once, after InitializeComponent.</summary>
    /// <param name="dialog">The editor dialog.</param>
    /// <param name="editor">Its view model. Alt+Left is added only when it implements <see cref="IInlineStepEditor"/>.</param>
    /// <param name="keyScope">The element that holds the dialog's content; it owns the Ctrl+Enter and Alt+Left accelerators.</param>
    /// <param name="focusField">Moves focus to the control of a field key the editor asks for.</param>
    /// <param name="firstField">
    /// The editor's first field, where the first Tab goes from the dialog's neutral first focus; read at that Tab. Null
    /// lets that Tab go to the first focusable element.
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

    // Editors open with no visible focus (owner decision): focus sits on the dialog itself, made a tab stop without system
    // focus visuals for the opening only, so there is no caret and no highlighted field, and a screen reader announces the
    // dialog by its own name. ContentDialog places its own first focus (its first focusable element: a tag field or a
    // chrome button) as it opens, after Opened has run; every focus change while it opens is redirected to the dialog in
    // GettingFocus, before any element shows focus, whatever device it reports (FirstFocusWindow). The opening ends at the
    // first new user input on the dialog, when the editor asks for a field, or at the first queued low-priority item after
    // Opened. A press is seen after the pressed control took focus, so a press during that short time still lands on the
    // dialog. When focus leaves the dialog, the first Tab goes to the first field, and the dialog leaves the Tab cycle
    // again, so later Tab and Shift+Tab cycle the fields as before.
    private static FirstFocus AttachFirstFocus(ContentDialog dialog, Func<Control?> firstField)
    {
        var state = new FirstFocus(dialog, firstField);
        NeutralFocus.AddOrUpdate(dialog, state);
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
        private readonly bool _wasTabStop;
        private readonly bool _usedSystemFocusVisuals;
        private bool _isInputDetached;
        private bool _isNeutralReleased;

        public FirstFocus(ContentDialog dialog, Func<Control?> firstField)
        {
            _dialog = dialog;
            _firstField = firstField;
            _wasTabStop = dialog.IsTabStop;
            _usedSystemFocusVisuals = dialog.UseSystemFocusVisuals;
            dialog.IsTabStop = true;
            dialog.UseSystemFocusVisuals = false;
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
            if (_window.ShouldRedirect(ReferenceEquals(args.NewFocusedElement, _dialog)))
            {
                args.TrySetNewFocusedElement(_dialog);
                return;
            }

            if (!ReferenceEquals(args.OldFocusedElement, _dialog) || ReferenceEquals(args.NewFocusedElement, _dialog))
            {
                return;
            }

            // Focus leaves the neutral first focus: the first Tab goes to the first field; a click, Shift+Tab, an arrow
            // or a field-error focus request keeps its own target.
            if (FirstFocusWindow.GoesToFirstField(MoveOf(args.Direction)) && _firstField() is { } first)
            {
                args.TrySetNewFocusedElement(first);
            }

            // The dialog still has focus during GettingFocus, so it leaves the Tab cycle once focus has moved.
            _dialog.DispatcherQueue.TryEnqueue(ReleaseNeutral);
        }

        public void End()
        {
            _window.Close();
            DetachInput();

            // A redirect that did not take leaves the dialog unfocused; it must not stay in the Tab cycle.
            if (_dialog.FocusState == FocusState.Unfocused)
            {
                ReleaseNeutral();
            }
        }

        // The neutral focus again, after the opening: the dialog rejoins the Tab cycle without focus visuals until focus
        // leaves it, and the first Tab from it goes to the first field, exactly as on open.
        public bool Rest()
        {
            if (_isNeutralReleased)
            {
                _isNeutralReleased = false;
                _dialog.GettingFocus += OnGettingFocus;
            }

            _dialog.IsTabStop = true;
            _dialog.UseSystemFocusVisuals = false;
            if (_dialog.Focus(FocusState.Programmatic))
            {
                return true;
            }

            ReleaseNeutral();
            return false;
        }

        private static FirstFocusMove MoveOf(FocusNavigationDirection direction) => direction switch
        {
            FocusNavigationDirection.None => FirstFocusMove.None,
            FocusNavigationDirection.Next => FirstFocusMove.Next,
            FocusNavigationDirection.Previous => FirstFocusMove.Previous,
            _ => FirstFocusMove.Directional,
        };

        private void OnInput(FirstFocusInput input, bool isKeyRepeat)
        {
            if (_window.OnInput(input, isKeyRepeat))
            {
                DetachInput();
            }
        }

        private void DetachInput()
        {
            if (_isInputDetached)
            {
                return;
            }

            _isInputDetached = true;
            _dialog.RemoveHandler(UIElement.PointerPressedEvent, PointerPressedHandler);
            _dialog.RemoveHandler(UIElement.PointerReleasedEvent, PointerReleasedHandler);
            _dialog.RemoveHandler(UIElement.KeyDownEvent, KeyDownHandler);
            _dialog.RemoveHandler(UIElement.KeyUpEvent, KeyUpHandler);
        }

        private void ReleaseNeutral()
        {
            if (_isNeutralReleased || _window.IsOpen)
            {
                return;
            }

            _isNeutralReleased = true;
            _dialog.GettingFocus -= OnGettingFocus;
            _dialog.IsTabStop = _wasTabStop;
            _dialog.UseSystemFocusVisuals = _usedSystemFocusVisuals;
        }
    }
}
