using System.ComponentModel;
using System.Reflection;
using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Scheduling;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Shell;
using MaxPlanner.Presentation.Tests.Testing;
using MaxPlanner.Presentation.Workspace;
using Microsoft.Extensions.Time.Testing;
using static MaxPlanner.Presentation.Tests.Testing.Records;

namespace MaxPlanner.Presentation.Tests.Workspace;

[Trait("Req", "T-07")]
public sealed class SliceBehaviourTests
{
    [Fact]
    [Trait("Req", "AC-06")]
    [Trait("Req", "B-09")]
    public void Checking_an_item_moves_it_to_completed_and_updates_board_progress_in_place()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Onboarding copy", "Mobile Banking", null, "Draft", "Review"));
        Templates(h);
        var board = h.Workspace.Board;
        var checklists = h.Workspace.Checklists;
        h.PumpUntil(() => checklists.Groups.Count == 1 && board.State == ViewState.Populated);
        h.Settle();
        var group = checklists.Groups.Single();
        var card = board.Columns.SelectMany(c => c.Cards).Single();
        card.ChecklistText.Should().Be("Checklist 0/2");

        h.Await(group.ToggleAsync(group.OpenItems[0], true));
        h.Settle();

        group.OpenItems.Select(i => i.Text).Should().Equal("Review");
        group.CompletedItems.Select(i => i.Text).Should().Equal("Draft");
        group.ProgressText.Should().Be("1 of 2 done");
        board.Columns.SelectMany(c => c.Cards).Single().Should().BeSameAs(card);
        card.ChecklistText.Should().Be("Checklist 1/2");
        h.Task(task).Status.Id.Should().Be(board.Columns[0].StatusId, "B-09: checking items never changes the task status");
    }

    [Fact]
    [Trait("Req", "B-09")]
    public void Two_quick_toggles_in_one_group_both_save()
    {
        using var h = WorkspaceHarness.Create(async s => await s.TaskAsync("Task", "Service", null, "One", "Two", "Three"));
        var checklists = h.Workspace.Checklists;
        h.PumpUntil(() => checklists.Groups.Count == 1);
        var group = checklists.Groups.Single();

        var first = group.ToggleAsync(group.OpenItems[0], true);
        var second = group.ToggleAsync(group.OpenItems[1], true);
        h.Await(System.Threading.Tasks.Task.WhenAll(first, second));
        h.Settle();

        group.CompletedItems.Select(i => i.Text).Should().Equal("One", "Two");
        group.ToggleError.Should().BeNull("the second toggle waits for the first result instead of conflicting");
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public void A_failed_toggle_puts_the_checkbox_back_and_reports_the_failure()
    {
        using var h = WorkspaceHarness.Create(async s => await s.TaskAsync("Task", "Service", null, "One"));
        var checklists = h.Workspace.Checklists;
        h.PumpUntil(() => checklists.Groups.Count == 1);
        var group = checklists.Groups.Single();
        var item = group.OpenItems.Single();
        var notified = new List<string?>();
        item.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        h.Coordinator.FailNext(new StorageFailureError(StorageFailureKind.DiskFull));
        h.Await(group.ToggleAsync(item, true));
        h.Settle();

        item.IsChecked.Should().BeFalse();
        notified.Should().Contain(nameof(Checklists.ChecklistItemViewModel.IsChecked), "the view re-reads the committed value to undo the click");
        group.OpenItems.Should().ContainSingle();
        group.ToggleError.Should().Be("Not saved. The drive is full.");
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Failed);
    }

    [Fact]
    [Trait("Req", "B-02.7")]
    public void Adding_a_blank_item_shows_an_inline_error_and_a_valid_item_is_added_and_the_box_cleared()
    {
        using var h = WorkspaceHarness.Create(async s => await s.TaskAsync("Task", "Service", null, "One"));
        var checklists = h.Workspace.Checklists;
        h.PumpUntil(() => checklists.Groups.Count == 1);
        var group = checklists.Groups.Single();
        var sequence = h.Store.Sequence;

        group.NewItemText = "   ";
        h.Await(group.AddItemCommand.ExecuteAsync(null));
        h.Settle();

        group.NewItemError.Should().Be("Field_Text is required.");
        h.Store.Sequence.Should().Be(sequence);
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Idle);

        group.NewItemText = "Two";
        h.Await(group.AddItemCommand.ExecuteAsync(null));
        h.Settle();

        group.NewItemError.Should().BeNull();
        group.NewItemText.Should().BeEmpty();
        group.OpenItems.Select(i => i.Text).Should().Equal("One", "Two");
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public async Task While_the_store_resyncs_editing_is_blocked_and_the_save_status_says_reloading()
    {
        using var h = new StoreHarness().StartWith(Snapshot(1, tasks: [Task("Card freeze")]));
        var ui = new ImmediateUiDispatcher();
        var coordinator = new RefusingCoordinator();
        using var workspace = new WorkspaceViewModel(h.Store, UseCasesOver(coordinator), new DispatcherQueueScheduler(ui), ui, new FakeStrings(), h.Clock);
        var grid = workspace.Grid;
        h.Scheduler.RunAll();
        var row = grid.Rows.Single();
        grid.BeginEdit(row, GridColumns.Title).Should().BeTrue();
        row.DraftText = "Draft kept through the resync";

        h.Publish(Change(3));

        workspace.Context.IsEditingBlocked.Should().BeTrue();
        workspace.SaveStatus.State.Should().Be(SaveState.Resyncing);
        workspace.SaveStatus.Text.Should().Be("SaveStatus_Resyncing");
        var commit = grid.CommitAsync(row);
        commit.IsCompletedSuccessfully.Should().BeTrue();
        (await commit).Should().BeFalse();
        row.ErrorMessage.Should().Be("Error_EditingBlocked");
        row.DraftText.Should().Be("Draft kept through the resync");
        coordinator.Calls.Should().Be(0);

        h.CompleteLoad(Snapshot(3, tasks: [Task("Card freeze")]));

        workspace.Context.IsEditingBlocked.Should().BeFalse();
        workspace.SaveStatus.State.Should().Be(SaveState.Idle);
    }

    [Fact]
    [Trait("Req", "T-07")]
    public void A_row_being_edited_holds_its_position_until_editing_ends_while_other_rows_re_sort()
    {
        TaskId alpha = default;
        TaskId charlie = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            alpha = await s.TaskAsync("Alpha");
            await s.TaskAsync("Bravo");
            charlie = await s.TaskAsync("Charlie");
        });
        var grid = h.Workspace.Grid;
        h.PumpUntil(() => grid.Rows.Count == 3);
        grid.SetOrder(new ByTitle());
        h.Settle();
        Titles(grid).Should().Equal("Alpha", "Bravo", "Charlie");
        var edited = grid.Rows[0];
        grid.BeginEdit(edited, GridColumns.Service).Should().BeTrue();

        h.External(u => u.UpdateTaskContent.ExecuteAsync(Rename(alpha, "Zulu"), default));
        h.External(u => u.UpdateTaskContent.ExecuteAsync(Rename(charlie, "Aardvark"), default));

        Titles(grid).Should().Equal(["Aardvark", "Zulu", "Bravo"], "the edited row stays where the user is typing; other rows re-sort");
        grid.Rows[1].Should().BeSameAs(edited);

        grid.CancelEdit(edited);
        h.Settle();

        Titles(grid).Should().Equal("Aardvark", "Bravo", "Zulu");
    }

    [Fact]
    [Trait("Req", "AC-17")]
    [Trait("Req", "T-11")]
    public void A_hundred_navigation_cycles_keep_the_live_subscription_count_stable_and_disposal_releases_it()
    {
        using var h = WorkspaceHarness.Create(async s => await s.TaskAsync("Task", "Service", null, "Item"));
        var preferences = new FakePreferencesStore();
        using var shell = new ShellViewModel(new FakeStrings(), new FakeDataFolder(), preferences);
        shell.AttachWorkspace(h.Workspace);

        Cycle(shell);
        h.Settle();
        var afterFirstCycle = h.Store.ActiveCacheSubscriptions;
        var pages = (h.Workspace.Board, h.Workspace.Grid, h.Workspace.Checklists);
        for (var i = 1; i < 100; i++)
        {
            Cycle(shell);
        }

        h.Settle();

        // Grid: tasks + Subtask cards; Board: tasks + Subtask cards per status column (8 seeded); Checklists: tasks.
        afterFirstCycle.Should().Be(2 + (8 * 2) + 1);
        h.Store.ActiveCacheSubscriptions.Should().Be(afterFirstCycle);
        (h.Workspace.Board, h.Workspace.Grid, h.Workspace.Checklists).Should().Be(pages, "page ViewModels are retained, never recreated");
        preferences.Current.ActivePage.Should().Be("checklists");
        shell.StatusSummary.Should().Be(h.Workspace.Checklists.Summary);

        shell.DetachWorkspace();
        h.Workspace.Dispose();
        h.Settle();
        h.Store.ActiveCacheSubscriptions.Should().Be(0);
    }

    [Fact]
    [Trait("Req", "T-08")]
    public void Restored_active_page_is_activated_and_has_view_model()
    {
        using var h = WorkspaceHarness.Create();
        using var shell = new ShellViewModel(new FakeStrings(), new FakeDataFolder(), new FakePreferencesStore(new Application.Preferences.UiPreferences { ActivePage = "calendar" }));
        shell.AttachWorkspace(h.Workspace);

        shell.CurrentPage.Should().Be(AppPage.Calendar, "B-04: the last active page is restored");
        shell.PageViewModel(AppPage.Calendar).Should().NotBeNull();
    }

    [Fact]
    [Trait("Req", "B-07")]
    public void Save_status_reports_actual_results_and_returns_to_idle_three_seconds_after_saving()
    {
        var time = new FakeTimeProvider();
        using var status = new SaveStatusViewModel(new FakeStrings(), new ImmediateUiDispatcher(), time);

        status.Begin();
        status.Begin();
        status.Complete();
        status.State.Should().Be(SaveState.Saving, "another command is still in flight");
        status.Complete();
        status.State.Should().Be(SaveState.Saved);
        time.Advance(TimeSpan.FromSeconds(2.9));
        status.State.Should().Be(SaveState.Saved);
        time.Advance(TimeSpan.FromSeconds(0.2));
        status.State.Should().Be(SaveState.Idle);

        status.Begin();
        status.Fail(new CommandFailure(new StorageFailureError(StorageFailureKind.DiskFull), "Full.", OperationId.New()));
        status.State.Should().Be(SaveState.Failed);
        status.IsFailureNoticeOpen.Should().BeTrue();

        status.Begin();
        status.Reject();
        status.State.Should().Be(SaveState.Failed, "a validation refusal is not a persistence result");

        status.SetResyncing(true);
        status.State.Should().Be(SaveState.Resyncing);
        status.SetResyncing(false);
        status.State.Should().Be(SaveState.Failed);
    }

    [Fact]
    [Trait("Req", "T-04")]
    public void View_models_use_use_cases_and_presentation_services_never_storage_ports_or_infrastructure()
    {
        Type[] forbidden = [typeof(IMutationCoordinator), typeof(IChangeFeed), typeof(IReadSnapshotSource), typeof(IWriteTransaction), typeof(IOperationStatusQuery)];
        var viewModels = typeof(WorkspaceViewModel).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("ViewModel", StringComparison.Ordinal) || t == typeof(WorkspaceContext))
            .ToList();
        viewModels.Should().HaveCountGreaterThan(8);

        var violations = viewModels.SelectMany(t =>
                t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Select(f => (t, f.FieldType))
                    .Concat(t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SelectMany(c => c.GetParameters()).Select(p => (t, p.ParameterType))))
            .Where(d => forbidden.Contains(d.Item2) || (d.Item2.Namespace ?? string.Empty).StartsWith("MaxPlanner.Infrastructure", StringComparison.Ordinal)
                || (d.Item2.Namespace ?? string.Empty).StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal))
            .Select(d => $"{d.t.Name} -> {d.Item2.Name}")
            .ToList();

        violations.Should().BeEmpty();
    }

    private static void Templates(WorkspaceHarness h) =>
        h.Strings.With("Checklists_Progress", "{0} of {1} done").With("Board_ChecklistProgress", "Checklist {0}/{1}");

    private static void Cycle(ShellViewModel shell)
    {
        shell.NavigateTo(AppPage.Board);
        shell.NavigateTo(AppPage.Grid);
        shell.NavigateTo(AppPage.Checklists);
    }

    private static List<string> Titles(GridPageViewModel grid) => [.. grid.Rows.Select(r => r.TitleText)];

    private static UpdateTaskContentCommand Rename(TaskId id, string title) =>
        new(OperationId.New(), id, 1, new TaskContentDraft(title, "Mobile Banking", null, null, [], null, CustomValue.NoValues));

    private static WorkspaceUseCases UseCasesOver(IMutationCoordinator coordinator) => WorkspaceUseCases.Create(coordinator);

    /// <summary>A Phase 9 sort stand-in: by title, on the row's held sort snapshot (design §7.7).</summary>
    private sealed class ByTitle : IComparer<GridRowViewModel>
    {
        public int Compare(GridRowViewModel? x, GridRowViewModel? y) =>
            string.CompareOrdinal(x?.SortModel.Task?.EffectiveTitle, y?.SortModel.Task?.EffectiveTitle);
    }

    /// <summary>Fails every command; a test that expects no command to start asserts <see cref="Calls"/> is zero.</summary>
    private sealed class RefusingCoordinator : IMutationCoordinator
    {
        public int Calls { get; private set; }

        public Task<AppResult<CommandOutcome<T>>> ExecuteAsync<T>(OperationId operationId, string operationKind, Func<IWriteTransaction, TxResult<T>> work, CancellationToken cancellationToken)
        {
            Calls++;
            return System.Threading.Tasks.Task.FromResult(AppResult.Failure<CommandOutcome<T>>(new StorageFailureError(StorageFailureKind.Unavailable)));
        }
    }
}
