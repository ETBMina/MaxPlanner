using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Settings;

/// <summary>The custom field dialog (design §4.6). Code-behind forwards clicks and uses a full-size dialog in small windows.</summary>
public sealed partial class FieldDefinitionDialog : ContentDialog
{
    public FieldDefinitionDialog(FieldDefinitionEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Opened += (_, _) => LabelBox.Focus(FocusState.Programmatic);
        Loaded += (_, _) =>
        {
            if (XamlRoot is { } root)
            {
                FullSizeDesired = root.Size.Width < 640 || root.Size.Height < 600;
            }
        };
    }

    public FieldDefinitionEditorViewModel ViewModel { get; }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // The dialog closes only after the save committed; a failure keeps it open with its errors.
        args.Cancel = true;
        _ = ViewModel.SaveAsync();
    }

    private void OnAddOptionClick(object sender, RoutedEventArgs e) => ViewModel.AddOption();

    private void OnRemoveOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FieldOptionRowViewModel option })
        {
            ViewModel.RemoveOption(option);
        }
    }
}
