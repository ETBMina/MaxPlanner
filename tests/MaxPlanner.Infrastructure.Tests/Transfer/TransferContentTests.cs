using System.IO.Compression;
using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using MaxPlanner.Infrastructure.Tests.Persistence;
using MaxPlanner.Infrastructure.Transfer;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>
/// ADR 0026 with real SQLite datasets and files: configuration-only and data-only exports in every format that allows
/// them, data-only imports that never change configuration, and configuration replaces that match, add, update, remove,
/// deactivate, and remap in-use values per B-04, all in one transaction.
/// </summary>
[Trait("Req", "B-12")]
[Trait("Req", "B-04")]
[Trait("Req", "T-10")]
public sealed class TransferContentTests : IDisposable
{
    private readonly TempFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    // ----- Export -----
    [Theory]
    [InlineData(TransferFileFormat.Json, "config.json")]
    [InlineData(TransferFileFormat.Xlsx, "config.xlsx")]
    [InlineData(TransferFileFormat.CsvBundle, "config.zip")]
    public async Task A_configuration_only_export_holds_every_definition_and_no_records(TransferFileFormat format, string name)
    {
        await using var db = await CreateAsync();
        await TransferFixture.PopulateAsync(db);
        var workflow = (await db.SnapshotAsync()).Workflow;
        var path = _folder.File(name);

        var report = await ExportAsync(db, format, TransferContent.ConfigurationOnly, path);
        var document = await Transfers.ReadAsync(path);

        document.TopLevelRecordCount.Should().Be(0);
        document.Links.Should().BeEmpty();
        document.Lookups.TaskStatuses.Select(s => s.Id).Should().BeEquivalentTo(workflow.TaskStatuses.Select(s => s.Id.Value));
        document.Lookups.People.Select(p => p.Name).Should().BeEquivalentTo(["Alice", "Bob"]);
        document.FieldDefinitions.Select(f => f.Id).Should().BeEquivalentTo(workflow.Fields.Select(f => f.Id.Value));
        document.Defaults.Should().NotBeNull();
        report.Content.Should().Be(TransferContent.ConfigurationOnly);
        report.Counts.Keys.Should().BeEquivalentTo([ImportEntity.Lookup, ImportEntity.Field]);
        report.Limitations.Should().Contain(ExportLimitationCodes.ConfigurationOnly);
        if (format == TransferFileFormat.Xlsx)
        {
            TestWorkbook.Read(await File.ReadAllBytesAsync(path, Ct)).Keys.Should().BeEquivalentTo(
                [TransferTables.MetadataSheet, TransferTables.LookupsSheet, TransferTables.FieldsSheet], "a configuration workbook has the definition sheets only");
        }

        if (format == TransferFileFormat.CsvBundle)
        {
            await using var zip = await ZipFile.OpenReadAsync(path, Ct);
            zip.Entries.Select(e => e.FullName).Should().Equal("metadata.json");
        }
    }

