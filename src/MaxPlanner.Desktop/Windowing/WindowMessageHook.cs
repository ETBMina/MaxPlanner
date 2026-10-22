using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Desktop.Windowing;

/// <summary>
/// Observes the main window's Win32 messages that WinUI does not surface: clock and time-zone changes,
/// resume from sleep, and DPI changes. Uses <c>SetWindowSubclass</c>, so other handlers keep working.
/// </summary>
/// <remarks>
/// The events are raised on the UI thread from inside the window procedure; handlers must be quick and
/// must not throw. Create and dispose on the UI thread, and dispose before the window is destroyed.
/// </remarks>
internal sealed unsafe partial class WindowMessageHook : IDisposable
{
    private const uint WmSettingChange = 0x001A;
    private const uint WmTimeChange = 0x001E;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmNcDestroy = 0x0082;
    private const nint PbtApmResumeAutomatic = 0x12;
    private const nuint SubclassId = 0x4D50;

    private readonly nint _windowHandle;
    private readonly ILogger _logger;
    private GCHandle _self;
    private bool _isInstalled;

    public WindowMessageHook(nint windowHandle, ILogger<WindowMessageHook> logger)
    {
        _windowHandle = windowHandle;
        _logger = logger;
        _self = GCHandle.Alloc(this);
        _isInstalled = NativeMethods.SetWindowSubclass(windowHandle, &SubclassProc, SubclassId, (nuint)GCHandle.ToIntPtr(_self));
        if (!_isInstalled)
        {
            Log.InstallFailed(_logger);
        }
    }

    /// <summary><c>WM_TIMECHANGE</c>, or <c>WM_SETTINGCHANGE</c> for the "intl" (regional and time-zone) section.</summary>
    public event EventHandler? ClockOrTimeZoneChanged;

    /// <summary><c>WM_POWERBROADCAST</c> with <c>PBT_APMRESUMEAUTOMATIC</c>.</summary>
    public event EventHandler? Resumed;

    /// <summary><c>WM_DPICHANGED</c>; the argument is the new DPI.</summary>
    public event EventHandler<uint>? DpiChanged;

    public bool IsInstalled => _isInstalled;

    public void Dispose()
    {
        Uninstall();
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SubclassProc(nint windowHandle, uint message, nint wParam, nint lParam, nuint subclassId, nuint referenceData)
    {
        if (GCHandle.FromIntPtr((nint)referenceData).Target is WindowMessageHook hook)
        {
            // An exception must never unwind into the native window procedure.
            try
            {
                hook.OnMessage(message, wParam, lParam);
            }
            catch (Exception ex)
            {
                Log.HandlerFailed(hook._logger, message, ex);
            }
        }

        return NativeMethods.DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void OnMessage(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmTimeChange:
                Log.Received(_logger, "WM_TIMECHANGE");
                ClockOrTimeZoneChanged?.Invoke(this, EventArgs.Empty);
                break;
            case WmSettingChange when lParam != 0 && Marshal.PtrToStringUni(lParam) == "intl":
                Log.Received(_logger, "WM_SETTINGCHANGE(intl)");
                ClockOrTimeZoneChanged?.Invoke(this, EventArgs.Empty);
                break;
            case WmPowerBroadcast when wParam == PbtApmResumeAutomatic:
                Log.Received(_logger, "WM_POWERBROADCAST(resume)");
                Resumed?.Invoke(this, EventArgs.Empty);
                break;
            case WmDpiChanged:
                // The low word of wParam is the new X-axis DPI.
                DpiChanged?.Invoke(this, (uint)(wParam & 0xFFFF));
                break;
            case WmNcDestroy:
                Uninstall();
                break;
        }
    }

    private void Uninstall()
    {
        if (_isInstalled)
        {
            _isInstalled = false;
            NativeMethods.RemoveWindowSubclass(_windowHandle, &SubclassProc, SubclassId);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5101, Level = LogLevel.Warning, Message = "Subclassing the main window failed; clock, time-zone, and resume messages fall back to activation checks.")]
        public static partial void InstallFailed(ILogger logger);

        [LoggerMessage(EventId = 5102, Level = LogLevel.Information, Message = "Window message {Message} received.")]
        public static partial void Received(ILogger logger, string message);

        [LoggerMessage(EventId = 5103, Level = LogLevel.Error, Message = "Handling window message 0x{Message:X4} failed.")]
        public static partial void HandlerFailed(ILogger logger, uint message, Exception exception);
    }
}
