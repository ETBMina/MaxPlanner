using AwesomeAssertions;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Tests.Fields;

[Trait("Req", "B-11")]
public sealed class CustomFieldTests
{
    private static readonly FieldOption Low = new(new FieldOptionId(Guid.NewGuid()), LookupName.Create("Low").Value, 0);
    private static readonly FieldOption High = new(new FieldOptionId(Guid.NewGuid()), LookupName.Create("High").Value, 1);

    [Theory]
    [InlineData("risk_level", true, null)]
    [InlineData("Go-Live", true, null)]
    [InlineData("1st", false, DomainErrorCodes.InvalidKey)]
    [InlineData("has space", false, DomainErrorCodes.InvalidKey)]
    [InlineData("  ", false, DomainErrorCodes.Required)]
    [InlineData("Start_Date", false, DomainErrorCodes.ReservedKey)]
    [InlineData("owner", false, DomainErrorCodes.ReservedKey)]
    public void Keys_are_identifiers_that_cannot_shadow_built_in_fields(string raw, bool valid, string? code)
    {
        var key = FieldKey.Create(raw);

        key.IsSuccess.Should().Be(valid);
        key.Error?.Code.Should().Be(code);
    }

    [Fact]
    public void Keys_normalize_case_for_per_target_uniqueness()
    {
        FieldKey.Create("Risk").Value.Normalized.Should().Be(FieldKey.Create(" risk ").Value.Normalized);
    }

    [Fact]
    public void Lookup_fields_need_distinct_options_and_other_kinds_take_none()
    {
        var noOptions = Define("risk", FieldKind.Lookup, options: []);
        var textWithOptions = Define("code", FieldKind.Text, options: [Low]);
        var duplicateLabels = Define("risk", FieldKind.Lookup, options: [Low, High with { Label = LookupName.Create("low").Value }]);

        noOptions.Error!.Code.Should().Be(DomainErrorCodes.InvalidFieldOptions);
        textWithOptions.Error!.Code.Should().Be(DomainErrorCodes.InvalidFieldOptions);
        duplicateLabels.Error!.Code.Should().Be(DomainErrorCodes.DuplicateName);
    }

    [Fact]
    public void A_default_value_is_validated_against_the_kind_and_the_options()
    {
        Define("risk", FieldKind.Lookup, defaultValue: new LookupCustomValue(new FieldOptionId(Guid.NewGuid())), options: [Low])
            .Error.Should().Be(new DomainError(DomainErrorCodes.UnknownLookupOption, "DefaultValue"));
        Define("points", FieldKind.Number, defaultValue: new TextCustomValue("three"))
            .Error!.Code.Should().Be(DomainErrorCodes.CustomValueKindMismatch);
        Define("risk", FieldKind.Lookup, defaultValue: new LookupCustomValue(High.Id), options: [High, Low]).Value.Options
            .Should().Equal(Low, High);
    }

    [Fact]
    public void Validation_reports_missing_required_values_mismatched_kinds_long_text_and_unknown_options()
    {
        var required = Define("ticket", FieldKind.Text, isRequired: true, order: 0).Value;
        var points = Define("points", FieldKind.Number, order: 1).Value;
        var summary = Define("summary", FieldKind.Text, order: 2).Value;
        var risk = Define("risk", FieldKind.Lookup, order: 3, options: [Low]).Value;
        var stray = new FieldId(Guid.NewGuid());
        var values = new Dictionary<FieldId, CustomValue>
        {
            [required.Id] = new TextCustomValue("   "),
            [points.Id] = new DateCustomValue(new DateOnly(2026, 1, 1)),
            [summary.Id] = new TextCustomValue(new string('x', DomainLimits.CustomText + 1)),
            [risk.Id] = new LookupCustomValue(High.Id),
            [stray] = new NumberCustomValue(1),
        };

        var errors = CustomFieldPolicy.Errors(FieldTarget.Task, [risk, summary, points, required], values);

        errors.Should().Equal(
            new DomainError(DomainErrorCodes.Required, "Custom:ticket"),
            new DomainError(DomainErrorCodes.CustomValueKindMismatch, "Custom:points"),
            new DomainError(DomainErrorCodes.TooLong, "Custom:summary", DomainLimits.CustomText),
            new DomainError(DomainErrorCodes.UnknownLookupOption, "Custom:risk"),
            new DomainError(DomainErrorCodes.UnknownField, $"Custom:{stray}"));
        CustomFieldPolicy.Validate(FieldTarget.Task, [risk, summary, points, required], values).Should().Be(errors[0]);
    }

    [Fact]
    public void Definitions_of_the_other_entity_type_do_not_apply()
    {
        var crField = Define("budget", FieldKind.Number, isRequired: true, target: FieldTarget.ChangeRequest).Value;

        CustomFieldPolicy.Validate(FieldTarget.Task, [crField], CustomValue.NoValues).Should().BeNull();
        CustomFieldPolicy.Validate(FieldTarget.Task, [crField], new Dictionary<FieldId, CustomValue> { [crField.Id] = new NumberCustomValue(5) })!
            .Code.Should().Be(DomainErrorCodes.UnknownField);
    }

    [Fact]
    public void Values_of_removed_fields_are_neither_required_nor_validated_and_survive_an_edit()
    {
        var removed = Define("legacy", FieldKind.Number, isRequired: true).Value.Deactivate();
        var active = Define("notes2", FieldKind.MultilineText).Value;
        var stored = new Dictionary<FieldId, CustomValue>
        {
            [removed.Id] = new TextCustomValue("kept although the field was a Number"),
            [active.Id] = new MultilineTextCustomValue("old"),
        };
        var submitted = new Dictionary<FieldId, CustomValue>
        {
            [active.Id] = new MultilineTextCustomValue("new\nlines"),
            [removed.Id] = new NumberCustomValue(1),
        };

        var merged = CustomFieldPolicy.MergeForUpdate(FieldTarget.Task, [removed, active], stored, submitted);

        removed.IsActive.Should().BeFalse();
        CustomFieldPolicy.Validate(FieldTarget.Task, [removed, active], merged).Should().BeNull();
        merged[removed.Id].Should().Be(stored[removed.Id], "the editor cannot change a removed field's value");
        merged[active.Id].Should().Be(new MultilineTextCustomValue("new\nlines"));
    }

    [Fact]
    public void Url_values_reuse_the_http_only_url_rule()
    {
        var link = Define("design", FieldKind.Url).Value;

        HttpUrl.CreateOptional("file:///c:/x", "Custom:design").Error!.Code.Should().Be(DomainErrorCodes.InvalidUrl);
        CustomFieldPolicy.ValidateValue(link, new UrlCustomValue(HttpUrl.CreateOptional("https://example.test/spec").Value!)).Should().BeNull();
    }

    private static DomainResult<FieldDefinition> Define(
        string key,
        FieldKind kind,
        bool isRequired = false,
        CustomValue? defaultValue = null,
        int order = 0,
        IReadOnlyList<FieldOption>? options = null,
        FieldTarget target = FieldTarget.Task) =>
        FieldDefinition.Create(new FieldId(Guid.NewGuid()), FieldKey.Create(key).Value, target, LookupName.Create(key).Value, kind,
            isRequired, defaultValue, order, options ?? (kind == FieldKind.Lookup ? [Low, High] : []));
}
