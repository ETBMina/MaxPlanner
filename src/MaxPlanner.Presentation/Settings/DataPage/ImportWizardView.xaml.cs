using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Settings.DataPage;

/// <summary>The Import wizard of the Data page (design §4.6). Code-behind handles layout only.</summary>
public sealed partial class ImportWizardView : UserControl
{
    public ImportWizardView(ImportWizardViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public ImportWizardViewModel ViewModel { get; }

    /// <summary>Design §4.6: the step list at L; the "Step n of m" caption at M/S.</summary>
    public void SetLayout(bool isLarge, double padding)
    {
        ImportPanel.Padding = new Thickness(padding, 0, padding, 16);
        ImportStepList.Visibility = isLarge ? Visibility.Visible : Visibility.Collapsed;
        ImportCaption.Visibility = isLarge ? Visibility.Collapsed : Visibility.Visible;
    }
}
