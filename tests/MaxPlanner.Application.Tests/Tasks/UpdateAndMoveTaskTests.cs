using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Tasks;

public sealed class UpdateAndMoveTaskTests
{
    private readonly TestWorkspace _workspace = new();

    [Fact]
    [Trait("Req", "B-02.8")]
    public async Task Saving_content_commits_the_whole_draft_as_the_next_revision()
    {
        var id = await _workspace.AddTaskAsync();
        var draft = Draft("Card freeze v2", "Cards API", "Line 1\nLine 2") with { DesignerIds = [Alice.Id, Alice.Id], TagIds = [Mobile.Id] };

        var outcome = Succeeded(await Update(id, 1, draft));

        var task = _workspace.Task(id);
        outcome.Value.Should().Be(2);
        task.OwnTitle!.Value.Should().Be("Card freeze v2");
        task.Notes!.Value.Should().Be("Line 1\nLine 2");
        task.DesignerIds.Should().Equal(Alice.Id);
        _workspace.Coordinator.Published[^1].UpsertedTasks.Should().ContainSingle().Which.Should().Be(task);
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public async Task A_stale_revision_is_a_conflict_and_keeps_the_committed_task()
    {
        var id = await _workspace.AddTaskAsync();
        Succeeded(await Update(id, 1, Draft("Newer")));

        var stale = await Update(id, 1, Draft("Older editor"));

        Failed<long, ConcurrencyConflictError>(stale).Should().Be(new ConcurrencyConflictError(EntityKind.Task, id.Value, 1, 2));
        _workspace.Task(id).OwnTitle!.Value.Should().Be("Newer");
    }

    [Fact]
    [Trait("Req", "AC-28")]
    public async Task A_linked_task_refuses_an_own_title_together_with_other_errors()
    {
        var task = await _workspace.AddLinkedTaskAsync(await _workspace.AddCrAsync());

        var result = await Update(task, 1, Draft("Override", "Cards API") with { TagIds = [new TagId(Guid.NewGuid())] });

        Failed<long, ValidationFailedError>(result).Errors.Should().Equal(
            new DomainError(DomainErrorCodes.TitleNotAllowedWhenLinked, "Title"),
            new DomainError(DomainErrorCodes.UnknownReference, "Tags"));
    }

    [Fact]
    [Trait("Req", "B-05")]
    public async Task An_archived_task_is_read_only()
    {
        var id = await _workspace.AddTaskAsync();
        _workspace.Arrange(t => t.Archive(_workspace.Time.GetUtcNow()).Value, id);

        Failed<long, ValidationFailedError>(await Update(id, 2, Draft("Edit"))).Errors[0].Code.Should().Be(DomainErrorCodes.ArchivedReadOnly);
    }

    [Fact]
    [Trait("Req", "B-11")]
    public async Task Values_of_a_removed_custom_field_survive_an_edit_that_does_not_send_them()
    {
        var legacy = FieldDefinition.Create(new FieldId(Guid.NewGuid()), FieldKey.Create("legacy").Value, FieldTarget.Task,
            LookupName.Create("Legacy").Value, FieldKind.Text, false, null, 0, []).Value;
        _workspace.Data.Workflow = SeedWorkflow with { Fields = [legacy] };
        var id = new TaskId(Guid.NewGuid());
        var withValue = Draft() with { CustomValues = new Dictionary<FieldId, CustomValue> { [legacy.Id] = new TextCustomValue("kept") } };
        Succeeded(await new CreateTask(_workspace.Coordinator).ExecuteAsync(new CreateTaskCommand(OperationId.New(), id, withValue), CancellationToken.None));
        _workspace.Data.Workflow = SeedWorkflow with { Fields = [legacy.Deactivate()] };

        Succeeded(await Update(id, 1, Draft("Edited without custom values")));

        _workspace.Task(id).CustomValues.Should().ContainKey(legacy.Id).WhoseValue.Should().Be(new TextCustomValue("kept"));
    }

    [Fact]
    [Trait("Req", "B-04")]
    [Trait("Req", "AC-05")]
    public async Task Moving_to_another_column_changes_status_dates_and_rank_together()
    {
        var waiting = await _workspace.AddTaskAsync("Already there", InProgress);
        var id = await _workspace.AddTaskAsync();

        Succeeded(await Move(id, 1, InProgress, BoardPlacement.AppendToEnd));

        var task = _workspace.Task(id);
        task.StatusId.Should().Be(InProgress.Id);
        task.Dates.Start.Should().Be(Today);
        task.Rank.Should().Be(_workspace.Task(waiting).Rank + RankPolicy.Step);
    }

    [Fact]
    [Trait("Req", "B-07")]
    public async Task Dropping_between_neighbours_with_room_takes_the_midpoint_and_touches_nobody_else()
    {
        var a = await _workspace.AddTaskAsync("A", Review);
        var b = await _workspace.AddTaskAsync("B", Review);
        var moved = await _workspace.AddTaskAsync("Moved");

        Succeeded(await Move(moved, 1, Review, BoardPlacement.Between(_workspace.Task(a).Rank, _workspace.Task(b).Rank)));

        _workspace.Task(moved).Rank.Should().Be(1_536);
        _workspace.Coordinator.Published[^1].UpsertedTasks.Select(t => t.Id).Should().Equal(moved);
    }

    [Fact]
    [Trait("Req", "B-07")]
    public async Task Without_room_the_full_column_is_renumbered_in_the_same_commit_writing_only_changed_cards()
    {
        var a = await _workspace.AddTaskAsync("A", Review);
        var b = await _workspace.AddTaskAsync("B", Review);
        var c = await _workspace.AddTaskAsync("C", Review);
        var card = Succeeded(await new CreateSubtaskCard(_workspace.Coordinator).ExecuteAsync(
            new CreateSubtaskCardCommand(OperationId.New(), a, new SubtaskCardId(Guid.NewGuid()), "Hidden by a filter", StatusId: Review.Id),
            CancellationToken.None)).Value;
        var moved = await _workspace.AddTaskAsync("Moved");
        var now = _workspace.Time.GetUtcNow();
        _workspace.Arrange(t => t.MoveTo(Review, Review, 1_025, DateIntent.Unchanged, DateIntent.Unchanged, Today, now).Value, b);
        _workspace.Arrange(t => t.MoveTo(Review, Review, 4_096, DateIntent.Unchanged, DateIntent.Unchanged, Today, now).Value, c);
        _workspace.Data.SubtaskCards[card] = _workspace.Card(card).MoveTo(Review, Review, 5_120, DateIntent.Unchanged, DateIntent.Unchanged, Today, now).Value;
        var revisionOfA = _workspace.Task(a).Revision;

        Succeeded(await Move(moved, 1, Review, BoardPlacement.Between(1_024, 1_025)));

        // A, Moved, B, C, card → 1024, 2048, 3072, 4096, 5120: only Moved and B change.
        _workspace.Task(moved).Rank.Should().Be(2_048);
        _workspace.Task(b).Rank.Should().Be(3_072);
        _workspace.Task(a).Revision.Should().Be(revisionOfA);
        _workspace.Task(c).Rank.Should().Be(4_096);
        _workspace.Card(card).Rank.Should().Be(5_120);
        var changes = _workspace.Coordinator.Published[^1];
        changes.UpsertedTasks.Select(t => t.Id).Should().BeEquivalentTo([moved, b]);
        changes.UpsertedSubtaskCards.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public async Task A_move_from_a_stale_card_is_a_conflict()
    {
        var id = await _workspace.AddTaskAsync();
        Succeeded(await Move(id, 1, Review, BoardPlacement.AppendToEnd));

        Failed<long, ConcurrencyConflictError>(await Move(id, 1, Completed, BoardPlacement.AppendToEnd));
        _workspace.Task(id).StatusId.Should().Be(Review.Id);
    }

    private Task<AppResult<CommandOutcome<long>>> Update(TaskId id, long revision, TaskContentDraft draft) =>
        new UpdateTaskContent(_workspace.Coordinator).ExecuteAsync(new UpdateTaskContentCommand(OperationId.New(), id, revision, draft), CancellationToken.None);

    private Task<AppResult<CommandOutcome<long>>> Move(TaskId id, long revision, TaskStatusDefinition target, BoardPlacement placement) =>
        new MoveTask(_workspace.Coordinator).ExecuteAsync(new MoveTaskCommand(OperationId.New(), id, revision, target.Id, placement), CancellationToken.None);
}
