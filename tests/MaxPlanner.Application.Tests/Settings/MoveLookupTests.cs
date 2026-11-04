using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Settings;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Workflow;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Settings;

[Trait("Req", "B-04")]
public sealed class MoveLookupTests
{
    private readonly TestWorkspace _workspace = new();

    [Fact]
    public async Task Move_down_by_one_swaps_with_the_next_status()
    {
        Succeeded(await Move(LookupKind.TaskStatus, Backlog.Id.Value, 1));

        Names().Take(3).Should().Equal("Blocked", "Backlog", "In Progress");
    }

    [Fact]
    public async Task A_drag_moves_a_status_several_places_and_shifts_the_ones_between()
    {
        Succeeded(await Move(LookupKind.TaskStatus, Completed.Id.Value, -4));

        Names().Should().Equal("Backlog", "Completed", "Blocked", "In Progress", "Review", "Unit Testing", "Designing", "Developing");
        _workspace.Data.Workflow.TaskStatuses.Select(s => s.Order).Should().Equal(0, 1, 2, 3, 4, 5, 6, 7);
    }

    [Fact]
    public async Task A_move_past_either_end_is_refused_and_the_order_is_kept()
    {
        var before = Names();

        Failed<long, ValidationFailedError>(await Move(LookupKind.TaskStatus, Blocked.Id.Value, -2));

        Names().Should().Equal(before);
    }

    [Fact]
    public async Task A_stale_order_stamp_is_refused()
    {
        var command = new MoveLookupCommand(OperationId.New(), LookupKind.TaskStatus, Backlog.Id.Value, "stale", 1);

        var result = await new MoveLookup(_workspace.Coordinator).ExecuteAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        Names().First().Should().Be("Backlog");
    }

    private string[] Names() => [.. _workspace.Data.Workflow.TaskStatuses.OrderBy(s => s.Order).Select(s => s.Name.Display)];

    private Task<AppResult<CommandOutcome<long>>> Move(LookupKind kind, Guid id, int offset) =>
        new MoveLookup(_workspace.Coordinator).ExecuteAsync(
            new MoveLookupCommand(OperationId.New(), kind, id, WorkflowStamps.Order(_workspace.Data.Workflow, kind), offset), CancellationToken.None);
}
