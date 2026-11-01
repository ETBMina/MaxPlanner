using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Transfer;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Transfer;

/// <summary>
/// ADR 0026 planning rules: a data-only import matches only what its records use and never changes configuration; a
/// configuration import replaces the configuration and needs the B-04 required lookups; records are ignored.
/// </summary>
[Trait("Req", "B-12")]
public sealed class ImportContentPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private static ReadSnapshot EmptySnapshot { get; } = new(0, [], [], [], [], [], [], SeedWorkflow);

    private static ImportPlan Plan(TransferDocument document, TransferContent content) =>
        ImportPlanner.Plan(document, EmptySnapshot, ImportMode.CreateOnly, ImportContext.Empty, Now, new ImportOptions(content));

    [Fact]
    public void Data_only_matches_a_used_value_by_name_when_its_id_differs()
    {
        var fileStatus = Guid.NewGuid();
        var document = new TransferDocument
        {
            Lookups = new TransferLookups { TaskStatuses = [new TransferTaskStatus(fileStatus, "blocked", 0, false, false, false)] },
            Tasks = [new TransferTask { Id = Guid.NewGuid(), OwnTitle = "Imported", StatusId = fileStatus }],
        };

        var plan = Plan(document, TransferContent.DataOnly);

        plan.HasErrors.Should().BeFalse();
        plan.NewWorkflow.Should().BeNull();
        plan.Tasks.Should().ContainSingle().Which.Record.StatusId.Should().Be(Blocked.Id);
    }

    [Fact]
    public void Data_only_ignores_an_unmatched_value_that_no_record_uses_and_blocks_on_one_that_is_used()
    {
        var ghost = Guid.NewGuid();
        var carol = Guid.NewGuid();
        var document = new TransferDocument
        {
            Lookups = new TransferLookups { People = [new TransferNamedLookup(ghost, "Ghost"), new TransferNamedLookup(carol, "Carol")] },
            Tasks = [new TransferTask { Id = Guid.NewGuid(), OwnTitle = "Imported", DesignerId = carol }],
        };

        var plan = Plan(document, TransferContent.DataOnly);

        plan.Findings.Should().ContainSingle(f => f.Code == ImportFindingCodes.ConfigurationMissing)
            .Which.Should().Match<ImportFinding>(f => f.Severity == ImportSeverity.Error && f.Detail == "Carol");
        plan.CanApply.Should().BeFalse();
        plan.NewWorkflow.Should().BeNull("a data-only import never adds a person");
    }

    [Fact]
    public void A_configuration_replace_requires_a_task_status_a_cr_status_and_a_priority()
    {
        var missing = Guid.NewGuid();
        var plan = Plan(new TransferDocument { Defaults = new TransferDefaults(missing, missing, missing, null, missing, missing) }, TransferContent.ConfigurationOnly);

        plan.Findings.Where(f => f.Code == ImportFindingCodes.ConfigurationRequired).Select(f => f.Field)
            .Should().BeEquivalentTo(["TaskStatus", "CrStatus", "Priority"]);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public void A_configuration_only_import_ignores_the_records_of_the_file()
    {
        var document = Configuration() with { Tasks = [new TransferTask { Id = Guid.NewGuid(), OwnTitle = "Ignored" }] };

        var plan = Plan(document, TransferContent.ConfigurationOnly);

        plan.Tasks.Should().BeEmpty();
        plan.Findings.Should().Contain(f => f.Code == ImportFindingCodes.RecordsIgnored && f.Detail == "1");
    }

    [Fact]
    public void A_replace_with_the_current_configuration_changes_nothing_and_needs_no_confirmation()
    {
        var plan = Plan(Configuration(), TransferContent.ConfigurationOnly);

        plan.HasErrors.Should().BeFalse();
        plan.IsConfigurationReplace.Should().BeFalse();
        plan.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void A_replace_with_duplicate_names_in_the_file_is_an_error()
    {
        var document = Configuration();
        document = document with
        {
            Lookups = document.Lookups with { Tags = [new TransferNamedLookup(Guid.NewGuid(), "Web"), new TransferNamedLookup(Guid.NewGuid(), "WEB")] },
        };

        var plan = Plan(document, TransferContent.ConfigurationOnly);

        plan.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.Code == ImportFindingCodes.DuplicateInSource && f.Field == "Tag");
    }

    [Fact]
    public void A_file_field_default_that_names_a_person_missing_from_the_file_drops_that_person_with_a_warning()
    {
        var reviewers = Guid.NewGuid();
        var document = Configuration();
        document = document with
        {
            Lookups = document.Lookups with { People = [new TransferNamedLookup(Alice.Id.Value, "Alice")] },
            FieldDefinitions =
            [
                new TransferFieldDefinition(reviewers, "reviewers", "Task", "Reviewers", "People", false,
                    new TransferValue("People", Ids: [Alice.Id.Value, Guid.NewGuid()]), 0, [], true),
            ],
        };

        var plan = Plan(document, TransferContent.ConfigurationOnly);

        plan.Findings.Should().Contain(f => f.Severity == ImportSeverity.Warning && f.Code == ImportFindingCodes.DefaultDropped && f.Detail == "reviewers");
        plan.NewWorkflow!.Fields.Single(f => f.Id.Value == reviewers).DefaultValue.Should().BeOfType<Domain.Fields.PeopleCustomValue>()
            .Which.PersonIds.Should().Equal(Alice.Id);
    }

    [Fact]
    public void Fields_match_by_id_before_key_so_a_new_field_never_reuses_a_current_fields_id()
    {
        // The target has field Y with key k1; the file has A (X, k1) and B (Y, k2). B takes Y by ID first, so A is new.
        var y = Guid.NewGuid();
        var x = Guid.NewGuid();
        var current = Domain.Fields.FieldDefinition.Create(new Domain.Identifiers.FieldId(y), Domain.Fields.FieldKey.Create("k1").Value, Domain.Fields.FieldTarget.Task,
            Domain.Values.LookupName.Create("K1").Value, Domain.Fields.FieldKind.Number, false, null, 0, []).Value;
        var snapshot = EmptySnapshot with { Workflow = SeedWorkflow with { Fields = [current] } };
        var document = Configuration() with
        {
            FieldDefinitions =
            [
                new TransferFieldDefinition(x, "k1", "Task", "A", "Number", false, null, 0, [], true),
                new TransferFieldDefinition(y, "k2", "Task", "B", "Number", false, null, 1, [], true),
            ],
        };

        var plan = ImportPlanner.Plan(document, snapshot, ImportMode.CreateOnly, ImportContext.Empty, Now, new ImportOptions(TransferContent.ConfigurationOnly));

        plan.Findings.Should().Contain(f => f.Code == ImportFindingCodes.DuplicateInSource && f.Detail == "k1",
            "B keeps the key k1 of the field it matched by ID, so A's key collides and is reported instead of written");
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public void A_file_option_with_an_invalid_label_is_an_error_and_not_silently_dropped()
    {
        var document = Configuration() with
        {
            FieldDefinitions =
            [
                new TransferFieldDefinition(Guid.NewGuid(), "risk", "Task", "Risk", "Lookup", false, null, 0,
                    [new TransferFieldOption(Guid.NewGuid(), "High", 0), new TransferFieldOption(Guid.NewGuid(), "   ", 1)], true),
            ],
        };

        var plan = Plan(document, TransferContent.ConfigurationOnly);

        plan.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.Code == ImportFindingCodes.Invalid && f.Field == "Options");
    }

    [Fact]
    public void A_view_export_previewed_as_configuration_is_an_error_and_plans_no_replace()
    {
        var view = TransferDocumentBuilder.Build(EmptySnapshot, Guid.NewGuid(), Now, new ViewExportScope("tasks", "All", new HashSet<Guid>(), new HashSet<Guid>()));

        foreach (var content in new[] { TransferContent.Both, TransferContent.ConfigurationOnly })
        {
            var plan = Plan(view, content);
            plan.Findings.Should().Contain(f => f.Code == ImportFindingCodes.ConfigurationIncomplete);
            plan.NewWorkflow.Should().BeNull();
        }
    }

    /// <summary>The seed configuration as a file holds it.</summary>
    private static TransferDocument Configuration() =>
        TransferDocumentBuilder.Build(EmptySnapshot, Guid.NewGuid(), Now, EntireDatasetScope.Instance, TransferContent.ConfigurationOnly);
}
