using System.Text;
using AwesomeAssertions;
using MaxPlanner.Application.Transfer;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>B-12 JSON: a hand-written file that omits collections, or writes them as null, reads them as empty.</summary>
[Trait("Req", "B-12")]
public sealed class JsonMissingCollectionsTests : IDisposable
{
    private readonly TempFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task A_header_only_file_reads_counts_and_plans_as_empty()
    {
        var document = await ReadJsonAsync("""{ "format": "max-planner-transfer", "formatVersion": 1 }""");

        document.Lookups.TaskStatuses.Should().BeEmpty();
        document.Lookups.CrStatuses.Should().BeEmpty();
        document.Lookups.Priorities.Should().BeEmpty();
        document.Lookups.Squads.Should().BeEmpty();
        document.Lookups.People.Should().BeEmpty();
        document.Lookups.Tags.Should().BeEmpty();
        document.FieldDefinitions.Should().BeEmpty();
        document.Links.Should().BeEmpty();
        document.TopLevelRecordCount.Should().Be(0);
        await PreviewAsync(document);
    }

    [Fact]
    public async Task A_file_with_one_change_request_and_no_other_collections_reads_counts_and_plans()
    {
        var document = await ReadJsonAsync($$"""
            { "format": "max-planner-transfer", "formatVersion": 1,
              "changeRequests": [ { "id": "{{Guid.NewGuid()}}", "number": "CR-1", "title": "Only CR" } ] }
            """);

        document.TopLevelRecordCount.Should().Be(1);
        document.ChangeRequests.Single().CustomValues.Should().BeEmpty();
        var plan = await PreviewAsync(document);
        plan.Counts[ImportEntity.ChangeRequest].Should().NotBe(ImportEntityCounts.Zero);
    }

    [Fact]
    public async Task Explicit_nulls_for_collections_nested_collections_and_elements_read_as_empty()
    {
        var document = await ReadJsonAsync($$"""
            { "format": "max-planner-transfer", "formatVersion": 1,
              "lookups": { "taskStatuses": null, "crStatuses": null, "priorities": null, "squads": null, "people": [null], "tags": null },
              "fieldDefinitions": [ { "id": "{{Guid.NewGuid()}}", "key": "k", "target": "Task", "label": "K", "kind": "Text",
                                      "isRequired": false, "displayOrder": 0, "options": null, "isActive": true }, null ],
              "tasks": [ { "id": "{{Guid.NewGuid()}}", "ownTitle": "T", "designerIds": null, "developerIds": null, "tagIds": null,
                           "checklistItems": [null], "customValues": { "x": null } } ],
              "subtaskCards": null, "changeRequests": null, "links": null,
              "reviews": [ { "id": "{{Guid.NewGuid()}}", "title": "R", "services": [null] } ], "generalTodos": null }
            """);

        document.Lookups.People.Should().BeEmpty();
        document.FieldDefinitions.Should().ContainSingle().Which.Options.Should().BeEmpty();
        var task = document.Tasks.Should().ContainSingle().Subject;
        task.DesignerIds.Should().BeEmpty();
        task.DeveloperIds.Should().BeEmpty();
        task.TagIds.Should().BeEmpty();
        task.ChecklistItems.Should().BeEmpty();
        task.CustomValues.Should().BeEmpty();
        document.Reviews.Single().Services.Should().BeEmpty();
        document.SubtaskCards.Should().BeEmpty();
        document.ChangeRequests.Should().BeEmpty();
        document.GeneralTodos.Should().BeEmpty();
        document.TopLevelRecordCount.Should().Be(2);
        await PreviewAsync(document);
    }

    private static async Task<ImportPlan> PreviewAsync(TransferDocument document)
    {
        await using var target = await CreateAsync();
        var result = await Transfers.Importer(target).PreviewAsync(document, ImportMode.CreateOnly, Ct);
        result.Error.Should().BeNull();
        return result.Value;
    }

    private async Task<TransferDocument> ReadJsonAsync(string json)
    {
        var path = _folder.File("hand-written.json");
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), Ct);
        return await Transfers.ReadAsync(path);
    }
}
