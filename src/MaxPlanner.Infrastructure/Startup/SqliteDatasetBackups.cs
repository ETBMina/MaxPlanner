using MaxPlanner.Application.Common;
using MaxPlanner.Application.Startup;
using MaxPlanner.Infrastructure.Backup;
using MaxPlanner.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Infrastructure.Startup;

/// <summary><see cref="IDatasetBackups"/> over <see cref="BackupMaintenance"/>, <see cref="BackupCatalog"/>, and <see cref="DailyBackupService"/>.</summary>
public sealed class SqliteDatasetBackups : IDatasetBackups
{
    private readonly SqliteDataset _dataset;
    private readonly string _appVersion;
    private readonly DailyBackupService _daily;

    public SqliteDatasetBackups(SqliteDataset dataset, TimeProvider time, string appVersion, ILogger<DailyBackupService>? dailyLogger = null)
    {
        _dataset = dataset ?? throw new ArgumentNullException(nameof(dataset));
        _appVersion = appVersion;
        _daily = new DailyBackupService(dataset, time, appVersion, dailyLogger);
    }

    public string BackupsDirectory => _dataset.Paths.BackupsDirectory;

    public AppResult<string> Create(BackupKind kind, DateTimeOffset utcNow) =>
        BackupMaintenance.Create(_dataset, KindName(kind), utcNow, _appVersion);

    public async Task<DailyBackupReport> RunDailyAsync(CancellationToken cancellationToken)
    {
        var result = await _daily.RunAsync(cancellationToken).ConfigureAwait(false);
        return new DailyBackupReport(result.Outcome == DailyBackupOutcome.Failed, result.FailureKind);
    }

    public IReadOnlyList<BackupSummary> List() =>
        [.. BackupCatalog.List(BackupsDirectory).Select(e => new BackupSummary(e.Path, e.Name, e.CreatedAtUtc, e.Kind, e.SizeBytes, e.Manifest.ToString(), e.IsManual))];

    public bool DeleteManual(string backupFolder) => BackupMaintenance.DeleteManual(BackupsDirectory, backupFolder);

    /// <summary>The ADR 0015 folder-name kind of <paramref name="kind"/>.</summary>
    public static string KindName(BackupKind kind) => kind switch
    {
        BackupKind.Manual => BackupKinds.Manual,
        BackupKind.ImportSafety => BackupKinds.ImportSafety,
        BackupKind.RestoreSafety => BackupKinds.RestoreSafety,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
