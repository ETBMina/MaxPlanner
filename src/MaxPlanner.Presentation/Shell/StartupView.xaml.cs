using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Shell;

public sealed partial class StartupView : UserControl
{
    public StartupView()
    {
        InitializeComponent();
    }

    public ShellViewModel? ViewModel { get; set; }
}
