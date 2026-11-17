using System.Reactive.Concurrency;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.Startup;
using MaxPlanner.Desktop.Startup;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Startup;
using MaxPlanner.Infrastructure.Storage;
using MaxPlanner.Presentation.Common.Scheduling;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace MaxPlanner.Desktop.Composition;

/// <summary>
/// The composition root for app-lifetime services. Dataset services are added later by
/// <see cref="DatasetSession"/>, because they exist only after the dataset opened. Nothing outside
/// composition resolves services from a container.
/// </summary>
internal static class AppComposition
{
    /// <param name="requestClose">Starts the normal close path (used by the recovery page's Exit).</param>
    public static ServiceProvider Build(StartupContext context, DispatcherQueue uiQueue, Action requestClose)
    {
        var services = new ServiceCollection();

        // Owned by Program.Main (StartupContext), so registered as instances the container does not dispose.
        services.AddSingleton(context);
        services.AddSingleton(context.Paths);
        services.AddSingleton(context.LoggerFactory);
        services.AddSingleton<IUiPreferencesStore>(context.Preferences);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<ILocalizedStrings, ResourceStrings>();

        // ADR 0020: the "General" squad label is localized, so the composer gets it from the resources.
        services.AddSingleton(sp => new ReadModelComposer(sp.GetRequiredService<ILocalizedStrings>().GetString("Squad_General")));

        services.AddSingleton<IUiDispatcher>(new DispatcherQueueUiDispatcher(uiQueue));
        services.AddSingleton<IScheduler>(sp => new DispatcherQueueScheduler(sp.GetRequiredService<IUiDispatcher>()));
        services.AddSingleton<LocalDayBoundarySource>();
        services.AddSingleton<IDayBoundarySource>(sp => sp.GetRequiredService<LocalDayBoundarySource>());

        services.AddSingleton<IDataFolderLauncher, ExplorerDataFolderLauncher>();
        services.AddSingleton<IUrlLauncher, ShellUrlLauncher>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<Maintenance.WindowFilePickers>();

        // Startup, recovery, and restore (T-09, ADR 0015): the Application coordinator over the Infrastructure storage
        // and restore adapters, with the shell and the per-dataset session on the Desktop side.
        services.AddSingleton<IDatasetStorage>(sp => new SqliteDatasetStorage(
            context.Paths, new SqliteDatasetOptions(sp.GetRequiredService<TimeProvider>(), context.LoggerFactory)));
        services.AddSingleton<IRestoreOperations>(new FileRestoreOperations(context.Paths, StartupContext.AppVersion));
        services.AddSingleton(sp => new ShellSessionFactory(
            sp,
            sp.GetRequiredService<ShellViewModel>(),
            sp.GetRequiredService<IScheduler>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<ILocalizedStrings>(),
            sp.GetRequiredService<Maintenance.WindowFilePickers>(),
            context,
            sp.GetRequiredService<ILogger<ShellSessionFactory>>()));
        services.AddSingleton(sp => new ShellStartupView(
            sp.GetRequiredService<ShellViewModel>(),
            sp.GetRequiredService<ILocalizedStrings>(),
            sp.GetRequiredService<IDataFolderLauncher>(),
            sp.GetRequiredService<Maintenance.WindowFilePickers>(),
            context,
            requestClose,
            sp.GetRequiredService<ILogger<ShellStartupView>>()));
        services.AddSingleton(sp =>
        {
            var view = sp.GetRequiredService<ShellStartupView>();
            var coordinator = new StartupCoordinator(
                sp.GetRequiredService<IDatasetStorage>(),
                sp.GetRequiredService<IRestoreOperations>(),
                sp.GetRequiredService<ShellSessionFactory>(),
                view,
                sp.GetRequiredService<TimeProvider>(),
                new StartupEnvironment(context.Inspection.HasPriorUse, StartupContext.AppVersion, () => context.ElapsedMilliseconds),
                sp.GetRequiredService<ILogger<StartupCoordinator>>());
            view.Connect(coordinator);
            return coordinator;
        });

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
