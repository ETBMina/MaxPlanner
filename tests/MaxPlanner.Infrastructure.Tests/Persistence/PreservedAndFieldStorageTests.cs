using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

[Trait("Req", "T-05")]
[Trait("Req", "T-11.2")]
public sealed class PreservedAndFieldStorageTests
{
    [Fact]
    public async Task Cr_preserved_metadata_survives_edits_and_a_restart()
    {
        await using var db = await CreateAsync();
        var alice = db.AddPerson("Alice");
        var preserved = new PreservedMetadata(null, ["OLD-77"], """{"jira":{"key":"MP-77","labels":["a","b"]}}""");
        var id = new ChangeRequestId(Guid.NewGuid());
        var content = new ChangeRequestContent(CrNumber.Create("CR-77").Value, TitleText.Create("Imported CR").Value, alice, null,
            (await db.SnapshotAsync()).Workflow.Defaults.DefaultPriorityId, null, null, null, null, null, CustomValue.NoValues);

        // No import use case exists yet, so the insert runs through the stores as an import will.
        Succeeded(await db.Coordinator.ExecuteAsync(OperationId.New(), "test.cr-import", tx =>
        {
            var cr = ChangeRequest.Create(id, content, tx.Workflow.Read().CrStatuses[0], tx.UtcNow, preserved);
            tx.ChangeRequests.Insert(cr);
            return TxResult.Commit(cr.Id);
        }, Ct));
        Succeeded(await db.Coordinator.ExecuteAsync(OperationId.New(), "test.cr-edit", tx =>
        {
            var cr = tx.ChangeRequests.Find(id)!;
            var edited = cr.Update(content with { Title = TitleText.Create("Renamed").Value }, tx.Workflow.Read().CrStatuses[1], tx.UtcNow);
            tx.ChangeRequests.Update(edited);
            return TxResult.Commit(edited.Revision);
        }, Ct));
        var plain = await db.AddCrAsync(alice, "CR-78");

        await db.ReopenAsync();
        var crs = (await db.SnapshotAsync()).ChangeRequests.ToDictionary(c => c.Id);

        var loaded = crs[id];
        (loaded.Title.Value, loaded.Revision).Should().Be(("Renamed", 2));
        loaded.Preserved.PreLinkTitle.Should().BeNull();
        loaded.Preserved.UnselectedCrReferences.Should().Equal("OLD-77");
        loaded.Preserved.LegacyJson.Should().Be(preserved.LegacyJson);
        crs[plain].Preserved.Should().Be(PreservedMetadata.Empty);
        db.Scalar("SELECT COUNT(*) FROM change_requests WHERE preserved_json IS NULL;").Should().Be(1, "nothing to preserve stores NULL");
    }

    [Fact]
    public async Task A_cr_row_with_task_only_preserved_content_is_reported_as_corrupt()
    {
        await using var db = await CreateAsync();
        await db.AddCrAsync(db.AddPerson("Alice"));
        db.Execute("""UPDATE change_requests SET preserved_json = '{"PreLinkTitle":"Only tasks are linked"}';""");

        var snapshot = await db.Dataset.Snapshots.LoadAsync(Ct);

        snapshot.Error.Should().Be(new StorageFailureError(StorageFailureKind.Corrupt));
    }

    [Fact]
    [Trait("Req", "B-11")]
    public async Task Field_definitions_reload_exactly_as_stored_including_inactive_ones()
    {
        await using var db = await CreateAsync();
        var risk = Guid.NewGuid();
        var high = Guid.NewGuid();
        var low = Guid.NewGuid();
        db.Execute(
            """
            INSERT INTO field_definitions (id, target, field_key, key_norm, label, kind, is_required, default_value_json, display_order, options_json, is_active)
            VALUES ($id, 'ChangeRequest', 'Risk', 'risk', 'Delivery risk', 'Lookup', 1, $default, 3, $options, 0);
            """,
            ("$id", risk),
            ("$default", $$"""{"Kind":"Lookup","Option":"{{low}}"}"""),
            ("$options", $$"""[{"Id":"{{high}}","Label":"High","Order":1},{"Id":"{{low}}","Label":"Low","Order":0}]"""));

        // "owner" became a reserved key for new fields; a stored field that uses it must still load.
        var owner = db.AddField("owner", FieldKind.Text);

        await db.ReopenAsync();
        var fields = (await db.SnapshotAsync()).Workflow.Fields.ToDictionary(f => f.Id.Value);

        var field = fields[risk];
        (field.Key.Display, field.Target, field.Label.Display, field.Kind).Should().Be(("Risk", FieldTarget.ChangeRequest, "Delivery risk", FieldKind.Lookup));
        (field.IsRequired, field.DisplayOrder, field.IsActive).Should().Be((true, 3, false));
        field.Options.Select(o => (o.Id.Value, o.Label.Display, o.Order)).Should().Equal((high, "High", 1), (low, "Low", 0));
        field.DefaultValue.Should().Be(new LookupCustomValue(new FieldOptionId(low)));
        fields[owner.Value].IsActive.Should().BeTrue();
    }

    [Fact]
    [Trait("Req", "B-11")]
    public async Task A_structurally_invalid_field_definition_is_reported_as_corrupt()
    {
        await using var db = await CreateAsync();
        db.AddField("risk", FieldKind.Lookup, optionsJson: "[]");

        var snapshot = await db.Dataset.Snapshots.LoadAsync(Ct);

        snapshot.Error.Should().Be(new StorageFailureError(StorageFailureKind.Corrupt));
    }
}
