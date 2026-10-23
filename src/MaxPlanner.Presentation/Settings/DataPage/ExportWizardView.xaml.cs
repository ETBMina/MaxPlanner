using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Settings.DataPage;

/// <summary>The Export wizard of the Data page (design §4.6). Code-behind handles layout only.</summary>
public sealed partial class ExportWizardView : UserControl
{
    public ExportWizardView(ExportWizardViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public ExportWizardViewModel ViewModel { get; }

    /// <summary>Design §4.6: the step list at L; the "Step n of m" caption at M/S.</summary>
    public void SetLayout(bool isLarge, double padding)
    {
        ExportPanel.Padding = new Thickness(padding, 0, padding, 16);
        ExportStepList.Visibility = isLarge ? Visibility.Visible : Visibility.Collapsed;
        ExportCaption.Visibility = isLarge ? Visibility.Collapsed : Visibility.Visible;
    }
}
