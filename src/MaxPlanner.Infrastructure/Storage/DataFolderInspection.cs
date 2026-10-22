namespace MaxPlanner.Infrastructure.Storage;

/// <summary>
/// What the portable <c>data/</c> folder contained at startup, before this launch wrote anything into it.
/// </summary>
/// <param name="HasRestoreMarker">ADR 0015: an unfinished restore; the dataset must not be opened.</param>
/// <param name="HasPriorUse">
/// True when the folder shows that a dataset was used here before: preferences, backups, SQLite side
/// files, or a restore marker. A missing <c>data.db</c> in such a folder is a recovery case, never a first
/// run, so an empty dataset is not created automatically (T-09, AC-15).
/// </param>
public sealed record DataFolderInspection(bool HasRestoreMarker, bool HasPriorUse)
{
    /// <summary>
    /// Inspects the folder. Call it before logging or preferences write anything; the probe's
    /// <c>temp/</c> folder and the lock file are not evidence of prior use, and log files are excluded
    /// because they hold no business data.
    /// </summary>
    public static DataFolderInspection Inspect(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var hasMarker = File.Exists(paths.RestoreMarkerFile);
        var hasPriorUse = hasMarker
            || File.Exists(paths.ConfigurationFile)
            || File.Exists(paths.LastGoodConfigurationFile)
            || File.Exists(paths.DatabaseFile + "-wal")
            || File.Exists(paths.DatabaseFile + "-shm")
            || File.Exists(paths.DatabaseFile + "-journal")
            || HasEntries(paths.BackupsDirectory);
        return new DataFolderInspection(hasMarker, hasPriorUse);
    }

    private static bool HasEntries(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any();
}
