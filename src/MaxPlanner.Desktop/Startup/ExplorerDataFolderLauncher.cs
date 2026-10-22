using System.ComponentModel;
using System.Diagnostics;
using MaxPlanner.Infrastructure.Storage;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Desktop.Startup;

/// <summary>Opens <c>data/</c> in File Explorer. Only the folder is passed, so no file is ever opened or run.</summary>
internal sealed partial class ExplorerDataFolderLauncher(DataPaths paths, ILogger<ExplorerDataFolderLauncher> logger) : IDataFolderLauncher
{
    public string DataDirectory => paths.DataDirectory;

    public void OpenDataFolder()
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
            start.ArgumentList.Add(paths.DataDirectory);
            Process.Start(start)?.Dispose();
        }
        catch (Win32Exception ex)
        {
            Log.OpenFailed(logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5401, Level = LogLevel.Warning, Message = "Opening the data folder in File Explorer failed.")]
        public static partial void OpenFailed(ILogger logger, Exception exception);
    }
}
