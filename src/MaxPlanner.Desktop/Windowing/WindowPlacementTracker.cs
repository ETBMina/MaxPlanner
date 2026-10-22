using MaxPlanner.Application.Preferences;
using MaxPlanner.Presentation.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace MaxPlanner.Desktop.Windowing;

/// <summary>
/// Restores the main window's saved bounds at startup and records changes into the preferences
/// (design §2.1, T-09). Use on the UI thread.
/// </summary>
internal sealed partial class WindowPlacementTracker : IDisposable
{
    // design §2.1, in effective pixels.
    private const int FirstRunWidth = 1280;
    private const int FirstRunHeight = 800;
    private const int MinimumExtent = 500;

    private readonly AppWindow _appWindow;
    private readonly nint _windowHandle;
    private readonly IUiPreferencesStore _preferences;
    private readonly ILogger _logger;
    private PixelRect _lastRestoredBounds;
    private bool _isTracking;

    public WindowPlacementTracker(AppWindow appWindow, nint windowHandle, IUiPreferencesStore preferences, ILogger<WindowPlacementTracker> logger)
    {
        _appWindow = appWindow;
        _windowHandle = windowHandle;
        _preferences = preferences;
        _logger = logger;
    }

    /// <summary>Places the window before it is shown, then starts recording changes.</summary>
    public void Restore(WindowPlacementPreference? saved)
    {
        var scale = Scale();
        ApplyMinimumSize(scale);
        var primary = ToPixel(DisplayArea.Primary.WorkArea);
        var decision = WindowPlacementPolicy.Resolve(
            saved,
            ReadWorkAreas(primary),
            primary,
            ((int)Math.Round(FirstRunWidth * scale), (int)Math.Round(FirstRunHeight * scale)),
            ((int)Math.Round(MinimumExtent * scale), (int)Math.Round(MinimumExtent * scale)));

        _appWindow.MoveAndResize(new RectInt32(decision.Bounds.X, decision.Bounds.Y, decision.Bounds.Width, decision.Bounds.Height));
        _lastRestoredBounds = decision.Bounds;
        if (decision.Maximize && _appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        Log.Restored(_logger, saved is null ? "first-run default" : "saved", decision.Maximize);
        _appWindow.Changed += OnChanged;
        _isTracking = true;
    }

    /// <summary>The DPI changed (moved to another monitor): keep the minimum at 500 effective pixels.</summary>
    public void OnDpiChanged(uint dpi) => ApplyMinimumSize(dpi / 96.0);

    /// <summary>Records the current placement now (used when closing).</summary>
    public void Capture()
    {
        var state = (_appWindow.Presenter as OverlappedPresenter)?.State;
        if (state == OverlappedPresenterState.Minimized)
        {
            // A minimized window keeps the placement it had before minimizing.
            return;
        }

        if (state == OverlappedPresenterState.Restored)
        {
            _lastRestoredBounds = new PixelRect(_appWindow.Position.X, _appWindow.Position.Y, _appWindow.Size.Width, _appWindow.Size.Height);
        }

        var workArea = ToPixel(DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest).WorkArea);
        var placement = new WindowPlacementPreference(
            _lastRestoredBounds,
            state == OverlappedPresenterState.Maximized ? WindowStatePreference.Maximized : WindowStatePreference.Normal,
            workArea);
        _preferences.Update(p => p with { Window = placement });
    }

    public void Dispose()
    {
        if (_isTracking)
        {
            _appWindow.Changed -= OnChanged;
            _isTracking = false;
        }
    }

    private static PixelRect ToPixel(RectInt32 rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static List<PixelRect> ReadWorkAreas(PixelRect primary)
    {
        var areas = new List<PixelRect>();
        var displays = DisplayArea.FindAll();

        // Indexed access: enumerating this projected list has thrown InvalidCastException in some
        // Windows App SDK releases.
        for (var i = 0; i < displays.Count; i++)
        {
            areas.Add(ToPixel(displays[i].WorkArea));
        }

        return areas.Count > 0 ? areas : [primary];
    }

    private double Scale()
    {
        var dpi = NativeMethods.GetDpiForWindow(_windowHandle);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    // design §2.1: the presenter minimum is in physical pixels, converted from 500 epx.
    private void ApplyMinimumSize(double scale)
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            var minimum = (int)Math.Round(MinimumExtent * scale);
            presenter.PreferredMinimumWidth = minimum;
            presenter.PreferredMinimumHeight = minimum;
        }
    }

    private void OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
        {
            Capture();
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5201, Level = LogLevel.Information, Message = "Window placed from {Source} bounds (maximized: {Maximized}).")]
        public static partial void Restored(ILogger logger, string source, bool maximized);
    }
}
