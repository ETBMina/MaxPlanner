using System.Text;
using AwesomeAssertions;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Infrastructure.Tests.Persistence;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>
/// ADR 0025 across the JSON and CSV formats: their files are unchanged, a full JSON round trip keeps the stored LF text,
/// and CR LF in an imported JSON or CSV file is stored as LF.
/// </summary>
[Trait("Req", "B-12")]
[Trait("Req", "T-10")]
public sealed class LineBreakFormatTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task A_full_json_export_imports_into_an_empty_dataset_with_the_same_stored_text()
    {
        await using var source = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(source);
        var path = _folder.File("full.json");
        (await Transfers.Exporter(source).ExportAsync(EntireDatasetScope.Instance, TransferFileFormat.Json, null, path, Ct)).Error.Should().BeNull();
        await using var target = await CreateAsync();

        await Transfers.ApplyAsync(target, await Transfers.PreviewAsync(target, path));

        var actual = await target.SnapshotAsync();
        actual.Tasks.Single(t => t.Id == fixture.LinkedOwnTitle).Notes!.Value.Should().Be(TransferFixture.StoredNotes);
        actual.ChangeRequests.Single(c => c.Id == fixture.Cr).Description!.Value.Should().Be("Scope\nOut of scope: ✓");
        (await Transfers.PreviewAsync(source, path, ImportMode.Merge)).RecordWriteCount.Should().Be(0, "a merge of the app's own export changes nothing");
    }

    [Fact]
    public async Task Crlf_in_an_imported_csv_table_is_stored_as_lf()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var id = fixture.Unlinked.Value;
        var path = _folder.Write("tasks.csv", Encoding.UTF8.GetBytes($"Id,OwnTitle,Notes\r\n{id},007 Unlinked,\"From a\r\nWindows file\r\"\r\n"));

        await Transfers.ApplyAsync(db, await Transfers.PreviewAsync(db, path, ImportMode.Merge));

        (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.Unlinked).Notes!.Value.Should().Be("From a\nWindows file\n");
    }

    [Fact]
    public async Task Crlf_in_an_imported_json_file_is_stored_as_lf()
    {
        await using var db = await CreateAsync();
        var id = Guid.NewGuid();
        var path = _folder.Write("tasks.json", Encoding.UTF8.GetBytes(
            $$"""{"format":"max-planner-transfer","formatVersion":1,"lookups":{"taskStatuses":[],"crStatuses":[],"priorities":[],"squads":[],"people":[],"tags":[]},"tasks":[{"id":"{{id}}","ownTitle":"From JSON","notes":"a\r\nb\rc"}],"subtaskCards":[],"changeRequests":[],"links":[],"reviews":[],"generalTodos":[],"fieldDefinitions":[]}"""));

        await Transfers.ApplyAsync(db, await Transfers.PreviewAsync(db, path));

        (await db.SnapshotAsync()).Tasks.Single().Notes!.Value.Should().Be("a\nb\nc");
    }
}
