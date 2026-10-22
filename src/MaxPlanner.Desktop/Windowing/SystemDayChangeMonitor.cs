using MaxPlanner.Presentation.Common.Store;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.System.Power;

namespace MaxPlanner.Desktop.Windowing;

/// <summary>
/// Feeds the operating-system signals that may change the local date into <see cref="LocalDayBoundarySource"/>
/// (T-07): window activation, clock or time-zone changes, and resume from sleep. The midnight timer itself
/// lives in the source.
/// </summary>
/// <remarks>
/// Resume is detected through the Windows App SDK <see cref="PowerManager.SystemSuspendStatusChanged"/> event
/// (confirmed with <c>winapp find-api</c>) and also through <c>WM_POWERBROADCAST</c>; a duplicate check is
/// harmless because the source emits only when the date really changed.
/// </remarks>
internal sealed partial class SystemDayChangeMonitor : IDisposable
{
    private readonly Window _window;
    private readonly WindowMessageHook _hook;
    private readonly LocalDayBoundarySource _days;
    private readonly ILogger _logger;
    private readonly bool _isPowerSubscribed;

    public SystemDayChangeMonitor(Window window, WindowMessageHook hook, LocalDayBoundarySource days, ILogger<SystemDayChangeMonitor> logger)
    {
        _window = window;
        _hook = hook;
        _days = days;
        _logger = logger;
        _window.Activated += OnActivated;
        _hook.ClockOrTimeZoneChanged += OnClockOrTimeZoneChanged;
        _hook.Resumed += OnResumedMessage;
        try
        {
            PowerManager.SystemSuspendStatusChanged += OnSuspendStatusChanged;
            _isPowerSubscribed = true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.PowerManagerUnavailable(_logger, ex);
        }
    }

    public void Dispose()
    {
        _window.Activated -= OnActivated;
        _hook.ClockOrTimeZoneChanged -= OnClockOrTimeZoneChanged;
        _hook.Resumed -= OnResumedMessage;
        if (_isPowerSubscribed)
        {
            PowerManager.SystemSuspendStatusChanged -= OnSuspendStatusChanged;
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _days.Check(DayCheckReason.Activation);
        }
    }

    private void OnClockOrTimeZoneChanged(object? sender, EventArgs e) => _days.Check(DayCheckReason.ClockOrTimeZoneChange);

    private void OnResumedMessage(object? sender, EventArgs e) => _days.Check(DayCheckReason.Resume);

    // Raised on a background thread; the source is safe to call from any thread.
    private void OnSuspendStatusChanged(object? sender, object e)
    {
        if (PowerManager.SystemSuspendStatus is SystemSuspendStatus.AutoResume or SystemSuspendStatus.ManualResume)
        {
            Log.Resumed(_logger);
            _days.Check(DayCheckReason.Resume);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5301, Level = LogLevel.Warning, Message = "PowerManager suspend notifications are unavailable; resume is detected through WM_POWERBROADCAST only.")]
        public static partial void PowerManagerUnavailable(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 5302, Level = LogLevel.Information, Message = "System resumed from sleep (PowerManager).")]
        public static partial void Resumed(ILogger logger);
    }
}
