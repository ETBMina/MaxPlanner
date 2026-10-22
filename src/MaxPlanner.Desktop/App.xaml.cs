using MaxPlanner.Presentation.Shell;
using Microsoft.Windows.AppLifecycle;

namespace MaxPlanner.Desktop;

// Fully qualified: inside MaxPlanner.*, "Application" resolves to the MaxPlanner.Application namespace.
public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(new ShellViewModel());

        // A second launch of the same folder redirects here (Program.Main); the event arrives on a
        // background thread, so marshal to the window's dispatcher before touching the window.
        AppInstance.GetCurrent().Activated += (_, _) =>
            _window.DispatcherQueue.TryEnqueue(() => NativeMethods.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(_window)));

        _window.Activate();
    }
}
