using MaxPlanner.Application.Startup;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Storage;

namespace MaxPlanner.Infrastructure.Startup;

/// <summary>An open <see cref="SqliteDataset"/> as the startup coordinator sees it; disposing it closes the dataset.</summary>
public sealed class OpenedSqliteDataset(SqliteDataset dataset) : IOpenedDataset
{
    public SqliteDataset Dataset { get; } = dataset ?? throw new ArgumentNullException(nameof(dataset));

    public Guid Id => Dataset.Id.Value;

    public int SchemaVersion => Dataset.SchemaVersion;

    public ValueTask DisposeAsync() => Dataset.DisposeAsync();
}

/// <summary><see cref="IDatasetStorage"/> over <see cref="SqliteDataset"/> in the portable data folder.</summary>
public sealed class SqliteDatasetStorage(DataPaths paths, SqliteDatasetOptions options) : IDatasetStorage
{
    private readonly DataPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly SqliteDatasetOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public bool HasRestoreMarker() => File.Exists(_paths.RestoreMarkerFile);

    public async Task<DatasetOpenAttempt> OpenExistingAsync() => Map(await SqliteDataset.OpenExistingAsync(_paths, _options).ConfigureAwait(false));

    public async Task<DatasetOpenAttempt> CreateNewAsync() => Map(await SqliteDataset.CreateNewAsync(_paths, _options).ConfigureAwait(false));

    private static DatasetOpenAttempt Map(DatasetOpenResult result) => new(
        result.Status switch
        {
            DatasetOpenStatus.Opened => DatasetOpenOutcome.Opened,
            DatasetOpenStatus.Missing => DatasetOpenOutcome.Missing,
            DatasetOpenStatus.AlreadyExists => DatasetOpenOutcome.AlreadyExists,
            _ => DatasetOpenOutcome.Failed,
        },
        result.Dataset is { } dataset ? new OpenedSqliteDataset(dataset) : null,
        result.Error,
        result.SafetyBackupDirectory);
}
