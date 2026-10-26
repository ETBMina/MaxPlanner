using System.ComponentModel;
using MaxPlanner.Presentation.ChangeRequests;
using MaxPlanner.Presentation.Common.Controls;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Reviews;
using MaxPlanner.Presentation.Settings;
using MaxPlanner.Presentation.SubtaskCards;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Workspace;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MaxPlanner.Presentation.Shell;

/// <summary>
/// Shows the dialogs ViewModels request and raises their announcements (design §5.0, §8.3). Dialogs use the
/// window's XamlRoot and current theme, are shown one at a time, and give focus back to the element that
/// opened them when it still exists (design §7.7). A press on a dialog's smoke closes it like Cancel, and an
/// editor's dirty close shows <see cref="DirtyCloseConfirmation"/> above it (design §5.8). View-side only; use on
/// the UI thread.
/// </summary>
internal sealed class DialogHost
{
    private readonly FrameworkElement _owner;
    private Task _previous = Task.CompletedTask;
    private WorkspaceContext? _context;
    private IDialogViewModel? _open;
    private Action? _resumeWindowClose;
    private CancellationTokenSource? _dirtyClosePrompt;

    public DialogHost(FrameworkElement owner) => _owner = owner;

    /// <summary>
    /// Design §5.8: the main window asks before closing. An open editor with unsaved changes shows the dirty-close
    /// confirmation and the close is deferred; <paramref name="resumeClose"/> runs when that editor closes after Save or Discard.
    /// </summary>
    /// <returns>True when the window may close now.</returns>
    public bool TryApproveWindowClose(Action resumeClose)
    {
        ArgumentNullException.ThrowIfNull(resumeClose);
        if (_open is not IWindowCloseGuard guard || guard.TryCloseForWindow())
        {
            return true;
        }

        _resumeWindowClose = guard.IsWindowClosePending ? resumeClose : null;
        return false;
    }

    public void Attach(WorkspaceContext? context)
    {
        if (ReferenceEquals(_context, context))
        {
            return;
        }

        if (_context is not null)
        {
            _context.DialogRequested -= OnDialogRequested;
            _context.Announced -= OnAnnounced;
        }

        _context = context;
        if (context is not null)
        {
            context.DialogRequested += OnDialogRequested;
            context.Announced += OnAnnounced;
        }
    }

    private void OnDialogRequested(object? sender, DialogRequest request) => _ = ShowAsync(request);

    private async Task ShowAsync(DialogRequest request)
    {
        // WinUI allows one open ContentDialog per XamlRoot; requests wait for the previous dialog (design §5.0).
        var previous = _previous;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _previous = done.Task;
        await previous.ConfigureAwait(true);
        try
        {
            var root = _owner.XamlRoot;
            var restoreTo = root is null ? null : FocusManager.GetFocusedElement(root) as UIElement;
            var fallback = FocusFallback.Capture(restoreTo);
            var dialog = Create(request.ViewModel);
            dialog.XamlRoot = root;

            // Dialogs live outside the root's subtree, so they take the app theme explicitly (design §3 rule 1).
            dialog.RequestedTheme = (root?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
            var closeRequested = false;
            void Close(object? s, EventArgs e)
            {
                closeRequested = true;
                dialog.Hide();
            }

            // Design §5.0: Esc, Cancel and an outside click do nothing while the dialog's command runs.
            void OnClosing(ContentDialog s, ContentDialogClosingEventArgs args)
            {
                if (args.Result == ContentDialogResult.None && !closeRequested && request.ViewModel is IBusyDialog { IsSaving: true })
                {
                    args.Cancel = true;
                }
            }

            void OnEditorChanged(object? s, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(IDirtyCloseEditor.IsDirtyClosePromptOpen) && s is IDirtyCloseEditor editor)
                {
                    OnDirtyClosePromptChanged(editor, dialog);
                }
            }

            dialog.Opened += (_, _) =>
            {
                NameFooterButtons(dialog);
                HookOutsidePress(dialog);
            };
            dialog.Closing += OnClosing;
            request.ViewModel.CloseRequested += Close;
            var dirtyEditor = request.ViewModel as IDirtyCloseEditor;
            if (dirtyEditor is not null)
            {
                dirtyEditor.PropertyChanged += OnEditorChanged;
            }

            _open = request.ViewModel;
            try
            {
                var result = await dialog.ShowAsync();
                if (request.ViewModel is ConfirmationViewModel confirmation && result != ContentDialogResult.None)
                {
                    confirmation.Result = result == ContentDialogResult.Primary ? ConfirmationResult.Primary : ConfirmationResult.Secondary;
                }
            }
            finally
            {
                request.ViewModel.CloseRequested -= Close;
                dialog.Closing -= OnClosing;
                if (dirtyEditor is not null)
                {
                    dirtyEditor.PropertyChanged -= OnEditorChanged;
                }

                _dirtyClosePrompt?.Cancel();
                _open = null;
            }

            // Design §5.8: the window close that the dirty-close confirmation interrupted continues after Save or Discard.
            var resume = _resumeWindowClose;
            _resumeWindowClose = null;
            if (resume is not null && request.ViewModel is IWindowCloseGuard { IsWindowClosePending: true })
            {
                resume();
                return;
            }

            // Design §7.7: the invoking element, else the next item of its list, else the list itself.
            if (FocusFallback.IsAlive(restoreTo))
            {
                restoreTo!.Focus(FocusState.Programmatic);
            }
            else
            {
                fallback?.Restore();
            }
        }
        finally
        {
            done.SetResult();
            request.MarkClosed();
        }
    }

