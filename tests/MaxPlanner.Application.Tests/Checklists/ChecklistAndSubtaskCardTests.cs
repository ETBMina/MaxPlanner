using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Checklists;

public sealed class ChecklistAndSubtaskCardTests
{
    private readonly TestWorkspace _workspace = new();

    [Fact]
    [Trait("Req", "B-02.7")]
    public async Task Blank_checklist_text_is_rejected_before_the_transaction()
    {
        var task = await _workspace.AddTaskAsync();

        var result = await AddItem(task, 1, new ChecklistItemId(Guid.NewGuid()), "   ");

        Failed<long, ValidationFailedError>(result).Errors.Should().Equal(new DomainError(DomainErrorCodes.Required, "Text"));
        _workspace.Coordinator.TransactionsStarted.Should().Be(1, "only the arranging task creation ran");
    }

    [Fact]
    [Trait("Req", "B-04")]
    [Trait("Req", "AC-06")]
    public async Task Toggling_an_item_changes_only_that_item_and_the_task_revision()
    {
        var task = await _workspace.AddTaskAsync(status: InProgress);
        var item = new ChecklistItemId(Guid.NewGuid());
        Succeeded(await AddItem(task, 1, item, "Haptics"));
        var before = _workspace.Task(task);

        var outcome = Succeeded(await Toggle(task, 2, item, true));

        var after = _workspace.Task(task);
        outcome.Value.Should().Be(3);
        after.ChecklistItems.Single().IsCompleted.Should().BeTrue();
        after.StatusId.Should().Be(before.StatusId);
        after.Dates.Should().Be(before.Dates);
        _workspace.Coordinator.Published[^1].UpsertedTasks.Should().ContainSingle();
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public async Task Toggling_from_a_stale_task_revision_is_a_conflict()
    {
        var task = await _workspace.AddTaskAsync();
        var item = new ChecklistItemId(Guid.NewGuid());
        Succeeded(await AddItem(task, 1, item, "Haptics"));

        Failed<long, ConcurrencyConflictError>(await Toggle(task, 1, item, true));
    }

    [Fact]
    [Trait("Req", "B-16.3")]
    [Trait("Req", "AC-32")]
    public async Task Popping_out_an_open_item_removes_it_and_creates_a_card_in_the_default_status_atomically()
    {
        var task = await _workspace.AddTaskAsync(status: Completed);
        var item = new ChecklistItemId(Guid.NewGuid());
        Succeeded(await AddItem(task, 1, item, "Fix freeze copy"));
        var parentBefore = _workspace.Task(task);
        var cardId = new SubtaskCardId(Guid.NewGuid());

        var outcome = Succeeded(await PopOut(task, 2, item, cardId));

        var card = _workspace.Card(cardId);
        outcome.Value.Should().Be(cardId);
        card.Subtitle.Value.Should().Be("Fix freeze copy");
        card.StatusId.Should().Be(Backlog.Id);
        card.ParentTaskId.Should().Be(task);
        card.Notes.Should().BeNull("the whole text fits in the subtitle");
        var parent = _workspace.Task(task);
        parent.ChecklistItems.Should().BeEmpty();
        parent.StatusId.Should().Be(parentBefore.StatusId);
        parent.Dates.Should().Be(parentBefore.Dates);
        var changes = _workspace.Coordinator.Published[^1];
        changes.UpsertedTasks.Should().ContainSingle().Which.Should().Be(parent);
        changes.UpsertedSubtaskCards.Should().ContainSingle().Which.Should().Be(card);
    }

    [Fact]
    [Trait("Req", "B-16.3")]
    [Trait("Req", "AC-32")]
    public async Task A_checked_item_becomes_a_card_in_the_first_completed_status_with_its_own_end_date()
    {
        var task = await _workspace.AddTaskAsync();
        var item = new ChecklistItemId(Guid.NewGuid());
        Succeeded(await AddItem(task, 1, item, "Done already"));
        Succeeded(await Toggle(task, 2, item, true));
        var cardId = new SubtaskCardId(Guid.NewGuid());

        Succeeded(await PopOut(task, 3, item, cardId));

        _workspace.Card(cardId).StatusId.Should().Be(Completed.Id);
        _workspace.Card(cardId).Dates.End.Should().Be(Today);
        _workspace.Task(task).Dates.Should().Be(ActualDates.None, "the parent's dates never change");
    }

    [Fact]
    [Trait("Req", "B-16.3")]
    [Trait("Req", "B-02.10")]
    public async Task A_long_or_multiline_item_pops_out_with_a_shortened_subtitle_and_its_full_text_in_the_notes()
    {
        var task = await _workspace.AddTaskAsync();
        var item = new ChecklistItemId(Guid.NewGuid());
        var text = new string('a', 240) + " " + new string('b', 20) + "\nSecond line";
        Succeeded(await AddItem(task, 1, item, text));
        var cardId = new SubtaskCardId(Guid.NewGuid());

        Succeeded(await PopOut(task, 2, item, cardId));

        _workspace.Card(cardId).Subtitle.Value.Should().Be(new string('a', 240) + "…");
        _workspace.Card(cardId).Notes!.Value.Should().Be(text);
        _workspace.Task(task).ChecklistItems.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "B-05")]
    public async Task A_popped_out_item_is_reported_as_deleted_to_a_stale_checklist_view()
    {
        var task = await _workspace.AddTaskAsync();
        var item = new ChecklistItemId(Guid.NewGuid());
        Succeeded(await AddItem(task, 1, item, "Pop me"));
        Succeeded(await PopOut(task, 2, item, new SubtaskCardId(Guid.NewGuid())));

        var result = await Toggle(task, 3, item, true);

        Failed<long, RecordNotFoundError>(result).Should().Be(new RecordNotFoundError(EntityKind.ChecklistItem, item.Value, WasDeleted: true));
    }

