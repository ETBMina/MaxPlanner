using AwesomeAssertions;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Domain.Tests.Fixtures;

namespace MaxPlanner.Domain.Tests.Tasks;

/// <summary>Rebuilding records from storage (slice 1d mappers): exact values, no rules re-run, corruption reported.</summary>
[Trait("Req", "T-05")]
public sealed class RehydrateTests
{
    private static readonly DateTimeOffset Created = Now.AddDays(-30);
    private static readonly ChangeRequestId Cr = new(Guid.NewGuid());
    private static readonly FieldId Points = new(Guid.NewGuid());

    [Fact]
    public void A_task_is_rebuilt_with_every_stored_value_and_no_automation_or_revision_bump()
    {
        var items = new[] { new ChecklistItem(new ChecklistItemId(Guid.NewGuid()), ChecklistText.Create("Done").Value, true, 1_024) };
        var preserved = new PreservedMetadata("Old own title", ["CR-9"], """{"x":1}""");
        var content = new TaskContent(null, Service(), [new PersonId(Guid.NewGuid())], [], [new TagId(Guid.NewGuid())],
            LongText.CreateOptional("Notes", "Notes").Value, new Dictionary<FieldId, CustomValue> { [Points] = new NumberCustomValue(5) });

        // In Progress sets StartDate on creation, but rehydration must keep the stored null.
        var task = TaskItem.Rehydrate(new TaskId(Guid.NewGuid()), content, InProgress.Id, Medium, null, Today.AddDays(-1),
            isArchived: true, rank: 7_168, items, Cr, preserved, Created, Now, revision: 12).Value;

        task.Dates.Start.Should().BeNull();
        task.Dates.End.Should().Be(Today.AddDays(-1));
        task.Revision.Should().Be(12);
        task.CreatedAtUtc.Should().Be(Created);
        task.UpdatedAtUtc.Should().Be(Now);
        task.IsArchived.Should().BeTrue();
        task.Rank.Should().Be(7_168);
        task.ChangeRequestId.Should().Be(Cr);
        task.Preserved.Should().Be(preserved);
        task.ChecklistItems.Should().Equal(items);
        task.CustomValues[Points].Should().Be(new NumberCustomValue(5));
        task.OwnTitle.Should().BeNull();
    }

    [Fact]
    public void A_corrupt_task_row_is_reported_instead_of_loaded()
    {
        var linkedWithTitle = new TaskContent(Title(), Service(), [], [], [], null, CustomValue.NoValues);
        var unlinkedWithoutTitle = new TaskContent(null, null, [], [], [], null, CustomValue.NoValues);
        var duplicateItemId = new ChecklistItemId(Guid.NewGuid());
        ChecklistItem[] duplicates =
        [
            new(duplicateItemId, ChecklistText.Create("A").Value, false, 1),
            new(duplicateItemId, ChecklistText.Create("B").Value, false, 2),
        ];

        RehydrateTask(linkedWithTitle, Cr).Error!.Code.Should().Be(DomainErrorCodes.TitleNotAllowedWhenLinked);
        RehydrateTask(unlinkedWithoutTitle, null).Error!.Code.Should().Be(DomainErrorCodes.Required);
        RehydrateTask(Content(), null, start: Today, end: Today.AddDays(-1)).Error!.Code.Should().Be(DomainErrorCodes.DateRangeInverted);
        RehydrateTask(Content(), null, revision: 0).Error.Should().Be(new DomainError(DomainErrorCodes.InvalidStoredValue, "Revision"));
        RehydrateTask(Content(), null, updated: Created.AddTicks(-1)).Error.Should().Be(new DomainError(DomainErrorCodes.InvalidStoredValue, "UpdatedAtUtc"));
        RehydrateTask(Content(), null, items: duplicates).Error.Should().Be(new DomainError(DomainErrorCodes.InvalidStoredValue, "ChecklistItems"));
    }

    [Fact]
    [Trait("Req", "B-16")]
    public void A_subtask_card_is_rebuilt_as_stored_and_rejects_inverted_dates()
    {
        var card = SubtaskCard.Rehydrate(new SubtaskCardId(Guid.NewGuid()), new TaskId(Guid.NewGuid()), SubtitleText.Create("Follow-up").Value,
            Completed.Id, Today.AddDays(-3), null, null, 2_048, Created, Now, 4).Value;
        var inverted = SubtaskCard.Rehydrate(new SubtaskCardId(Guid.NewGuid()), new TaskId(Guid.NewGuid()), SubtitleText.Create("Bad").Value,
            Backlog.Id, Today, Today.AddDays(-1), null, 1, Created, Now, 1);

        card.Dates.End.Should().BeNull("a completed status does not stamp an end date on load");
        card.Revision.Should().Be(4);
        card.Rank.Should().Be(2_048);
        inverted.Error!.Code.Should().Be(DomainErrorCodes.DateRangeInverted);
    }

