using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.Settings;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Application.Tests.Settings;

/// <summary>Design §4.6: the Copy ID format, its placeholders, and its gap trimming.</summary>
public sealed class CopyIdFormatterTests
{
    private static readonly CopyIdValues All = new("CR-1067", "INT-7541", "Card limits API", "Cards-API", "Retail Cards");

    [Fact]
    public void All_values_fill_the_format() =>
        CopyIdFormatter.Apply("{CRNum} - {TaskNum} | {Title} ({Service}, {Squad})", All)
            .Should().Be("CR-1067 - INT-7541 | Card limits API (Cards-API, Retail Cards)");

    [Fact]
    public void A_missing_CR_number_drops_the_separator_after_it() =>
        CopyIdFormatter.Apply("{CRNum} - {TaskNum}", All with { CrNumber = null }).Should().Be("INT-7541");

    [Fact]
    public void A_missing_task_number_drops_the_separator_before_it() =>
        CopyIdFormatter.Apply("{CRNum} - {TaskNum}", All with { TaskNumber = "  " }).Should().Be("CR-1067");

    [Fact]
    public void A_missing_middle_value_leaves_one_separator() =>
        CopyIdFormatter.Apply("{CRNum} - {TaskNum} - {Title}", All with { TaskNumber = null }).Should().Be("CR-1067 - Card limits API");

    [Fact]
    public void Text_before_and_after_needs_the_value_next_to_it()
    {
        CopyIdFormatter.Apply("[{CRNum}] {Title}!", All with { CrNumber = null }).Should().Be("Card limits API!");
        CopyIdFormatter.Apply("[{CRNum}] {Title}!", All with { Title = null }).Should().Be("[CR-1067");
    }

    [Fact]
    public void No_values_give_an_empty_text() =>
        CopyIdFormatter.Apply("{CRNum} - {TaskNum}", new CopyIdValues(null, null, null, null, null)).Should().BeEmpty();

    [Fact]
    public void Values_containing_the_separator_stay_intact() =>
        CopyIdFormatter.Apply("{CRNum}-{Service}-{TaskNum}", All with { TaskNumber = null }).Should().Be("CR-1067-Cards-API");

    [Fact]
    public void Placeholders_match_case_insensitively() =>
        CopyIdFormatter.Apply("{crnum}/{TASKNUM}/{sQuAd}", All).Should().Be("CR-1067/INT-7541/Retail Cards");

    [Fact]
    public void An_unknown_placeholder_is_left_as_typed() =>
        CopyIdFormatter.Apply("{Jira} {CRNum} - {TaskNum}", All with { CrNumber = null }).Should().Be("INT-7541");

    [Fact]
    public void An_unknown_placeholder_alone_is_kept() =>
        CopyIdFormatter.Apply(" {Jira}-1 ", All).Should().Be("{Jira}-1");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_format_uses_the_default(string? format)
    {
        CopyIdFormatter.Normalize(format).Should().Be(CopyIdFormatter.Default);
        CopyIdFormatter.Apply(format, All).Should().Be("CR-1067 - INT-7541");
    }

    [Fact]
    public void A_subtask_card_uses_the_inherited_CR_values_and_the_parent_title_and_service()
    {
        var cr = new InheritedCrValues(new ChangeRequestId(Guid.NewGuid()), "CR-1067", "INT-7541", new SquadId(Guid.NewGuid()), "Retail Cards", null, null);
        var status = new LookupRef<TaskStatusId>(new TaskStatusId(Guid.NewGuid()), "Backlog");
        var card = new SubtaskCardReadModel(
            new SubtaskCardId(Guid.NewGuid()), BoardCardKind.SubtaskCard, new TaskId(Guid.NewGuid()), "Subtitle", "Parent title", "Cards API", [],
            false, status, 0, false, cr, null, null, null, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

        var values = CopyIdValues.From(card);

        values.Should().Be(new CopyIdValues("CR-1067", "INT-7541", "Parent title", "Cards API", "Retail Cards"));
        CopyIdFormatter.Apply("{CRNum} - {TaskNum} {Title}", values).Should().Be("CR-1067 - INT-7541 Parent title");
    }

    [Fact]
    public void An_unlinked_record_has_no_squad_value()
    {
        var cr = new InheritedCrValues(null, null, null, null, "General", null, null);
        var status = new LookupRef<TaskStatusId>(new TaskStatusId(Guid.NewGuid()), "Backlog");
        var card = new SubtaskCardReadModel(
            new SubtaskCardId(Guid.NewGuid()), BoardCardKind.SubtaskCard, new TaskId(Guid.NewGuid()), "Subtitle", "Parent", null, [],
            false, status, 0, false, cr, null, null, null, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

        CopyIdValues.From(card).Squad.Should().BeNull();
    }
}
