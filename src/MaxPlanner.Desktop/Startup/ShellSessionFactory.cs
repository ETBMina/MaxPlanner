using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MaxPlanner.Application.Startup;
using MaxPlanner.Desktop.Composition;
using MaxPlanner.Infrastructure.Startup;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Desktop.Startup;

/// <summary>
/// Builds a <see cref="DatasetSession"/> over an opened dataset and shows it in the shell: the store, the pages, the
/// T-11 navigation and startup-timing logs, and the first-ready signal the coordinator waits for. Runs on the UI thread.
/// </summary>
internal sealed partial class ShellSessionFactory : IDatasetSessionFactory
{
    private readonly IServiceProvider _appServices;
    private readonly ShellViewModel _shell;
    private readonly IScheduler _uiScheduler;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILocalizedStrings _strings;
    private readonly IFilePickerService _pickers;
    private readonly StartupContext _context;
    private readonly ILogger _logger;

    /// <param name="appServices">The app container; <see cref="DatasetSession"/> forwards its shared services.</param>
    public ShellSessionFactory(
        IServiceProvider appServices,
        ShellViewModel shell,
        IScheduler uiScheduler,
        IUiDispatcher uiDispatcher,
        ILocalizedStrings strings,
        IFilePickerService pickers,
        StartupContext context,
        ILogger<ShellSessionFactory> logger)
    {
        _appServices = appServices;
        _shell = shell;
        _uiScheduler = uiScheduler;
        _uiDispatcher = uiDispatcher;
        _strings = strings;
        _pickers = pickers;
        _context = context;
        _logger = logger;
    }

    public IDatasetSession Create(IOpenedDataset dataset, DatasetSessionHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        if (dataset is not OpenedSqliteDataset sqlite)
        {
            throw new ArgumentException("The dataset was not opened by the SQLite storage.", nameof(dataset));
        }

        var session = DatasetSession.Create(_appServices, sqlite.Dataset);
        _shell.AttachStore(session.Store, _uiScheduler);
        _shell.AttachWorkspace(session.CreateWorkspace(_uiScheduler, _uiDispatcher, _strings, _pickers, hooks.Restore));
        return new ShellSession(this, session, hooks.OnFirstReady);
    }

    // T-11 startup budget ("usable initial view"): the first frame rendered after the store became ready. The
    // low-priority enqueue lets the page bindings that the ready state scheduled run first. scripts/run-performance.ps1
    // reads this line (event 5016).
    private void LogFirstRenderedFrame()
    {
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            void OnRendered(object? sender, Microsoft.UI.Xaml.Media.RenderedEventArgs e)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendered -= OnRendered;
                Log.FirstViewRendered(_logger, _context.ElapsedMilliseconds);
            }

            Microsoft.UI.Xaml.Media.CompositionTarget.Rendered += OnRendered;
        });
    }

    private sealed class ShellSession : IDatasetSession
    {
        private readonly ShellSessionFactory _owner;
        private readonly DatasetSession _session;
        private readonly Action _onFirstReady;
        private readonly IDisposable _readySubscription;

        public ShellSession(ShellSessionFactory owner, DatasetSession session, Action onFirstReady)
        {
            _owner = owner;
            _session = session;
            _onFirstReady = onFirstReady;
            owner._shell.PropertyChanged += OnShellPropertyChanged;
            _readySubscription = session.Store.State
                .Where(state => state.Status == ProjectionStatus.Ready)
                .Take(1)
                .ObserveOn(owner._uiScheduler)
                .Subscribe(_ => OnFirstReady());
        }

        public IDatasetBackups Backups => _session.Backups;

        public void Start() => _session.Store.Start();

        /// <summary>Stops the shell hooks, then runs the T-06.6 close of the session (pages, store, dataset, container).</summary>
        public ValueTask DisposeAsync()
        {
            _readySubscription.Dispose();
            _owner._shell.PropertyChanged -= OnShellPropertyChanged;
            return _session.DisposeAsync();
        }

        // T-11 / AC-17 evidence: every navigation logs the live store subscriptions and the working set, so a
        // navigation run shows whether either grows (ADR 0010 open point).
        private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.CurrentPage))
            {
                var page = _owner._shell.CurrentPage.ToString();
                var workingSetMiB = Environment.WorkingSet / (1024 * 1024);
                Log.Navigated(_owner._logger, page, _session.Store.ActiveCacheSubscriptions, workingSetMiB);
            }
        }

        private void OnFirstReady()
        {
            var store = _session.Store;
            var elapsedMs = _owner._context.ElapsedMilliseconds;
            Log.Ready(_owner._logger, elapsedMs, store.Tasks.Count, store.ChangeRequests.Count, store.ReviewItems.Count);
            _owner.LogFirstRenderedFrame();
            _onFirstReady();
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5003, Level = LogLevel.Information, Message = "Startup ready: usable view at {ElapsedMs} ms after launch ({Tasks} tasks, {ChangeRequests} CRs, {Reviews} reviews).")]
        public static partial void Ready(ILogger logger, long elapsedMs, int tasks, int changeRequests, int reviews);

        [LoggerMessage(EventId = 5016, Level = LogLevel.Information, Message = "Startup rendered: first frame with the ready view at {ElapsedMs} ms after launch.")]
        public static partial void FirstViewRendered(ILogger logger, long elapsedMs);

        [LoggerMessage(EventId = 5010, Level = LogLevel.Information, Message = "Navigated to {Page:l}; live store subscriptions {Subscriptions}; working set {WorkingSetMiB} MiB.")]
        public static partial void Navigated(ILogger logger, string page, int subscriptions, long workingSetMiB);
    }
}
