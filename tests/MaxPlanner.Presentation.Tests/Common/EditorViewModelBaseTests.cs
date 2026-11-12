using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Common;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Tests.Testing;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// The editor state machine every draft editor shares (B-02.8–9, T-05, design §5.8, §5.10), on a probe editor
/// whose command is scripted: save and retry, the dirty-close prompt, the window-close guard, follow-ups,
/// failures, and the commit subscription.
/// </summary>
[Trait("Req", "B-02.8")]
public sealed class EditorViewModelBaseTests
{
    [Fact]
    [Trait("Req", "T-05")]
    public void An_unknown_outcome_is_retried_under_the_same_operation_and_anything_else_starts_a_new_one()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        editor.Results.Enqueue(new CommitOutcomeUnknownError(OperationId.New()));
        editor.Results.Enqueue(new StorageFailureError(StorageFailureKind.DiskFull));
        editor.Results.Enqueue(null);

        h.Await(editor.SaveAsync()).Should().BeFalse();
        h.Await(editor.SaveAsync()).Should().BeFalse();
        h.Await(editor.SaveAsync()).Should().BeTrue();

        editor.Operations.Should().HaveCount(3);
        editor.Operations[1].Should().Be(editor.Operations[0], "T-05: the unknown outcome is retried as the same operation");
        editor.Operations[2].Should().NotBe(editor.Operations[1], "a storage failure is final, so the next save is a new intent");
    }

    [Fact]
    [Trait("Req", "T-05")]
    public void A_successful_save_after_an_unknown_outcome_clears_the_retry_operation()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context, isNew: true);
        editor.Results.Enqueue(new CommitOutcomeUnknownError(OperationId.New()));
        h.Await(editor.SaveAsync()).Should().BeFalse();
        h.Await(editor.SaveAsync()).Should().BeTrue();

        h.Await(editor.SaveAsync()).Should().BeTrue();

        editor.Operations.Should().HaveCount(3);
        editor.Operations[1].Should().Be(editor.Operations[0]);
        editor.Operations[2].Should().NotBe(editor.Operations[1], "the committed operation is done; the next save is a new intent");
    }

    [Fact]
    [Trait("Req", "T-05")]
    public void The_retry_operation_survives_attempts_that_send_nothing()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        editor.Results.Enqueue(new CommitOutcomeUnknownError(OperationId.New()));
        h.Await(editor.SaveAsync()).Should().BeFalse();

        h.Workspace.Context.IsEditingBlocked = true;
        h.Await(editor.SaveAsync()).Should().BeFalse();
        h.Workspace.Context.IsEditingBlocked = false;
        editor.IsPrepared = false;
        h.Await(editor.SaveAsync()).Should().BeFalse();
        editor.IsPrepared = true;
        h.Await(editor.SaveAsync()).Should().BeTrue();

        editor.Operations.Should().HaveCount(2, "the blocked and the refused attempts send no command");
        editor.Operations[1].Should().Be(editor.Operations[0], "T-05: the retry still reuses the unknown outcome's operation");
    }

    [Fact]
    public void Starting_the_command_closes_an_open_dirty_prompt_but_a_refused_draft_leaves_it_open()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true, IsPrepared = false };
        editor.TryClose().Should().BeFalse();
        editor.IsDirtyClosePromptOpen.Should().BeTrue();

        h.Await(editor.SaveAsync()).Should().BeFalse();
        editor.IsDirtyClosePromptOpen.Should().BeTrue("the prompt closes only when the command starts");

        editor.IsPrepared = true;
        var hold = new TaskCompletionSource<AppError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.Pending = hold.Task;
        var save = editor.SaveAsync();
        h.PumpUntil(() => editor.Operations.Count == 1);
        editor.IsDirtyClosePromptOpen.Should().BeFalse("a save that is not answering the prompt still closes it");

        hold.SetResult(new StorageFailureError(StorageFailureKind.DiskFull));
        h.Await(save).Should().BeFalse();
        editor.IsDirtyClosePromptOpen.Should().BeFalse();
    }

    [Fact]
    public void Following_committed_changes_twice_is_refused()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context);

        editor.Invoking(e => e.ObserveAgain()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Saving_a_clean_existing_record_closes_without_a_command_but_a_new_one_is_sent()
    {
        using var h = WorkspaceHarness.Create();
        using var clean = new ProbeEditor(h.Workspace.Context);
        var closed = 0;
        clean.CloseRequested += (_, _) => closed++;

        h.Await(clean.SaveAsync()).Should().BeTrue();

        clean.Operations.Should().BeEmpty();
        closed.Should().Be(1);
        clean.TryClose().Should().BeTrue("an approved editor closes without asking");

        using var created = new ProbeEditor(h.Workspace.Context, isNew: true);
        h.Await(created.SaveAsync()).Should().BeTrue();
        created.Operations.Should().ContainSingle();
        created.Saved.Should().Be(1);
    }

    [Fact]
    public void A_successful_save_runs_the_pipeline_in_order_and_closes()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;
        var saving = new List<bool>();
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(editor.IsSaving))
            {
                saving.Add(editor.IsSaving);
            }
        };

        h.Await(editor.SaveAsync()).Should().BeTrue();

        editor.Calls.Should().Equal("CommitPendingInput", "ClearFieldErrors", "TryPrepareSave", "Execute", "OnSaved");
        saving.Should().Equal(true, false);
        closed.Should().Be(1);
        editor.IsSaving.Should().BeFalse();
    }

    [Fact]
    public void Saving_while_editing_is_blocked_shows_why_and_sends_nothing()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        h.Workspace.Context.IsEditingBlocked = true;

        h.Await(editor.SaveAsync()).Should().BeFalse();

        editor.SaveError.Should().Be("Error_EditingBlocked");
        editor.HasSaveError.Should().BeTrue();
        editor.Operations.Should().BeEmpty();
        editor.IsSaving.Should().BeFalse();
    }

    [Fact]
    public void A_draft_refused_before_the_command_keeps_the_editor_open()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true, IsPrepared = false };

        h.Await(editor.SaveAsync()).Should().BeFalse();

        editor.Operations.Should().BeEmpty();
        editor.IsSaving.Should().BeFalse();
        editor.TryClose().Should().BeFalse("the draft still has changes");
    }

    [Fact]
    [Trait("Req", "B-02.9")]
    public void A_conflict_keeps_the_draft_and_ending_it_clears_the_message()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        var announced = new List<Announcement>();
        h.Workspace.Context.Announced += (_, a) => announced.Add(a);
        editor.Results.Enqueue(new ConcurrencyConflictError(EntityKind.Task, Guid.NewGuid(), 1, 2));

        h.Await(editor.SaveAsync()).Should().BeFalse();

        editor.IsConflict.Should().BeTrue();
        editor.SaveError.Should().Be("Editor_ConflictMessage");
        editor.IsDirty.Should().BeTrue();
        announced.Should().ContainSingle().Which.Should().Be(new Announcement("This Record_Task changed elsewhere.", true));

        editor.Resolve();
        editor.IsConflict.Should().BeFalse();
        editor.SaveError.Should().BeNull();
        editor.HasSaveError.Should().BeFalse();
    }

    [Fact]
    public void Validation_errors_go_to_the_editor_and_another_failure_shows_its_text()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        string? focused = null;
        editor.FocusRequested += (_, key) => focused = key;
        editor.Results.Enqueue(new ValidationFailedError([new DomainError(DomainErrorCodes.Required, "Title")]));
        editor.Results.Enqueue(new StorageFailureError(StorageFailureKind.DiskFull));

        h.Await(editor.SaveAsync()).Should().BeFalse();
        editor.ShownErrors.Should().ContainSingle().Which.Field.Should().Be("Title");
        editor.ValidationSummary.Should().Be("TaskEditor_FixField");
        editor.HasValidationSummary.Should().BeTrue();
        focused.Should().Be("Title");

        h.Await(editor.SaveAsync()).Should().BeFalse();
        editor.ValidationSummary.Should().BeNull("every save starts without the previous errors");
        editor.SaveError.Should().Be("Not saved. The drive is full.");
        editor.IsConflict.Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "B-02.6")]
    public void A_dirty_draft_asks_before_closing_and_continue_editing_drops_the_follow_up()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;

        editor.FollowUpWith(ProbeFollowUp.Open);
        editor.FollowUp.Should().Be(ProbeFollowUp.Open);
        editor.IsDirtyClosePromptOpen.Should().BeTrue();
        closed.Should().Be(0);

        editor.ContinueEditing();
        editor.IsDirtyClosePromptOpen.Should().BeFalse();
        editor.FollowUp.Should().Be(ProbeFollowUp.None);

        editor.TryClose().Should().BeFalse();
        editor.DiscardChanges();
        closed.Should().Be(1);
        editor.IsDirtyClosePromptOpen.Should().BeFalse();
        editor.Operations.Should().BeEmpty();
    }

    [Fact]
    public void A_clean_draft_closes_for_a_follow_up_at_once()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context);
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;

        editor.FollowUpWith(ProbeFollowUp.Open);

        closed.Should().Be(1);
        editor.FollowUp.Should().Be(ProbeFollowUp.Open);
    }

    [Fact]
    public void While_saving_the_editor_neither_closes_nor_follows_up()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        var hold = new TaskCompletionSource<AppError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.Pending = hold.Task;

        var save = editor.SaveAsync();
        h.PumpUntil(() => editor.Operations.Count == 1);
        editor.IsSaving.Should().BeTrue();
        editor.TryClose().Should().BeFalse();
        editor.IsDirtyClosePromptOpen.Should().BeFalse("nothing asks while the command runs");
        editor.FollowUpWith(ProbeFollowUp.Open);
        editor.FollowUp.Should().Be(ProbeFollowUp.None);
        h.Await(editor.SaveAsync()).Should().BeFalse("a second save while saving is ignored");

        hold.SetResult(null);
        h.Await(save).Should().BeTrue();
        editor.Operations.Should().ContainSingle();
    }

    [Fact]
    public void The_save_command_saves_and_closes_like_save_and_never_disables_itself()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;
        var hold = new TaskCompletionSource<AppError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.Pending = hold.Task;

        editor.SaveCommand.CanExecute(null).Should().BeTrue();
        var save = editor.SaveCommand.ExecuteAsync(null);
        h.PumpUntil(() => editor.Operations.Count == 1);
        editor.SaveCommand.CanExecute(null).Should().BeTrue("the command allows concurrent runs, so its button stays as it was");
        h.Await(editor.SaveCommand.ExecuteAsync(null));
        editor.Operations.Should().ContainSingle("SaveAsync ignores a second save while saving");

        hold.SetResult(null);
        h.Await(save);
        editor.Calls.Should().Equal("CommitPendingInput", "ClearFieldErrors", "TryPrepareSave", "Execute", "OnSaved");
        closed.Should().Be(1);
    }

    [Fact]
    public void The_save_command_on_a_failed_save_keeps_the_editor_open()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;
        editor.Results.Enqueue(new StorageFailureError(StorageFailureKind.DiskFull));

        h.Await(editor.SaveCommand.ExecuteAsync(null));

        closed.Should().Be(0);
        editor.SaveError.Should().Be("Not saved. The drive is full.");
        editor.TryClose().Should().BeFalse("the draft still has changes");
    }

    [Fact]
    [Trait("Req", "T-05")]
    public void A_save_that_throws_ends_saving_keeps_the_draft_and_retries_under_the_same_operation()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        var announced = new List<Announcement>();
        h.Workspace.Context.Announced += (_, a) => announced.Add(a);
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;
        editor.Pending = Task.FromException<AppError?>(new InvalidOperationException("defect"));

        h.Await(editor.SaveCommand.ExecuteAsync(null));

        editor.IsSaving.Should().BeFalse();
        editor.SaveError.Should().Be("Error_Unexpected");
        editor.IsDirty.Should().BeTrue("the draft stays");
        closed.Should().Be(0);
        announced.Should().Equal(new Announcement("Error_Unexpected", true));
        h.Workspace.Context.SaveStatus.IsSaving.Should().BeFalse("the indicator leaves its busy state");
        h.Workspace.Context.SaveStatus.IsFailureNoticeOpen.Should().BeTrue();

        editor.Pending = null;
        h.Await(editor.SaveAsync()).Should().BeTrue();
        editor.Operations.Should().HaveCount(2);
        editor.Operations[1].Should().Be(editor.Operations[0], "a thrown save has an unknown outcome, so the retry reuses its operation");
        closed.Should().Be(1);
    }

    [Fact]
    public void An_editor_whose_save_threw_can_still_close()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        editor.Pending = Task.FromException<AppError?>(new InvalidOperationException("defect"));
        h.Await(editor.SaveAsync()).Should().BeFalse();

        editor.TryClose().Should().BeFalse("the dirty draft asks first");
        editor.IsDirtyClosePromptOpen.Should().BeTrue();
        editor.DiscardChanges();
        editor.TryClose().Should().BeTrue();
    }

    [Fact]
    [Trait("Req", "B-02.8")]
    public void A_window_close_on_a_dirty_draft_stays_pending_until_the_prompt_is_answered()
    {
        using var h = WorkspaceHarness.Create();
        using var editor = new ProbeEditor(h.Workspace.Context) { HasChanges = true };
        editor.Results.Enqueue(new StorageFailureError(StorageFailureKind.DiskFull));

        editor.TryCloseForWindow().Should().BeFalse();
        editor.IsWindowClosePending.Should().BeTrue();
        editor.IsDirtyClosePromptOpen.Should().BeTrue();

        h.Await(editor.SaveFromPromptAsync()).Should().BeFalse();
        editor.IsWindowClosePending.Should().BeFalse("a failed save abandons the window close");
        editor.IsDirtyClosePromptOpen.Should().BeFalse();

        editor.TryCloseForWindow().Should().BeFalse();
        editor.ContinueEditing();
        editor.IsWindowClosePending.Should().BeFalse();

        editor.TryCloseForWindow().Should().BeFalse();
        h.Await(editor.SaveFromPromptAsync()).Should().BeTrue();
        editor.IsWindowClosePending.Should().BeTrue("the saved editor closes and the window may close too");
        editor.TryCloseForWindow().Should().BeTrue();
    }

    [Fact]
    public void Dirty_tracking_waits_while_loading_and_commits_reach_the_editor_until_it_is_disposed()
    {
        using var h = WorkspaceHarness.Create(async s => await s.TaskAsync("Task"));
        var editor = new ProbeEditor(h.Workspace.Context);

        editor.LoadChanged();
        editor.IsDirty.Should().BeFalse("dirty tracking waits while the editor loads");
        editor.HasChanges = true;
        editor.IsDirty.Should().BeTrue();

        h.External(u => u.CreateTask.ExecuteAsync(new Application.Tasks.CreateTaskCommand(OperationId.New(), new Domain.Identifiers.TaskId(Guid.NewGuid()),
            new Application.Tasks.TaskContentDraft("Other", "Service", null, null, [], null, Domain.Fields.CustomValue.NoValues)), TestContext.Current.CancellationToken));
        editor.Commits.Should().Be(1);

        editor.Dispose();
        h.External(u => u.CreateTask.ExecuteAsync(new Application.Tasks.CreateTaskCommand(OperationId.New(), new Domain.Identifiers.TaskId(Guid.NewGuid()),
            new Application.Tasks.TaskContentDraft("More", "Service", null, null, [], null, Domain.Fields.CustomValue.NoValues)), TestContext.Current.CancellationToken));
        editor.Commits.Should().Be(1);
    }

    private enum ProbeFollowUp
    {
        None,
        Open,
    }

    /// <summary>The smallest editor: one "changed" flag as its draft, a scripted command, and a record of each hook it ran.</summary>
    private sealed class ProbeEditor : EditorViewModelBase<ProbeFollowUp>
    {
        private bool _hasChanges;

        public ProbeEditor(WorkspaceContext context, bool isNew = false)
            : base(context, isNew, "Record_Task") => ObserveCommits();

        public Queue<AppError?> Results { get; } = new();

        public Task<AppError?>? Pending { get; set; }

        public List<OperationId> Operations { get; } = [];

        public List<string> Calls { get; } = [];

        public List<DomainError> ShownErrors { get; } = [];

        public int Saved { get; private set; }

        public int Commits { get; private set; }

        public bool IsPrepared { get; set; } = true;

        public bool HasChanges
        {
            get => _hasChanges;
            set
            {
                _hasChanges = value;
                RecomputeDirty();
            }
        }

        public override bool CanSave => !IsSaving;

        protected override bool NeedsClosePrompt => IsDirty;

        public void FollowUpWith(ProbeFollowUp followUp) => CloseForFollowUp(followUp);

        public void Resolve() => EndConflict();

        public void ObserveAgain() => ObserveCommits();

        public void LoadChanged()
        {
            IsLoading = true;
            HasChanges = true;
            IsLoading = false;
        }

        protected override async Task<(AppError? Error, long Sequence)> ExecuteSaveAsync(OperationId operation)
        {
            Calls.Add("Execute");
            Operations.Add(operation);
            var error = Pending is { } pending ? await pending.ConfigureAwait(true) : Results.Count > 0 ? Results.Dequeue() : null;
            Pending = null;
            return (error, 0);
        }

        protected override void CommitPendingInput() => Calls.Add(nameof(CommitPendingInput));

        protected override bool TryPrepareSave()
        {
            Calls.Add(nameof(TryPrepareSave));
            return IsPrepared;
        }

        protected override void OnSaved()
        {
            Calls.Add(nameof(OnSaved));
            Saved++;
        }

        protected override void ClearFieldErrors()
        {
            Calls.Add(nameof(ClearFieldErrors));
            ShownErrors.Clear();
        }

        protected override void ShowFieldErrors(IReadOnlyList<DomainError> errors)
        {
            ShownErrors.AddRange(errors);
            ShowValidationSummary(errors.Count, errors[0].Field);
        }

        protected override bool ComputeIsDirty() => _hasChanges;

        protected override void OnCommitted() => Commits++;
    }
}
