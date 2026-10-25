using MaxPlanner.Presentation.ChangeRequests;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Reviews;
using MaxPlanner.Presentation.Settings;
using MaxPlanner.Presentation.SubtaskCards;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Workspace;
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
/// opened them when it still exists (design §7.7). View-side only; use on the UI thread.
/// </summary>
internal sealed class DialogHost
{
    private readonly FrameworkElement _owner;
    private Task _previous = Task.CompletedTask;
    private WorkspaceContext? _context;
    private IDialogViewModel? _open;
    private Action? _resumeWindowClose;

    public DialogHost(FrameworkElement owner) => _owner = owner;

    /// <summary>
    /// Design §5.8: the main window asks before closing. An open editor with unsaved changes shows its dirty prompt
    /// and the close is deferred; <paramref name="resumeClose"/> runs when that editor closes after Save or Discard.
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
            void Close(object? s, EventArgs e) => dialog.Hide();
            dialog.Opened += (_, _) => NameFooterButtons(dialog);
            request.ViewModel.CloseRequested += Close;
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
                _open = null;
            }

            // Design §5.8: the window close that the dirty prompt interrupted continues after Save or Discard.
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