    [Fact]
    public void A_change_request_is_rebuilt_with_its_revision_and_custom_values()
    {
        var content = new ChangeRequestContent(CrNumber.Create("CR-7").Value, Title("Statement export"), new PersonId(Guid.NewGuid()), null, Medium,
            PlannedRange.Create(Today, Today.AddDays(2)).Value, null, null, null, TaskNumber.CreateOptional("007").Value,
            new Dictionary<FieldId, CustomValue> { [Points] = new NumberCustomValue(8) });
        var status = new CrStatusId(Guid.NewGuid());

        var preserved = new PreservedMetadata(null, ["CR-7-legacy"], """{"jira":"MP-7"}""");

        var cr = ChangeRequest.Rehydrate(Cr, content, status, preserved, Created, Now, 9).Value;

        cr.Revision.Should().Be(9);
        cr.StatusId.Should().Be(status);
        cr.TaskNumber!.Value.Should().Be("007");
        cr.Planned!.DurationDays.Should().Be(3);
        cr.CustomValues[Points].Should().Be(new NumberCustomValue(8));
        cr.Preserved.Should().BeSameAs(preserved);
        ChangeRequest.Rehydrate(Cr, content, status, preserved, Created, Now, 0).Error!.Code.Should().Be(DomainErrorCodes.InvalidStoredValue);
        ChangeRequest.Rehydrate(Cr, content, status, preserved with { PreLinkTitle = "Only tasks are linked" }, Created, Now, 9).Error
            .Should().Be(new DomainError(DomainErrorCodes.InvalidStoredValue, "Preserved"));
    }

    [Fact]
    [Trait("Req", "B-17")]
    public void A_review_item_needs_a_completion_day_exactly_when_it_is_done()
    {
        var content = new ReviewContent(Title("Review export"), null, null, [Service("Cards"), Service("cards")], null, null);

        var done = ReviewItem.Rehydrate(new ReviewItemId(Guid.NewGuid()), content, true, Today.AddDays(-2), 3, Created, Now, 5).Value;
        var doneWithoutDay = ReviewItem.Rehydrate(new ReviewItemId(Guid.NewGuid()), content, true, null, 3, Created, Now, 5);
        var openWithDay = ReviewItem.Rehydrate(new ReviewItemId(Guid.NewGuid()), content, false, Today, 3, Created, Now, 5);

        done.CompletedOn.Should().Be(Today.AddDays(-2));
        done.Content.Services.Should().HaveCount(2, "stored values are not re-normalized on load");
        doneWithoutDay.Error.Should().Be(new DomainError(DomainErrorCodes.InvalidStoredValue, "CompletedOn"));
        openWithDay.Error!.Code.Should().Be(DomainErrorCodes.InvalidStoredValue);
    }

    [Fact]
    [Trait("Req", "B-09")]
    public void A_general_todo_is_rebuilt_as_stored()
    {
        var todo = GeneralTodo.Rehydrate(new GeneralTodoId(Guid.NewGuid()), ChecklistText.Create("Renew badge").Value, true, 3_072, Created, Now, 2).Value;

        todo.IsCompleted.Should().BeTrue();
        todo.Rank.Should().Be(3_072);
        todo.Revision.Should().Be(2);
        GeneralTodo.Rehydrate(todo.Id, todo.Text, false, 1, Now, Created, 2).Error!.Field.Should().Be("UpdatedAtUtc");
    }

    private static DomainResult<TaskItem> RehydrateTask(
        TaskContent content,
        ChangeRequestId? cr,
        DateOnly? start = null,
        DateOnly? end = null,
        long revision = 1,
        DateTimeOffset? updated = null,
        IReadOnlyList<ChecklistItem>? items = null) =>
        TaskItem.Rehydrate(new TaskId(Guid.NewGuid()), content, Backlog.Id, Medium, start, end, false, 1, items ?? [], cr,
            PreservedMetadata.Empty, Created, updated ?? Now, revision);
}
