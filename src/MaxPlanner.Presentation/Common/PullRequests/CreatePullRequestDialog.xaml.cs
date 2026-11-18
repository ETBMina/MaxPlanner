using System.ComponentModel;
using MaxPlanner.Presentation.Common.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace MaxPlanner.Presentation.Common.PullRequests;

/// <summary>
/// The Create pull request dialog. Buttons bind <see cref="CreatePullRequestViewModel"/> commands and
/// <see cref="EditorDialogShell"/> wires the primary button, Ctrl+Enter, the close guard and focus requests; code-behind
/// builds the result text with its inline #id link and moves focus to the result's first action (design §5.11).
/// </summary>
public sealed partial class CreatePullRequestDialog : ContentDialog
{
    // Design §5.11: a single column of about 480 epx; smaller windows still get the shared editor sizing.
    private const double PreferredWidth = 480;

    public CreatePullRequestDialog(CreatePullRequestViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        EditorDialogShell.Attach(this, viewModel, Root, OnFocusRequested, FirstField, ApplyLayout);
        ApplyResultText();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public CreatePullRequestViewModel ViewModel { get; }

    public static InfoBarSeverity ToSeverity(PullRequestResultSeverity severity) => severity switch
    {
        PullRequestResultSeverity.Success => InfoBarSeverity.Success,
        PullRequestResultSeverity.Warning => InfoBarSeverity.Warning,
        PullRequestResultSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    // The fields are locked while setup is incomplete; the first Tab then goes to Open Settings.
    private Control FirstField() => ViewModel.IsSetupIncomplete ? OpenSettingsButton : SourceBox;

    private void ApplyLayout(Windows.Foundation.Size size)
    {
        if (Content is FrameworkElement content && !FullSizeDesired)
        {
            content.Width = Math.Min(PreferredWidth, EditorDialogSizing.ContentWidth(size.Width));
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CreatePullRequestViewModel.ResultMessage) or nameof(CreatePullRequestViewModel.LinkText))
        {
            ApplyResultText();
        }
    }

    // "Pull request #12 created." with #12 as a Hyperlink to the pull request page (the full address is never shown).
    private void ApplyResultText()
    {
        ResultText.Inlines.Clear();
        if (ViewModel.ResultBeforeLink is { Length: > 0 } before)
        {
            ResultText.Inlines.Add(new Run { Text = before });
        }

        if (ViewModel.LinkText is { Length: > 0 } link)
        {
            var hyperlink = new Hyperlink();
            hyperlink.Inlines.Add(new Run { Text = link });
            AutomationProperties.SetAutomationId(hyperlink, "PullRequest_Link");
            AutomationProperties.SetName(hyperlink, ViewModel.LinkName ?? link);
            hyperlink.Click += (_, _) => ViewModel.OpenPullRequestCommand.Execute(null);
            ResultText.Inlines.Add(hyperlink);
        }

        if (ViewModel.ResultAfterLink is { Length: > 0 } after)
        {
            ResultText.Inlines.Add(new Run { Text = after });
        }
    }

    private void OnFocusRequested(string field)
    {
        Action? focus = field switch
        {
            CreatePullRequestViewModel.ResultFocusKey => FocusResult,
            _ => null,
        };
        if (focus is not null)
        {
            DispatcherQueue.TryEnqueue(() => focus());
        }
    }

    // Design §5.11: focus moves to the result's first action: the link, else its button, else the primary button.
    private void FocusResult()
    {
        if (ResultText.Inlines.OfType<Hyperlink>().FirstOrDefault() is { } link && link.Focus(FocusState.Programmatic))
        {
            return;
        }

        foreach (var button in new[] { OpenExistingButton, OpenSettingsButton, TryAgainButton })
        {
            if (button.Visibility == Visibility.Visible && button.Focus(FocusState.Programmatic))
            {
                return;
            }
        }

        SourceBox.Focus(FocusState.Programmatic);
    }
}
