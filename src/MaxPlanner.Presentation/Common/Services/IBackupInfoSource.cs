namespace MaxPlanner.Presentation.Common.Services;

/// <summary>The newest backup, as Settings › Data shows it (T-09).</summary>
/// <param name="Kind">The ADR 0015 kind, for example <c>daily</c> or <c>migration-safety</c>.</param>
public sealed record LatestBackup(DateTimeOffset CreatedAtUtc, string Kind);

/// <summary>Reads backup information for Settings › Data. Implemented by the Desktop composition over the portable data folder.</summary>
public interface IBackupInfoSource
{
    /// <summary>The newest complete backup, or null when none exists. Reads the file system off the UI thread.</summary>
    Task<LatestBackup?> GetLatestAsync(CancellationToken cancellationToken);
}
