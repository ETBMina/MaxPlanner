using System.Reactive.Concurrency;
using MaxPlanner.Desktop.Composition;
using MaxPlanner.Desktop.Startup;
using MaxPlanner.Desktop.Windowing;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.Windows.AppLifecycle;

namespace MaxPlanner.Desktop;

// Fully qualified: inside MaxPlanner.*, "Application" resolves to the MaxPlanner.Application namespace.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The Application object lives for the whole process and is never disposed; its window helpers are disposed at the end of ShutdownAsync.")]
public partial class App : Microsoft.UI.Xaml.Application
{
    private readonly StartupContext _context;
    private readonly ILogger _logger;
    private ServiceProvider? _services;
    private MainWindow? _window;
    private StartupCoordinator? _coordinator;
    private WindowPlacementTracker? _placement;
    private WindowMessageHook? _messageHook;
    private SystemDayChangeMonitor? _dayMonitor;
    private ShutdownState _shutdown;

    internal App(StartupContext context)
    {
        _context = context;
        _logger = context.LoggerFactory.CreateLogger<App>();
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    private enum ShutdownState
    {
        Running,
        InProgress,
        Completed,
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _services = AppComposition.Build(_context, DispatcherQueue.GetForCurrentThread(), BeginClose);
        var preferences = _context.LoadedPreferences.Preferences;
        var window = new MainWindow(_services.GetRequiredService<ShellViewModel>());
        _window = window;
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);

        _services.GetRequiredService<ThemeService>().Attach(window.RootElement, window.AppWindow.TitleBar);
        _services.GetRequiredService<ThemeService>().Apply(preferences.Theme);
        _placement = new WindowPlacementTracker(window.AppWindow, windowHandle, _context.Preferences, _services.GetRequiredService<ILogger<WindowPlacementTracker>>());
        _placement.Restore(preferences.Window);

        _messageHook = new WindowMessageHook(windowHandle, _services.GetRequiredService<ILogger<WindowMessageHook>>());
        _messageHook.DpiChanged += (_, dpi) => _placement.OnDpiChanged(dpi);
        _dayMonitor = new SystemDayChangeMonitor(
            window, _messageHook, _services.GetRequiredService<LocalDayBoundarySource>(), _services.GetRequiredService<ILogger<SystemDayChangeMonitor>>());

        // A second launch of the same folder redirects here (Program.Main); the event arrives on a
        // background thread, so marshal to the window's dispatcher before touching the window.
        AppInstance.GetCurrent().Activated += (_, _) =>
            window.DispatcherQueue.TryEnqueue(() => NativeMethods.BringToFront(windowHandle));

        window.AppWindow.Closing += OnClosing;
        window.Activate();
        var elapsedMs = _context.ElapsedMilliseconds;
        Log.WindowShown(_logger, elapsedMs);

        _coordinator = _services.GetRequiredService<StartupCoordinator>();
        _ = _coordinator.RunAsync();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdown == ShutdownState.Completed)
        {
            return;
        }

        // T-06.6: the first close is cancelled so commits can drain without blocking the UI thread; the
        // window closes for real once ShutdownAsync has finished.
        args.Cancel = true;
        BeginClose();
    }

    private void BeginClose()
    {
        if (_shutdown != ShutdownState.Running)
        {
            return;
        }

        _shutdown = ShutdownState.InProgress;
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        var started = _context.ElapsedMilliseconds;
        try
        {
            _placement?.Capture();
            if (_coordinator is not null)
            {
                await _coordinator.ShutdownAsync();
                _context.ExitCode = _coordinator.ExitCode;
            }

            await _context.Preferences.FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Closing must finish even after a failure; committed data is already durable (T-06.1).
            Log.ShutdownFailed(_logger, ex);
        }
        finally
        {
            _dayMonitor?.Dispose();
            _placement?.Dispose();
            _messageHook?.Dispose();
            _services?.GetRequiredService<ShellViewModel>().Dispose();
            if (_services is not null)
            {
                await _services.DisposeAsync();
            }

            var elapsedMs = _context.ElapsedMilliseconds - started;
            Log.ShutdownCompleted(_logger, elapsedMs);
            _shutdown = ShutdownState.Completed;
            _window?.Close();
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.UnhandledUiException(_logger, e.Exception);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5601, Level = LogLevel.Information, Message = "Window shown at {ElapsedMs} ms after launch.")]
        public static partial void WindowShown(ILogger logger, long elapsedMs);

        [LoggerMessage(EventId = 5602, Level = LogLevel.Information, Message = "Shutdown completed in {ElapsedMs} ms.")]
        public static partial void ShutdownCompleted(ILogger logger, long elapsedMs);

        [LoggerMessage(EventId = 5603, Level = LogLevel.Error, Message = "Shutdown failed; closing anyway.")]
        public static partial void ShutdownFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 5604, Level = LogLevel.Critical, Message = "Unhandled exception on the UI thread.")]
        public static partial void UnhandledUiException(ILogger logger, Exception exception);
    }
}