    [Fact]
    [Trait("Req", "B-16.4")]
    public async Task A_new_card_gets_its_own_creation_dates_and_appends_after_tasks_in_its_column()
    {
        var parent = await _workspace.AddTaskAsync(status: Completed);
        var columnTask = await _workspace.AddTaskAsync("In progress already", InProgress);
        var parentBefore = _workspace.Task(parent);

        var card = Succeeded(await CreateCard(parent, InProgress.Id)).Value;

        _workspace.Card(card).Dates.Start.Should().Be(Today);
        _workspace.Card(card).Rank.Should().BeGreaterThan(_workspace.Task(columnTask).Rank);
        _workspace.Task(parent).Should().Be(parentBefore);
    }

    [Fact]
    [Trait("Req", "B-16.6")]
    public async Task Cards_cannot_be_added_to_an_archived_or_missing_task()
    {
        var archived = await _workspace.AddTaskAsync();
        _workspace.Arrange(t => t.Archive(_workspace.Time.GetUtcNow()).Value, archived);

        Failed<SubtaskCardId, ValidationFailedError>(await CreateCard(archived)).Errors.Should().Equal(new DomainError(DomainErrorCodes.ArchivedReadOnly, "Parent"));
        Failed<SubtaskCardId, RecordNotFoundError>(await CreateCard(new TaskId(Guid.NewGuid()))).WasDeleted.Should().BeFalse();
        _workspace.Data.SubtaskCards.Should().BeEmpty();
    }

    private Task<AppResult<CommandOutcome<long>>> AddItem(TaskId task, long revision, ChecklistItemId item, string text) =>
        new AddChecklistItem(_workspace.Coordinator).ExecuteAsync(new AddChecklistItemCommand(OperationId.New(), task, revision, item, text), CancellationToken.None);

    private Task<AppResult<CommandOutcome<long>>> Toggle(TaskId task, long revision, ChecklistItemId item, bool done) =>
        new SetChecklistItemCompleted(_workspace.Coordinator).ExecuteAsync(
            new SetChecklistItemCompletedCommand(OperationId.New(), task, revision, item, done), CancellationToken.None);

    private Task<AppResult<CommandOutcome<SubtaskCardId>>> PopOut(TaskId task, long revision, ChecklistItemId item, SubtaskCardId card) =>
        new PopOutChecklistItem(_workspace.Coordinator).ExecuteAsync(new PopOutChecklistItemCommand(OperationId.New(), task, revision, item, card), CancellationToken.None);

    private Task<AppResult<CommandOutcome<SubtaskCardId>>> CreateCard(TaskId parent, TaskStatusId? status = null) =>
        new CreateSubtaskCard(_workspace.Coordinator).ExecuteAsync(
            new CreateSubtaskCardCommand(OperationId.New(), parent, new SubtaskCardId(Guid.NewGuid()), "Follow-up", StatusId: status), CancellationToken.None);
}