    [Fact]
    public async Task Configuration_only_is_refused_for_a_single_csv_table()
    {
        await using var db = await CreateAsync();

        var export = () => Transfers.Exporter(db).ExportAsync(EntireDatasetScope.Instance, TransferFileFormat.CsvTable, TransferTable.Tasks, _folder.File("t.csv"), Ct, TransferContent.ConfigurationOnly);

        await export.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(TransferFileFormat.Json, "data.json")]
    [InlineData(TransferFileFormat.Xlsx, "data.xlsx")]
    [InlineData(TransferFileFormat.CsvBundle, "data.zip")]
    public async Task A_data_only_export_holds_the_records_and_only_the_configuration_they_use(TransferFileFormat format, string name)
    {
        await using var db = await CreateAsync();
        await TransferFixture.PopulateAsync(db);
        db.AddPerson("Unused person");
        db.AddTag("Spare");
        var snapshot = await db.SnapshotAsync();
        var path = _folder.File(name);

        var report = await ExportAsync(db, format, TransferContent.DataOnly, path);
        var document = await Transfers.ReadAsync(path);

        document.Tasks.Should().HaveCount(snapshot.Tasks.Count);
        document.ChangeRequests.Should().HaveCount(snapshot.ChangeRequests.Count);
        document.Defaults.Should().BeNull("a data-only file has no business defaults");
        document.Lookups.TaskStatuses.Select(s => s.Id).Should().BeEquivalentTo(
            snapshot.Tasks.Select(t => t.StatusId.Value).Concat(snapshot.SubtaskCards.Select(c => c.StatusId.Value)).Distinct());
        document.Lookups.People.Select(p => p.Name).Should().BeEquivalentTo(["Alice", "Bob"]);
        document.Lookups.Tags.Select(t => t.Name).Should().Equal("Mobile");
        document.FieldDefinitions.Single(f => f.Key == "risk").Options.Select(o => o.Label).Should().Equal("High");
        report.Limitations.Should().Contain(ExportLimitationCodes.DataOnly);
    }

    // ----- Data only import -----
    [Fact]
    public async Task A_data_only_import_blocks_on_a_value_it_cannot_match_and_never_writes_configuration()
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var path = _folder.File("data.json");
        await ExportAsync(source, TransferFileFormat.Json, TransferContent.DataOnly, path);
        var document = await Transfers.ReadAsync(path);
        await using var target = await CreateAsync();

        var plan = await PreviewAsync(target, document, TransferContent.DataOnly);

