using System.Text.Json;
using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Application.Transfer.External;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Transfer;

/// <summary>
/// ADR 0027: external columns mapped to Created, Checklist items, or a custom field (existing or created by the import),
/// and the keep-or-discard choice for unmapped columns. Drafts are planned with <see cref="ImportPlanner"/>, as the wizard does.
/// </summary>
[Trait("Req", "B-13")]
[Trait("Adr", "0027")]
public sealed class ExternalMappingTargetsTests
{
    private const string Sheet = "sheet";
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo PlusThree = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");

    private static readonly FieldDefinition Risk = FieldDefinition.Create(new FieldId(Guid.NewGuid()), FieldKey.Create("risk").Value, FieldTarget.Task, LookupName.Create("Risk").Value,
        FieldKind.Lookup, false, null, 0, [new FieldOption(new FieldOptionId(Guid.NewGuid()), LookupName.Create("Low").Value, 0), new FieldOption(new FieldOptionId(Guid.NewGuid()), LookupName.Create("High").Value, 1)]).Value;

    private static readonly FieldDefinition Points = FieldDefinition.Create(new FieldId(Guid.NewGuid()), FieldKey.Create("points").Value, FieldTarget.Task, LookupName.Create("Points").Value,
        FieldKind.Number, false, null, 1, []).Value;

    private static readonly WorkflowSnapshot WithFields = SeedWorkflow with { Fields = [Risk, Points] };

    // ----- Aliases -----
    [Theory]
    [InlineData("created")]
    [InlineData("Created At")]
    [InlineData("createdAt")]
    [InlineData("created_at")]
    [InlineData("Created on")]
    [InlineData("Creation date")]
    public void A_created_header_proposes_created_for_every_entity(string header)
    {
        foreach (var entity in new[] { ExternalEntity.Task, ExternalEntity.ChangeRequest, ExternalEntity.Review, ExternalEntity.GeneralTodo })
        {
            ExternalFieldAliases.Propose(entity, [header]).Should().ContainSingle().Which.Should().Be((ExternalField.Created, false), entity.ToString());
        }
    }

    [Theory]
    [InlineData("subtasks")]
    [InlineData("Sub-tasks")]
    [InlineData("Checklist")]
    [InlineData("checklist items")]
    public void A_checklist_header_proposes_checklist_items_for_tasks_only(string header)
    {
        ExternalFieldAliases.Propose(ExternalEntity.Task, [header]).Should().Equal((ExternalField.ChecklistItems, false));
        ExternalFieldAliases.Propose(ExternalEntity.ChangeRequest, [header]).Should().Equal((ExternalField.Ignore, false));
    }

    // ----- Created -----
    [Theory]
    [InlineData("2025-01-02T03:04:05Z", "2025-01-02T03:04:05+00:00")]
    [InlineData("2025-01-02T03:04:05.250Z", "2025-01-02T03:04:05.250+00:00")]
    [InlineData("2025-01-02T03:04:05+02:00", "2025-01-02T03:04:05+02:00")]
    [InlineData("2025-01-02T03:04:05", "2025-01-02T03:04:05+03:00")]
    [InlineData("2025-01-02 03:04", "2025-01-02T03:04:00+03:00")]
    [InlineData(" 2025-01-02 ", "2025-01-02T00:00:00+03:00")]
    public void Created_accepts_iso_date_times_with_or_without_an_offset_and_iso_dates(string text, string expected)
    {
        ExternalValues.TryParseCreated(text, PlusThree, out var created).Should().BeTrue();

        created.Should().Be(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("01/02/2025")]
    [InlineData("yesterday")]
    [InlineData("2025-13-01")]
    [InlineData("2025-01-02T25:00")]
    [InlineData("45000")]
    public void Created_rejects_anything_else(string text) =>
        ExternalValues.TryParseCreated(text, PlusThree, out _).Should().BeFalse();

    [Theory]
    [InlineData("0001-01-01", 3)]
    [InlineData("0001-01-01T02:59:59", 3)]
    [InlineData("9999-12-31T23:00", -5)]
    [InlineData("9999-12-31T23:30", -1)]
    public void A_created_value_whose_utc_instant_is_out_of_range_is_invalid_not_an_exception(string text, int offsetHours)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test" + offsetHours, TimeSpan.FromHours(offsetHours), "Test", "Test");

        ExternalValues.TryParseCreated(text, zone, out _).Should().BeFalse();
        var draft = ExternalImportPlanner.Build(new ExternalSource([Table(["Title", "Created"], ["One", text])], []),
            Options(Mapping(ExternalField.Title, ExternalField.Created)) with { TimeZone = zone }, SeedWorkflow, new Dictionary<string, Guid>(), new Dictionary<(string, string), Guid>());
        draft.Findings.Should().ContainSingle(f => f.Severity == ImportSeverity.Error).Which.Code.Should().Be(ImportFindingCodes.InvalidCreated);
    }

