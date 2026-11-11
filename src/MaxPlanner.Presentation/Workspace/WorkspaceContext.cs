using System.Reactive.Concurrency;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Presentation.ChangeRequests;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Filters;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Tasks;

namespace MaxPlanner.Presentation.Workspace;

/// <summary>
/// What every page of one open dataset shares: the projection store, the use cases, the UI scheduler, the
/// save indicator, whether editing is currently allowed, and the dialog and announcement channels. Use it
/// on the UI thread.
/// </summary>
public sealed partial class WorkspaceContext : ObservableObject
{
    private TaskCommands? _tasks;
    private ChangeRequestCommands? _changeRequests;
    private TaskFilterScopeViewModel? _taskFilters;
    private CrFilterScopeViewModel? _crFilters;

    /// <param name="urlLauncher">Opens design links (B-02.11); null opens nothing (tests, no window).</param>
    public WorkspaceContext(
        WorkspaceProjectionStore store,
        WorkspaceUseCases useCases,
        IScheduler uiScheduler,
        ILocalizedStrings strings,
        SaveStatusViewModel saveStatus,
        IUrlLauncher? urlLauncher = null,
        IUiPreferencesStore? preferences = null)
    {
        UrlLauncher = urlLauncher ?? NoUrlLauncher.Instance;
        Preferences = preferences ?? new InMemoryUiPreferences();
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

    /// <summary>A CR editor saved a CR (B-08.12: the Calendar says when the saved CR is hidden by its filters).</summary>
    public event EventHandler<ChangeRequestId>? ChangeRequestSaved;

    /// <summary>A page's Export… asked for Settings › Data › Export preset with its current view (B-12).</summary>
    public event EventHandler<global::MaxPlanner.Application.Transfer.ViewExportScope>? ExportViewRequested;

    public WorkspaceProjectionStore Store { get; }

    public WorkspaceUseCases UseCases { get; }

    public IScheduler UiScheduler { get; }

    public ILocalizedStrings Strings { get; }

    public SaveStatusViewModel SaveStatus { get; }

    public IUrlLauncher UrlLauncher { get; }

    /// <summary>UI preferences (T-09): Grid layout and sort, archive visibility. In memory when no store is given (tests).</summary>
    public IUiPreferencesStore Preferences { get; }

    /// <summary>
    /// Settings › Appearance › Show tags on Board cards (design §4.2, §4.6): on unless the user turned it off. A UI
    /// preference, so it is saved in the preferences file and never exported (ADR 0026). Setting it saves it, and the
    /// change notification reaches every open Board card at once.
    /// </summary>
    public bool ShowBoardCardTags
    {
        get => Preferences.Current.ShowBoardCardTags ?? true;
        set
        {
            if (value == ShowBoardCardTags)
            {
                return;
            }

            Preferences.Update(p => p with { ShowBoardCardTags = value });
            OnPropertyChanged();
        }
    }

    /// <summary>The task and Subtask card commands shared by the Board, Grid, Checklists, and the editors.</summary>
    public TaskCommands Tasks => _tasks ??= new TaskCommands(this);

    /// <summary>The CR commands (open, new, create task from a CR) shared by the pages and editors.</summary>
    public ChangeRequestCommands ChangeRequests => _changeRequests ??= new ChangeRequestCommands(this);

    /// <summary>The shared Board/Grid task filter scope (B-10), created on first use and disposed with the workspace.</summary>
    public TaskFilterScopeViewModel TaskFilters => _taskFilters ??= new TaskFilterScopeViewModel(this);

    /// <summary>The Calendar CR filter scope (B-10).</summary>
    public CrFilterScopeViewModel CrFilters => _crFilters ??= new CrFilterScopeViewModel(this);

    /// <summary>The Dashboard drill-down context stack (design §2.9).</summary>
    public DrillDownNavigator Navigator { get; } = new();

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

    /// <summary>Called by the CR editor after a save was displayed (B-08.12).</summary>
    public void ReportChangeRequestSaved(ChangeRequestId id) => ChangeRequestSaved?.Invoke(this, id);

    public void RequestExportView(global::MaxPlanner.Application.Transfer.ViewExportScope view) => ExportViewRequested?.Invoke(this, view);

    /// <summary>Disposes the filter scopes; the workspace calls it when the dataset closes.</summary>
    internal void DisposeFilters()
    {
        _taskFilters?.Dispose();
        _crFilters?.Dispose();
    }

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
