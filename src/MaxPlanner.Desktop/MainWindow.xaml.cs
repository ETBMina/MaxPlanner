using MaxPlanner.Presentation.Shell;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

namespace MaxPlanner.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        Title = new ResourceLoader().GetString("AppDisplayName");
        ApplyBackdrop();
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>True when the window uses Mica; false when it uses the solid fallback surface.</summary>
    public bool UsesMica { get; private set; }

    // ADR 0008: Mica when the OS supports it. When it does not, the window gets the solid base
    // brush so content never sits on an undefined surface. With transparency effects turned off,
    // over Remote Desktop, or in power-saving modes, MicaBackdrop itself falls back to a solid
    // color, so that case needs no code here.
    private void ApplyBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
            UsesMica = true;
        }
        else
        {
            RootGrid.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        }
    }
}
