namespace GridGate;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Microsoft.UI.Xaml.Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // "--fallback" opens the ListView comparison (ADR 0004 option 2) instead of the TableView candidate.
        var saveService = new FakeSaveService();
        _window = Environment.GetCommandLineArgs().Contains("--fallback", StringComparer.OrdinalIgnoreCase)
            ? new FallbackWindow(saveService)
            : new MainWindow(saveService);
        _window.Activate();
    }
}
