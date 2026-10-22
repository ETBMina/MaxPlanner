using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.SubtaskCards;

public sealed class EditSubtaskCardTests
{
    private readonly TestWorkspace _workspace = new();

    [Fact]
    [Trait("Req", "B-16.2")]
    public async Task Editing_the_subtitle_changes_only_the_card()
    {
        var (parent, card) = await AddCardAsync(parentStatus: Completed);
        var parentBefore = _workspace.Task(parent);

        var outcome = Succeeded(await Edit(card, 1, "Fix rounding", "Seen in release 3"));

        outcome.Value.Should().Be(2);
        _workspace.Card(card).Subtitle.Value.Should().Be("Fix rounding");
        _workspace.Card(card).Notes!.Value.Should().Be("Seen in release 3");
        _workspace.Task(parent).Should().Be(parentBefore);
        _workspace.Coordinator.Published[^1].UpsertedSubtaskCards.Should().ContainSingle();
        _workspace.Coordinator.Published[^1].UpsertedTasks.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "B-02.9")]
    public async Task A_blank_subtitle_is_rejected_before_the_transaction()
    {
        var (_, card) = await AddCardAsync();
        var started = _workspace.Coordinator.TransactionsStarted;

        var result = await Edit(card, 1, "  ", null);

        Failed<long, ValidationFailedError>(result).Errors.Should().ContainSingle(e => e.Code == DomainErrorCodes.Required);
        _workspace.Coordinator.TransactionsStarted.Should().Be(started);
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public async Task An_edit_from_a_stale_revision_is_a_conflict_that_changes_nothing()
    {
        var (_, card) = await AddCardAsync();
        Succeeded(await Edit(card, 1, "First", null));

        var conflict = Failed<long, ConcurrencyConflictError>(await Edit(card, 1, "Second", null));

        (conflict.ExpectedRevision, conflict.ActualRevision).Should().Be((1, 2));
        _workspace.Card(card).Subtitle.Value.Should().Be("First");
    }

    [Fact]
    [Trait("Req", "B-16.6")]
    public async Task A_card_of_an_archived_parent_is_read_only()
    {
        var (parent, card) = await AddCardAsync();
        _workspace.Arrange(t => t.Archive(_workspace.Time.GetUtcNow()).Value, parent);

        var edit = Failed<long, ValidationFailedError>(await Edit(card, 1, "Changed", null));
        var move = Failed<long, ValidationFailedError>(await Move(card, 1, InProgress.Id));

        edit.Errors.Should().ContainSingle(e => e.Code == DomainErrorCodes.ArchivedReadOnly);
        move.Errors.Should().ContainSingle(e => e.Code == DomainErrorCodes.ArchivedReadOnly);
    }

    [Fact]
    [Trait("Req", "B-16.4")]
    [Trait("Req", "AC-05")]
    public async Task Moving_a_card_applies_date_rules_to_the_card_only_and_appends_to_the_column()
    {
        var (parent, card) = await AddCardAsync(parentStatus: Completed);
        var existing = await _workspace.AddTaskAsync("Already in progress", InProgress);
        var parentBefore = _workspace.Task(parent);

        var outcome = Succeeded(await Move(card, 1, InProgress.Id));

        var moved = _workspace.Card(card);
        outcome.Value.Should().Be(2);
        moved.StatusId.Should().Be(InProgress.Id);
        moved.Dates.Start.Should().Be(Today);
        moved.Rank.Should().BeGreaterThan(_workspace.Task(existing).Rank, "a status change appends to the full destination column (B-07)");
        _workspace.Task(parent).Should().Be(parentBefore);
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public async Task A_move_from_a_stale_revision_is_a_conflict()
    {
        var (_, card) = await AddCardAsync();
        Succeeded(await Edit(card, 1, "First", null));

        Failed<long, ConcurrencyConflictError>(await Move(card, 1, InProgress.Id));

        _workspace.Card(card).StatusId.Should().Be(Backlog.Id);
    }

    private async Task<(TaskId Parent, SubtaskCardId Card)> AddCardAsync(TaskStatusDefinition? parentStatus = null)
    {
        var parent = await _workspace.AddTaskAsync(status: parentStatus);
        var card = new SubtaskCardId(Guid.NewGuid());
        Succeeded(await new CreateSubtaskCard(_workspace.Coordinator).ExecuteAsync(
            new CreateSubtaskCardCommand(OperationId.New(), parent, card, "Copy fix"), CancellationToken.None));
        return (parent, card);
    }

    private Task<AppResult<CommandOutcome<long>>> Edit(SubtaskCardId card, long revision, string? subtitle, string? notes) =>
        new UpdateSubtaskCardContent(_workspace.Coordinator).ExecuteAsync(
            new UpdateSubtaskCardContentCommand(OperationId.New(), card, revision, subtitle, notes), CancellationToken.None);

    private Task<AppResult<CommandOutcome<long>>> Move(SubtaskCardId card, long revision, TaskStatusId target) =>
        new MoveSubtaskCard(_workspace.Coordinator).ExecuteAsync(
            new MoveSubtaskCardCommand(OperationId.New(), card, revision, target, BoardPlacement.AppendToEnd), CancellationToken.None);
}
