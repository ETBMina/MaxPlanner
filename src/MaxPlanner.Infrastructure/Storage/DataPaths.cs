namespace MaxPlanner.Infrastructure.Storage;

/// <summary>
/// App-owned locations for one portable dataset.
/// </summary>
/// <remarks>
/// T-03.4: every path derives from the application folder, never from the current
/// working directory or the user profile, so the folder can be moved as a unit.
/// </remarks>
public sealed class DataPaths
{
    private DataPaths(string applicationDirectory)
    {
        ApplicationDirectory = applicationDirectory;
        DataDirectory = Path.Combine(applicationDirectory, "data");
        DatabaseFile = Path.Combine(DataDirectory, "data.db");
        ConfigurationFile = Path.Combine(DataDirectory, "configuration.json");
        LastGoodConfigurationFile = Path.Combine(DataDirectory, "configuration.last-good.json");
        RestoreMarkerFile = Path.Combine(DataDirectory, "restore.pending.json");
        BackupsDirectory = Path.Combine(DataDirectory, "backups");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        TempDirectory = Path.Combine(DataDirectory, "temp");
        LockFile = Path.Combine(DataDirectory, ".lock");
    }

    public string ApplicationDirectory { get; }

    public string DataDirectory { get; }

    public string DatabaseFile { get; }

    public string ConfigurationFile { get; }

    /// <summary>The previous successfully written preferences, kept by every save (T-09).</summary>
    public string LastGoodConfigurationFile { get; }

    /// <summary>ADR 0015: present only while a restore is in progress or was interrupted.</summary>
    public string RestoreMarkerFile { get; }

    public string BackupsDirectory { get; }

    public string LogsDirectory { get; }

    public string TempDirectory { get; }

    public string LockFile { get; }

    public static DataPaths ForApplication() => ForDirectory(AppContext.BaseDirectory);

    /// <summary>Creates paths rooted at <paramref name="applicationDirectory"/>; tests use isolated folders.</summary>
    public static DataPaths ForDirectory(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        return new DataPaths(Path.GetFullPath(applicationDirectory));
    }
}
