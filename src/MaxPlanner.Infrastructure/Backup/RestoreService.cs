using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using MaxPlanner.Infrastructure.Persistence.Migrations;
using MaxPlanner.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Backup;

/// <summary>The checks a restore runs on the staged copy before anything is replaced (T-09, design §4.6 Restore step 2).</summary>
public enum RestoreCheckKind
{
    Manifest,
    Checksums,
    SchemaVersion,
    Integrity,
    ForeignKeys,

    /// <summary>Every record and link loads with the domain's structural rules (IDs, links, revisions, dates).</summary>
    Records,
}

/// <param name="Detail">A non-sensitive detail, for example the schema versions found and supported.</param>
public sealed record RestoreCheck(RestoreCheckKind Kind, bool Passed, string? Detail = null);

/// <summary>A backup staged and validated for restore. Nothing in <c>data/</c> has changed yet.</summary>
public sealed record RestoreValidation(
    string SourceFolder,
    string RestoreFolder,
    string StagedDatabase,
    IReadOnlyList<RestoreCheck> Checks,
    string? DatabaseSha256,
    int SchemaVersion,
    Guid? DatasetId,
    DateTimeOffset? BackupCreatedAtUtc,
    string? BackupKind)
{
    public bool IsValid => Checks.Count > 0 && Checks.All(c => c.Passed);
}

public enum RestoreRecoveryOutcome
{
    /// <summary>The restored database is in place; the marker was removed.</summary>
    RolledForward,

    /// <summary>The previous database was put back; the marker was removed.</summary>
    RolledBack,

    /// <summary>Neither copy could be verified; the marker is kept and nothing was guessed (AC-15).</summary>
    Unresolved,
}

/// <summary>
/// The ADR 0015 restore protocol. <see cref="Validate"/> stages and checks a backup while the dataset is still open;
/// <see cref="Swap"/> replaces <c>data.db</c> under the <c>restore.pending.json</c> marker after the dataset is closed;
/// <see cref="RecoverInterrupted"/> finishes or undoes an interrupted swap at startup. Two renames are not one
/// transaction, so the marker records enough to tell which side of the swap a crash left behind.
/// </summary>
/// <remarks>
/// Interim choice (implementation-plan Phase 15): a restore replaces the business database only. The running app's
/// <c>configuration.json</c> (UI preferences, T-09) is kept; the backup's copy stays in the backup folder.
/// </remarks>
public static class RestoreService
{
    public const string PhaseSwapping = "swapping";
    public const string PhaseCommitted = "committed";

