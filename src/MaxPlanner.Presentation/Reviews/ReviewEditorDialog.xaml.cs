using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MaxPlanner.Presentation.Reviews;

/// <summary>
/// The review editor dialog. Code-behind only forwards clicks and keys to <see cref="ReviewEditorViewModel"/>,
/// moves focus where the ViewModel asks, and uses a full-size dialog in small or short windows (design §5.0).
/// </summary>
public sealed partial class ReviewEditorDialog : ContentDialog
{
    public ReviewEditorDialog(ReviewEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        viewModel.FocusRequested += OnFocusRequested;
        Loaded += (_, _) => ApplyLayout();
        Opened += (_, _) => TitleBox.Focus(FocusState.Programmatic);
        Unloaded += (_, _) =>
        {
            viewModel.FocusRequested -= OnFocusRequested;
            if (XamlRoot is not null)
            {
                XamlRoot.Changed -= OnXamlRootChanged;
            }
        };
    }

    public ReviewEditorViewModel ViewModel { get; }

    private void ApplyLayout()
    {
        if (XamlRoot is not { } root)
        {
            return;
        }

        root.Changed -= OnXamlRootChanged;
        root.Changed += OnXamlRootChanged;
        FullSizeDesired = root.Size.Width < 640 || root.Size.Height < 600;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ApplyLayout();

    private void OnFocusRequested(object? sender, string field)
    {
        // Design §5.0: focus moves to the first invalid field.
        Action? focus = field switch
        {
            "Title" => () => TitleBox.Focus(FocusState.Programmatic),
            "CrNumber" => () => CrNumberBox.Focus(FocusState.Programmatic),
            "TaskNumber" => () => TaskNumberBox.Focus(FocusState.Programmatic),
            "Services" => ServicesPicker.FocusBox,
            "Developer" => DeveloperPicker.FocusBox,
            "Comments" => () => CommentsBox.Focus(FocusState.Programmatic),
            _ => null,
        };
        if (focus is not null)
        {
            DispatcherQueue.TryEnqueue(() => focus());
        }
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // The dialog closes only after the save committed (the ViewModel asks for it); a failure keeps it open.
        args.Cancel = true;
        _ = ViewModel.SaveAsync();
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result != ContentDialogResult.None)
        {
            return;
        }

        // Design §5.8: while the dirty-close confirmation is open it owns the decision, so the editor stays open.
        args.Cancel = ViewModel.IsDirtyClosePromptOpen || !ViewModel.TryClose();
    }

    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = ViewModel.SaveAsync();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => ViewModel.RequestDelete();

    private void OnOpenCrClick(object sender, RoutedEventArgs e) => ViewModel.OpenMatchingCr();

    private void OnReloadClick(object sender, RoutedEventArgs e) => ViewModel.Reload();
}
