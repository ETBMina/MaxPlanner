using System.Diagnostics;
using System.Reflection;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Infrastructure.Logging;
using MaxPlanner.Infrastructure.Preferences;
using MaxPlanner.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaxPlanner.Desktop.Startup;

/// <summary>
/// What <see cref="Program.Main"/> prepares before XAML starts: the folder inspection, logging, and the
/// loaded preferences, so the window can open at its saved size and theme without a visible jump and
/// without file I/O on the UI thread. Disposing it flushes and closes the logs.
/// </summary>
internal sealed class StartupContext : IDisposable
{
    private StartupContext(DataPaths paths, DataFolderInspection inspection, ILoggerFactory loggerFactory, JsonUiPreferencesStore preferences, PreferencesLoadResult loadedPreferences, long startTimestamp)
    {
        Paths = paths;
        Inspection = inspection;
        LoggerFactory = loggerFactory;
        Preferences = preferences;
        LoadedPreferences = loadedPreferences;
        StartTimestamp = startTimestamp;
    }

    public static string AppVersion { get; } =
        typeof(StartupContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public DataPaths Paths { get; }

    /// <summary>The data folder as found at launch, before this process wrote preferences or logs.</summary>
    public DataFolderInspection Inspection { get; }

    public ILoggerFactory LoggerFactory { get; }

    public JsonUiPreferencesStore Preferences { get; }

    public PreferencesLoadResult LoadedPreferences { get; }

    /// <summary><see cref="Stopwatch.GetTimestamp"/> at the start of <see cref="Program.Main"/>.</summary>
    public long StartTimestamp { get; }

    /// <summary>ADR 0003 exit code chosen by the app before it closes.</summary>
    public int ExitCode { get; set; }

    public long ElapsedMilliseconds => (long)Stopwatch.GetElapsedTime(StartTimestamp).TotalMilliseconds;

    /// <summary>Call after the dataset lock is held, so only this process writes logs and preferences.</summary>
    public static StartupContext Create(DataPaths paths, long startTimestamp)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var inspection = DataFolderInspection.Inspect(paths);
        ILoggerFactory loggerFactory;
        try
        {
            loggerFactory = AppLogging.CreateFactory(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is diagnostic; it must never stop the app from opening healthy data.
            loggerFactory = NullLoggerFactory.Instance;
        }

        var preferences = new JsonUiPreferencesStore(paths, TimeProvider.System, loggerFactory.CreateLogger<JsonUiPreferencesStore>());
        var loaded = preferences.Load();
        return new StartupContext(paths, inspection, loggerFactory, preferences, loaded, startTimestamp);
    }

    public void Dispose()
    {
        Preferences.Dispose();
        LoggerFactory.Dispose();
    }
}
