using System.Text.Json.Nodes;
using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Infrastructure.Tests.Persistence;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>Design §4.6, ADR 0026: the Copy ID format is a dataset setting that is stored, exported, and imported.</summary>
[Trait("Req", "B-12")]
public sealed class CopyIdFormatTransferTests : IDisposable
{
    private readonly TempFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task The_format_is_stored_and_survives_a_reopen()
    {
        await using var db = await CreateAsync();
        await TransferFixture.PopulateAsync(db);

        await db.ReopenAsync();

        (await db.SnapshotAsync()).Workflow.Defaults.CopyIdFormat.Should().Be(TransferFixture.CopyIdFormat);
    }

    [Fact]
    public async Task A_blank_format_saves_the_default()
    {
        await using var db = await CreateAsync();
        await TransferFixture.PopulateAsync(db);

        await SaveFormatAsync(db, "   ");

        (await db.SnapshotAsync()).Workflow.Defaults.CopyIdFormat.Should().Be(CopyIdFormatter.Default);
    }

    [Theory]
    [InlineData(TransferFileFormat.Json, "both.json")]
    [InlineData(TransferFileFormat.Xlsx, "both.xlsx")]
    [InlineData(TransferFileFormat.CsvBundle, "both.zip")]
    public async Task Export_writes_the_format_and_import_restores_it(TransferFileFormat format, string name)
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var path = _folder.File(name);
        await ExportAsync(source, format, path);

        var document = await Transfers.ReadAsync(path);
        await using var target = await CreateAsync();
        await ImportAsync(target, document);

        document.Defaults!.CopyIdFormat.Should().Be(TransferFixture.CopyIdFormat);
        (await target.SnapshotAsync()).Workflow.Defaults.CopyIdFormat.Should().Be(TransferFixture.CopyIdFormat);
    }

    // Formula-like text: CSV tables neutralise it, but definitions travel in metadata.json and XLSX inline strings.
    [Theory]
    [InlineData(TransferFileFormat.CsvBundle, "dash.zip", "-{CRNum}")]
    [InlineData(TransferFileFormat.CsvBundle, "at.zip", "@{Squad}")]
    [InlineData(TransferFileFormat.CsvBundle, "eq.zip", "={TaskNum}")]
    [InlineData(TransferFileFormat.Xlsx, "dash.xlsx", "-{CRNum}")]
    [InlineData(TransferFileFormat.Xlsx, "at.xlsx", "@{Squad}")]
    [InlineData(TransferFileFormat.Json, "dash.json", "-{CRNum}")]
    public async Task A_format_starting_with_a_formula_character_round_trips_unchanged(TransferFileFormat format, string name, string copyIdFormat)
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        await SaveFormatAsync(source, copyIdFormat);
        var path = _folder.File(name);
        await ExportAsync(source, format, path);

        await using var target = await CreateAsync();
        await ImportAsync(target, await Transfers.ReadAsync(path));

        (await target.SnapshotAsync()).Workflow.Defaults.CopyIdFormat.Should().Be(copyIdFormat);
    }

    [Fact]
    public async Task A_file_from_an_earlier_version_without_the_format_imports_the_default()
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var path = _folder.File("old.json");
        await ExportAsync(source, TransferFileFormat.Json, path);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!;
        json["defaults"]!.AsObject().Remove("copyIdFormat").Should().BeTrue();
        await File.WriteAllTextAsync(path, json.ToJsonString(), Ct);

        var document = await Transfers.ReadAsync(path);
        await using var target = await CreateAsync();
        await SaveFormatAsync(target, "{Title}");
        await ImportAsync(target, document);

        document.Defaults!.CopyIdFormat.Should().BeNull();
        (await target.SnapshotAsync()).Workflow.Defaults.CopyIdFormat.Should().Be(CopyIdFormatter.Default);
    }

    private static async Task SaveFormatAsync(TestDataset db, string format)
    {
        var workflow = (await db.SnapshotAsync()).Workflow;
        var d = workflow.Defaults;
        Succeeded(await new UpdateWorkflowDefaults(db.Coordinator).ExecuteAsync(new UpdateWorkflowDefaultsCommand(
            OperationId.New(), WorkflowStamps.Defaults(workflow), d.DefaultTaskStatusId, d.DefaultCrStatusId, d.DefaultPriorityId,
            d.DefaultCrOwnerId, d.CalendarSchedulingStatusId, d.CrBacklogStatusId, format), Ct));
    }

    private static async Task ExportAsync(TestDataset db, TransferFileFormat format, string path)
    {
        var result = await Transfers.Exporter(db).ExportAsync(EntireDatasetScope.Instance, format, null, path, Ct, TransferContent.Both);
        result.Error.Should().BeNull();
    }

    private static async Task ImportAsync(TestDataset db, TransferDocument document)
    {
        var plan = await Transfers.Importer(db).PreviewAsync(document, ImportMode.CreateOnly, Ct, options: new ImportOptions(TransferContent.Both, null));
        plan.Error.Should().BeNull();
        await Transfers.ApplyAsync(db, plan.Value);
    }
}
