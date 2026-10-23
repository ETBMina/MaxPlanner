using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Desktop.Maintenance;
using MaxPlanner.Infrastructure.Transfer;
using MaxPlanner.Desktop.Startup;
using MaxPlanner.Infrastructure.Backup;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Settings;
using MaxPlanner.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Desktop.Composition;

/// <summary>
/// The services of the one open dataset (one session per process): the dataset and its ports, the
/// projection store, the daily backup, and the use cases. Built only after the dataset opened, because
/// none of these can exist without it.
/// </summary>
/// <remarks>
/// <see cref="DisposeAsync"/> runs the T-06.6 shutdown order explicitly: the pages first (on the UI thread,
/// before the first await), then the store (no more projection work), then the dataset (drain queued
/// commits, checkpoint the WAL, close), then the container.
/// </remarks>
internal sealed class DatasetSession : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private int _isDisposed;

    private DatasetSession(ServiceProvider provider, SqliteDataset dataset)
    {
        _provider = provider;
        Dataset = dataset;
        Store = provider.GetRequiredService<WorkspaceProjectionStore>();
        Backups = provider.GetRequiredService<DailyBackupService>();
    }

    /// <summary>The pages of this dataset; created on the UI thread by <see cref="CreateWorkspace"/>.</summary>
    public WorkspaceViewModel? Workspace { get; private set; }

    public SqliteDataset Dataset { get; }

    public WorkspaceProjectionStore Store { get; }

    public DailyBackupService Backups { get; }

    public IServiceProvider Services => _provider;

    public static DatasetSession Create(IServiceProvider app, SqliteDataset dataset)
    {
        var services = new ServiceCollection();

        // Shared, app-lifetime services are forwarded as instances; the app container owns them.
        services.AddSingleton(app.GetRequiredService<TimeProvider>());
        services.AddSingleton(app.GetRequiredService<ILoggerFactory>());
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(app.GetRequiredService<ReadModelComposer>());
        services.AddSingleton(app.GetRequiredService<IDayBoundarySource>());
        services.AddSingleton(app.GetRequiredService<IUrlLauncher>());
        services.AddSingleton(app.GetRequiredService<IUiPreferencesStore>());
        services.AddSingleton(app.GetRequiredService<ThemeService>());
        services.AddSingleton(app.GetRequiredService<IDataFolderLauncher>());

        // Owned and disposed explicitly by DisposeAsync, in the required order.
        services.AddSingleton(dataset);
#if DEBUG
        // Phase 2 UI evidence only; never compiled into Release (see DebugCommitHooks).
        services.AddSingleton<IMutationCoordinator>(DebugCommitHooks.IsEnabled
            ? new DebugCommitHooks(dataset.Coordinator, dataset.Paths.DataDirectory)
            : dataset.Coordinator);
#else
        services.AddSingleton<IMutationCoordinator>(dataset.Coordinator);
#endif
        services.AddSingleton<IChangeFeed>(dataset.Coordinator);
        services.AddSingleton<IOperationStatusQuery>(dataset.Coordinator);
        services.AddSingleton(dataset.Snapshots);

        services.AddSingleton(sp => new WorkspaceProjectionStore(
            sp.GetRequiredService<IChangeFeed>(),
            sp.GetRequiredService<IReadSnapshotSource>(),
            sp.GetRequiredService<ReadModelComposer>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IDayBoundarySource>()));
        services.AddSingleton(sp => new DailyBackupService(
            dataset, sp.GetRequiredService<TimeProvider>(), StartupContext.AppVersion, sp.GetRequiredService<ILogger<DailyBackupService>>()));

        // Use cases are stateless; one instance of each serves the pages and editors of the session.
        services.AddSingleton(sp => WorkspaceUseCases.Create(sp.GetRequiredService<IMutationCoordinator>()));
        services.AddSingleton(sp => SettingsUseCases.Create(sp.GetRequiredService<IMutationCoordinator>(), sp.GetRequiredService<IReadSnapshotSource>()));

        // B-12, B-13 (ADR 0012): transfer use cases over the dataset's reader and writer, with the Infrastructure adapters.
        services.AddSingleton(sp => new ExportTransfer(sp.GetRequiredService<IReadSnapshotSource>(), TransferAdapters.CreateWriter(), dataset.Id.Value, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ImportTransfer(sp.GetRequiredService<IMutationCoordinator>(), sp.GetRequiredService<IReadSnapshotSource>(), dataset.ImportState, sp.GetRequiredService<TimeProvider>()));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        return new DatasetSession(provider, dataset);
    }

    /// <summary>Creates the pages of the session. Call once, on the UI thread, after the store has started.</summary>
    /// <param name="pickers">The main window's file pickers (Data page).</param>
    /// <param name="restore">Runs a validated restore; the startup coordinator owns the session it closes and reopens.</param>
    public WorkspaceViewModel CreateWorkspace(
        System.Reactive.Concurrency.IScheduler uiScheduler,
        IUiDispatcher dispatcher,
        ILocalizedStrings strings,
        IFilePickerService pickers,
        Func<RestoreValidation, Task<RestoreRunOutcome>> restore)
    {
        var data = new DataServices(
            _provider.GetRequiredService<ExportTransfer>(),
            _provider.GetRequiredService<ImportTransfer>(),
            TransferAdapters.CreateReader(),
            TransferAdapters.CreateExternalReader(),
            Dataset.ImportState,
            new DataMaintenanceService(Dataset, _provider.GetRequiredService<TimeProvider>(), restore, _provider.GetRequiredService<ILogger<DataMaintenanceService>>()),
            pickers);
        Workspace ??= new WorkspaceViewModel(
            Store,
            _provider.GetRequiredService<WorkspaceUseCases>(),
            uiScheduler,
            dispatcher,
            strings,
            _provider.GetRequiredService<TimeProvider>(),
            _provider.GetRequiredService<IUrlLauncher>(),
            _provider.GetRequiredService<IUiPreferencesStore>(),
            new SettingsServices(
                _provider.GetRequiredService<SettingsUseCases>(),
                _provider.GetRequiredService<ThemeService>(),
                _provider.GetRequiredService<IDataFolderLauncher>(),
                new BackupInfoSource(Dataset.Paths.BackupsDirectory),
                data));
        return Workspace;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        // Page subscriptions belong to the UI thread; this runs there, before the first await.
        Workspace?.Dispose();
        Store.Dispose();
        await Dataset.DisposeAsync().ConfigureAwait(false);
        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}
