using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Infrastructure.Tests.Persistence;
using MaxPlanner.Infrastructure.Transfer;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>B-12 CSV with real SQLite files: a single table and the bundle export non-empty tables and import back unchanged.</summary>
[Trait("Req", "B-12")]
public sealed class CsvExportTests : IDisposable
{
    private readonly TempFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    private async Task<string> ExportJsonAsync(TestDataset db)
    {
        var path = _folder.File("source.json");
        var result = await Transfers.Exporter(db).ExportAsync(EntireDatasetScope.Instance, TransferFileFormat.Json, null, path, Ct);
        result.Error.Should().BeNull();
        return path;
    }

    [Fact]
    public async Task A_single_csv_table_export_of_a_non_empty_table_succeeds_and_reads_back_every_row()
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var path = _folder.File("tasks.csv");

        var result = await Transfers.Exporter(source).ExportAsync(EntireDatasetScope.Instance, TransferFileFormat.CsvTable, TransferTable.Tasks, path, Ct);

        result.Error.Should().BeNull();
        var read = await TransferAdapters.CreateReader().ReadAsync(path, TransferTable.Tasks, Ct);
        read.Error.Should().BeNull();
        read.Value.Tasks.Select(t => t.Id).Should().BeEquivalentTo((await source.SnapshotAsync()).Tasks.Select(t => t.Id.Value));
    }

    [Fact]
    public async Task A_csv_bundle_export_succeeds_and_holds_every_kind_of_record()
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var path = _folder.File("bundle.zip");

        var result = await Transfers.Exporter(source).ExportAsync(EntireDatasetScope.Instance, TransferFileFormat.CsvBundle, null, path, Ct);

        result.Error.Should().BeNull();
        var document = await Transfers.ReadAsync(path);
        document.Tasks.Should().NotBeEmpty();
        document.ChangeRequests.Should().NotBeEmpty();
        document.SubtaskCards.Should().NotBeEmpty();
        document.Reviews.Should().NotBeEmpty();
        document.GeneralTodos.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_csv_bundle_export_imported_into_an_empty_dataset_reproduces_the_business_content()
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var path = _folder.File("round-trip.zip");
        var result = await Transfers.Exporter(source).ExportAsync(EntireDatasetScope.Instance, TransferFileFormat.CsvBundle, null, path, Ct);
        result.Error.Should().BeNull();
        await using var target = await CreateAsync();

        var plan = await Transfers.PreviewAsync(target, path);
        plan.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error || f.Severity == ImportSeverity.Conflict);
        await Transfers.ApplyAsync(target, plan);

        TransferFixture.Shape(await target.SnapshotAsync()).Should().BeEquivalentTo(
            TransferFixture.Shape(await source.SnapshotAsync()), o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task A_cancelled_csv_export_is_reported_as_cancelled_and_leaves_no_file()
    {
        await using var source = await CreateAsync();
        await TransferFixture.PopulateAsync(source);
        var document = await Transfers.ReadAsync(await ExportJsonAsync(source));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await TransferAdapters.CreateWriter().WriteAsync(document, TransferFileFormat.CsvTable, TransferTable.Tasks, _folder.File("cancelled.csv"), cancelled.Token);

        result.Error.Should().BeOfType<OperationCancelledError>();
        Directory.GetFiles(_folder.Path).Should().NotContain(f => f.EndsWith(".csv", StringComparison.Ordinal) || f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unexpected_writer_failure_is_a_transfer_file_error_not_an_exception()
    {
        var document = new TransferDocument { Lookups = null! };

        var result = await TransferAdapters.CreateWriter().WriteAsync(document, TransferFileFormat.CsvBundle, null, _folder.File("broken.zip"), Ct);

        result.Error.Should().BeOfType<TransferFileError>();
        Directory.GetFiles(_folder.Path).Should().BeEmpty("a failed write leaves no temporary file");
    }
}
