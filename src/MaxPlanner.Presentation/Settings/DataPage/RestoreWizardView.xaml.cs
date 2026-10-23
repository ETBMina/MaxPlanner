using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Settings.DataPage;

/// <summary>The Restore wizard of the Data page (design §4.6). Code-behind handles layout only.</summary>
public sealed partial class RestoreWizardView : UserControl
{
    public RestoreWizardView(RestoreWizardViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public RestoreWizardViewModel ViewModel { get; }

    /// <summary>Design §4.6: the step list at L; the "Step n of m" caption at M/S.</summary>
    public void SetLayout(bool isLarge, double padding)
    {
        RestorePanel.Padding = new Thickness(padding, 0, padding, 16);
        RestoreStepList.Visibility = isLarge ? Visibility.Visible : Visibility.Collapsed;
        RestoreCaption.Visibility = isLarge ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRestoreBackupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItem: BackupRowViewModel row })
        {
            ViewModel.SelectedBackup = row;
        }
    }
}
