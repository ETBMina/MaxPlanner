using System.Security.Cryptography;
using System.Text;
using MaxPlanner.Infrastructure.Storage;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.ApplicationModel.Resources;

namespace MaxPlanner.Desktop;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) so single-instance redirection, the data-folder
/// probe, and the dataset lock all run before any XAML or database work (T-03.6, T-06.5, ADR 0013).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main()
    {
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

        Microsoft.UI.Xaml.Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });

        return ExitCodes.Ok;
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
}

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Redirected = 0;
    public const int DataNotWritable = 2;
    public const int DatasetLocked = 3;
}
