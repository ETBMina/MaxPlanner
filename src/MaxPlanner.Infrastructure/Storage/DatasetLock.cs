namespace MaxPlanner.Infrastructure.Storage;

/// <summary>
/// Exclusive, process-lifetime ownership of one portable dataset (T-06.5, ADR 0013).
/// </summary>
/// <remarks>
/// The lock is an open <c>data/.lock</c> handle with <see cref="FileShare.None"/>. Windows releases
/// it when the process exits, including after a crash, so a stale lock can never block a restart.
/// Activation redirection is only a convenience; this handle is the guarantee that a second
/// process cannot write the same dataset.
/// </remarks>
public sealed class DatasetLock : IDisposable
{
    private readonly FileStream _handle;

    private DatasetLock(FileStream handle) => _handle = handle;

    /// <summary>Acquires the lock, or returns <see langword="null"/> when another process holds it.</summary>
    /// <exception cref="IOException">The lock file could not be created for a reason other than sharing.</exception>
    /// <exception cref="UnauthorizedAccessException">The data folder is not writable.</exception>
    public static DatasetLock? TryAcquire(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.DataDirectory);
        try
        {
            var handle = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new DatasetLock(handle);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return null;
        }
    }

    public void Dispose() => _handle.Dispose();

    // ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33) surface as IOException HResults.
    private static bool IsSharingViolation(IOException ex) =>
        (ex.HResult & 0xFFFF) is 32 or 33;
}