    /// <summary>Copies the backup's database into <c>data/temp/restore-&lt;id&gt;/staging</c> and runs every check on the copy.</summary>
    public static RestoreValidation Validate(DataPaths paths, string backupFolder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFolder);
        var restoreFolder = Path.Combine(paths.TempDirectory, "restore-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(restoreFolder, "staging");
        var staged = Path.Combine(staging, BackupWriter.DatabaseFileName);
        var checks = new List<RestoreCheck>();
        var manifest = BackupCatalog.ReadManifest(backupFolder);
        var entry = manifest?.Files.FirstOrDefault(f => f.Name == BackupWriter.DatabaseFileName);
        var source = Path.Combine(backupFolder, BackupWriter.DatabaseFileName);
        checks.Add(new RestoreCheck(RestoreCheckKind.Manifest, manifest is not null && entry is not null && File.Exists(source)));
        if (!checks[^1].Passed)
        {
            return new RestoreValidation(backupFolder, restoreFolder, staged, checks, null, 0, null, null, null);
        }

        Directory.CreateDirectory(staging);
        File.Copy(source, staged);
        cancellationToken.ThrowIfCancellationRequested();
        var sha = Sha256(staged);
        checks.Add(new RestoreCheck(RestoreCheckKind.Checksums, string.Equals(sha, entry!.Sha256, StringComparison.OrdinalIgnoreCase) && new FileInfo(staged).Length == entry.Length));
        if (!checks[^1].Passed)
        {
            return new RestoreValidation(backupFolder, restoreFolder, staged, checks, sha, 0, manifest!.DatasetId, manifest.CreatedAtUtc, manifest.Kind);
        }

        var version = 0;
        Guid? datasetId = null;
        var readOnly = new SqliteConnectionStringBuilder { DataSource = staged, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        try
        {
            using var connection = new SqliteConnection(readOnly);
            connection.Open();
            version = SchemaMigrator.ReadUserVersion(connection);
            var supported = SqliteDataset.SupportedSchemaVersion;

            // An older schema is upgraded by the normal migration (with its own safety backup) when the restored
            // dataset opens; a newer or foreign one is refused (T-03.8).
            checks.Add(new RestoreCheck(RestoreCheckKind.SchemaVersion, version >= 1 && version <= supported,
                string.Create(CultureInfo.InvariantCulture, $"{version}/{supported}")));
            checks.Add(new RestoreCheck(RestoreCheckKind.Integrity, Single(connection, "PRAGMA integrity_check;") == "ok"));
            checks.Add(new RestoreCheck(RestoreCheckKind.ForeignKeys, !HasRows(connection, "PRAGMA foreign_key_check;")));
            if (version == supported && checks.All(c => c.Passed))
            {
                checks.Add(new RestoreCheck(RestoreCheckKind.Records, LoadsEveryRecord(connection, out datasetId)));
            }
            else
            {
                checks.Add(new RestoreCheck(RestoreCheckKind.Records, version >= 1 && version < supported && checks.All(c => c.Passed)));
            }
        }
        catch (SqliteException)
        {
            checks.Add(new RestoreCheck(RestoreCheckKind.Integrity, false));
        }

        return new RestoreValidation(backupFolder, restoreFolder, staged, checks, sha, version, datasetId ?? manifest!.DatasetId, manifest!.CreatedAtUtc, manifest.Kind);
    }

    /// <summary>Removes a staged restore that will not be applied.</summary>
    public static void Discard(RestoreValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        TryDeleteDirectory(validation.RestoreFolder);
    }

    /// <summary>
    /// A raw copy of the current database files for a restore from the recovery page, when the dataset cannot be opened
    /// for an online backup. The folder gets a manifest with checksums so it is listed like any safety backup.
    /// </summary>
    /// <returns>The safety folder, or null when there was no database file to keep.</returns>
    public static string? CreateRawSafetyCopy(DataPaths paths, DateTimeOffset utcNow, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var files = DatabaseFiles(paths.DataDirectory).Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            return null;
        }

        var staging = Path.Combine(paths.TempDirectory, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var described = new List<BackupManifestFile>();
        foreach (var file in files)
        {
            var copy = Path.Combine(staging, Path.GetFileName(file));
            File.Copy(file, copy);
            described.Add(new BackupManifestFile(Path.GetFileName(copy), new FileInfo(copy).Length, Sha256(copy)));
        }

        var manifest = new BackupManifest(BackupManifest.CurrentFormatVersion, appVersion, 0, Guid.Empty, BackupKinds.RestoreSafety, utcNow.ToUniversalTime(), described);
        File.WriteAllBytes(Path.Combine(staging, BackupManifest.FileName), JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJsonContext.Default.BackupManifest));
        Directory.CreateDirectory(paths.BackupsDirectory);
        var destination = Path.Combine(paths.BackupsDirectory, $"{BackupWriter.Stamp(utcNow)}-{BackupKinds.RestoreSafety}");
        for (var suffix = 2; Directory.Exists(destination); suffix++)
        {
            destination = Path.Combine(paths.BackupsDirectory, string.Create(CultureInfo.InvariantCulture, $"{BackupWriter.Stamp(utcNow)}-{BackupKinds.RestoreSafety}-{suffix}"));
        }

        Directory.Move(staging, destination);
        return destination;
    }

