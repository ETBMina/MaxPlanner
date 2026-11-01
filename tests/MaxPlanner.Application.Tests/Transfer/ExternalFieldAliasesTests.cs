using System.Globalization;
using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Application.Transfer.External;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Transfer;

/// <summary>
/// B-13.2 column aliases for user-mapped CSV/XLSX tables, the mapping review rules, and the T-10 record limit that every
/// import route shares.
/// </summary>
[Trait("Req", "B-13")]
public sealed class ExternalFieldAliasesTests
{
    [Theory]
    [InlineData("Key", ExternalField.SourceId)]
    [InlineData("External ID", ExternalField.SourceId)]
    [InlineData("Summary", ExternalField.Title)]
    [InlineData("  TITLE ", ExternalField.Title)]
    [InlineData("State", ExternalField.Status)]
    [InlineData("Prio", ExternalField.Priority)]
    [InlineData("Designers", ExternalField.Designers)]
    [InlineData("Assigned To", ExternalField.Developers)]
    [InlineData("Labels", ExternalField.Tags)]
    [InlineData("Description", ExternalField.Notes)]
    [InlineData("Start Date", ExternalField.StartDate)]
    [InlineData("Finish", ExternalField.EndDate)]
    [InlineData("isArchived", ExternalField.Archived)]
    [InlineData("Archived", ExternalField.Archived)]
    [InlineData("crNumber", ExternalField.CrNumber)]
    [InlineData("CR #", ExternalField.CrNumber)]
    [InlineData("taskNumber", ExternalField.TaskNumber)]
    [InlineData("Team", ExternalField.Squad)]
    [InlineData("CR Owner", ExternalField.Owner)]
    [InlineData("Random", ExternalField.Ignore)]
    public void A_task_column_header_proposes_its_field(string header, ExternalField expected) =>
        ExternalFieldAliases.Propose(ExternalEntity.Task, [header]).Should().Equal((expected, false));

    [Theory]
    [InlineData("number", ExternalField.CrNumber)]
    [InlineData("solutionDesign", ExternalField.SolutionDesignUrl)]
    [InlineData("solutionDesignUrl", ExternalField.SolutionDesignUrl)]
    [InlineData("Planned Start", ExternalField.PlannedStart)]
    [InlineData("Due", ExternalField.PlannedEnd)]
    [InlineData("Email Subject", ExternalField.EmailSubject)]
    [InlineData("owner", ExternalField.Owner)]
    [InlineData("Details", ExternalField.Notes)]
    public void A_cr_column_header_proposes_its_field(string header, ExternalField expected) =>
        ExternalFieldAliases.Propose(ExternalEntity.ChangeRequest, [header]).Should().Equal((expected, false));

    [Theory]
    [InlineData(ExternalEntity.Review, "Services", ExternalField.Service)]
    [InlineData(ExternalEntity.Review, "Review comments", ExternalField.Notes)]
    [InlineData(ExternalEntity.Review, "Completed on", ExternalField.CompletedOn)]
    [InlineData(ExternalEntity.Review, "Done", ExternalField.Done)]
    [InlineData(ExternalEntity.GeneralTodo, "To-do", ExternalField.Title)]
    [InlineData(ExternalEntity.GeneralTodo, "Completed", ExternalField.Done)]
    public void Aliases_depend_on_the_table_entity(ExternalEntity entity, string header, ExternalField expected) =>
        ExternalFieldAliases.Propose(entity, [header]).Should().Equal((expected, false));

    [Fact]
    public void Two_columns_mapped_to_one_field_need_review_and_the_table_is_not_imported()
    {
        var table = new ExternalTable("sheet", ExternalSourceKind.Xlsx, ExternalEntity.Task, ["Title", "Summary"],
            [new ExternalRow("sheet#1", 1, [new ExternalValue("A"), new ExternalValue("B")])]);
        var options = new ExternalImportOptions
        {
            SourceNamespace = "test",
            Tables = [new ExternalTableMapping("sheet", ExternalEntity.Task, [ExternalField.Title, ExternalField.Title])],
        };

        var draft = ExternalImportPlanner.Build(new ExternalSource([table], []), options, SeedWorkflow, new Dictionary<string, Guid>(), new Dictionary<(string, string), Guid>());

        draft.Findings.Should().Contain(new ImportFinding(ImportSeverity.Error, ImportEntity.File, "sheet", ImportFindingCodes.AmbiguousColumn, "Title", "Summary"));
        draft.Document.Tasks.Should().BeEmpty();
    }

    [Fact]
    public void A_task_table_without_a_title_column_is_reported_as_missing_it()
    {
        var table = new ExternalTable("sheet", ExternalSourceKind.Xlsx, ExternalEntity.Task, ["Service"],
            [new ExternalRow("sheet#1", 1, [new ExternalValue("Cards")])]);
        var options = new ExternalImportOptions
        {
            SourceNamespace = "test",
            Tables = [new ExternalTableMapping("sheet", ExternalEntity.Task, [ExternalField.Service])],
        };

        var draft = ExternalImportPlanner.Build(new ExternalSource([table], []), options, SeedWorkflow, new Dictionary<string, Guid>(), new Dictionary<(string, string), Guid>());

        draft.Findings.Should().Contain(new ImportFinding(ImportSeverity.Warning, ImportEntity.File, "sheet", ImportFindingCodes.MissingColumn, "Title"));
    }

    [Fact]
    [Trait("Req", "T-10")]
    public void A_document_over_the_record_limit_is_refused_before_any_record_is_planned()
    {
        var document = new TransferDocument
        {
            GeneralTodos = [.. Enumerable.Range(0, TransferLimits.MaxTopLevelRecords + 1).Select(_ => new TransferGeneralTodo { Id = Guid.NewGuid(), Text = "x" })],
        };

        var plan = ImportPlanner.Plan(document, new ReadSnapshot(0, [], [], [], [], [], [], SeedWorkflow), ImportMode.CreateOnly, ImportContext.Empty, DateTimeOffset.UnixEpoch);

        plan.Findings.Should().ContainSingle().Which.Should().Be(
            new ImportFinding(ImportSeverity.Error, ImportEntity.File, string.Empty, ImportFindingCodes.TooManyRecords, null, TransferLimits.MaxTopLevelRecords.ToString(CultureInfo.InvariantCulture)));
        (plan.GeneralTodos.Count, plan.CanApply).Should().Be((0, false));
    }
}
