using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Shell;

/// <summary>The startup splash (design §2.1), shown while the shell is starting.</summary>
public sealed partial class StartupView : UserControl
{
    public StartupView()
    {
        InitializeComponent();
    }

    public ShellViewModel? ViewModel { get; set; }

    /// <summary>The app icon image. The app sets it, because the icon is an asset of the app, not of this library.</summary>
    public Uri? IconUri
    {
        get => IconImage.UriSource;
        set => IconImage.UriSource = value;
    }
}
