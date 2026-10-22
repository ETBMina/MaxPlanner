using System.ComponentModel;
using System.Reactive.Concurrency;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;

namespace MaxPlanner.Presentation.Workspace;

/// <summary>
/// The Application use cases the slice pages call (T-07: every edit goes through the same command path).
/// Use cases are stateless, so one instance of each serves every page of the open dataset.
/// </summary>
public sealed record WorkspaceUseCases(
    CreateTask CreateTask,
    UpdateTaskContent UpdateTaskContent,
    MoveTask MoveTask,
    UpdateSubtaskCardContent UpdateSubtaskCardContent,
    MoveSubtaskCard MoveSubtaskCard,
    AddChecklistItem AddChecklistItem,
    SetChecklistItemCompleted SetChecklistItemCompleted);

/// <summary>
/// What every page of one open dataset shares: the projection store, the use cases, the UI scheduler, the
/// save indicator, and whether editing is currently allowed. Use it on the UI thread.
/// </summary>
public sealed partial class WorkspaceContext : ObservableObject
{
    public WorkspaceContext(
        WorkspaceProjectionStore store,
        WorkspaceUseCases useCases,
        IScheduler uiScheduler,
        ILocalizedStrings strings,
        SaveStatusViewModel saveStatus)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        UseCases = useCases ?? throw new ArgumentNullException(nameof(useCases));
        UiScheduler = uiScheduler ?? throw new ArgumentNullException(nameof(uiScheduler));
        Strings = strings ?? throw new ArgumentNullException(nameof(strings));
        SaveStatus = saveStatus ?? throw new ArgumentNullException(nameof(saveStatus));
    }

    public WorkspaceProjectionStore Store { get; }

    public WorkspaceUseCases UseCases { get; }

    public IScheduler UiScheduler { get; }

    public ILocalizedStrings Strings { get; }

    public SaveStatusViewModel SaveStatus { get; }

    /// <summary>
    /// T-07: true while the store is not Ready (loading, resyncing, faulted) or the app is closing. Editors
    /// must not start a command then, because the displayed revisions may be behind the database.
    /// </summary>
    [ObservableProperty]
    public partial bool IsEditingBlocked { get; internal set; } = true;
}

/// <summary>The state a page's <c>StateView</c> shows (design §2.5).</summary>
public enum ViewState
{
    Loading,
    Empty,
    FilteredEmpty,
    Error,
    Populated,
}

/// <summary>A retained page ViewModel (design §2.2: page ViewModels are kept for the session).</summary>
public interface IPageViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>The status-bar page summary (design §2.8).</summary>
    string Summary { get; }
}
