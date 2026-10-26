using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MaxPlanner.Application.Dashboard;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Calendar;
using MaxPlanner.Presentation.Checklists;
using MaxPlanner.Presentation.Dashboard;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Settings;
using MaxPlanner.Presentation.Shell;

namespace MaxPlanner.Presentation.Workspace;

/// <summary>
/// The pages of one open dataset. Page ViewModels are created on first visit and then retained until the
/// dataset closes, so navigating back and forth never creates a second subscription (T-07, AC-17,
/// design §2.2). Create, use, and dispose it on the UI thread; dispose it before the store.
/// </summary>
public sealed class WorkspaceViewModel : IDisposable
{
    private readonly IDisposable _stateSubscription;
    private readonly IDisposable _badgeSubscription;
    private DashboardPageViewModel? _dashboard;
    private BoardPageViewModel? _board;
    private GridPageViewModel? _grid;
    private CalendarPageViewModel? _calendar;
    private ChecklistsPageViewModel? _checklists;
    private SettingsPageViewModel? _settings;
    private readonly SettingsServices? _settingsServices;
    private bool _isClosing;
    private bool _isDisposed;

    public WorkspaceViewModel(
        WorkspaceProjectionStore store,
        WorkspaceUseCases useCases,
        IScheduler uiScheduler,
        IUiDispatcher dispatcher,
        ILocalizedStrings strings,
        TimeProvider time,
        IUrlLauncher? urlLauncher = null,
        IUiPreferencesStore? preferences = null,
        SettingsServices? settings = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _settingsServices = settings;
        SaveStatus = new SaveStatusViewModel(strings, dispatcher, time);
        Context = new WorkspaceContext(store, useCases, uiScheduler, strings, SaveStatus, urlLauncher, preferences);
        _stateSubscription = store.State.ObserveOn(uiScheduler).Subscribe(OnStoreState);
        _badgeSubscription = store.Committed.ObserveOn(uiScheduler).Subscribe(_ => UpdateOpenChecklistCount(store));
        Context.Navigator.DrillDownRequested += OnDrillDownRequested;
        Context.ExportViewRequested += OnExportViewRequested;
    }

    /// <summary>Raised when <see cref="OpenChecklistCount"/> changed (the Checklists nav badge, design §2.2).</summary>
    public event EventHandler? OpenChecklistCountChanged;

    /// <summary>
    /// Open General to-dos, open checklist items of active tasks, and open reviews: what the Checklists page lists
    /// as still to do. Kept here so the badge is right before the page is first visited.
    /// </summary>
    public int OpenChecklistCount { get; private set; }

    public WorkspaceContext Context { get; }

    public SaveStatusViewModel SaveStatus { get; }

    public DashboardPageViewModel Dashboard => _dashboard ??= new DashboardPageViewModel(Live());

    public BoardPageViewModel Board => _board ??= new BoardPageViewModel(Live());

    public GridPageViewModel Grid => _grid ??= new GridPageViewModel(Live());

    public CalendarPageViewModel Calendar => _calendar ??= new CalendarPageViewModel(Live());

    public ChecklistsPageViewModel Checklists => _checklists ??= new ChecklistsPageViewModel(Live());

    /// <summary>The Settings page, or null when the composition gave no Settings services (tests without them).</summary>
    public SettingsPageViewModel? Settings => _settingsServices is null ? null : _settings ??= new SettingsPageViewModel(Live(), _settingsServices);

    /// <summary>The pages created so far (for disposal and the navigation-budget evidence).</summary>
    public IEnumerable<IPageViewModel> CreatedPages =>
        new IPageViewModel?[] { _dashboard, _board, _grid, _calendar, _checklists, _settings }.OfType<IPageViewModel>();

    /// <summary>The app is closing (T-06.6): no new command may start, whatever the store reports.</summary>
    public void BeginClose()
    {
        _isClosing = true;
        Context.IsEditingBlocked = true;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _stateSubscription.Dispose();
        _badgeSubscription.Dispose();
        Context.Navigator.DrillDownRequested -= OnDrillDownRequested;
        Context.ExportViewRequested -= OnExportViewRequested;
        foreach (var page in CreatedPages)
        {
            page.Dispose();
        }

        Context.DisposeFilters();

        SaveStatus.Dispose();
    }

    /// <summary>
    /// Design §2.9: the destination gets a replacement filter context (never intersected with its old filters), the
    /// context is pushed for Return and Back, and the shell shows the destination. Task and Subtask card metrics open
    /// the Grid, Overdue CRs open Calendar › Records, and review metrics open the Checklists Reviews card (B-10, AC-20).
    /// </summary>
    private void OnDrillDownRequested(object? sender, DrillDownRequest request)
    {
        if (_isDisposed || _isClosing)
        {
            return;
        }

        switch (request.DrillDown)
        {
            case TaskDrillDown task:
                _ = Grid;
                Context.TaskFilters.ReplaceWithDashboard(task.Spec, request.Label);
                Context.Navigator.Push(AppPage.Grid, Context.TaskFilters.EndDashboardContext);
                break;
            case CrDrillDown cr:
                Calendar.SetMode(CalendarMode.Records);
                Context.CrFilters.ReplaceWithDashboard(cr.Spec, request.Label);
                Context.Navigator.Push(AppPage.Calendar, Context.CrFilters.EndDashboardContext);
                break;
            case ReviewDrillDown review:
                Checklists.ShowDashboardReviews(review.Spec, request.Label);
                Context.Navigator.Push(AppPage.Checklists, Checklists.EndDashboardContext);
                break;
            default:
                break;
        }
    }

    private void OnExportViewRequested(object? sender, global::MaxPlanner.Application.Transfer.ViewExportScope view)
    {
        if (_isDisposed || _isClosing || Settings is not { } settings)
        {
            return;
        }

        settings.OpenDataPage(MaxPlanner.Presentation.Settings.DataPage.DataMode.Export, view);
        Context.Navigator.RequestNavigation(AppPage.Settings);
    }

    private WorkspaceContext Live() => _isDisposed ? throw new ObjectDisposedException(nameof(WorkspaceViewModel)) : Context;

    private void UpdateOpenChecklistCount(WorkspaceProjectionStore store)
    {
        var count = store.GeneralTodos.Items.Count(t => !t.IsCompleted)
            + store.Tasks.Items.Where(t => !t.IsArchived).Sum(t => t.ChecklistTotal - t.ChecklistDone)
            + store.ReviewItems.Items.Count(r => !r.IsDone);
        if (count != OpenChecklistCount)
        {
            OpenChecklistCount = count;
            OpenChecklistCountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnStoreState(ProjectionState state)
    {
        Context.IsEditingBlocked = _isClosing || state.IsEditingBlocked;
        SaveStatus.SetResyncing(state.Status == ProjectionStatus.Resyncing);
    }
}
