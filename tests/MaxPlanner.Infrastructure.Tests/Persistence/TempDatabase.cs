using MaxPlanner.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

/// <summary>A real database file in a unique temp folder whose path contains a space and non-ASCII text.</summary>
internal sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        Directory = Path.Combine(Path.GetTempPath(), "MaxPlanner Tëst DB", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Factory = new SqliteConnectionFactory(Path.Combine(Directory, "data.db"));
    }

    public string Directory { get; }

    public SqliteConnectionFactory Factory { get; }

    public void Dispose()
    {
        // Pooled reader handles would otherwise keep the files locked.
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a crashed child process may still be releasing its handle.
        }
    }
}
