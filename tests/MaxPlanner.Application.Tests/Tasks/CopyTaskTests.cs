using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Tasks;

[Trait("Req", "B-06")]
public sealed class CopyTaskTests
{
    private readonly TestWorkspace _workspace = new();

    [Fact]
    [Trait("Req", "AC-09")]
    public async Task Copying_an_archived_linked_task_creates_a_new_active_unlinked_task_with_only_the_chosen_content()
    {
        var cr = await _workspace.AddCrAsync(title: "Instant card freeze");
        var source = await _workspace.AddLinkedTaskAsync(cr, status: Review);
        var item = new ChecklistItemId(Guid.NewGuid());
        Succeeded(await new UpdateTask(_workspace.Coordinator).ExecuteAsync(
            new UpdateTaskCommand(OperationId.New(), source, 1, Draft(null, "Mobile Banking", "Notes") with { TagIds = [Mobile.Id], DesignerIds = [Bob.Id] },
                Review.Id, High.Id, DateIntent.Set(Today.AddDays(-2)), DateIntent.Unchanged, [new(item, "Step", true)]),
            TestContext.Current.CancellationToken));
        Succeeded(await new CreateSubtaskCard(_workspace.Coordinator).ExecuteAsync(
            new CreateSubtaskCardCommand(OperationId.New(), source, new SubtaskCardId(Guid.NewGuid()), "Card"), TestContext.Current.CancellationToken));
        Succeeded(await new ArchiveTask(_workspace.Coordinator).ExecuteAsync(new ArchiveTaskCommand(OperationId.New(), source, 2), TestContext.Current.CancellationToken));
        var copyId = new TaskId(Guid.NewGuid());

        Succeeded(await Copy(source, copyId, "Instant card freeze", options: new CopyOptions { CopyTags = true, CopyChecklistItems = true }));

        var copy = _workspace.Task(copyId);
        copy.Revision.Should().Be(1);
        copy.IsArchived.Should().BeFalse();
        copy.ChangeRequestId.Should().BeNull("AC-09: copies start unlinked");
        copy.OwnTitle!.Value.Should().Be("Instant card freeze", "the own title is prefilled from the source's effective title");
        copy.Service.Should().BeNull("B-06: Service starts blank");
        copy.StatusId.Should().Be(Backlog.Id, "B-06: the configured default status unless Keep current status");
        copy.PriorityId.Should().Be(High.Id, "the source priority still exists");
        copy.TagIds.Should().Equal(Mobile.Id);
        copy.DesignerIds.Should().BeEmpty("people were not selected");
        copy.Notes.Should().BeNull();
        copy.Dates.Start.Should().BeNull("dates were not copied");
        copy.ChecklistItems.Should().ContainSingle().Which.Should().Match<Domain.Tasks.ChecklistItem>(i => i.Id != item && !i.IsCompleted && i.Text.Value == "Step");
        _workspace.Data.SubtaskCards.Values.Should().OnlyContain(c => c.ParentTaskId == source, "Subtask cards are not copied");
        _workspace.Task(source).IsArchived.Should().BeTrue("the source is unchanged");
        _workspace.Data.AssertIntegrity();
    }

    [Fact]
    public async Task Keep_current_status_and_preserve_completion_carry_the_source_state()
    {
        var source = await _workspace.AddTaskAsync("Source", InProgress);
        Succeeded(await new UpdateTask(_workspace.Coordinator).ExecuteAsync(
            new UpdateTaskCommand(OperationId.New(), source, 1, Draft("Source"), InProgress.Id, Medium.Id, DateIntent.Unchanged, DateIntent.Unchanged,
                [new(new ChecklistItemId(Guid.NewGuid()), "Done step", true)]), TestContext.Current.CancellationToken));
        var copyId = new TaskId(Guid.NewGuid());

        Succeeded(await Copy(source, copyId, "Copy", options: new CopyOptions { CopyStatus = true, CopyChecklistItems = true, PreserveChecklistCompletion = true, CopyDates = true }));

        var copy = _workspace.Task(copyId);
        copy.StatusId.Should().Be(InProgress.Id);
        copy.Dates.Start.Should().Be(Today, "the copied start date is kept");
        copy.ChecklistItems.Single().IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task An_explicit_status_from_the_dialog_applies_creation_dates_for_that_status()
    {
        var source = await _workspace.AddTaskAsync("Source");
        var copyId = new TaskId(Guid.NewGuid());

        Succeeded(await Copy(source, copyId, "Copy", status: Completed.Id));

        _workspace.Task(copyId).Dates.End.Should().Be(Today, "B-04: creation in a completed status fills EndDate");
    }

    [Fact]
    [Trait("Req", "B-02.4")]
    public async Task Linking_a_copy_needs_a_service_and_links_permanently()
    {
        var cr = await _workspace.AddCrAsync();
        var source = await _workspace.AddTaskAsync("Source");

        var withoutService = await Copy(source, new TaskId(Guid.NewGuid()), "Copy", linkTo: new CrLinkTarget(cr, 1));
        Failed<TaskId, ValidationFailedError>(withoutService).Errors.Single().Code.Should().Be(DomainErrorCodes.ServiceRequiredWhenLinked);

        var copyId = new TaskId(Guid.NewGuid());
        Succeeded(await Copy(source, copyId, "Ignored title", service: "Cards API", linkTo: new CrLinkTarget(cr, 1)));

        var copy = _workspace.Task(copyId);
        copy.ChangeRequestId.Should().Be(cr);
        copy.OwnTitle.Should().BeNull("a linked task shows its CR's title");
        _workspace.Data.Links.Should().ContainKey(copyId);
    }

    [Fact]
    [Trait("Req", "B-02.2")]
    public async Task An_unlinked_copy_needs_a_title()
    {
        var source = await _workspace.AddTaskAsync("Source");

        var result = await Copy(source, new TaskId(Guid.NewGuid()), "   ");

        Failed<TaskId, ValidationFailedError>(result).Errors.Single().Should().Be(new DomainError(DomainErrorCodes.Required, "Title"));
    }

    private Task<AppResult<CommandOutcome<TaskId>>> Copy(
        TaskId source,
        TaskId copy,
        string? title,
        string? service = null,
        CopyOptions? options = null,
        TaskStatusId? status = null,
        CrLinkTarget? linkTo = null) =>
        new CopyTask(_workspace.Coordinator).ExecuteAsync(
            new CopyTaskCommand(OperationId.New(), source, copy, title, service, options ?? new CopyOptions(), status, null, linkTo),
            CancellationToken.None);
}
