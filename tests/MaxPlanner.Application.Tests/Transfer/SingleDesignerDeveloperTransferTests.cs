using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Application.Transfer.External;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Transfer;

/// <summary>
/// ADR 0024 transfers: the format keeps Designer and Developer arrays, and an import that finds several people keeps
/// the first and warns, naming the people it drops.
/// </summary>
[Trait("Req", "B-12")]
[Trait("Req", "B-13")]
public sealed class SingleDesignerDeveloperTransferTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private static ReadSnapshot EmptySnapshot { get; } = new(0, [], [], [], [], [], [], SeedWorkflow);

    [Fact]
    public void A_json_import_keeps_the_first_designer_and_developer_and_names_the_dropped_people()
    {
        var document = new TransferDocument
        {
            Tasks =
            [
                new TransferTask { Id = Guid.NewGuid(), OwnTitle = "Imported", DesignerIds = [Alice.Id.Value, Bob.Id.Value], DeveloperIds = [Bob.Id.Value] },
            ],
        };

        var plan = ImportPlanner.Plan(document, EmptySnapshot, ImportMode.CreateOnly, ImportContext.Empty, Now);

        plan.HasErrors.Should().BeFalse();
        var task = plan.Tasks.Should().ContainSingle().Which.Record;
        task.DesignerId.Should().Be(Alice.Id);
        task.DeveloperId.Should().Be(Bob.Id);
        plan.Findings.Should().ContainSingle(f => f.Code == ImportFindingCodes.PeopleDropped)
            .Which.Should().Be(new ImportFinding(ImportSeverity.Warning, ImportEntity.Task, task.Id.Value.ToString(), ImportFindingCodes.PeopleDropped, "DesignerIds", "Bob"));
    }

    [Fact]
    public void A_json_import_that_names_one_person_twice_keeps_them_without_a_warning()
    {
        var document = new TransferDocument
        {
            Tasks = [new TransferTask { Id = Guid.NewGuid(), OwnTitle = "Imported", DesignerIds = [Alice.Id.Value, Alice.Id.Value] }],
        };

        var plan = ImportPlanner.Plan(document, EmptySnapshot, ImportMode.CreateOnly, ImportContext.Empty, Now);

        plan.Tasks.Should().ContainSingle().Which.Record.DesignerId.Should().Be(Alice.Id);
        plan.Findings.Should().NotContain(f => f.Code == ImportFindingCodes.PeopleDropped);
    }

    [Fact]
    public void A_json_import_with_an_unknown_extra_person_stays_strict_and_reports_the_unknown_reference()
    {
        var id = Guid.NewGuid();
        var document = new TransferDocument
        {
            Tasks = [new TransferTask { Id = id, OwnTitle = "Imported", DesignerIds = [Alice.Id.Value, Guid.NewGuid()] }],
        };

        var plan = ImportPlanner.Plan(document, EmptySnapshot, ImportMode.CreateOnly, ImportContext.Empty, Now);

        plan.HasErrors.Should().BeTrue("an app-written file never names an unknown person");
        plan.Tasks.Should().BeEmpty();
        plan.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.Code == ImportFindingCodes.UnknownReference
            && f.Field == "DesignerIds" && f.RecordKey == id.ToString());
    }

    [Fact]
    public void An_external_import_keeps_the_first_name_that_resolves_and_an_invalid_name_before_it_keeps_its_error()
    {
        var tooLong = new string('x', Domain.Common.DomainLimits.LookupName + 1);
        var table = new ExternalTable(
            "tasks.csv",
            ExternalSourceKind.Csv,
            ExternalEntity.Task,
            ["Title", "Designers"],
            [new ExternalRow("row-1", 2, [new ExternalValue("Card freeze"), new ExternalValue(null, [tooLong, "Bob", "Dana"])])]);
        var options = new ExternalImportOptions
        {
            SourceNamespace = "test",
            Tables = [new ExternalTableMapping("tasks.csv", ExternalEntity.Task, [ExternalField.Title, ExternalField.Designers])],
        };

        var draft = ExternalImportPlanner.Build(new ExternalSource([table], []), options, SeedWorkflow, new Dictionary<string, Guid>(), new Dictionary<(string, string), Guid>());

        draft.Document.Tasks.Should().ContainSingle().Which.DesignerIds.Should().Equal(Bob.Id.Value);
        draft.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.Code == ImportFindingCodes.Invalid);
        draft.Findings.Should().ContainSingle(f => f.Code == ImportFindingCodes.PeopleDropped).Which.Detail.Should().Be("Dana");
        draft.Values.Select(v => v.SourceName).Should().NotContain("Dana");
    }

    [Fact]
    public void An_external_import_resolves_only_the_first_name_so_no_person_is_created_for_a_dropped_one()
    {
        var table = new ExternalTable(
            "tasks.csv",
            ExternalSourceKind.Csv,
            ExternalEntity.Task,
            ["Title", "Designers", "Developers"],
            [new ExternalRow("row-1", 2, [new ExternalValue("Card freeze"), new ExternalValue("Alice; Dana"), new ExternalValue(null, ["Erin", "Bob"])])]);
        var options = new ExternalImportOptions
        {
            SourceNamespace = "test",
            Tables = [new ExternalTableMapping("tasks.csv", ExternalEntity.Task, [ExternalField.Title, ExternalField.Designers, ExternalField.Developers])],
        };

        var draft = ExternalImportPlanner.Build(new ExternalSource([table], []), options, SeedWorkflow, new Dictionary<string, Guid>(), new Dictionary<(string, string), Guid>());

        var task = draft.Document.Tasks.Should().ContainSingle().Subject;
        task.DesignerIds.Should().Equal(Alice.Id.Value);
        task.DeveloperIds.Should().ContainSingle("Erin is new and is the first Developer");
        draft.Values.Select(v => v.SourceName).Should().NotContain(["Dana", "Bob"]);
        draft.Findings.Where(f => f.Code == ImportFindingCodes.PeopleDropped).Select(f => (f.Severity, f.Field, f.Detail))
            .Should().BeEquivalentTo([(ImportSeverity.Warning, "DesignerIds", "Dana"), (ImportSeverity.Warning, "DeveloperIds", "Bob")]);
    }
}