    private void OnDirtyClosePromptChanged(IDirtyCloseEditor editor, ContentDialog dialog)
    {
        if (!editor.IsDirtyClosePromptOpen)
        {
            // The editor left the prompt by itself (for example a save or reload started elsewhere).
            _dirtyClosePrompt?.Cancel();
            return;
        }

        if (_dirtyClosePrompt is null && dialog.XamlRoot is { } root)
        {
            _ = ConfirmDirtyCloseAsync(editor, dialog, root);
        }
    }

    /// <summary>
    /// Design §5.8: every dirty-close trigger (Esc, Cancel, an outside click, a follow-up command, the window close)
    /// reaches this through the editor's refused TryClose, so the Save / Discard / Continue editing decision lives in
    /// one place. Save runs the editor's normal save, which closes the editor only when it succeeds.
    /// </summary>
    private async Task ConfirmDirtyCloseAsync(IDirtyCloseEditor editor, ContentDialog dialog, XamlRoot root)
    {
        var returnTo = FocusManager.GetFocusedElement(root) as UIElement;
        using var prompt = new CancellationTokenSource();
        _dirtyClosePrompt = prompt;
        DirtyCloseChoice choice;
        try
        {
            // The trigger may be inside the dialog's Closing handler; the confirmation opens after it returns.
            await Task.Yield();
            choice = prompt.IsCancellationRequested
                ? DirtyCloseChoice.ContinueEditing
                : await DirtyCloseConfirmation.ShowAsync(root, dialog.RequestedTheme, prompt.Token).ConfigureAwait(true);
        }
        finally
        {
            _dirtyClosePrompt = null;
        }

        if (prompt.IsCancellationRequested || !editor.IsDirtyClosePromptOpen)
        {
            return;
        }

        switch (choice)
        {
            case DirtyCloseChoice.Save:
                if (!await editor.SaveFromPromptAsync().ConfigureAwait(true))
                {
                    // The editor stays open with its errors. Its own focus request (the first invalid field) is queued
                    // first; only when nothing in the dialog took focus does focus return to where the user was.
                    _owner.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!IsWithin(FocusManager.GetFocusedElement(root) as DependencyObject, dialog))
                        {
                            RestoreFocus(returnTo);
                        }
                    });
                }

                break;
            case DirtyCloseChoice.Discard:
                editor.DiscardChanges();
                break;
            default:
                editor.ContinueEditing();
                RestoreFocus(returnTo);
                break;
        }
    }

    private static void RestoreFocus(UIElement? element)
    {
        if (FocusFallback.IsAlive(element))
        {
            element!.Focus(FocusState.Programmatic);
        }
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Design §5.0: a press on the smoke around a dialog closes it like Cancel; editors then apply their dirty check
    /// in Closing, and a confirmation's Result stays Cancel. A mouse right-click is ignored.
    /// </summary>
    /// <remarks>
    /// The template's <c>SmokeLayerBackground</c> is not reliably the element that receives the press: in a windowed
    /// app the dialog's smoke can be drawn by a separate popup beneath the dialog's own popup. So the press is caught on
    /// the dialog root (which fills the window) with a bounds check against <c>BackgroundElement</c>, and on any
    /// Rectangle-only popup the dialog opened (that smoke popup). Flyouts and drop-downs opened from the dialog are
    /// separate popups whose presses never route to either.
    /// </remarks>
    private static void HookOutsidePress(ContentDialog dialog)
    {
        var border = FindDescendant(dialog, "BackgroundElement");
        var handler = new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(null);
            if (point.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
            {
                return;
            }

            if (border is not null && IsInside(border, e.GetCurrentPoint(border).Position))
            {
                return;
            }

            e.Handled = true;
            dialog.Hide();
        });

        dialog.AddHandler(UIElement.PointerPressedEvent, handler, handledEventsToo: true);
        if (FindDescendant(dialog, "SmokeLayerBackground") is { } smoke)
        {
            smoke.AddHandler(UIElement.PointerPressedEvent, handler, handledEventsToo: true);
        }

        if (dialog.XamlRoot is { } root)
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            {
                if (popup.Child is Microsoft.UI.Xaml.Shapes.Rectangle layer)
                {
                    layer.AddHandler(UIElement.PointerPressedEvent, handler, handledEventsToo: true);
                }
            }
        }
    }

    private static bool IsInside(FrameworkElement element, Windows.Foundation.Point position) =>
        position.X >= 0 && position.Y >= 0 && position.X <= element.ActualWidth && position.Y <= element.ActualHeight;

    private static ContentDialog Create(IDialogViewModel viewModel) => viewModel switch
    {
        TaskEditorViewModel editor => new TaskEditorDialog(editor),
        CopyTaskViewModel copy => new CopyTaskDialog(copy),
        SubtaskCardEditorViewModel subtaskCard => new SubtaskCardEditorDialog(subtaskCard),
        CrEditorViewModel changeRequest => new CrEditorDialog(changeRequest),
        ReviewEditorViewModel review => new ReviewEditorDialog(review),
        LookupRemovalViewModel removal => new LookupRemovalDialog(removal),
        FieldDefinitionEditorViewModel field => new FieldDefinitionDialog(field),
        ConfirmationViewModel confirmation => Confirmation(confirmation),
        _ => throw new NotSupportedException($"No dialog is registered for {viewModel.GetType().Name}."),
    };

    // Design §5.7: a verb-labelled primary action, an optional alternative, and Cancel as the default button.
    private static ContentDialog Confirmation(ConfirmationViewModel viewModel)
    {
        var dialog = new ContentDialog
        {
            Title = viewModel.Title,
            Content = new TextBlock { Text = viewModel.Message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = viewModel.PrimaryText,
            SecondaryButtonText = viewModel.SecondaryText ?? string.Empty,
            CloseButtonText = viewModel.CloseText,
            DefaultButton = ContentDialogButton.Close,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["DefaultContentDialogStyle"],
        };
        AutomationProperties.SetAutomationId(dialog, viewModel.AutomationId);
        return dialog;
    }

    /// <summary>
    /// Design §8.1: the footer buttons are template parts whose default AutomationIds ("PrimaryButton", "CloseButton")
    /// repeat in every dialog. Prefixing them with the dialog's area ("DeleteTask_PrimaryButton") makes them stable,
    /// unique UI Automation targets. Runs once the template exists.
    /// </summary>
    private static void NameFooterButtons(ContentDialog dialog)
    {
        var dialogId = AutomationProperties.GetAutomationId(dialog);
        if (string.IsNullOrEmpty(dialogId))
        {
            return;
        }

        var area = dialogId.EndsWith("_Dialog", StringComparison.Ordinal) ? dialogId[..^"_Dialog".Length] : dialogId;
        foreach (var part in new[] { "PrimaryButton", "SecondaryButton", "CloseButton" })
        {
            if (FindDescendant(dialog, part) is { } button && string.IsNullOrEmpty(AutomationProperties.GetAutomationId(button)))
            {
                AutomationProperties.SetAutomationId(button, area + "_" + part);
            }
        }
    }

    private static FrameworkElement? FindDescendant(DependencyObject parent, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name)
            {
                return element;
            }

            if (FindDescendant(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // Design §8.3: announcements use the UIA notification event; failures interrupt (ActionAborted).
    private void OnAnnounced(object? sender, Announcement announcement)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(_owner) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_owner);
        peer?.RaiseNotificationEvent(
            announcement.IsFailure ? AutomationNotificationKind.ActionAborted : AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent,
            announcement.Text,
            announcement.IsFailure ? "CommandFailed" : "CommandCompleted");
    }

    /// <summary>
    /// Design §7.7 fallback when the element that opened a dialog is gone after it closes (for example a deleted
    /// card): the item now at the same index of the same list, else the list itself.
    /// </summary>
    private sealed class FocusFallback
    {
        private readonly WeakReference<ListViewBase> _list;
        private readonly int _index;

        private FocusFallback(ListViewBase list, int index)
        {
            _list = new WeakReference<ListViewBase>(list);
            _index = index;
        }

        public static bool IsAlive(UIElement? element) => element switch
        {
            null => false,
            FrameworkElement framework => framework.IsLoaded && framework.XamlRoot is not null && IsShown(framework),
            _ => element.XamlRoot is not null && IsShown(element),
        };

        // A page left during the dialog (a drill-down) stays loaded but collapsed, and its elements cannot take focus.
        private static bool IsShown(DependencyObject element)
        {
            for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is UIElement { Visibility: Visibility.Collapsed })
                {
                    return false;
                }
            }

            return true;
        }

        public static FocusFallback? Capture(UIElement? focused)
        {
            DependencyObject? current = focused;
            SelectorItem? container = null;
            while (current is not null)
            {
                if (current is SelectorItem item && container is null)
                {
                    container = item;
                }

                if (current is ListViewBase list)
                {
                    return new FocusFallback(list, container is null ? -1 : list.IndexFromContainer(container));
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        public void Restore()
        {
            if (!_list.TryGetTarget(out var list) || !IsAlive(list))
            {
                return;
            }

            var count = list.Items.Count;
            if (_index >= 0 && count > 0 && list.ContainerFromIndex(Math.Min(_index, count - 1)) is Control next)
            {
                next.Focus(FocusState.Programmatic);
                return;
            }

            list.Focus(FocusState.Programmatic);
        }
    }
}
