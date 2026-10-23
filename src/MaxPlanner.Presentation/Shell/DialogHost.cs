using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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

    public DialogHost(FrameworkElement owner) => _owner = owner;

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
            var dialog = Create(request.ViewModel);
            dialog.XamlRoot = root;

            // Dialogs live outside the root's subtree, so they take the app theme explicitly (design §3 rule 1).
            dialog.RequestedTheme = (root?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
            void Close(object? s, EventArgs e) => dialog.Hide();
            request.ViewModel.CloseRequested += Close;
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
            }

            if (restoreTo is { XamlRoot: not null })
            {
                restoreTo.Focus(FocusState.Programmatic);
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
        ConfirmationViewModel confirmation => Confirmation(confirmation),
        SubtaskCardPromptViewModel prompt => SubtaskPrompt(prompt),
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

    private static ContentDialog SubtaskPrompt(SubtaskCardPromptViewModel viewModel)
    {
        var box = new TextBox { Header = viewModel.SubtitleHeader, MaxLength = 250 };
        AutomationProperties.SetAutomationId(box, "SubtaskPrompt_SubtitleBox");
        AutomationProperties.SetIsRequiredForForm(box, true);
        box.TextChanged += (_, _) => viewModel.Subtitle = box.Text;
        var error = new FieldError();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SubtaskCardPromptViewModel.Error))
            {
                error.Text = viewModel.Error;
            }
        };
        var caption = new TextBlock { Text = viewModel.Caption, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(caption);
        panel.Children.Add(box);
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = viewModel.DialogTitle,
            Content = panel,
            PrimaryButtonText = viewModel.AddButtonText,
            CloseButtonText = viewModel.CancelText,
            DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["DefaultContentDialogStyle"],
        };
        AutomationProperties.SetAutomationId(dialog, "SubtaskPrompt_Dialog");
        dialog.PrimaryButtonClick += (sender, args) =>
        {
            // The card is created first; the ViewModel closes the dialog only on success.
            args.Cancel = true;
            _ = viewModel.CreateAsync();
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        return dialog;
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
}
