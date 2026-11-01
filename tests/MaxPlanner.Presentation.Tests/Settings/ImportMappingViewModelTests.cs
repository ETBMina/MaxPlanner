using AwesomeAssertions;
using MaxPlanner.Application.Transfer.External;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Presentation.Settings.DataPage;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Settings;

/// <summary>
/// ADR 0027: the Map step's column rows: custom-field targets (existing, proposed by name, or created), the keep-or-discard
/// choice for unmapped columns, and the mapping they hand to the planner.
/// </summary>
[Trait("Req", "B-13")]
[Trait("Adr", "0027")]
public sealed class ImportMappingViewModelTests
{
    private static readonly IReadOnlyList<ExternalEntity> Entities = [ExternalEntity.Task, ExternalEntity.ChangeRequest, ExternalEntity.Review, ExternalEntity.GeneralTodo, ExternalEntity.Ignore];

    private static readonly FieldDefinition Risk = FieldDefinition.Create(new FieldId(Guid.NewGuid()), FieldKey.Create("risk").Value, FieldTarget.Task, LookupName.Create("Risk").Value,
        FieldKind.Text, false, null, 0, []).Value;

    private static readonly FieldDefinition Off = FieldDefinition.Create(new FieldId(Guid.NewGuid()), FieldKey.Create("old").Value, FieldTarget.Task, LookupName.Create("Old").Value,
        FieldKind.Text, false, null, 1, []).Value.Deactivate();

    [Fact]
    public void A_header_matching_an_active_custom_field_proposes_it_and_aliases_still_win_for_built_in_fields()
    {
        var table = Table(ExternalSourceKind.Xlsx, ["Title", "RISK", "Old", "subtasks", "createdAt", "Extra"], ["One", "High", "x", "a", "2025-01-01", "e"]);

        var vm = new ImportTableViewModel(table, Entities, Choices(), new FakeStrings(), [Risk, Off]);

        vm.Columns.Select(c => (c.Field, c.CustomFieldChoice)).Should().Equal(
            (ExternalField.Title, null),
            (ExternalField.CustomField, ExternalCustomFieldChoice.Existing(Risk.Id.Value)),
            (ExternalField.Ignore, null),
            (ExternalField.ChecklistItems, null),
            (ExternalField.Created, null),
            (ExternalField.Ignore, null));
        vm.Columns[0].FieldChoices.Should().HaveCount(ExternalFieldAliases.FieldsFor(ExternalEntity.Task).Count + 2, "one active custom field and Create; a turned-off field is not offered");
        vm.Columns[0].FieldChoices[^1].Should().Be("Import_Field_CreateCustom");
    }

    [Fact]
    public void Markdown_columns_never_propose_checklist_items()
    {
        var vm = new ImportTableViewModel(Table(ExternalSourceKind.Markdown, ["title", "subtasks"], ["One", "a"]), Entities, Choices(), new FakeStrings(), []);

        vm.Columns[1].Field.Should().Be(ExternalField.Ignore);
    }

    [Fact]
    public void To_mapping_keys_custom_fields_and_discarded_columns_by_column_index_and_discards_only_ignored_columns()
    {
        var vm = new ImportTableViewModel(Table(ExternalSourceKind.Xlsx, ["Title", "Points", "Extra", "Secret"], ["One", "5", "e", "s"]), Entities, Choices(), new FakeStrings(), []);
        var points = vm.Columns[1];
        points.FieldIndex = points.FieldChoices.Count - 1;
        points.NewFieldLabel = "Story points";
        vm.Columns[3].UnmappedIndex = 1;
        vm.Columns[0].UnmappedIndex = 1;

        var mapping = vm.ToMapping();

        mapping.ColumnFields.Should().Equal(ExternalField.Title, ExternalField.CustomField, ExternalField.Ignore, ExternalField.Ignore);
        mapping.CustomFields.Should().BeEquivalentTo(new Dictionary<int, ExternalCustomFieldChoice> { [1] = ExternalCustomFieldChoice.New("Story points", FieldKind.Number) });
        mapping.DiscardedColumns.Should().BeEquivalentTo([3], "a mapped column is never discarded, whatever its hidden choice says");
        (vm.Columns[2].IsDiscarded, vm.Columns[3].IsDiscarded, vm.Columns[0].IsDiscarded).Should().Be((false, true, false));
        vm.Columns[2].ShowsKeepChoice.Should().BeTrue();
    }

    [Fact]
    public void Unmapped_columns_of_reviews_and_todos_are_always_discarded_without_a_keep_choice()
    {
        var table = Table(ExternalSourceKind.Xlsx, ["Title", "Extra"], ["One", "e"]) with { SuggestedEntity = ExternalEntity.Review };

        var vm = new ImportTableViewModel(table, Entities, Choices(), new FakeStrings(), [Risk]);

        var extra = vm.Columns[1];
        (extra.IsIgnored, extra.CanKeepHidden, extra.ShowsKeepChoice, extra.ShowsNotStored, extra.IsDiscarded).Should().Be((true, false, false, true, true));
        vm.Columns[0].FieldChoices.Should().NotContain("Import_Field_CreateCustom", "reviews have no custom fields");
        vm.ToMapping().DiscardedColumns.Should().BeEquivalentTo([1]);
    }

    [Fact]
    public void The_kind_is_suggested_once_when_create_custom_field_is_first_chosen()
    {
        var calls = 0;
        var column = new ImportColumnViewModel("Due", "2026-01-01", [new ImportFieldChoice("Ignore", ExternalField.Ignore, null), new ImportFieldChoice("Create", ExternalField.CustomField, null)],
            0, false, () => { calls++; return FieldKind.Date; }, ["Text", "MultilineText", "Number", "Date", "Url", "Lookup"], ["Keep", "Discard"], new FakeStrings());

        calls.Should().Be(0, "nothing is read until the user creates a field");
        column.FieldIndex = 1;
        column.NewFieldKindIndex.Should().Be(ExternalValues.CreatableKinds.ToList().IndexOf(FieldKind.Date));
        column.NewFieldKindIndex = 0;
        column.FieldIndex = 0;
        column.FieldIndex = 1;

        calls.Should().Be(1);
        column.NewFieldKindIndex.Should().Be(0, "the user's own kind choice is kept");
        column.CustomFieldChoice.Should().Be(ExternalCustomFieldChoice.New("Due", FieldKind.Text));
    }

    private static IReadOnlyList<string> Choices() => [.. Entities.Select(e => e.ToString())];

    private static ExternalTable Table(ExternalSourceKind kind, string[] columns, params string[][] rows) =>
        new("sheet", kind, ExternalEntity.Task, columns, [.. rows.Select((r, i) => new ExternalRow("sheet#" + (i + 1), i + 1, [.. r.Select(v => (ExternalValue?)new ExternalValue(v))]))]);
}
