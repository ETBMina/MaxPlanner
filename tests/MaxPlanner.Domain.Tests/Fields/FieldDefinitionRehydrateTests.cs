using AwesomeAssertions;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Tests.Fields;

/// <summary>Rebuilding stored field definitions (slice 1d loader): exact values, no creation rules, corruption reported.</summary>
[Trait("Req", "B-11")]
[Trait("Req", "T-05")]
public sealed class FieldDefinitionRehydrateTests
{
    private static readonly FieldOption High = new(new FieldOptionId(Guid.NewGuid()), LookupName.Create("High").Value, 1);
    private static readonly FieldOption Low = new(new FieldOptionId(Guid.NewGuid()), LookupName.Create("Low").Value, 0);

    [Fact]
    public void An_inactive_lookup_field_is_rebuilt_with_every_stored_value()
    {
        var id = new FieldId(Guid.NewGuid());
        var key = FieldKey.Rehydrate("risk").Value;

        // Options are kept in their stored order, not re-sorted as creation does.
        var field = Rehydrate(FieldKind.Lookup, [High, Low], new LookupCustomValue(Low.Id), id: id, key: key, isActive: false).Value;

        (field.Id, field.Key, field.Target, field.Label.Display, field.Kind).Should().Be((id, key, FieldTarget.ChangeRequest, "Risk", FieldKind.Lookup));
        (field.IsRequired, field.DisplayOrder, field.IsActive).Should().Be((true, 4, false));
        field.Options.Should().Equal(High, Low);
        field.DefaultValue.Should().Be(new LookupCustomValue(Low.Id));
    }

    [Fact]
    public void A_stored_key_that_a_later_version_reserved_still_loads_but_cannot_be_created()
    {
        FieldKey.Rehydrate("owner").IsSuccess.Should().BeTrue();
        FieldKey.Create("owner").Error!.Code.Should().Be(DomainErrorCodes.ReservedKey);
        FieldKey.Rehydrate("1st").Error!.Code.Should().Be(DomainErrorCodes.InvalidKey);
        FieldKey.Rehydrate(" ").Error!.Code.Should().Be(DomainErrorCodes.Required);
    }

    [Fact]
    public void Creation_rules_such_as_unique_option_labels_are_not_re_run()
    {
        var sameLabel = new FieldOption(new FieldOptionId(Guid.NewGuid()), High.Label, 2);

        Rehydrate(FieldKind.Lookup, [High, sameLabel]).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Structurally_invalid_definitions_are_reported_instead_of_loaded()
    {
        var duplicateId = High with { Label = LookupName.Create("Again").Value };

        Rehydrate(FieldKind.Lookup, []).Error.Should().Be(new DomainError(DomainErrorCodes.InvalidStoredValue, "Options"));
        Rehydrate(FieldKind.Text, [High]).Error!.Field.Should().Be("Options");
        Rehydrate(FieldKind.Lookup, [High, duplicateId]).Error!.Field.Should().Be("Options");
        Rehydrate(FieldKind.Number, [], new TextCustomValue("3")).Error.Should().Be(new DomainError(DomainErrorCodes.InvalidStoredValue, "DefaultValue"));
        Rehydrate(FieldKind.Lookup, [High], new LookupCustomValue(new FieldOptionId(Guid.NewGuid()))).Error!.Field.Should().Be("DefaultValue");
        Rehydrate((FieldKind)99, []).Error!.Field.Should().Be("Kind");
    }

    private static DomainResult<FieldDefinition> Rehydrate(
        FieldKind kind,
        IReadOnlyList<FieldOption> options,
        CustomValue? defaultValue = null,
        FieldId? id = null,
        FieldKey? key = null,
        bool isActive = true) =>
        FieldDefinition.Rehydrate(id ?? new FieldId(Guid.NewGuid()), key ?? FieldKey.Rehydrate("risk").Value, FieldTarget.ChangeRequest,
            LookupName.Create("Risk").Value, kind, isRequired: true, defaultValue, displayOrder: 4, options, isActive);
}
