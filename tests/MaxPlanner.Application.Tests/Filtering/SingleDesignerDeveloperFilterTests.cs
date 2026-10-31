using AwesomeAssertions;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Filtering;

/// <summary>ADR 0024: Designer and Developer filters stay multi-select and match a task's single person.</summary>
[Trait("Req", "B-10")]
public sealed class SingleDesignerDeveloperFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly TaskFilterContext Context = new(_ => null, _ => null);

    [Fact]
    public void Several_selected_designers_match_a_task_whose_designer_is_any_of_them()
    {
        var task = Compose(designer: Alice.Id, developer: Bob.Id);

        Matches(new TaskFilterSpec { Designers = [Bob.Id, Alice.Id] }, task).Should().BeTrue();
        Matches(new TaskFilterSpec { Designers = [Bob.Id] }, task).Should().BeFalse();
        Matches(new TaskFilterSpec { Developers = [Bob.Id] }, task).Should().BeTrue();
        Matches(new TaskFilterSpec { Developers = [Alice.Id] }, task).Should().BeFalse();
    }

    [Fact]
    public void A_task_without_a_designer_fails_a_designer_filter_and_search_finds_the_single_names()
    {
        var task = Compose(designer: null, developer: Bob.Id);

        Matches(new TaskFilterSpec { Designers = [Alice.Id, Bob.Id] }, task).Should().BeFalse();
        Matches(new TaskFilterSpec { SearchText = "bo" }, task).Should().BeTrue();
        Matches(new TaskFilterSpec { SearchText = "alice" }, task).Should().BeFalse();
    }

    private static bool Matches(TaskFilterSpec spec, TaskReadModel task) => TaskFilterMatcher.Matches(spec, task, Context);

    private static TaskReadModel Compose(PersonId? designer, PersonId? developer)
    {
        var content = new TaskContent(TitleText.Create("Card freeze").Value, null, designer, developer, [], null, CustomValue.NoValues);
        var task = TaskItem.Create(new TaskId(Guid.NewGuid()), content, Backlog, Medium.Id, DateIntent.Unchanged, DateIntent.Unchanged, null, 1_024, Today, Now).Value;
        return new ReadModelComposer().ComposeTask(task, null, 0, SeedWorkflow);
    }
}
