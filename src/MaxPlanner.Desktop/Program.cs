using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MaxPlanner.Desktop.Startup;
using MaxPlanner.Infrastructure.Storage;
using MaxPlanner.Presentation.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.ApplicationModel.Resources;

namespace MaxPlanner.Desktop;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) so single-instance redirection, the data-folder
/// probe, and the dataset lock all run before any XAML or database work (T-03.6, T-06.5, ADR 0013).
/// </summary>
public static partial class Program
{
    [STAThread]
    public static int Main()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var paths = DataPaths.ForApplication();

        // Two portable copies have different data folders, so each gets its own instance key.
        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey(paths));
        if (!keyInstance.IsCurrent)
        {
            if (RedirectToRunningInstance(keyInstance))
            {
                return ExitCodes.Redirected;
            }

            // The owner may be shutting down or hung; never exit silently (spike 8 finding).
            ShowStartupError(Text("Startup_DatasetLocked"));
            return ExitCodes.DatasetLocked;
        }

        if (DataDirectoryProbe.Probe(paths) != DataDirectoryProbeResult.Writable)
        {
            var format = CompositeFormat.Parse(Text("Startup_DataNotWritable"));
            ShowStartupError(string.Format(System.Globalization.CultureInfo.CurrentCulture, format, paths.DataDirectory));
            return ExitCodes.DataNotWritable;
        }

        // The lock outlives Application.Start, which returns only when the app exits.
        using var datasetLock = DatasetLock.TryAcquire(paths);
        if (datasetLock is null)
        {
            ShowStartupError(Text("Startup_DatasetLocked"));
            return ExitCodes.DatasetLocked;
        }

        // Inspecting the folder, logging, and preferences happen here, before XAML, so the window opens with
        // its saved bounds and theme and the UI thread never does this file I/O. Disposing flushes the logs.
        using var context = StartupContext.Create(paths, startTimestamp);
        var logger = context.LoggerFactory.CreateLogger(typeof(Program).FullName!);
        Log.Launched(logger, StartupContext.AppVersion, Environment.OSVersion.VersionString, RuntimeInformation.FrameworkDescription, context.Inspection.HasPriorUse);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.UnhandledException(logger, e.ExceptionObject as Exception);
            context.LoggerFactory.Dispose();
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Log.UnobservedTaskException(logger, e.Exception);

        Microsoft.UI.Xaml.Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(context);
        });

        Log.Exited(logger, context.ExitCode, context.ElapsedMilliseconds);
        return context.ExitCode;
    }

    private static string InstanceKey(DataPaths paths)
    {
        var normalized = paths.DataDirectory.ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "MaxPlanner.Dataset." + Convert.ToHexString(hash)[..32];
    }

    private static bool RedirectToRunningInstance(AppInstance owner)
    {
        // Let the owning process take the foreground once it handles the redirected activation.
        _ = NativeMethods.AllowSetForegroundWindow(owner.ProcessId);

        // RedirectActivationToAsync must not be awaited on this STA thread. Wait for it on a pool
        // thread instead; a managed wait on an STA thread keeps pumping COM messages.
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var redirect = Task.Run(() => owner.RedirectActivationToAsync(activation).AsTask());
        return redirect.Wait(TimeSpan.FromSeconds(10));
    }

    private static string Text(string key) => new ResourceLoader().GetString(key);

    private static void ShowStartupError(string message) =>
        NativeMethods.ShowError(Text("AppDisplayName"), message);

    private static partial class Log
    {
        [LoggerMessage(EventId = 5501, Level = LogLevel.Information, Message = "Max Planner {AppVersion} starting on {OsVersion}, {Runtime}; data folder used before: {HasPriorUse}.")]
        public static partial void Launched(ILogger logger, string appVersion, string osVersion, string runtime, bool hasPriorUse);

        [LoggerMessage(EventId = 5502, Level = LogLevel.Information, Message = "Exiting with code {ExitCode} after {ElapsedMs} ms.")]
        public static partial void Exited(ILogger logger, int exitCode, long elapsedMs);

        [LoggerMessage(EventId = 5503, Level = LogLevel.Critical, Message = "Unhandled exception; the process is terminating.")]
        public static partial void UnhandledException(ILogger logger, Exception? exception);

        [LoggerMessage(EventId = 5504, Level = LogLevel.Error, Message = "A task faulted and its exception was never observed.")]
        public static partial void UnobservedTaskException(ILogger logger, Exception exception);
    }
}

/// <summary>Process exit codes (ADR 0003).</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Redirected = 0;
    public const int DataNotWritable = 2;
    public const int DatasetLocked = 3;
    public const int NewerSchema = 4;
    public const int StartupFailure = 5;

    /// <summary>Closing from the recovery page reports why the dataset was not used.</summary>
    public static int ForRecovery(RecoveryKind kind) => kind == RecoveryKind.UnsupportedSchema ? NewerSchema : StartupFailure;
}
