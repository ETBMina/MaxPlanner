using AwesomeAssertions;
using MaxPlanner.Application.Settings;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Settings;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Settings;

/// <summary>Design §5.9: the removal dialog ends every save, so it never stays on "Saving…".</summary>
[Trait("Req", "B-04")]
public sealed class LookupRemovalViewModelTests
{
    [Fact]
    public void An_unexpected_exception_ends_the_save_and_keeps_the_dialog_open()
    {
        using var slice = WorkspaceHarness.Create();
        var context = slice.Workspace.Context;
        var workflow = slice.Await(slice.Dataset.Snapshots.LoadAsync(CancellationToken.None)).Value.Workflow;
        var unused = workflow.TaskStatuses.First(s => s.Id != workflow.Defaults.DefaultTaskStatusId);
        var vm = new LookupRemovalViewModel(context, SettingsUseCases.Create(slice.Coordinator, slice.Dataset.Snapshots), workflow, LookupKind.TaskStatus, unused.Id.Value, unused.Name.Display);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        slice.Await(vm.LoadAsync());
        vm.IsUnused.Should().BeTrue();

        slice.Coordinator.ThrowNext(new InvalidOperationException("defect"));
        var removed = slice.Await(vm.RemoveAsync());

        removed.Should().BeFalse();
        vm.IsSaving.Should().BeFalse();
        vm.CanRemove.Should().BeTrue();
        vm.Error.Should().Be(slice.Strings.GetString("Error_Unexpected"));
        context.SaveStatus.IsSaving.Should().BeFalse();
        closed.Should().BeFalse();
    }
}
