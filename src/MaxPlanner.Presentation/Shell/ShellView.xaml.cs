using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Shell;

public sealed partial class ShellView : UserControl
{
    public ShellView()
    {
        InitializeComponent();
    }

    public ShellViewModel? ViewModel { get; set; }
}
