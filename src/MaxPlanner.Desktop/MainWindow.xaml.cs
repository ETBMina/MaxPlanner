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

        // Design §2.1: in LeftMinimal (S) the pane toggle lives in the title bar and opens the overlay pane.
        ShellContent.TitleBarPaneToggleChanged += (_, _) => AppTitleBar.IsPaneToggleButtonVisible = ShellContent.IsTitleBarPaneToggleVisible;
        AppTitleBar.PaneToggleRequested += (_, _) => ShellContent.TogglePane();

        // Design §2.9: the TitleBar Back button pops the Dashboard drill-down stack (Alt+Left does the same).
        ShellContent.BackStateChanged += (_, _) => AppTitleBar.IsBackButtonVisible = ShellContent.CanGoBack;
        AppTitleBar.BackRequested += (_, _) => ShellContent.GoBack();
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>The element that carries the app theme mode (design §3 rule 1).</summary>
    public FrameworkElement RootElement => RootGrid;

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
