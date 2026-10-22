namespace MaxPlanner.Infrastructure.Storage;

/// <summary>
/// Checks, before any database is opened, that the portable <c>data/</c> folder can be written (T-03.6).
/// </summary>
/// <remarks>
/// There is deliberately no fallback location: when the probe fails the app explains how to move
/// the folder and exits, rather than silently opening a different, empty dataset in AppData.
/// </remarks>
public static class DataDirectoryProbe
{
    public static DataDirectoryProbeResult Probe(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            Directory.CreateDirectory(paths.TempDirectory);
            var probeFile = Path.Combine(paths.TempDirectory, ".probe-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probeFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
            {
                stream.WriteByte(1);
            }

            File.Delete(probeFile);
            return DataDirectoryProbeResult.Writable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DataDirectoryProbeResult.NotWritable;
        }
    }
}

public enum DataDirectoryProbeResult
{
    Writable,
    NotWritable,
}
