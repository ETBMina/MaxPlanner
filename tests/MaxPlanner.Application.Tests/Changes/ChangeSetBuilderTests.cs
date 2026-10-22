using AwesomeAssertions;
using MaxPlanner.Application.Changes;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Changes;

[Trait("Req", "T-07")]
public sealed class ChangeSetBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_record_appears_once_with_its_final_state()
    {
        var builder = new ChangeSetBuilder();
        var task = NewTask();
        var renamed = task.UpdateContent(task.Content() with { OwnTitle = TitleText.Create("Renamed").Value }, Now).Value;
        var deleted = NewTask();

        builder.UpsertTask(task);
        builder.UpsertTask(renamed);
        builder.UpsertTask(deleted);
        builder.DeleteTask(deleted.Id);
        var operation = OperationId.New();
        var set = builder.Build(7, operation, "test.op", Now);

        builder.IsEmpty.Should().BeFalse();
        set.Sequence.Should().Be(7);
        set.OperationId.Should().Be(operation);
        set.UpsertedTasks.Should().Equal(renamed);
        set.DeletedTasks.Should().Equal(deleted.Id);
        set.Workflow.Should().BeNull();
        set.RequiresFullReload.Should().BeFalse();
    }

    [Fact]
    public void A_new_builder_is_empty()
    {
        new ChangeSetBuilder().IsEmpty.Should().BeTrue();
    }

    private static TaskItem NewTask() =>
        TaskItem.Create(new TaskId(Guid.NewGuid()), new TaskContent(TitleText.Create("Task").Value, null, [], [], [], null, CustomValue.NoValues),
            Backlog, Medium.Id, DateIntent.Unchanged, DateIntent.Unchanged, null, 1, Today, Now).Value;
}

internal static class TaskItemTestExtensions
{
    public static TaskContent Content(this TaskItem task) =>
        new(task.OwnTitle, task.Service, task.DesignerIds, task.DeveloperIds, task.TagIds, task.Notes, task.CustomValues);
}
