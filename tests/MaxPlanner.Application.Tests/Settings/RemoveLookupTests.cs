using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Settings;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Workflow;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Settings;

[Trait("Req", "B-04")]
public sealed class RemoveLookupTests
{
    private readonly TestWorkspace _workspace = new();

    [Fact]
    public async Task An_unused_task_status_is_removed_without_a_replacement()
    {
        Succeeded(await Remove(LookupKind.TaskStatus, Blocked.Id.Value, replacement: null, LookupUsageCounts.None));

        _workspace.Data.Workflow.TaskStatuses.Should().NotContain(s => s.Id == Blocked.Id);
    }

    [Fact]
    public async Task An_unused_cr_status_is_removed_without_a_replacement()
    {
        await _workspace.AddCrAsync();

        Succeeded(await Remove(LookupKind.CrStatus, CrCompleted.Id.Value, replacement: null, LookupUsageCounts.None));

        _workspace.Data.Workflow.CrStatuses.Should().NotContain(s => s.Id == CrCompleted.Id);
    }

    [Fact]
    public async Task A_used_task_status_without_a_replacement_is_refused_and_kept()
    {
        await _workspace.AddTaskAsync(status: InProgress);
        var usage = new LookupUsageCounts(1, 0, 0, 0, 0, WorkspaceDefaultRoles.None);

        var error = Failed<long, ValidationFailedError>(await Remove(LookupKind.TaskStatus, InProgress.Id.Value, replacement: null, usage));

        error.Errors.Should().ContainSingle(e => e.Code == DomainErrorCodes.ReplacementRequired);
        _workspace.Data.Workflow.TaskStatuses.Should().Contain(s => s.Id == InProgress.Id);
    }

    private Task<AppResult<CommandOutcome<long>>> Remove(LookupKind kind, Guid id, Guid? replacement, LookupUsageCounts usage) =>
        new RemoveLookup(_workspace.Coordinator).ExecuteAsync(new RemoveLookupCommand(OperationId.New(), kind, id, replacement, usage), CancellationToken.None);
}
