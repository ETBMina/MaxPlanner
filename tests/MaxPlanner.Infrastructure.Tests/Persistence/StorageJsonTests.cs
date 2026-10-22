using AwesomeAssertions;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Persistence.Mapping;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

[Trait("Req", "T-05")]
public sealed class StorageJsonTests
{
    [Fact]
    public void Every_custom_value_kind_round_trips_through_its_json_column()
    {
        var values = new Dictionary<FieldId, CustomValue>
        {
            [new FieldId(Guid.NewGuid())] = new TextCustomValue("Plain ✓"),
            [new FieldId(Guid.NewGuid())] = new MultilineTextCustomValue("Line 1\r\nLine 2"),
            [new FieldId(Guid.NewGuid())] = new NumberCustomValue(12345678901234.5678m),
            [new FieldId(Guid.NewGuid())] = new DateCustomValue(new DateOnly(2026, 2, 28)),
            [new FieldId(Guid.NewGuid())] = new LookupCustomValue(new FieldOptionId(Guid.NewGuid())),
            [new FieldId(Guid.NewGuid())] = new PeopleCustomValue([new PersonId(Guid.NewGuid()), new PersonId(Guid.NewGuid())]),
            [new FieldId(Guid.NewGuid())] = new TagsCustomValue([new TagId(Guid.NewGuid())]),
            [new FieldId(Guid.NewGuid())] = new UrlCustomValue(HttpUrl.CreateOptional("https://example.test/a?b=1").Value!),
        };

        var restored = StorageJson.ReadCustomValues(StorageJson.WriteCustomValues(values), "tasks");

        restored.Should().HaveCount(values.Count);
        foreach (var (id, value) in values)
        {
            Describe(restored[id]).Should().Be(Describe(value));
        }
    }

    [Fact]
    public void No_custom_values_and_no_preserved_content_store_null()
    {
        StorageJson.WriteCustomValues(CustomValue.NoValues).Should().BeNull();
        StorageJson.WritePreserved(PreservedMetadata.Empty).Should().BeNull();
        StorageJson.ReadPreserved(null, "tasks").Should().Be(PreservedMetadata.Empty);
    }

    [Fact]
    public void Preserved_metadata_round_trips_including_raw_legacy_json()
    {
        var preserved = new PreservedMetadata("Old title", ["CR-1", "CR-2"], """{"legacy":{"x":[1,2]}}""");

        var restored = StorageJson.ReadPreserved(StorageJson.WritePreserved(preserved), "tasks");

        (restored.PreLinkTitle, restored.LegacyJson).Should().Be((preserved.PreLinkTitle, preserved.LegacyJson));
        restored.UnselectedCrReferences.Should().Equal("CR-1", "CR-2");
    }

    [Theory]
    [InlineData("""{"00000000-0000-0000-0000-000000000001":{"Kind":"Spreadsheet"}}""")]
    [InlineData("""{"00000000-0000-0000-0000-000000000001":{"Kind":"Number"}}""")]
    [InlineData("""{"not-a-guid":{"Kind":"Text","Text":"x"}}""")]
    [InlineData("""{broken""")]
    public void Unreadable_custom_values_are_reported_as_corrupt(string json)
    {
        var read = () => StorageJson.ReadCustomValues(json, "tasks");

        read.Should().Throw<CorruptStoredDataException>();
    }

    private static string Describe(CustomValue value) => value switch
    {
        PeopleCustomValue people => "People:" + string.Join(",", people.PersonIds),
        TagsCustomValue tags => "Tags:" + string.Join(",", tags.TagIds),
        UrlCustomValue url => "Url:" + url.Url,
        _ => value.ToString(),
    };
}
