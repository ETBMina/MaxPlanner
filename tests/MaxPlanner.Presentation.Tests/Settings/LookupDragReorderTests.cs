using AwesomeAssertions;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Settings;

namespace MaxPlanner.Presentation.Tests.Settings;

/// <summary>
/// Design §9: a native ListView reorder moves the row first; the view model turns the move into a MoveLookup offset
/// and, on refusal, syncs the rows back to the committed order with the same row instances (ADR 0010).
/// </summary>
[Trait("Req", "B-04")]
[Trait("Adr", "0010")]
public sealed class LookupDragReorderTests
{
    private readonly LookupSectionViewModel _section = new(LookupKind.TaskStatus, "TaskStatuses", "Task statuses", "");
    private readonly Guid[] _ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    public LookupDragReorderTests()
    {
        foreach (var id in _ids)
        {
            _section.MutableRows.Add(new LookupRowViewModel(LookupKind.TaskStatus, id, "TaskStatuses"));
        }
    }

    [Theory]
    [InlineData(0, 3, 3)]
    [InlineData(3, 1, -2)]
    [InlineData(2, 2, 0)]
    public void The_offset_is_the_distance_the_list_moved_the_row(int from, int to, int expected)
    {
        var row = _section.ListRows[from];
        _section.ListRows.Move(from, to);

        _section.DragOffset(row, from).Should().Be(expected);
    }

    [Fact]
    public void A_row_that_left_the_list_or_an_unknown_start_gives_no_move()
    {
        var row = _section.ListRows[1];

        _section.DragOffset(row, -1).Should().Be(0);
        _section.ListRows.Remove(row);
        _section.DragOffset(row, 1).Should().Be(0);
    }

    [Fact]
    public void A_refused_drag_is_restored_to_the_committed_order_with_the_same_rows()
    {
        var before = _section.ListRows.ToList();
        _section.ListRows.Move(0, 2);

        KeyedCollectionSync.Sync(_section.MutableRows, _ids, id => id, r => r.Id, id => throw new InvalidOperationException(), (_, _) => { });

        _section.ListRows.Should().Equal(before);
    }
}
