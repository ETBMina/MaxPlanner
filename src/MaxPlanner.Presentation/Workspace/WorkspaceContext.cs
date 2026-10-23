using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Tasks;

namespace MaxPlanner.Presentation.Workspace;

/// <summary>
/// The Application use cases the pages and editors call (T-07: every edit goes through the same command path).
/// Use cases are stateless, so one instance of each serves every page of the open dataset.
/// </summary>
public sealed record WorkspaceUseCases(
    CreateTask CreateTask,
    UpdateTaskContent UpdateTaskContent,
    MoveTask MoveTask,
    UpdateSubtaskCardContent UpdateSubtaskCardContent,
    MoveSubtaskCard MoveSubtaskCard,
    AddChecklistItem AddChecklistItem,
    SetChecklistItemCompleted SetChecklistItemCompleted,
    UpdateTask UpdateTask,
    CopyTask CopyTask,
    ArchiveTask ArchiveTask,
    RestoreTask RestoreTask,
    DeleteTask DeleteTask,
    LinkTaskToChangeRequest LinkTaskToChangeRequest,
    CreateSubtaskCard CreateSubtaskCard,
    DeleteSubtaskCard DeleteSubtaskCard,
    PopOutChecklistItem PopOutChecklistItem)
{
    /// <summary>Every use case over one dataset's coordinator; the composition root and tests share this list.</summary>
    public static WorkspaceUseCases Create(IMutationCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return new(
            new CreateTask(coordinator),
            new UpdateTaskContent(coordinator),
            new MoveTask(coordinator),
            new UpdateSubtaskCardContent(coordinator),
            new MoveSubtaskCard(coordinator),
            new AddChecklistItem(coordinator),
            new SetChecklistItemCompleted(coordinator),
            new UpdateTask(coordinator),
            new CopyTask(coordinator),
            new ArchiveTask(coordinator),
            new RestoreTask(coordinator),
            new DeleteTask(coordinator),
            new LinkTaskToChangeRequest(coordinator),
            new CreateSubtaskCard(coordinator),
            new DeleteSubtaskCard(coordinator),
            new PopOutChecklistItem(coordinator));
    }
}

/// <summary>A screen-reader announcement (design §8.3).</summary>
/// <param name="IsFailure">ActionAborted rather than ActionCompleted.</param>
public sealed record Announcement(string Text, bool IsFailure);

/// <summary>
/// What every page of one open dataset shares: the projection store, the use cases, the UI scheduler, the
/// save indicator, whether editing is currently allowed, and the dialog and announcement channels. Use it
/// on the UI thread.
/// </summary>
public sealed partial class WorkspaceContext : ObservableObject
{
    private TaskCommands? _tasks;

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

    /// <summary>
    /// Raised when a ViewModel needs a dialog (design §5). The shell shows it with the window's XamlRoot,
    /// one at a time, and calls <see cref="DialogRequest.MarkClosed"/>; tests answer it directly.
    /// </summary>
    public event EventHandler<DialogRequest>? DialogRequested;

    /// <summary>Raised for screen-reader announcements (design §8.3); the shell raises the UIA notification.</summary>
    public event EventHandler<Announcement>? Announced;

    public WorkspaceProjectionStore Store { get; }

    public WorkspaceUseCases UseCases { get; }

    public IScheduler UiScheduler { get; }

    public ILocalizedStrings Strings { get; }

    public SaveStatusViewModel SaveStatus { get; }

    /// <summary>The task commands shared by the Board, Grid, Checklists, and the editors.</summary>
    public TaskCommands Tasks => _tasks ??= new TaskCommands(this);

    /// <summary>
    /// T-07: true while the store is not Ready (loading, resyncing, faulted) or the app is closing. Editors
    /// must not start a command then, because the displayed revisions may be behind the database.
    /// </summary>
    [ObservableProperty]
    public partial bool IsEditingBlocked { get; internal set; } = true;

    /// <summary>Shows a dialog and completes when it closed. Without a dialog host (no window) nothing is shown.</summary>
    public Task ShowDialogAsync(IDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (DialogRequested is not { } handler)
        {
            return Task.CompletedTask;
        }

        var request = new DialogRequest(viewModel);
        handler(this, request);
        return request.Closed;
    }

    public void Announce(string text, bool isFailure = false) => Announced?.Invoke(this, new Announcement(text, isFailure));

    /// <summary>
    /// Completes once the page lists show the committed state of <paramref name="sequence"/> (T-07: show
    /// committed state only), or after <paramref name="timeout"/> (for example during a resync).
    /// </summary>
    /// <remarks>
    /// Store notifications reach the UI scheduler in order, and the batch notification follows the cache
    /// updates of its batch. When the store already passed the sequence, a marker sent through the store
    /// scheduler and then the UI scheduler runs after every update that was queued before it.
    /// </remarks>
    public async Task WhenDisplayedAsync(long sequence, TimeSpan timeout)
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var committed = Store.Committed.ObserveOn(UiScheduler).Where(c => c.Sequence >= sequence).Take(1).Subscribe(_ => shown.TrySetResult());
        using var marker = Store.Scheduler.Schedule(() =>
        {
            if (Store.Sequence >= sequence)
            {
                UiScheduler.Schedule(() => shown.TrySetResult());
            }
        });
        await Task.WhenAny(shown.Task, Task.Delay(timeout)).ConfigureAwait(true);
    }
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
