using AwesomeAssertions;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;
using static MaxPlanner.Domain.Tests.Fixtures;

namespace MaxPlanner.Domain.Tests.ChangeRequests;

public sealed class ChangeRequestAndReviewTests
{
    private static readonly CrStatusDefinition CrBacklog = new(new CrStatusId(Guid.NewGuid()), LookupName.Create("Backlog").Value, 0, false, true);
    private static readonly CrStatusDefinition CrInProgress = new(new CrStatusId(Guid.NewGuid()), LookupName.Create("In Progress").Value, 1, false, false);

    [Fact]
    [Trait("Req", "B-08.5")]
    public void Scheduling_sets_a_one_day_range_and_the_scheduling_status_together()
    {
        var cr = NewCr().ScheduleOn(Today, CrInProgress, Now);

        cr.Planned!.DurationDays.Should().Be(1);
        cr.Planned.Start.Should().Be(Today);
        cr.StatusId.Should().Be(CrInProgress.Id);
    }

    [Fact]
    [Trait("Req", "B-08.8")]
    [Trait("Req", "AC-11")]
    public void Dragging_an_edge_moves_only_that_endpoint_and_rejects_inverted_ranges()
    {
        var cr = NewCr().ScheduleOn(Today, CrInProgress, Now);

        var stretched = cr.SetPlannedEndpoint(PlannedEndpoint.End, Today.AddDays(9), Now).Value;
        var inverted = stretched.SetPlannedEndpoint(PlannedEndpoint.Start, Today.AddDays(10), Now);

        stretched.Planned!.Start.Should().Be(Today);
        stretched.Planned.DurationDays.Should().Be(10);
        inverted.Error!.Code.Should().Be(DomainErrorCodes.DateRangeInverted);
    }

    [Fact]
    [Trait("Req", "B-08.11")]
    public void Moving_to_backlog_clears_both_planned_dates_explicitly()
    {
        var cr = NewCr().ScheduleOn(Today, CrInProgress, Now).MoveToBacklog(CrBacklog, Now);

        cr.Planned.Should().BeNull();
        cr.StatusId.Should().Be(CrBacklog.Id);
    }

    [Fact]
    [Trait("Req", "B-17")]
    [Trait("Req", "AC-33")]
    public void Completing_a_review_records_today_and_unchecking_clears_it()
    {
        var content = new ReviewContent(Title("Review statement export"), null, null,
            [ServiceText.CreateOptional("Statement Archive").Value!, ServiceText.CreateOptional("statement archive").Value!], null, null);
        var review = ReviewItem.Create(new ReviewItemId(Guid.NewGuid()), content, 1, Now);

        var done = review.SetDone(true, Today, Now);
        var reopened = done.SetDone(false, Today.AddDays(1), Now);

        review.Content.Services.Should().ContainSingle("service names are de-duplicated case-insensitively");
        done.CompletedOn.Should().Be(Today);
        reopened.CompletedOn.Should().BeNull();
        reopened.IsDone.Should().BeFalse();
    }

    private static ChangeRequest NewCr() =>
        ChangeRequest.Create(new ChangeRequestId(Guid.NewGuid()),
            new ChangeRequestContent(CrNumber.Create("CR-1042").Value, Title("Instant card freeze"), new PersonId(Guid.NewGuid()),
                null, Medium, null, null, null, null, null, CustomValue.NoValues),
            CrBacklog, Now);
}
