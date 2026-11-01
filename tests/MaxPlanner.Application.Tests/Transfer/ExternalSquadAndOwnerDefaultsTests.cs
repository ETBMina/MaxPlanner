using AwesomeAssertions;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Application.Transfer.External;
using MaxPlanner.Domain.Workflow;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Transfer;

/// <summary>
/// B-13.7 and ADR 0020: an empty or "General" squad imports as no squad ("General"), and CRs without an owner take the
/// fallback owner the wizard asks for after people are mapped: an existing person or a person the import creates.
/// </summary>
[Trait("Req", "B-13")]
public sealed class ExternalSquadAndOwnerDefaultsTests
{
    private const string Sheet = "crs";
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("General")]
    [InlineData("general (no squad)")]
    public void An_empty_or_general_squad_is_no_squad_and_creates_no_squad(string squad)
    {
        var draft = Build((Alice.Id.Value, null), ["CR-1", "One", squad, "Alice"]);

        var cr = draft.Document.ChangeRequests.Should().ContainSingle().Subject;
        cr.SquadId.Should().BeNull();
        cr.SuppliedFields.Should().Contain("SquadId", "a mapped empty squad sets the CR to General");
        draft.Document.Lookups.Squads.Should().BeEmpty();
        draft.Values.Should().NotContain(v => v.Kind == LookupKind.Squad);
        draft.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error);
    }

    [Fact]
    public void A_named_squad_still_resolves()
    {
        var draft = Build((null, null), ["CR-1", "One", "Retail Finance", "Alice"]);

        draft.Document.ChangeRequests.Single().SquadId.Should().Be(RetailFinance.Id.Value);
    }

    [Fact]
    public void Crs_without_an_owner_are_counted_and_report_an_error_without_a_fallback()
    {
        var draft = Build((null, null), ["CR-1", "One", "", ""], ["CR-2", "Two", "", ""], ["CR-3", "Three", "", "Bob"]);

        draft.MissingOwnerCount.Should().Be(2);
        draft.Findings.Where(f => f.Code == ImportFindingCodes.OwnerRequired).Should().HaveCount(2);
    }

    [Fact]
    public void An_existing_fallback_owner_is_applied_to_every_crs_without_an_owner()
    {
        var draft = Build((Bob.Id.Value, null), ["CR-1", "One", "", ""], ["CR-2", "Two", "", "Alice"]);

        draft.MissingOwnerCount.Should().Be(1);
        draft.Document.ChangeRequests.Single(c => c.Number == "CR-1").OwnerId.Should().Be(Bob.Id.Value);
        draft.Document.ChangeRequests.Single(c => c.Number == "CR-2").OwnerId.Should().Be(Alice.Id.Value);
        draft.Findings.Should().NotContain(f => f.Code == ImportFindingCodes.OwnerRequired);
    }

    [Fact]
    public void A_new_person_from_the_import_can_be_the_fallback_owner()
    {
        var draft = Build((null, "Carol"), ["CR-1", "One", "", ""], ["CR-2", "Two", "", "Carol"]);

        var carol = draft.Document.Lookups.People.Should().ContainSingle(p => p.Name == "Carol").Subject;
        draft.Document.ChangeRequests.Should().OnlyContain(c => c.OwnerId == carol.Id);
        draft.Findings.Should().NotContain(f => f.Code == ImportFindingCodes.OwnerRequired);
    }

    private static ExternalImportDraft Build((Guid? Id, string? Name) fallbackOwner, params string[][] rows)
    {
        var table = new ExternalTable(Sheet, ExternalSourceKind.Xlsx, ExternalEntity.ChangeRequest, ["Number", "Title", "Squad", "Owner"],
            [.. rows.Select((r, i) => new ExternalRow(Sheet + "#" + (i + 1), i + 1, [.. r.Select(v => (ExternalValue?)new ExternalValue(v))]))]);
        var options = new ExternalImportOptions
        {
            FallbackOwnerId = fallbackOwner.Id,
            FallbackOwnerName = fallbackOwner.Name,
            SourceNamespace = "test",
            NowUtc = Now,
            TimeZone = TimeZoneInfo.Utc,
            Tables = [new ExternalTableMapping(Sheet, ExternalEntity.ChangeRequest, [ExternalField.CrNumber, ExternalField.Title, ExternalField.Squad, ExternalField.Owner])],
        };
        return ExternalImportPlanner.Build(new ExternalSource([table], []), options, SeedWorkflow, new Dictionary<string, Guid>(), new Dictionary<(string, string), Guid>());
    }
}
