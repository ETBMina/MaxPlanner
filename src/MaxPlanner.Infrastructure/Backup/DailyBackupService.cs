using System.Globalization;
using System.Text.RegularExpressions;
using MaxPlanner.Application.Common;
using MaxPlanner.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaxPlanner.Infrastructure.Backup;

public enum DailyBackupOutcome
{
    Created,

    /// <summary>A daily backup of the current local day already exists; nothing was written.</summary>
    AlreadyDoneToday,

    /// <summary>The backup could not be written; see <see cref="DailyBackupResult.FailureKind"/>. Startup continues.</summary>
    Failed,
}

/// <param name="BackupDirectory">The new folder when <see cref="DailyBackupOutcome.Created"/>.</param>
/// <param name="PrunedDirectories">Daily folders removed by retention after a successful backup.</param>
public sealed record DailyBackupResult(
    DailyBackupOutcome Outcome,
    string? BackupDirectory = null,
    StorageFailureKind? FailureKind = null,
    IReadOnlyList<string>? PrunedDirectories = null);

/// <summary>
/// The automatic backup on the first successful open of each local day, keeping the newest
/// <see cref="RetainedDailyBackups"/> daily backups (T-09, ADR 0015).
/// </summary>
/// <remarks>
/// Whether today's backup exists is read from the backup folder names, so no extra state is stored.
/// Retention only ever deletes <c>-daily</c> folders; manual and safety backups are never touched.
/// </remarks>
public sealed partial class DailyBackupService
{
    public const int RetainedDailyBackups = 7;

    private readonly SqliteDataset _dataset;
    private readonly TimeProvider _time;
    private readonly string _appVersion;
    private readonly ILogger _logger;

    public DailyBackupService(SqliteDataset dataset, TimeProvider time, string appVersion, ILogger<DailyBackupService>? logger = null)
    {
        _dataset = dataset ?? throw new ArgumentNullException(nameof(dataset));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        _appVersion = appVersion;
        _logger = logger ?? NullLogger<DailyBackupService>.Instance;
    }

    /// <summary>Runs on a background thread. Storage failures are returned as <see cref="DailyBackupOutcome.Failed"/>, never thrown.</summary>
    /// <exception cref="OperationCanceledException">Cancelled before the copy started.</exception>
    public Task<DailyBackupResult> RunAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Run(cancellationToken), cancellationToken);

    private static StorageFailureKind? Classify(Exception exception)
    {
        if (SqliteFailures.Classify(exception) is { } kind)
        {
            return kind;
        }

        return exception switch
        {
            // ERROR_HANDLE_DISK_FULL (39) and ERROR_DISK_FULL (112).
            IOException io when (io.HResult & 0xFFFF) is 39 or 112 => StorageFailureKind.DiskFull,
            IOException or SqliteException => StorageFailureKind.Io,
            UnauthorizedAccessException => StorageFailureKind.Unavailable,
            _ => null,
        };
    }

    [GeneratedRegex(@"^(?<stamp>\d{8}T\d{6}Z)-daily(-\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DailyFolderName();

    private DailyBackupResult Run(CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        try
        {
            if (HasDailyBackupForToday())
            {
                Log.AlreadyDone(_logger);
                return new DailyBackupResult(DailyBackupOutcome.AlreadyDoneToday);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var directory = BackupWriter.Create(_dataset, BackupKinds.Daily, _time.GetUtcNow(), _appVersion);
            var pruned = PruneDailyBackups();
            var folderName = Path.GetFileName(directory);
            var elapsedMs = (long)_time.GetElapsedTime(started).TotalMilliseconds;
            Log.Created(_logger, folderName, pruned.Count, elapsedMs);
            return new DailyBackupResult(DailyBackupOutcome.Created, directory, PrunedDirectories: pruned);
        }
        catch (Exception ex) when (Classify(ex) is { } kind)
        {
            Log.Failed(_logger, kind, ex);
            return new DailyBackupResult(DailyBackupOutcome.Failed, FailureKind: kind);
        }
    }

    private bool HasDailyBackupForToday()
    {
        var today = _time.LocalToday();
        return DailyFolders().Any(folder =>
            File.Exists(Path.Combine(folder.Path, BackupManifest.FileName))
            && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(folder.CreatedAtUtc, _time.LocalTimeZone).DateTime) == today);
    }

    private List<string> PruneDailyBackups()
    {
        var pruned = new List<string>();
        foreach (var folder in DailyFolders().OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(RetainedDailyBackups))
        {
            try
            {
                Directory.Delete(folder.Path, recursive: true);
                pruned.Add(folder.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be removed now is retried at the next daily backup.
                Log.PruneFailed(_logger, folder.Name, ex);
            }
        }

        return pruned;
    }

    private IEnumerable<DailyFolder> DailyFolders()
    {
        var root = _dataset.Paths.BackupsDirectory;
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(path);
            var match = DailyFolderName().Match(name);
            if (match.Success && DateTimeOffset.TryParseExact(
                    match.Groups["stamp"].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var createdAtUtc))
            {
                yield return new DailyFolder(path, name, createdAtUtc);
            }
        }
    }

    private readonly record struct DailyFolder(string Path, string Name, DateTimeOffset CreatedAtUtc);

    private static partial class Log
    {
        [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Daily backup {Folder} created in {ElapsedMs} ms; {PrunedCount} older daily backup(s) pruned.")]
        public static partial void Created(ILogger logger, string folder, int prunedCount, long elapsedMs);

        [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Daily backup skipped: today's daily backup already exists.")]
        public static partial void AlreadyDone(ILogger logger);

        [LoggerMessage(EventId = 3003, Level = LogLevel.Error, Message = "Daily backup failed with storage failure {Kind}.")]
        public static partial void Failed(ILogger logger, StorageFailureKind kind, Exception exception);

        [LoggerMessage(EventId = 3004, Level = LogLevel.Warning, Message = "Could not prune daily backup {Folder}.")]
        public static partial void PruneFailed(ILogger logger, string folder, Exception exception);
    }
}
