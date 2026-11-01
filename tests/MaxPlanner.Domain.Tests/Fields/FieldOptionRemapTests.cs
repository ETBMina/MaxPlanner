using AwesomeAssertions;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using static MaxPlanner.Domain.Tests.Fixtures;

namespace MaxPlanner.Domain.Tests.Fields;

/// <summary>ADR 0026: a Lookup option removed by a configuration import is replaced or cleared in every record.</summary>
[Trait("Req", "B-11")]
public sealed class FieldOptionRemapTests
{
    private static readonly FieldId Risk = new(Guid.NewGuid());
    private static readonly FieldOptionId High = new(Guid.NewGuid());
    private static readonly FieldOptionId Medium = new(Guid.NewGuid());

    [Fact]
    public void A_replacement_takes_over_the_removed_option_and_touches_the_task()
    {
        var task = WithRisk(High);

        var remapped = task.Remap(new FieldOptionRemap(Risk, High, Medium), Now)!;

        remapped.CustomValues[Risk].Should().Be(new LookupCustomValue(Medium));
        remapped.Revision.Should().Be(task.Revision + 1);
        remapped.Dates.Should().Be(task.Dates);
    }

    [Fact]
    public void Clearing_removes_only_that_fields_value()
    {
        var remapped = WithRisk(High).Remap(new FieldOptionRemap(Risk, High, null), Now)!;

        remapped.CustomValues.Should().NotContainKey(Risk);
    }

    [Fact]
    public void A_task_with_another_option_is_not_changed()
    {
        WithRisk(Medium).Remap(new FieldOptionRemap(Risk, High, null), Now).Should().BeNull();
    }

    private static TaskItem WithRisk(FieldOptionId option) =>
        UnlinkedTask().UpdateContent(Content() with { CustomValues = new Dictionary<FieldId, CustomValue> { [Risk] = new LookupCustomValue(option) } }, Now).Value;
}
