using PlatformControls.Projection;

namespace PlatformControls;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Microsoft.UI.Xaml.Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => SpikeLog.Write($"UNHANDLED {e.Exception}");
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        SpikeHost.Initialize(dispatcherQueue, recordCount: 10_000);
        _window = new MainWindow();
        _window.Activate();
    }
}