    /// <summary>
    /// Replaces <c>data/data.db</c> with the validated staged copy. The dataset must be closed and its pooled
    /// connections cleared first. Steps: write the marker (swapping), move the current files to <c>previous/</c>, move
    /// the staged copy in, verify its checksum, mark committed, remove the marker.
    /// </summary>
    /// <returns>True when the restored database is in place; false when the swap was rolled back (marker removed).</returns>
    /// <exception cref="IOException">The swap could neither finish nor roll back; the marker stays for startup recovery.</exception>
    public static bool Swap(DataPaths paths, RestoreValidation validation)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(validation);
        if (!validation.IsValid || validation.DatabaseSha256 is null)
        {
            throw new InvalidOperationException("Only a validated restore can be swapped in.");
        }

        // Pooled read-only handles keep the database open after Dispose; release them so the files can move.
        SqliteConnection.ClearAllPools();
        var previous = Path.Combine(validation.RestoreFolder, "previous");
        Directory.CreateDirectory(previous);
        var marker = new RestoreMarker(PhaseSwapping, validation.RestoreFolder, validation.StagedDatabase, previous, validation.DatabaseSha256, DateTimeOffset.UtcNow);
        WriteMarker(paths, marker);
        try
        {
            MoveCurrentToPrevious(paths, previous);
            File.Move(validation.StagedDatabase, paths.DatabaseFile);
            if (!string.Equals(Sha256(paths.DatabaseFile), validation.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The restored database does not match its checksum.");
            }

            WriteMarker(paths, marker with { Phase = PhaseCommitted });
            File.Delete(paths.RestoreMarkerFile);
            TryDeleteDirectory(validation.RestoreFolder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put the previous files back; only a verified rollback removes the marker.
            if (RollBack(paths, previous))
            {
                File.Delete(paths.RestoreMarkerFile);
                return false;
            }

            throw;
        }
    }

    /// <summary>
    /// ADR 0015 startup recovery: rolls forward when the restored copy (in <c>data/</c> or still staged) verifies against
    /// the marker's checksum, otherwise rolls back from <c>previous/</c>. Never substitutes an empty database.
    /// </summary>
    public static RestoreRecoveryOutcome RecoverInterrupted(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        SqliteConnection.ClearAllPools();
        RestoreMarker? marker;
        try
        {
            marker = JsonSerializer.Deserialize(File.ReadAllBytes(paths.RestoreMarkerFile), RestoreJsonContext.Default.RestoreMarker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return RestoreRecoveryOutcome.Unresolved;
        }

        // The marker is a file in data/; never act on paths outside data/temp, whatever it says.
        if (marker is null || !IsUnder(paths.TempDirectory, marker.RestoreFolder) || !IsUnder(marker.RestoreFolder, marker.StagedDatabase)
            || !IsUnder(marker.RestoreFolder, marker.PreviousFolder))
        {
            return RestoreRecoveryOutcome.Unresolved;
        }

        try
        {
            if (marker.Phase == PhaseCommitted || (File.Exists(paths.DatabaseFile) && Verifies(paths.DatabaseFile, marker.DatabaseSha256)))
            {
                return Finish(paths, marker, RestoreRecoveryOutcome.RolledForward);
            }

            if (File.Exists(marker.StagedDatabase) && Verifies(marker.StagedDatabase, marker.DatabaseSha256))
            {
                MoveCurrentToPrevious(paths, marker.PreviousFolder);
                File.Move(marker.StagedDatabase, paths.DatabaseFile);
                return Verifies(paths.DatabaseFile, marker.DatabaseSha256) ? Finish(paths, marker, RestoreRecoveryOutcome.RolledForward) : RestoreRecoveryOutcome.Unresolved;
            }

            return RollBack(paths, marker.PreviousFolder) && File.Exists(paths.DatabaseFile)
                ? Finish(paths, marker, RestoreRecoveryOutcome.RolledBack)
                : RestoreRecoveryOutcome.Unresolved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RestoreRecoveryOutcome.Unresolved;
        }
    }

    private static RestoreRecoveryOutcome Finish(DataPaths paths, RestoreMarker marker, RestoreRecoveryOutcome outcome)
    {
        File.Delete(paths.RestoreMarkerFile);
        TryDeleteDirectory(marker.RestoreFolder);
        return outcome;
    }

    private static void MoveCurrentToPrevious(DataPaths paths, string previous)
    {
        Directory.CreateDirectory(previous);
        foreach (var file in DatabaseFiles(paths.DataDirectory).Where(File.Exists))
        {
            var target = Path.Combine(previous, Path.GetFileName(file));
            if (!File.Exists(target))
            {
                File.Move(file, target);
            }
            else
            {
                File.Delete(file);
            }
        }
    }

    private static bool RollBack(DataPaths paths, string previous)
    {
        try
        {
            if (!File.Exists(Path.Combine(previous, BackupWriter.DatabaseFileName)))
            {
                // Nothing was moved away yet, so data/ still holds the original files.
                return File.Exists(paths.DatabaseFile);
            }

            foreach (var file in DatabaseFiles(paths.DataDirectory).Where(File.Exists))
            {
                File.Delete(file);
            }

            foreach (var file in DatabaseFiles(previous).Where(File.Exists))
            {
                File.Move(file, Path.Combine(paths.DataDirectory, Path.GetFileName(file)));
            }

            return File.Exists(paths.DatabaseFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> DatabaseFiles(string directory) =>
        new[] { string.Empty, "-wal", "-shm" }.Select(suffix => Path.Combine(directory, BackupWriter.DatabaseFileName + suffix));

    private static void WriteMarker(DataPaths paths, RestoreMarker marker)
    {
        var temp = paths.RestoreMarkerFile + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(marker, RestoreJsonContext.Default.RestoreMarker));
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, paths.RestoreMarkerFile, overwrite: true);
    }

    private static bool IsUnder(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Verifies(string file, string sha) => string.Equals(Sha256(file), sha, StringComparison.OrdinalIgnoreCase);

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string? Single(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var first = reader.Read() ? reader.GetString(0) : null;
        return reader.Read() ? null : first;
    }

    private static bool HasRows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    private static bool LoadsEveryRecord(SqliteConnection connection, out Guid? datasetId)
    {
        datasetId = null;
        try
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var loader = new RecordLoader(connection, transaction);
            loader.LoadWorkflow();
            var tasks = loader.LoadTasks().Select(t => t.Id).ToHashSet();
            var cards = loader.LoadSubtaskCards();
            var crs = loader.LoadChangeRequests().Select(c => c.Id).ToHashSet();
            var links = loader.LoadLinks();
            loader.LoadReviewItems();
            loader.LoadGeneralTodos();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT dataset_id FROM app_metadata WHERE id = 1;";
                using var reader = command.ExecuteReader();
                datasetId = reader.Read() ? reader.GetGuid(0, "app_metadata", "dataset_id") : null;
            }

            // Links and cards refer to records that exist (foreign keys already hold; this checks the loaded shapes).
            return datasetId is not null && links.All(l => tasks.Contains(l.TaskId) && crs.Contains(l.ChangeRequestId)) && cards.All(c => tasks.Contains(c.ParentTaskId));
        }
        catch (CorruptStoredDataException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // data/temp is app scratch space; a leftover never counts as a backup or a dataset.
        }
    }
}

/// <summary><c>restore.pending.json</c> (ADR 0015).</summary>
/// <param name="Phase"><see cref="RestoreService.PhaseSwapping"/> or <see cref="RestoreService.PhaseCommitted"/>.</param>
/// <param name="DatabaseSha256">The checksum the restored <c>data.db</c> must have.</param>
public sealed record RestoreMarker(string Phase, string RestoreFolder, string StagedDatabase, string PreviousFolder, string DatabaseSha256, DateTimeOffset CreatedAtUtc);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RestoreMarker))]
internal sealed partial class RestoreJsonContext : JsonSerializerContext;
