using AwesomeAssertions;
using MaxPlanner.Application.Dashboard;
using MaxPlanner.Application.Settings;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Common.Filters;
using MaxPlanner.Presentation.Dashboard;
using MaxPlanner.Presentation.Settings;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// Commands that replaced click handlers on pages and dialogs (A-02): each runs the action the handler ran, and none
/// disables its control, because none has a CanExecute rule of its own.
/// </summary>
public sealed class BoundCommandTests
{
    [Fact]
    public void A_filter_chip_command_removes_its_filter()
    {
        var removed = 0;
        var chip = new FilterChipViewModel("Priority", "Priority: High", "Remove filter Priority: High", isDashboard: false, () => removed++);

        chip.RemoveCommand.CanExecute(null).Should().BeTrue();
        chip.RemoveCommand.Execute(null);

        removed.Should().Be(1);
    }

    [Fact]
    public void The_field_dialog_add_option_command_adds_an_empty_option()
    {
        using var h = WorkspaceHarness.Create();
        var workflow = h.Await(h.Dataset.Snapshots.LoadAsync(CancellationToken.None)).Value.Workflow;
        var vm = FieldDefinitionEditorViewModel.ForNew(h.Workspace.Context, SettingsUseCases.Create(h.Coordinator, h.Dataset.Snapshots), workflow, FieldTarget.Task);
        var before = vm.Options.Count;

        vm.AddOptionCommand.Execute(null);

        vm.Options.Should().HaveCount(before + 1);
        vm.Options[^1].Label.Should().BeEmpty();
    }

    [Fact]
    public void The_lookup_removal_command_removes_the_value_and_closes()
    {
        using var h = WorkspaceHarness.Create();
        var context = h.Workspace.Context;
        var workflow = h.Await(h.Dataset.Snapshots.LoadAsync(CancellationToken.None)).Value.Workflow;
        var unused = workflow.TaskStatuses.First(s => s.Id != workflow.Defaults.DefaultTaskStatusId);
        var vm = new LookupRemovalViewModel(context, SettingsUseCases.Create(h.Coordinator, h.Dataset.Snapshots), workflow, LookupKind.TaskStatus, unused.Id.Value, unused.Name.Display);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        h.Await(vm.LoadAsync());

        h.Await(vm.RemoveCommand.ExecuteAsync(null));

        closed.Should().BeTrue();
        vm.IsSaving.Should().BeFalse();
    }

    [Fact]
    public void A_dashboard_row_command_opens_its_record_and_never_disables_the_row()
    {
        using var h = WorkspaceHarness.Create(async s =>
        {
            await s.TaskAsync("Unlinked");
            await s.CrAsync("CR-1", "Late");
        });
        var opened = new List<string>();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new UnlinkedRowViewModel(h.Store.Tasks.Items.Single(), h.Strings, () =>
        {
            opened.Add("task");
            return hold.Task;
        });
        var cr = new OverdueRowViewModel(new OverdueCrRow(h.Store.ChangeRequests.Items.Single(), 3), h.Strings, () =>
        {
            opened.Add("cr");
            return Task.CompletedTask;
        });

        task.OpenCommand.Execute(null);
        task.OpenCommand.CanExecute(null).Should().BeTrue("the row stays enabled while the editor opens, as before");
        cr.OpenCommand.Execute(null);
        hold.SetResult();

        opened.Should().Equal("task", "cr");
    }
}