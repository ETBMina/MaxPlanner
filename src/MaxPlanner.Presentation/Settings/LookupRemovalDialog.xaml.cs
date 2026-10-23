using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Settings;

/// <summary>The lookup removal dialog (design §5.9). Code-behind forwards the primary click and the mode choice.</summary>
public sealed partial class LookupRemovalDialog : ContentDialog
{
    public LookupRemovalDialog(LookupRemovalViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        var strings = new ResourceStrings();
        ModeRadios.ItemsSource = new[] { strings.GetString("LookupRemove_ReplaceOption"), strings.GetString("LookupRemove_ClearOption") };
        ModeRadios.SelectedIndex = 0;
    }

    public LookupRemovalViewModel ViewModel { get; }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // The dialog closes only after the removal committed (the ViewModel asks for it); a failure keeps it open.
        args.Cancel = true;
        _ = ViewModel.RemoveAsync();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e) => ViewModel.ClearInstead = ModeRadios.SelectedIndex == 1;
}
