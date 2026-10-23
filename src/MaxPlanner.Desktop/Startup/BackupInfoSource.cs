using MaxPlanner.Infrastructure.Backup;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Desktop.Startup;

/// <summary>Reads the newest backup under the portable <c>data/backups</c> folder for Settings › Data (T-09).</summary>
internal sealed class BackupInfoSource(string backupsDirectory) : IBackupInfoSource
{
    public Task<LatestBackup?> GetLatestAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () => BackupCatalog.FindLatest(backupsDirectory) is { } latest ? new LatestBackup(latest.CreatedAtUtc, latest.Kind) : null,
            cancellationToken);
}
