using AwesomeAssertions;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Domain.Tests.Fixtures;

namespace MaxPlanner.Domain.Tests.Tasks;

[Trait("Req", "B-16")]
[Trait("Req", "AC-32")]
public sealed class SubtaskCardTests
{
    [Fact]
    public void A_card_under_a_completed_task_gets_its_own_dates_and_leaves_the_parent_untouched()
    {
        var parent = UnlinkedTask(Completed);
        var card = SubtaskCard.Create(new SubtaskCardId(Guid.NewGuid()), parent.Id, SubtitleText.Create("Fix freeze copy").Value, null,
            Backlog, DateIntent.Unchanged, DateIntent.Unchanged, 1_024, Today, Now).Value;

        var started = card.MoveTo(Backlog, InProgress, 1_024, DateIntent.Unchanged, DateIntent.Unchanged, Today.AddDays(1), Now).Value;
        var done = started.MoveTo(InProgress, Completed, 1_024, DateIntent.Unchanged, DateIntent.Unchanged, Today.AddDays(2), Now).Value;

        done.Dates.Start.Should().Be(Today.AddDays(1));
        done.Dates.End.Should().Be(Today.AddDays(2));
        parent.StatusId.Should().Be(Completed.Id);
        parent.Dates.End.Should().Be(Today);
    }

    [Fact]
    public void A_card_requires_a_subtitle()
    {
        SubtitleText.Create("  ").IsSuccess.Should().BeFalse();
    }
}