        plan.HasErrors.Should().BeTrue();
        plan.NewWorkflow.Should().BeNull("a data-only import never changes configuration");
        plan.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.Code == ImportFindingCodes.ConfigurationMissing && f.Detail == "Alice");
        var apply = await Transfers.Importer(target).ApplyAsync(plan, OperationId.New(), Ct);
        apply.Error.Should().BeOfType<ImportNotApplicableError>();
    }

    [Theory]
    [InlineData(TransferFileFormat.Json, "data.json")]
    [InlineData(TransferFileFormat.Xlsx, "data.xlsx")]
    [InlineData(TransferFileFormat.CsvBundle, "data.zip")]
    public async Task A_data_only_import_matches_values_by_name_and_leaves_the_configuration_unchanged(TransferFileFormat format, string name)
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var path = _folder.File(name);
        await ExportAsync(source, format, TransferContent.DataOnly, path);
        var document = await Transfers.ReadAsync(path);
        await using var target = await CreateAsync();
        var alice = target.AddPerson("ALICE");
        target.AddPerson("Bob");
        target.AddTag("mobile");
        target.AddField("estimate", FieldKind.Number);
        target.AddField("risk", FieldKind.Lookup, $$"""[{"Id":"{{Guid.NewGuid()}}","Label":"High","Order":0}]""");
        var before = ImportPlanner.WorkflowStamp((await target.SnapshotAsync()).Workflow);

        var plan = await PreviewAsync(target, document, TransferContent.DataOnly);
        await Transfers.ApplyAsync(target, plan);

        var after = await target.SnapshotAsync();
        ImportPlanner.WorkflowStamp(after.Workflow).Should().Be(before);
        after.Tasks.Should().HaveCount(document.Tasks.Count);
        after.Tasks.Should().Contain(t => t.DesignerId == alice, "Alice is matched by her normalized name");
    }

    [Theory]
    [InlineData(true, TransferContent.Both)]
    [InlineData(true, TransferContent.ConfigurationOnly)]
    [InlineData(false, TransferContent.Both)]
    [InlineData(false, TransferContent.ConfigurationOnly)]
    public async Task A_view_or_data_only_export_never_replaces_the_configuration(bool isView, TransferContent content)
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var path = _folder.File("partial.json");
        TransferExportScope scope = isView
            ? new ViewExportScope("tasks", "All", new HashSet<Guid> { fixture.Unlinked.Value }, new HashSet<Guid>())
            : EntireDatasetScope.Instance;
        (await Transfers.Exporter(db).ExportAsync(scope, TransferFileFormat.Json, null, path, Ct, TransferContent.DataOnly)).Error.Should().BeNull();
        var document = await Transfers.ReadAsync(path);

        var plan = await PreviewAsync(db, document, content);

        ImportPlanner.HasCompleteConfiguration(document).Should().BeFalse();
        plan.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.Code == ImportFindingCodes.ConfigurationIncomplete);
        plan.IsConfigurationReplace.Should().BeFalse();
        plan.NewWorkflow.Should().BeNull("a partial configuration never removes the values it does not hold");
        plan.CanApply.Should().BeFalse();
    }

    // ----- Configuration replace -----
    [Fact]
    public async Task A_replace_matches_by_id_then_by_name_and_adds_updates_and_removes_values()
    {
        await using var db = await CreateAsync();
        await TransferFixture.PopulateAsync(db);
        var snapshot = await db.SnapshotAsync();
        var review = await db.TaskStatusAsync("Review");
        var unused = UnusedTaskStatus(snapshot);
        var low = snapshot.Workflow.Priorities.Single(p => p.Name.Display == "Low");
        var qa = Guid.NewGuid();
        var document = await ConfigurationAsync(db);
        document = document with
        {
            Lookups = document.Lookups with
            {
                TaskStatuses = [.. document.Lookups.TaskStatuses.Where(s => s.Id != unused.Id.Value)
                    .Select(s => s.Id == review.Id.Value ? s with { Name = "Code review" } : s), new TransferTaskStatus(qa, "QA", 99, false, false, false)],
                Priorities = [.. document.Lookups.Priorities.Select(p => p.Id == low.Id.Value ? p with { Id = Guid.NewGuid(), Name = "LOW" } : p)],
            },
        };

        var plan = await PreviewAsync(db, document, TransferContent.ConfigurationOnly);
        await Transfers.ApplyAsync(db, plan);

        plan.IsConfigurationReplace.Should().BeTrue();
        plan.Removals.Should().BeEmpty("the removed status is unused");
        plan.Findings.Should().Contain(f => f.Code == ImportFindingCodes.ConfigurationAdded && f.Detail == "QA")
            .And.Contain(f => f.Code == ImportFindingCodes.ConfigurationUpdated && f.Detail == "Code review")
            .And.Contain(f => f.Code == ImportFindingCodes.ConfigurationRemoved && f.Detail == unused.Name.Display)
            .And.Contain(f => f.Code == ImportFindingCodes.LookupMapped && f.Detail == "LOW");
        var workflow = (await db.SnapshotAsync()).Workflow;
        workflow.FindTaskStatus(review.Id)!.Name.Display.Should().Be("Code review", "a value matched by ID keeps its ID and takes the file's name");
        workflow.FindTaskStatus(new TaskStatusId(qa)).Should().NotBeNull();
        workflow.FindTaskStatus(unused.Id).Should().BeNull();
        workflow.FindPriority(low.Id)!.Name.Display.Should().Be("LOW", "a value matched by name keeps its ID");
    }

    [Fact]
    public async Task A_replace_that_swaps_two_names_applies_without_a_uniqueness_collision()
    {
        await using var db = await CreateAsync();
        var review = await db.TaskStatusAsync("Review");
        var blocked = await db.TaskStatusAsync("Blocked");
        var document = await ConfigurationAsync(db);
        document = document with
        {
            Lookups = document.Lookups with
            {
                TaskStatuses = [.. document.Lookups.TaskStatuses.Select(s =>
                    s.Id == review.Id.Value ? s with { Name = "Blocked" } : s.Id == blocked.Id.Value ? s with { Name = "Review" } : s)],
            },
        };

        await Transfers.ApplyAsync(db, await PreviewAsync(db, document, TransferContent.ConfigurationOnly));

        var workflow = (await db.SnapshotAsync()).Workflow;
        workflow.FindTaskStatus(review.Id)!.Name.Display.Should().Be("Blocked");
        workflow.FindTaskStatus(blocked.Id)!.Name.Display.Should().Be("Review");
    }

    [Fact]
    public async Task An_in_use_removal_blocks_apply_until_a_replacement_is_chosen_and_then_remaps_the_records()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var blocked = await db.TaskStatusAsync("Blocked");
        var inProgress = await db.TaskStatusAsync("In Progress");
        var document = await ConfigurationAsync(db);
        document = document with { Lookups = document.Lookups with { TaskStatuses = [.. document.Lookups.TaskStatuses.Where(s => s.Id != blocked.Id.Value)] } };
        var before = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.Unlinked);

        var blockedPlan = await PreviewAsync(db, document, TransferContent.ConfigurationOnly);

        var removal = blockedPlan.Removals.Should().ContainSingle().Which;
        removal.Key.Should().Be(ConfigurationRemovalKey.ForLookup(LookupKind.TaskStatus, blocked.Id.Value));
        removal.CanClear.Should().BeFalse("a status can only be replaced (B-04)");
        removal.Usage.Tasks.Should().Be(1);
        blockedPlan.CanApply.Should().BeFalse();
        blockedPlan.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.Code == ImportFindingCodes.RemovalUnresolved);

        var cleared = await PreviewAsync(db, document, TransferContent.ConfigurationOnly, new() { [removal.Key] = null });
        cleared.CanApply.Should().BeFalse("clearing a status is not allowed");

        var plan = await PreviewAsync(db, document, TransferContent.ConfigurationOnly, new() { [removal.Key] = inProgress.Id.Value });
        await Transfers.ApplyAsync(db, plan);

        var after = await db.SnapshotAsync();
        var task = after.Tasks.Single(t => t.Id == fixture.Unlinked);
        task.StatusId.Should().Be(inProgress.Id);
        task.Dates.Should().Be(before.Dates, "a replaced status is not a transition (B-04)");
        after.Workflow.FindTaskStatus(blocked.Id).Should().BeNull();
    }

    [Fact]
    public async Task The_people_default_of_a_field_missing_from_the_file_follows_the_removals()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var carol = db.AddPerson("Carol");
        var dave = db.AddPerson("Dave");
        var reviewers = db.AddField("reviewers", FieldKind.People);
        db.Execute("UPDATE field_definitions SET default_value_json = $v WHERE id = $id;",
            ("$v", StorageJson.WriteDefaultValue(new PeopleCustomValue([carol, dave]))), ("$id", reviewers.Value));
        Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), new TaskContentDraft("Carol designs", null, carol, null, [], null, CustomValue.NoValues)), Ct));
        var document = await ConfigurationAsync(db);
        document = document with
        {
            Lookups = document.Lookups with { People = [.. document.Lookups.People.Where(p => p.Id != carol.Value && p.Id != dave.Value)] },
            FieldDefinitions = [.. document.FieldDefinitions.Where(f => f.Id != reviewers.Value)],
        };
        var carolKey = ConfigurationRemovalKey.ForLookup(LookupKind.Person, carol.Value);

        await Transfers.ApplyAsync(db, await PreviewAsync(db, document, TransferContent.ConfigurationOnly, new() { [carolKey] = fixture.Alice.Value }));

        var field = (await db.SnapshotAsync()).Workflow.Fields.Single(f => f.Id == reviewers);
        field.IsActive.Should().BeFalse();
        field.DefaultValue.Should().BeOfType<PeopleCustomValue>().Which.PersonIds.Should().Equal(
            [fixture.Alice], "Carol is replaced by Alice, and Dave, removed without a replacement, is dropped");
    }

    [Fact]
    public async Task People_may_be_cleared_only_where_B04_allows_and_a_replacement_reaches_every_role()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var carol = db.AddPerson("Carol");
        var carolsTask = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), new TaskContentDraft("Carol designs", null, carol, null, [], null, CustomValue.NoValues)), Ct)).Value;
        var document = await ConfigurationAsync(db);
        document = document with
        {
            Lookups = document.Lookups with { People = [.. document.Lookups.People.Where(p => p.Id != carol.Value && p.Id != fixture.Bob.Value)] },
        };
        var carolKey = ConfigurationRemovalKey.ForLookup(LookupKind.Person, carol.Value);
        var bobKey = ConfigurationRemovalKey.ForLookup(LookupKind.Person, fixture.Bob.Value);

        var both = await PreviewAsync(db, document, TransferContent.ConfigurationOnly, new() { [carolKey] = null, [bobKey] = null });

        both.Removals.Single(r => r.Key == carolKey).Should().Match<ConfigurationRemoval>(r => r.CanClear && r.IsResolved);
        both.Removals.Single(r => r.Key == bobKey).Should().Match<ConfigurationRemoval>(r => !r.CanClear && !r.IsResolved, "Bob owns a CR, which can only be replaced");
        both.CanApply.Should().BeFalse();

        await Transfers.ApplyAsync(db, await PreviewAsync(db, document, TransferContent.ConfigurationOnly, new() { [carolKey] = null, [bobKey] = fixture.Alice.Value }));

        var after = await db.SnapshotAsync();
        after.Tasks.Single(t => t.Id == carolsTask).DesignerId.Should().BeNull();
        after.ChangeRequests.Single(c => c.Id == fixture.OtherCr).OwnerId.Should().Be(fixture.Alice);
        after.ReviewItems.Single(r => r.Id == fixture.Review).Content.DeveloperId.Should().Be(fixture.Alice);
        after.Tasks.Single(t => t.Id == fixture.LinkedOwnTitle).DeveloperId.Should().Be(fixture.Alice);
        after.Workflow.People.Select(p => p.Name.Display).Should().BeEquivalentTo(["Alice"]);
    }

    [Fact]
    public async Task Fields_missing_from_the_file_are_deactivated_and_an_in_use_removed_option_is_remapped()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var document = await ConfigurationAsync(db);
        var medium = Guid.NewGuid();
        var risk = document.FieldDefinitions.Single(f => f.Id == fixture.Risk.Value);
        var high = risk.Options.Single().Id;
        document = document with
        {
            FieldDefinitions = [risk with { Options = [new TransferFieldOption(medium, "Medium", 0), new TransferFieldOption(Guid.NewGuid(), "Low", 1)] }],
        };
        var key = ConfigurationRemovalKey.ForOption(fixture.Risk.Value, high);

        var unresolved = await PreviewAsync(db, document, TransferContent.ConfigurationOnly);
        unresolved.Removals.Should().ContainSingle(r => r.Key == key && r.CanClear && r.FieldLabel == "risk");
        unresolved.Findings.Should().Contain(f => f.Code == ImportFindingCodes.FieldDeactivated && f.Detail == "estimate");

        await Transfers.ApplyAsync(db, await PreviewAsync(db, document, TransferContent.ConfigurationOnly, new() { [key] = medium }));

        var after = await db.SnapshotAsync();
        after.Workflow.Fields.Single(f => f.Id == fixture.Estimate).IsActive.Should().BeFalse("a field is deactivated, never deleted (B-11)");
        var task = after.Tasks.Single(t => t.Id == fixture.LinkedOwnTitle);
        task.CustomValues[fixture.Estimate].Should().Be(new NumberCustomValue(3.25m), "the values of a deactivated field are kept");
        task.CustomValues[fixture.Risk].Should().Be(new LookupCustomValue(new FieldOptionId(medium)));
    }

    [Fact]
    public async Task Both_replaces_the_configuration_then_imports_the_data_in_one_step()
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var path = _folder.File("both.json");
        await ExportAsync(source, TransferFileFormat.Json, TransferContent.Both, path);
        var document = await Transfers.ReadAsync(path);
        await using var target = await CreateAsync();
        var legacy = Guid.NewGuid();
        Succeeded(await new AddLookup(target.Coordinator).ExecuteAsync(new AddLookupCommand(OperationId.New(), LookupKind.TaskStatus, legacy, "Legacy"), Ct));
        var oldTask = await target.AddTaskAsync("Old work", new TaskStatusId(legacy));
        var blocked = await target.TaskStatusAsync("Blocked");
        var key = ConfigurationRemovalKey.ForLookup(LookupKind.TaskStatus, legacy);

        var unresolved = await PreviewAsync(target, document, TransferContent.Both);
        unresolved.Removals.Should().ContainSingle(r => r.Key == key);
        unresolved.Removals[0].Choices.Should().Contain(c => c.Id == blocked.Id.Value, "the file's values match the target's by name and keep its IDs");

        var plan = await PreviewAsync(target, document, TransferContent.Both, new() { [key] = blocked.Id.Value });
        await Transfers.ApplyAsync(target, plan);

        var after = await target.SnapshotAsync();
        after.Tasks.Single(t => t.Id == oldTask).StatusId.Should().Be(blocked.Id);
        after.Tasks.Should().HaveCount(document.Tasks.Count + 1);
        after.Workflow.FindTaskStatus(new TaskStatusId(legacy)).Should().BeNull();
        after.Workflow.People.Select(p => p.Name.Display).Should().BeEquivalentTo(["Alice", "Bob"]);
    }

    [Fact]
    public async Task Both_into_a_dataset_without_removals_creates_the_new_values_before_the_records_that_use_them()
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var path = _folder.File("both.json");
        await ExportAsync(source, TransferFileFormat.Json, TransferContent.Both, path);
        await using var target = await CreateAsync();

        var plan = await PreviewAsync(target, await Transfers.ReadAsync(path), TransferContent.Both);
        await Transfers.ApplyAsync(target, plan);

        plan.Removals.Should().BeEmpty();
        TransferFixture.Shape(await target.SnapshotAsync()).Should().BeEquivalentTo(
            TransferFixture.Shape(await source.SnapshotAsync()), o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Both_with_merge_updates_a_record_that_the_replace_also_remaps()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var blocked = await db.TaskStatusAsync("Blocked");
        var review = await db.TaskStatusAsync("Review");
        var path = _folder.File("both.json");
        await ExportAsync(db, TransferFileFormat.Json, TransferContent.Both, path);
        var document = await Transfers.ReadAsync(path);
        document = document with
        {
            Lookups = document.Lookups with { TaskStatuses = [.. document.Lookups.TaskStatuses.Where(s => s.Id != blocked.Id.Value)] },
            Tasks = [.. document.Tasks.Select(t => t.Id == fixture.Unlinked.Value ? t with { OwnTitle = "Merged title", StatusId = review.Id.Value } : t)],
            SubtaskCards = [.. document.SubtaskCards.Select(c => c.StatusId == blocked.Id.Value ? c with { StatusId = review.Id.Value } : c)],
        };
        var key = ConfigurationRemovalKey.ForLookup(LookupKind.TaskStatus, blocked.Id.Value);
        var before = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.Unlinked);

        var plan = await PreviewAsync(db, document, TransferContent.Both, new() { [key] = (await db.TaskStatusAsync("In Progress")).Id.Value }, ImportMode.Merge);
        plan.Replacement!.Tasks.Should().Contain(t => t.Record.Id == fixture.Unlinked);
        plan.Tasks.Should().Contain(t => t.Record.Id == fixture.Unlinked);
        await Transfers.ApplyAsync(db, plan);

        var after = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.Unlinked);
        after.OwnTitle!.Value.Should().Be("Merged title");
        after.StatusId.Should().Be(review.Id, "the merge applies after the remap");
        after.Revision.Should().BeGreaterThan(before.Revision);
    }

    [Fact]
    public async Task A_remapped_record_edited_after_the_preview_makes_the_whole_import_stale()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var blocked = await db.TaskStatusAsync("Blocked");
        var document = await ConfigurationAsync(db);
        document = document with { Lookups = document.Lookups with { TaskStatuses = [.. document.Lookups.TaskStatuses.Where(s => s.Id != blocked.Id.Value)] } };
        var key = ConfigurationRemovalKey.ForLookup(LookupKind.TaskStatus, blocked.Id.Value);
        var plan = await PreviewAsync(db, document, TransferContent.ConfigurationOnly, new() { [key] = (await db.TaskStatusAsync("Review")).Id.Value });
        db.Execute("UPDATE tasks SET revision = revision + 1 WHERE id = $id;", ("$id", fixture.Unlinked.Value));

        var result = await Transfers.Importer(db).ApplyAsync(plan, OperationId.New(), Ct);

        result.Error.Should().BeOfType<ImportPlanStaleError>();
        (await db.SnapshotAsync()).Workflow.FindTaskStatus(blocked.Id).Should().NotBeNull("nothing is written");
    }

    [Fact]
    public async Task A_removed_option_that_starts_being_used_after_the_preview_makes_the_whole_import_stale()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        var level = db.AddField("level", FieldKind.Lookup, $$"""[{"Id":"{{keep}}","Label":"One","Order":0},{"Id":"{{drop}}","Label":"Two","Order":1}]""");
        var document = await ConfigurationAsync(db);
        document = document with
        {
            FieldDefinitions = [.. document.FieldDefinitions.Select(f => f.Id == level.Value ? f with { Options = [.. f.Options.Where(o => o.Id == keep)] } : f)],
        };
        var plan = await PreviewAsync(db, document, TransferContent.ConfigurationOnly);
        plan.Removals.Should().BeEmpty("the option is unused at preview time");
        db.Execute("UPDATE tasks SET custom_values_json = $v WHERE id = $id;",
            ("$v", StorageJson.WriteCustomValues(new Dictionary<FieldId, CustomValue> { [level] = new LookupCustomValue(new FieldOptionId(drop)) })), ("$id", fixture.Unlinked.Value));

        var result = await Transfers.Importer(db).ApplyAsync(plan, OperationId.New(), Ct);

        result.Error.Should().BeOfType<ImportPlanStaleError>();
        (await db.SnapshotAsync()).Workflow.Fields.Single(f => f.Id == level).Options.Should().HaveCount(2, "nothing is written");
    }

    [Fact]
    public async Task A_record_that_starts_using_a_removed_value_after_the_preview_makes_the_whole_import_stale()
    {
        await using var db = await CreateAsync();
        var snapshot = await db.SnapshotAsync();
        var unused = UnusedTaskStatus(snapshot);
        var document = await ConfigurationAsync(db);
        document = document with { Lookups = document.Lookups with { TaskStatuses = [.. document.Lookups.TaskStatuses.Where(s => s.Id != unused.Id.Value)] } };
        var plan = await PreviewAsync(db, document, TransferContent.ConfigurationOnly);
        await db.AddTaskAsync("Started meanwhile", unused.Id);

        var result = await Transfers.Importer(db).ApplyAsync(plan, OperationId.New(), Ct);

        result.Error.Should().BeOfType<ImportPlanStaleError>();
        (await db.SnapshotAsync()).Workflow.FindTaskStatus(unused.Id).Should().NotBeNull("nothing is written");
    }

    [Theory]
    [InlineData(TransferFileFormat.Json, "round.json")]
    [InlineData(TransferFileFormat.Xlsx, "round.xlsx")]
    [InlineData(TransferFileFormat.CsvBundle, "round.zip")]
    public async Task A_configuration_only_file_reproduces_the_configuration_in_an_empty_dataset(TransferFileFormat format, string name)
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var path = _folder.File(name);
        await ExportAsync(source, format, TransferContent.ConfigurationOnly, path);
        await using var target = await CreateAsync();

        await Transfers.ApplyAsync(target, await PreviewAsync(target, await Transfers.ReadAsync(path), TransferContent.ConfigurationOnly));

        var expected = (await source.SnapshotAsync()).Workflow;
        var actual = await target.SnapshotAsync();
        actual.Tasks.Should().BeEmpty();
        Names(actual.Workflow).Should().BeEquivalentTo(Names(expected), o => o.WithStrictOrdering());
    }

    private static object Names(WorkflowSnapshot w)
    {
        string Status(Guid id) => w.TaskStatuses.FirstOrDefault(s => s.Id.Value == id)?.Name.Display ?? w.CrStatuses.First(s => s.Id.Value == id).Name.Display;
        return new
        {
            TaskStatuses = w.TaskStatuses.OrderBy(s => s.Order).Select(s => (s.Name.Display, s.IsCompleted, s.SetsStartDate, s.IsBacklog)).ToList(),
            CrStatuses = w.CrStatuses.OrderBy(s => s.Order).Select(s => (s.Name.Display, s.IsCompleted, s.IsBacklog)).ToList(),
            Priorities = w.Priorities.OrderBy(p => p.Order).Select(p => p.Name.Display).ToList(),
            Squads = w.Squads.OrderBy(s => s.Order).Select(s => s.Name.Display).ToList(),
            People = w.People.Select(p => p.Name.Display).Order().ToList(),
            Tags = w.Tags.Select(t => t.Name.Display).Order().ToList(),
            Fields = w.Fields.OrderBy(f => f.Key.Normalized).Select(f => (f.Key.Display, f.Kind, f.IsActive, string.Join(",", f.Options.Select(o => o.Label.Display)))).ToList(),
            Defaults = (Status(w.Defaults.DefaultTaskStatusId.Value), Status(w.Defaults.DefaultCrStatusId.Value), w.FindPriority(w.Defaults.DefaultPriorityId)!.Name.Display,
                Status(w.Defaults.CalendarSchedulingStatusId.Value), Status(w.Defaults.CrBacklogStatusId.Value)),
        };
    }

    private static TaskStatusDefinition UnusedTaskStatus(ReadSnapshot snapshot)
    {
        var used = snapshot.Tasks.Select(t => t.StatusId).Concat(snapshot.SubtaskCards.Select(c => c.StatusId)).Append(snapshot.Workflow.Defaults.DefaultTaskStatusId).ToHashSet();
        return snapshot.Workflow.TaskStatuses.First(s => !used.Contains(s.Id));
    }

    private static async Task<ExportReport> ExportAsync(TestDataset db, TransferFileFormat format, TransferContent content, string path)
    {
        var result = await Transfers.Exporter(db).ExportAsync(EntireDatasetScope.Instance, format, null, path, Ct, content);
        result.Error.Should().BeNull();
        return result.Value;
    }

    private async Task<TransferDocument> ConfigurationAsync(TestDataset db)
    {
        var path = _folder.File(Guid.NewGuid().ToString("N") + ".json");
        await ExportAsync(db, TransferFileFormat.Json, TransferContent.ConfigurationOnly, path);
        return await Transfers.ReadAsync(path);
    }

    private static async Task<ImportPlan> PreviewAsync(
        TestDataset db,
        TransferDocument document,
        TransferContent content,
        Dictionary<ConfigurationRemovalKey, Guid?>? replacements = null,
        ImportMode mode = ImportMode.CreateOnly)
    {
        var result = await Transfers.Importer(db).PreviewAsync(document, mode, Ct, options: new ImportOptions(content, replacements));
        result.Error.Should().BeNull();
        return result.Value;
    }
}