    [Theory]
    [InlineData("2025-03-30T02:30", "2025-03-30T02:30:00+01:00")]
    [InlineData("2025-10-26T02:30", "2025-10-26T02:30:00+01:00")]
    [InlineData("2025-07-01T12:00", "2025-07-01T12:00:00+02:00")]
    [InlineData("2025-01-15T12:00", "2025-01-15T12:00:00+01:00")]
    public void A_local_time_in_a_daylight_saving_gap_or_overlap_takes_the_standard_offset(string text, string expected)
    {
        // Central European rules: +01:00, and +02:00 from the last Sunday of March 02:00 to the last Sunday of October 03:00.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test CET", TimeSpan.FromHours(1), "Test CET", "Test CET", "Test CEST", [rule]);

        ExternalValues.TryParseCreated(text, zone, out var created).Should().BeTrue();

        (created, created.Offset).Should().Be((DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture).Offset));
    }

    [Fact]
    public void A_created_column_sets_the_created_time_and_the_import_time_as_updated()
    {
        var draft = Build(Table(["Title", "Created"], ["Card freeze", "2025-05-01T10:00:00Z"]), Mapping(ExternalField.Title, ExternalField.Created));

        var plan = Plan(draft, Snapshot());

        var task = plan.Tasks.Should().ContainSingle().Subject.Record;
        (task.CreatedAtUtc, task.UpdatedAtUtc).Should().Be((new DateTimeOffset(2025, 5, 1, 10, 0, 0, TimeSpan.Zero), Now));
        draft.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error);
    }

    [Theory]
    [InlineData("01/05/2025", ImportFindingCodes.InvalidCreated)]
    [InlineData("2026-10-22T09:00:01Z", ImportFindingCodes.CreatedInFuture)]
    [InlineData("2027-01-01", ImportFindingCodes.CreatedInFuture)]
    public void An_unreadable_or_future_created_value_is_an_error(string value, string code)
    {
        foreach (var entity in new[] { ExternalEntity.Task, ExternalEntity.ChangeRequest, ExternalEntity.Review, ExternalEntity.GeneralTodo })
        {
            var columns = entity == ExternalEntity.ChangeRequest ? new[] { "Number", "Created" } : ["Title", "Created"];
            var fields = entity == ExternalEntity.ChangeRequest ? new[] { ExternalField.CrNumber, ExternalField.Created } : [ExternalField.Title, ExternalField.Created];
            var draft = Build(Table(columns, ["CR-1", value]), new ExternalTableMapping(Sheet, entity, fields));

            draft.Findings.Should().ContainSingle(f => f.Severity == ImportSeverity.Error && f.Code == code, entity.ToString())
                .Which.Detail.Should().Be(value);
        }
    }

    [Fact]
    public void In_merge_an_existing_record_keeps_its_own_created_time()
    {
        var table = Table(["Key", "Title"], ["T-1", "Card freeze"]);
        var first = Build(table, Mapping(ExternalField.SourceId, ExternalField.Title));
        var existing = Plan(first, Snapshot()).Tasks.Single().Record;
        var ledger = Ledger(first);

        var again = Build(Table(["Key", "Title", "Created"], ["T-1", "Card freeze, renamed", "2020-01-01"]),
            Mapping(ExternalField.SourceId, ExternalField.Title, ExternalField.Created), ImportMode.Merge, ledger);
        var plan = Plan(again, Snapshot(existing), ImportMode.Merge, Now.AddDays(1));

        var merged = plan.Tasks.Should().ContainSingle().Subject.Record;
        merged.OwnTitle!.Value.Should().Be("Card freeze, renamed");
        merged.CreatedAtUtc.Should().Be(existing.CreatedAtUtc, "T-10: an import never rewrites a record's created time");
    }

    // ----- Checklist items -----
    [Fact]
    public void A_checklist_cell_gives_one_item_per_non_empty_line_with_its_checkbox_state()
    {
        var items = ExternalValues.ParseChecklist(new ExternalValue("[ ] Draft\r\n- [x] Review\r\n\r\n* [X] Ship\n  plain line  \n- bulleted\n* starred\n-dash-led\n   \n[x]"));

        items.Should().Equal(
            new ExternalChecklistItem("Draft", false),
            new ExternalChecklistItem("Review", true),
            new ExternalChecklistItem("Ship", true),
            new ExternalChecklistItem("plain line", false),
            new ExternalChecklistItem("bulleted", false),
            new ExternalChecklistItem("starred", false),
            new ExternalChecklistItem("-dash-led", false));
    }

    [Fact]
    public void A_list_value_gives_one_checklist_item_per_entry()
    {
        ExternalValues.ParseChecklist(new ExternalValue(null, ["[x] One", "Two"])).Should().Equal(
            new ExternalChecklistItem("One", true), new ExternalChecklistItem("Two", false));
    }

    [Fact]
    public void In_merge_a_supplied_checklist_replaces_the_task_checklist_and_an_empty_cell_clears_it()
    {
        var first = Build(Table(["Key", "Title", "Subtasks"], ["T-1", "Card freeze", "One\n[x] Two"]), Mapping(ExternalField.SourceId, ExternalField.Title, ExternalField.ChecklistItems));
        var created = Plan(first, Snapshot()).Tasks.Single().Record;
        created.ChecklistItems.Select(i => (i.Text.Value, i.IsCompleted)).Should().Equal(("One", false), ("Two", true));

        var replaced = Plan(
            Build(Table(["Key", "Title", "Subtasks"], ["T-1", "Card freeze", "[x] Three"]), Mapping(ExternalField.SourceId, ExternalField.Title, ExternalField.ChecklistItems), ImportMode.Merge, Ledger(first)),
            Snapshot(created), ImportMode.Merge).Tasks.Single().Record;
        var cleared = Plan(
            Build(Table(["Key", "Title", "Subtasks"], ["T-1", "Card freeze", ""]), Mapping(ExternalField.SourceId, ExternalField.Title, ExternalField.ChecklistItems), ImportMode.Merge, Ledger(first)),
            Snapshot(created), ImportMode.Merge).Tasks.Single().Record;
        var untouched = Plan(
            Build(Table(["Key", "Title"], ["T-1", "Card freeze, renamed"]), Mapping(ExternalField.SourceId, ExternalField.Title), ImportMode.Merge, Ledger(first)),
            Snapshot(created), ImportMode.Merge).Tasks.Single().Record;

        replaced.ChecklistItems.Select(i => (i.Text.Value, i.IsCompleted)).Should().Equal(("Three", true));
        cleared.ChecklistItems.Should().BeEmpty();
        untouched.ChecklistItems.Should().Equal(created.ChecklistItems, "a source without the column leaves the checklist alone");
    }

    // ----- Custom fields -----
    [Theory]
    [InlineData(FieldKind.Date, "2026-01-02", "", "2025-12-31")]
    [InlineData(FieldKind.Number, "12", "-3.5", "1e3")]
    [InlineData(FieldKind.Url, "https://example.com/a", "http://example.com", "")]
    [InlineData(FieldKind.Text, "", "", "")]
    [InlineData(FieldKind.Text, "00123", "4", "5")]
    [InlineData(FieldKind.Number, "0", "0.5", "-0.25")]
    [InlineData(FieldKind.Text, "2026-01-02", "12", "plain")]
    [InlineData(FieldKind.Text, "https://example.com", "ftp://example.com", "")]
    public void The_kind_of_a_new_field_is_suggested_from_its_values(FieldKind expected, string a, string b, string c) =>
        ExternalValues.SuggestKind([new ExternalValue(a), new ExternalValue(b), null, new ExternalValue(c)]).Should().Be(expected);

    [Fact]
    public void A_column_maps_to_an_existing_custom_field_and_a_value_invalid_for_its_kind_is_an_error()
    {
        var table = Table(["Title", "Risk", "Points"], ["One", "high", "5"], ["Two", "Medium", "five"]);
        var mapping = Mapping(ExternalField.Title, ExternalField.CustomField, ExternalField.CustomField) with
        {
            CustomFields = new Dictionary<int, ExternalCustomFieldChoice> { [1] = ExternalCustomFieldChoice.Existing(Risk.Id.Value), [2] = ExternalCustomFieldChoice.Existing(Points.Id.Value) },
        };

        var draft = Build(table, mapping, workflow: WithFields);

        draft.Document.FieldDefinitions.Should().BeEmpty("existing fields are used, not created");
        var one = draft.Document.Tasks[0].CustomValues!;
        one[Risk.Id.Value.ToString("D")].Should().Be(new TransferValue("Lookup", Option: Risk.Options[1].Id.Value), "options match by normalized label");
        one[Points.Id.Value.ToString("D")].Should().Be(new TransferValue("Number", Number: 5m));
        draft.Findings.Where(f => f.Code == ImportFindingCodes.InvalidCustomValue).Select(f => (f.Severity, f.RecordKey, f.Field, f.Detail))
            .Should().BeEquivalentTo([(ImportSeverity.Error, "sheet#2", "Risk", "Medium"), (ImportSeverity.Error, "sheet#2", "Points", "five")], "values are never coerced");
        var plan = Plan(draft, Snapshot(workflow: WithFields));
        plan.Tasks.Single(t => t.Record.OwnTitle!.Value == "One").Record.CustomValues[Points.Id].Should().Be(new NumberCustomValue(5m));
    }

    [Fact]
    public void New_text_number_date_and_lookup_fields_are_created_in_the_same_plan_and_listed_in_the_preview()
    {
        var table = Table(["Title", "Team note", "Points", "Due on", "Risk level"], ["One", "Keep it", "5", "2026-12-01", "High"], ["Two", "", "1.5", "2026-12-02", "low"], ["Three", "x", "", "", "Low"]);
        var mapping = Mapping(ExternalField.Title, ExternalField.CustomField, ExternalField.CustomField, ExternalField.CustomField, ExternalField.CustomField) with
        {
            CustomFields = new Dictionary<int, ExternalCustomFieldChoice>
            {
                [1] = ExternalCustomFieldChoice.New("Team note", FieldKind.Text),
                [2] = ExternalCustomFieldChoice.New("Points", FieldKind.Number),
                [3] = ExternalCustomFieldChoice.New("Due on", FieldKind.Date),
                [4] = ExternalCustomFieldChoice.New("Risk level", FieldKind.Lookup),
            },
        };

        var draft = Build(table, mapping);
        var plan = Plan(draft, Snapshot());

        draft.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error);
        draft.Document.FieldDefinitions.Select(f => (f.Key, f.Label, f.Kind, f.IsRequired, f.IsActive)).Should().Equal(
            ("team_note", "Team note", "Text", false, true), ("points", "Points", "Number", false, true), ("due_on", "Due on", "Date", false, true), ("risk_level", "Risk level", "Lookup", false, true));
        draft.Document.FieldDefinitions[3].Options.Select(o => o.Label).Should().Equal(["High", "low"], "one option per distinct value, in source order");
        plan.Findings.Where(f => f.Code == ImportFindingCodes.FieldCreated).Should().HaveCount(4, "the preview lists the new fields");
        var fields = plan.NewWorkflow!.Fields.ToDictionary(f => f.Key.Display);
        var one = plan.Tasks.Single(t => t.Record.OwnTitle!.Value == "One").Record.CustomValues;
        one[fields["team_note"].Id].Should().Be(new TextCustomValue("Keep it"));
        one[fields["points"].Id].Should().Be(new NumberCustomValue(5m));
        one[fields["due_on"].Id].Should().Be(new DateCustomValue(new DateOnly(2026, 12, 1)));
        one[fields["risk_level"].Id].Should().Be(new LookupCustomValue(fields["risk_level"].Options[0].Id));
        plan.Tasks.Single(t => t.Record.OwnTitle!.Value == "Three").Record.CustomValues.Keys.Should().BeEquivalentTo([fields["team_note"].Id, fields["risk_level"].Id], "empty cells have no value");
        plan.CanApply.Should().BeTrue();
    }

    [Fact]
    public void A_value_invalid_for_a_new_fields_kind_is_an_error()
    {
        var mapping = Mapping(ExternalField.Title, ExternalField.CustomField, ExternalField.CustomField) with
        {
            CustomFields = new Dictionary<int, ExternalCustomFieldChoice>
            {
                [1] = ExternalCustomFieldChoice.New("Points", FieldKind.Number),
                [2] = ExternalCustomFieldChoice.New("Note", FieldKind.Text),
            },
        };

        var draft = Build(Table(["Title", "Points", "Note"], ["One", "1,000", "two\nlines"]), mapping);

        draft.Findings.Where(f => f.Code == ImportFindingCodes.InvalidCustomValue).Select(f => (f.Severity, f.Field)).Should().BeEquivalentTo(
            [(ImportSeverity.Error, "Points"), (ImportSeverity.Error, "Note")]);
    }

    [Theory]
    [InlineData("Status")]
    [InlineData("Points")]
    [InlineData("123")]
    public void A_new_field_whose_proposed_key_is_reserved_taken_or_empty_gets_a_generated_key_and_keeps_its_label(string label)
    {
        var mapping = Mapping(ExternalField.Title, ExternalField.CustomField) with
        {
            CustomFields = new Dictionary<int, ExternalCustomFieldChoice> { [1] = ExternalCustomFieldChoice.New(label, FieldKind.Text) },
        };

        var draft = Build(Table(["Title", "Column"], ["One", "x"]), mapping, workflow: WithFields);

        draft.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error);
        draft.Document.FieldDefinitions.Should().ContainSingle().Which.Should().Match<TransferFieldDefinition>(f => f.Key == "field" && f.Label == label);
        Plan(draft, Snapshot(workflow: WithFields)).CanApply.Should().BeTrue();
    }

    [Fact]
    public void Generated_keys_are_unique_per_target_among_existing_and_new_fields()
    {
        var existingField = FieldDefinition.Create(new FieldId(Guid.NewGuid()), FieldKey.Create("field").Value, FieldTarget.Task, LookupName.Create("Field").Value,
            FieldKind.Text, false, null, 2, []).Value.Deactivate();
        var mapping = Mapping(ExternalField.Title, ExternalField.CustomField, ExternalField.CustomField) with
        {
            CustomFields = new Dictionary<int, ExternalCustomFieldChoice>
            {
                [1] = ExternalCustomFieldChoice.New("1", FieldKind.Text),
                [2] = ExternalCustomFieldChoice.New("2", FieldKind.Number),
            },
        };

        var draft = Build(Table(["Title", "A", "B"], ["One", "x", "2"]), mapping, workflow: SeedWorkflow with { Fields = [existingField] });

        draft.Document.FieldDefinitions.Select(f => (f.Key, f.Label)).Should().Equal(("field_2", "1"), ("field_3", "2"));
    }

    [Fact]
    public void Two_tables_that_create_the_same_field_share_it_and_merge_lookup_options()
    {
        var first = new ExternalTable("a", ExternalSourceKind.Xlsx, ExternalEntity.Task, ["Title", "Risk"], [Row("a", 1, "One", "High"), Row("a", 2, "Two", "Low")]);
        var second = new ExternalTable("b", ExternalSourceKind.Xlsx, ExternalEntity.Task, ["Title", "Risk"], [Row("b", 1, "Three", "low"), Row("b", 2, "Four", "Medium")]);
        var risk = new Dictionary<int, ExternalCustomFieldChoice> { [1] = ExternalCustomFieldChoice.New("Risk", FieldKind.Lookup) };
        var options = Options(new ExternalTableMapping("a", ExternalEntity.Task, [ExternalField.Title, ExternalField.CustomField]) { CustomFields = risk },
            new ExternalTableMapping("b", ExternalEntity.Task, [ExternalField.Title, ExternalField.CustomField]) { CustomFields = risk });

        var draft = ExternalImportPlanner.Build(new ExternalSource([first, second], []), options, SeedWorkflow, new Dictionary<string, Guid>(), new Dictionary<(string, string), Guid>());
        var plan = Plan(draft, Snapshot());

        var field = draft.Document.FieldDefinitions.Should().ContainSingle().Subject;
        field.Options.Select(o => o.Label).Should().Equal("High", "Low", "Medium");
        draft.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error);
        var low = new LookupCustomValue(new FieldOptionId(field.Options[1].Id));
        plan.Tasks.Select(t => t.Record).Where(t => t.OwnTitle!.Value is "Two" or "Three").Should().OnlyContain(t => t.CustomValues[new FieldId(field.Id)] == low);
    }

    [Fact]
    public void Two_tables_that_create_a_field_with_one_label_and_different_kinds_is_an_error()
    {
        var first = new ExternalTable("a", ExternalSourceKind.Xlsx, ExternalEntity.Task, ["Title", "Risk"], [Row("a", 1, "One", "High")]);
        var second = new ExternalTable("b", ExternalSourceKind.Xlsx, ExternalEntity.Task, ["Title", "Risk"], [Row("b", 1, "Two", "3")]);
        var options = Options(
            new ExternalTableMapping("a", ExternalEntity.Task, [ExternalField.Title, ExternalField.CustomField]) { CustomFields = new Dictionary<int, ExternalCustomFieldChoice> { [1] = ExternalCustomFieldChoice.New("Risk", FieldKind.Lookup) } },
            new ExternalTableMapping("b", ExternalEntity.Task, [ExternalField.Title, ExternalField.CustomField]) { CustomFields = new Dictionary<int, ExternalCustomFieldChoice> { [1] = ExternalCustomFieldChoice.New("risk", FieldKind.Number) } });

        var draft = ExternalImportPlanner.Build(new ExternalSource([first, second], []), options, SeedWorkflow, new Dictionary<string, Guid>(), new Dictionary<(string, string), Guid>());

        draft.Findings.Should().ContainSingle(f => f.Severity == ImportSeverity.Error).Which.Should().Be(
            new ImportFinding(ImportSeverity.Error, ImportEntity.Field, "b", ImportFindingCodes.FieldKindMismatch, "Kind", "risk"));
        draft.Document.FieldDefinitions.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ExternalImportPlanner.MaxNewLookupOptions + 1)]
    public void A_new_lookup_field_without_values_or_with_too_many_is_an_error_that_suggests_text(int distinct)
    {
        var rows = distinct == 0
            ? new[] { new[] { "One", " " }, ["Two", ""] }
            : [.. Enumerable.Range(0, distinct).Select(i => new[] { "T" + i, "Value " + i })];
        var mapping = Mapping(ExternalField.Title, ExternalField.CustomField) with
        {
            CustomFields = new Dictionary<int, ExternalCustomFieldChoice> { [1] = ExternalCustomFieldChoice.New("Risk", FieldKind.Lookup) },
        };

        var draft = Build(Table(["Title", "Risk"], rows), mapping);

        draft.Findings.Should().ContainSingle(f => f.Severity == ImportSeverity.Error).Which.Should().Be(
            new ImportFinding(ImportSeverity.Error, ImportEntity.Field, Sheet, ImportFindingCodes.LookupFieldValues, null, "Risk"));
        draft.Document.FieldDefinitions.Should().BeEmpty();
    }

    [Fact]
    public void A_new_lookup_field_takes_exactly_the_maximum_number_of_values()
    {
        var mapping = Mapping(ExternalField.Title, ExternalField.CustomField) with
        {
            CustomFields = new Dictionary<int, ExternalCustomFieldChoice> { [1] = ExternalCustomFieldChoice.New("Risk", FieldKind.Lookup) },
        };

        var draft = Build(Table(["Title", "Risk"], [.. Enumerable.Range(0, ExternalImportPlanner.MaxNewLookupOptions).Select(i => new[] { "T" + i, "Value " + i })]), mapping);

        draft.Document.FieldDefinitions.Single().Options.Should().HaveCount(ExternalImportPlanner.MaxNewLookupOptions);
    }

    [Fact]
    public void An_external_field_that_matches_a_turned_off_field_is_an_error_and_writes_no_value()
    {
        var off = FieldDefinition.Create(new FieldId(Guid.NewGuid()), FieldKey.Create("risk").Value, FieldTarget.Task, LookupName.Create("Risk").Value,
            FieldKind.Text, false, null, 0, []).Value.Deactivate();
        var fieldId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var document = new TransferDocument
        {
            FieldDefinitions = [new TransferFieldDefinition(fieldId, "risk", "Task", "Risk", "Text", false, null, 0, [], true)],
            Tasks = [new TransferTask
            {
                Id = taskId,
                OwnTitle = "One",
                CustomValues = new Dictionary<string, TransferValue> { [fieldId.ToString("D")] = new TransferValue("Text", Text: "x") },
                SuppliedFields = new HashSet<string> { "OwnTitle", ImportPlanner.CustomValueMember(fieldId) },
            }],
        };

        var plan = ImportPlanner.Plan(document, Snapshot(workflow: SeedWorkflow with { Fields = [off] }), ImportMode.CreateOnly,
            new ImportContext(new HashSet<Guid>(), "test", new Dictionary<Guid, (string, string)> { [taskId] = (ImportEntity.Task, "T-1") }), Now);

        plan.Findings.Should().Contain(new ImportFinding(ImportSeverity.Error, ImportEntity.Field, fieldId.ToString(), ImportFindingCodes.FieldInactive, null, "risk"));
        plan.CanApply.Should().BeFalse();
        plan.Tasks.Should().NotContain(t => t.Record.CustomValues.ContainsKey(off.Id));
    }

    [Fact]
    [Trait("Req", "T-10")]
    public void A_canonical_merge_that_does_not_supply_custom_values_keeps_the_stored_ones()
    {
        var first = Build(Table(["Key", "Title", "Points"], ["T-1", "One", "3"]),
            Mapping(ExternalField.SourceId, ExternalField.Title, ExternalField.CustomField) with
            {
                CustomFields = new Dictionary<int, ExternalCustomFieldChoice> { [2] = ExternalCustomFieldChoice.Existing(Points.Id.Value) },
            },
            workflow: WithFields);
        var existing = Plan(first, Snapshot(workflow: WithFields)).Tasks.Single().Record;
        var canonical = new TransferDocument
        {
            Tasks = [new TransferTask { Id = existing.Id.Value, OwnTitle = "One, renamed", SuppliedFields = new HashSet<string> { "OwnTitle" } }],
        };

        var plan = ImportPlanner.Plan(canonical, Snapshot(existing, WithFields), ImportMode.Merge, ImportContext.Empty, Now.AddDays(1));

        var merged = plan.Tasks.Should().ContainSingle().Subject.Record;
        merged.OwnTitle!.Value.Should().Be("One, renamed");
        merged.CustomValues.Should().BeEquivalentTo(new Dictionary<FieldId, CustomValue> { [Points.Id] = new NumberCustomValue(3m) });
    }

    [Fact]
    public void In_merge_a_checklist_list_with_non_text_entries_is_an_error_and_never_clears_the_checklist()
    {
        var first = Build(Table(["Key", "Title", "Subtasks"], ["T-1", "One", "Keep me"]), Mapping(ExternalField.SourceId, ExternalField.Title, ExternalField.ChecklistItems));
        var existing = Plan(first, Snapshot()).Tasks.Single().Record;
        var table = new ExternalTable(Sheet, ExternalSourceKind.Json, ExternalEntity.Task, ["Key", "Title", "Subtasks"],
            [new ExternalRow("sheet#1", 1, [new ExternalValue("T-1"), new ExternalValue("One, renamed"), new ExternalValue(null, [], HasNonTextItems: true)])]);

        var draft = Build(table, Mapping(ExternalField.SourceId, ExternalField.Title, ExternalField.ChecklistItems), ImportMode.Merge, Ledger(first));
        var plan = Plan(draft, Snapshot(existing), ImportMode.Merge);

        draft.Findings.Should().ContainSingle(f => f.Severity == ImportSeverity.Error).Which.Code.Should().Be(ImportFindingCodes.InvalidChecklist);
        draft.Document.Tasks.Single().SuppliedFields.Should().NotContain("ChecklistItems");
        plan.Tasks.Single().Record.ChecklistItems.Should().Equal(existing.ChecklistItems);
    }

    [Fact]
    public void Checklist_items_are_not_proposed_for_markdown_and_a_mapped_column_beside_a_body_checklist_is_an_error()
    {
        ExternalFieldAliases.Propose(ExternalEntity.Task, ["subtasks"], ExternalSourceKind.Markdown).Should().Equal((ExternalField.Ignore, false));
        var table = new ExternalTable(Sheet, ExternalSourceKind.Markdown, ExternalEntity.Task, ["title", "subtasks"],
            [new ExternalRow("one.md", 1, [new ExternalValue("One"), new ExternalValue(null, ["From front matter"])], [new ExternalChecklistItem("From body", true)])]);

        var draft = Build(table, Mapping(ExternalField.Title, ExternalField.ChecklistItems));

        draft.Findings.Should().ContainSingle(f => f.Severity == ImportSeverity.Error).Which.Should().Be(
            new ImportFinding(ImportSeverity.Error, ImportEntity.Task, "sheet#1", ImportFindingCodes.ChecklistConflict, "ChecklistItems"));
        draft.Document.Tasks.Single().ChecklistItems!.Select(i => i.Text).Should().Equal("From body");
    }

    [Fact]
    public void Unmapped_columns_of_reviews_and_todos_are_reported_as_not_stored_and_created_is_not_a_supplied_member()
    {
        var review = Build(Table(["Title", "Created", "Extra"], ["One", "2025-01-01", "x"]),
            new ExternalTableMapping(Sheet, ExternalEntity.Review, [ExternalField.Title, ExternalField.Created, ExternalField.Ignore]));
        var todo = Build(Table(["Title", "Created", "Extra"], ["One", "2025-01-01", "x"]),
            new ExternalTableMapping(Sheet, ExternalEntity.GeneralTodo, [ExternalField.Title, ExternalField.Created, ExternalField.Ignore]));

        foreach (var draft in new[] { review, todo })
        {
            draft.Findings.Should().ContainSingle(f => f.Entity == ImportEntity.File).Which.Should().Be(
                new ImportFinding(ImportSeverity.Info, ImportEntity.File, Sheet, ImportFindingCodes.ColumnNotStored, null, "Extra"));
        }

        review.Document.Reviews.Single().SuppliedFields.Should().NotContain("Created");
        review.Document.Reviews.Single().CreatedAtUtc.Should().NotBeNull();
        todo.Document.GeneralTodos.Single().SuppliedFields.Should().NotContain("Created");
    }

    [Fact]
    public void In_merge_a_custom_column_changes_only_its_field_and_an_empty_cell_clears_it()
    {
        var first = Build(Table(["Key", "Title", "Risk", "Points"], ["T-1", "One", "Low", "3"]),
            Mapping(ExternalField.SourceId, ExternalField.Title, ExternalField.CustomField, ExternalField.CustomField) with
            {
                CustomFields = new Dictionary<int, ExternalCustomFieldChoice> { [2] = ExternalCustomFieldChoice.Existing(Risk.Id.Value), [3] = ExternalCustomFieldChoice.Existing(Points.Id.Value) },
            },
            workflow: WithFields);
        var existing = Plan(first, Snapshot(workflow: WithFields)).Tasks.Single().Record;
        ExternalTableMapping PointsOnly() => Mapping(ExternalField.SourceId, ExternalField.Title, ExternalField.CustomField) with
        {
            CustomFields = new Dictionary<int, ExternalCustomFieldChoice> { [2] = ExternalCustomFieldChoice.Existing(Points.Id.Value) },
        };

        var updated = Plan(Build(Table(["Key", "Title", "Points"], ["T-1", "One", "8"]), PointsOnly(), ImportMode.Merge, Ledger(first), WithFields),
            Snapshot(existing, WithFields), ImportMode.Merge).Tasks.Single().Record;
        var cleared = Plan(Build(Table(["Key", "Title", "Points"], ["T-1", "One", ""]), PointsOnly(), ImportMode.Merge, Ledger(first), WithFields),
            Snapshot(existing, WithFields), ImportMode.Merge).Tasks.Single().Record;

        updated.CustomValues.Should().BeEquivalentTo(new Dictionary<FieldId, CustomValue> { [Risk.Id] = new LookupCustomValue(Risk.Options[0].Id), [Points.Id] = new NumberCustomValue(8m) });
        cleared.CustomValues.Should().BeEquivalentTo(new Dictionary<FieldId, CustomValue> { [Risk.Id] = new LookupCustomValue(Risk.Options[0].Id) });
    }

    [Fact]
    public void Custom_fields_are_not_targets_for_reviews_or_general_todos()
    {
        var mapping = new ExternalTableMapping(Sheet, ExternalEntity.Review, [ExternalField.Title, ExternalField.CustomField])
        {
            CustomFields = new Dictionary<int, ExternalCustomFieldChoice> { [1] = ExternalCustomFieldChoice.New("Note", FieldKind.Text) },
        };

        var draft = Build(Table(["Title", "Note"], ["One", "x"]), mapping);

        draft.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.Code == ImportFindingCodes.CustomFieldInvalid);
        draft.Document.FieldDefinitions.Should().BeEmpty();
    }

    // ----- Unmapped columns -----
    [Fact]
    public void An_unmapped_column_is_kept_hidden_by_default_and_a_discarded_one_is_stored_nowhere()
    {
        var table = Table(["Title", "Extra", "Secret"], ["One", "kept value", "s3cr3t-value"]);
        var mapping = Mapping(ExternalField.Title, ExternalField.Ignore, ExternalField.Ignore) with { DiscardedColumns = new HashSet<int> { 2 } };

        var draft = Build(table, mapping);
        var plan = Plan(draft, Snapshot());

        var task = plan.Tasks.Single().Record;
        task.Preserved.LegacyJson.Should().Be("""{"Extra":"kept value"}""");
        JsonSerializer.Serialize(draft.Document).Should().NotContain("s3cr3t-value").And.NotContain("Secret");
        draft.Findings.Where(f => f.Entity == ImportEntity.File).Select(f => (f.Code, f.Detail)).Should().BeEquivalentTo(
            [(ImportFindingCodes.UnknownColumn, "Extra"), (ImportFindingCodes.ColumnDiscarded, "Secret")]);
    }

    [Fact]
    public void Mapped_created_checklist_and_custom_columns_are_not_preserved_as_hidden_metadata()
    {
        var mapping = Mapping(ExternalField.Title, ExternalField.Created, ExternalField.ChecklistItems, ExternalField.CustomField) with
        {
            CustomFields = new Dictionary<int, ExternalCustomFieldChoice> { [3] = ExternalCustomFieldChoice.New("Note", FieldKind.Text) },
        };

        var draft = Build(Table(["Title", "Created", "Subtasks", "Note"], ["One", "2025-01-01", "a", "b"]), mapping);

        draft.Document.Tasks.Single().Preserved.Should().BeNull();
    }

    // ----- Helpers -----
    private static ExternalTable Table(string[] columns, params string[][] rows) =>
        new(Sheet, ExternalSourceKind.Xlsx, ExternalEntity.Task, columns,
            [.. rows.Select((r, i) => new ExternalRow(Sheet + "#" + (i + 1), i + 1, [.. r.Select(v => (ExternalValue?)new ExternalValue(v))]))]);

    private static ExternalTableMapping Mapping(params ExternalField[] fields) => new(Sheet, ExternalEntity.Task, fields);

    private static ExternalRow Row(string table, int number, params string[] values) =>
        new(table + "#" + number, number, [.. values.Select(v => (ExternalValue?)new ExternalValue(v))]);

    private static ExternalImportOptions Options(params ExternalTableMapping[] tables) =>
        new() { SourceNamespace = "test", Tables = tables, NowUtc = Now, TimeZone = PlusThree };

    private static ExternalImportDraft Build(
        ExternalTable table,
        ExternalTableMapping mapping,
        ImportMode mode = ImportMode.CreateOnly,
        IReadOnlyDictionary<(string, string), Guid>? ledger = null,
        WorkflowSnapshot? workflow = null)
    {
        var options = new ExternalImportOptions { SourceNamespace = "test", Mode = mode, Tables = [mapping], NowUtc = Now, TimeZone = PlusThree };
        return ExternalImportPlanner.Build(new ExternalSource([table], []), options, workflow ?? SeedWorkflow, new Dictionary<string, Guid>(), ledger ?? new Dictionary<(string, string), Guid>());
    }

    /// <summary>The import ledger after applying <paramref name="draft"/>: each source key keeps the ID it got.</summary>
    private static Dictionary<(string, string), Guid> Ledger(ExternalImportDraft draft) => draft.SourceKeys.ToDictionary(p => (p.Value.EntityKind, p.Value.SourceKey), p => p.Key);

    private static ReadSnapshot Snapshot(TaskItem? task = null, WorkflowSnapshot? workflow = null) =>
        new(0, task is null ? [] : [task], [], [], [], [], [], workflow ?? SeedWorkflow);

    private static ImportPlan Plan(ExternalImportDraft draft, ReadSnapshot snapshot, ImportMode mode = ImportMode.CreateOnly, DateTimeOffset? now = null) =>
        ImportPlanner.Plan(draft.Document, snapshot, mode, new ImportContext(new HashSet<Guid>(), "test", draft.SourceKeys), now ?? Now);
}
