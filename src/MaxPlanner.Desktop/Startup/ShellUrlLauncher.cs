using MaxPlanner.Presentation.Common.Services;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Desktop.Startup;

/// <summary>
/// Opens user-activated design links in the default browser (B-02.11). Only absolute http/https URIs are
/// passed to the shell; any other scheme (file, ms-settings, custom protocols from imported data) is refused
/// here as well as by the Domain URL rule, so a stored value can never start a program.
/// </summary>
internal sealed partial class ShellUrlLauncher(ILogger<ShellUrlLauncher> logger) : IUrlLauncher
{
    public async Task<bool> OpenAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Refused(logger);
            return false;
        }

        try
        {
            // Windows.System.Launcher works unpackaged and hands the URI to the user's default browser.
            var opened = await Windows.System.Launcher.LaunchUriAsync(uri);
            if (!opened)
            {
                Log.NotOpened(logger);
            }

            return opened;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log.OpenFailed(logger, ex);
            return false;
        }
    }

    // The URL itself is never logged: it is user content (AGENTS.md §4 logging rule).
    private static partial class Log
    {
        [LoggerMessage(EventId = 5402, Level = LogLevel.Warning, Message = "A link with a scheme other than http/https was refused.")]
        public static partial void Refused(ILogger logger);

        [LoggerMessage(EventId = 5403, Level = LogLevel.Warning, Message = "The default browser did not open the link.")]
        public static partial void NotOpened(ILogger logger);

        [LoggerMessage(EventId = 5404, Level = LogLevel.Warning, Message = "Opening a link failed.")]
        public static partial void OpenFailed(ILogger logger, Exception exception);
    }
}
