using System.Reactive.Concurrency;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Desktop.Startup;
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
        services.AddSingleton(sp => new StartupCoordinator(
            context,
            sp,
            sp.GetRequiredService<ShellViewModel>(),
            sp.GetRequiredService<ILocalizedStrings>(),
            sp.GetRequiredService<IDataFolderLauncher>(),
            sp.GetRequiredService<IScheduler>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<TimeProvider>(),
            requestClose,
            sp.GetRequiredService<ILogger<StartupCoordinator>>()));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
