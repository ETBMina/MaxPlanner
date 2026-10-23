using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Checklists;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Grid;

namespace MaxPlanner.Presentation.Workspace;

/// <summary>
/// The pages of one open dataset. Page ViewModels are created on first visit and then retained until the
/// dataset closes, so navigating back and forth never creates a second subscription (T-07, AC-17,
/// design §2.2). Create, use, and dispose it on the UI thread; dispose it before the store.
/// </summary>
public sealed class WorkspaceViewModel : IDisposable
{
    private readonly IDisposable _stateSubscription;
    private BoardPageViewModel? _board;
    private GridPageViewModel? _grid;
    private ChecklistsPageViewModel? _checklists;
    private bool _isClosing;
    private bool _isDisposed;

    public WorkspaceViewModel(
        WorkspaceProjectionStore store,
        WorkspaceUseCases useCases,
        IScheduler uiScheduler,
        IUiDispatcher dispatcher,
        ILocalizedStrings strings,
        TimeProvider time,
        IUrlLauncher? urlLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        SaveStatus = new SaveStatusViewModel(strings, dispatcher, time);
        Context = new WorkspaceContext(store, useCases, uiScheduler, strings, SaveStatus, urlLauncher);
        _stateSubscription = store.State.ObserveOn(uiScheduler).Subscribe(OnStoreState);
    }

    public WorkspaceContext Context { get; }

    public SaveStatusViewModel SaveStatus { get; }

    public BoardPageViewModel Board => _board ??= new BoardPageViewModel(Live());

    public GridPageViewModel Grid => _grid ??= new GridPageViewModel(Live());

    public ChecklistsPageViewModel Checklists => _checklists ??= new ChecklistsPageViewModel(Live());

    /// <summary>The pages created so far (for disposal and the navigation-budget evidence).</summary>
    public IEnumerable<IPageViewModel> CreatedPages =>
        new IPageViewModel?[] { _board, _grid, _checklists }.OfType<IPageViewModel>();

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
        foreach (var page in CreatedPages)
        {
            page.Dispose();
        }

        SaveStatus.Dispose();
    }

    private WorkspaceContext Live() => _isDisposed ? throw new ObjectDisposedException(nameof(WorkspaceViewModel)) : Context;

    private void OnStoreState(ProjectionState state)
    {
        Context.IsEditingBlocked = _isClosing || state.IsEditingBlocked;
        SaveStatus.SetResyncing(state.Status == ProjectionStatus.Resyncing);
    }
}
