using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Checklists;

/// <summary>
/// The Checklists page (B-09, design §4.5), Phase 2 subset: one group per active task that has checklist
/// items, newest task first. General to-dos and Reviews arrive in Phase 8. Use on the UI thread.
/// </summary>
public sealed partial class ChecklistsPageViewModel : ObservableObject, IPageViewModel
{
    private readonly WorkspaceContext _context;
    private readonly ProjectionView<TaskReadModel, TaskId, ChecklistGroupViewModel> _view;
    private readonly IDisposable _commits;

    public ChecklistsPageViewModel(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _view = new ProjectionView<TaskReadModel, TaskId, ChecklistGroupViewModel>(
            context.Store.Tasks,
            context.Store.Scheduler,
            context.UiScheduler,
            task => new ChecklistGroupViewModel(task, context),
            NewestFirst.Instance,
            task => !task.IsArchived && task.ChecklistTotal > 0);
        _view.Initialized += (_, _) => UpdateState();
        ((INotifyCollectionChanged)_view.Items).CollectionChanged += OnGroupsChanged;

        // Item toggles change counts without changing group membership; the store's batch notification is
        // queued after the group updates of the same batch, so the summary reads the updated groups.
        _commits = context.Store.Committed.ObserveOn(context.UiScheduler).Subscribe(_ => UpdateState());
    }

    public ReadOnlyObservableCollection<ChecklistGroupViewModel> Groups => _view.Items;

    [ObservableProperty]
    public partial ViewState State { get; private set; } = ViewState.Loading;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    public void Dispose()
    {
        _commits.Dispose();
        ((INotifyCollectionChanged)_view.Items).CollectionChanged -= OnGroupsChanged;
        _view.Dispose();
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        State = !_view.IsInitialized ? ViewState.Loading : Groups.Count == 0 ? ViewState.Empty : ViewState.Populated;
        var open = Groups.Sum(g => g.OpenItems.Count);
        Summary = _context.Strings.Format(open == 1 ? "Checklists_SummaryOne" : "Checklists_Summary", open, Groups.Count);
    }

    /// <summary>B-09: task groups are ordered newest parent first, then ID.</summary>
    private sealed class NewestFirst : IComparer<ChecklistGroupViewModel>
    {
        public static NewestFirst Instance { get; } = new();

        public int Compare(ChecklistGroupViewModel? x, ChecklistGroupViewModel? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null || y is null)
            {
                return x is null ? -1 : 1;
            }

            var byCreated = y.Model.CreatedAtUtc.CompareTo(x.Model.CreatedAtUtc);
            return byCreated != 0 ? byCreated : x.Model.Id.Value.CompareTo(y.Model.Id.Value);
        }
    }
}
