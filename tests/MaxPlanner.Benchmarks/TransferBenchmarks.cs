using BenchmarkDotNet.Attributes;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Storage;
using MaxPlanner.Infrastructure.Transfer;

namespace MaxPlanner.Benchmarks;

/// <summary>
/// T-11 "UI during I/O/import" context: the whole-fixture export (JSON and XLSX) and the import of that JSON into an
/// empty dataset (read, preview, one all-or-nothing transaction). These run off the UI thread in the app; their duration
/// sets how long progress is shown. The UI-thread stall budget itself is measured in the app (run-performance.ps1).
/// </summary>
// Each operation takes seconds: one invocation per iteration, few iterations (the import also needs a new target each time).
[MemoryDiagnoser]
[InvocationCount(1)]
[WarmupCount(1)]
[IterationCount(5)]
public class TransferBenchmarks
{
    private FixtureCopy? _fixture;
    private string? _exportFolder;
    private string? _jsonExport;
    private string? _importRoot;
    private SqliteDataset? _importTarget;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixture = await FixtureCopy.OpenAsync();
        _exportFolder = FixtureCopy.NewTempFolder();
        _jsonExport = Path.Combine(_exportFolder, "fixture.json");
        await ExportAsync(TransferFileFormat.Json, _jsonExport);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }

        if (_exportFolder is not null)
        {
            FixtureCopy.DeleteFolder(_exportFolder);
        }
    }

    [Benchmark(Description = "Export the fixture to JSON")]
    public Task ExportJsonAsync() => ExportAsync(TransferFileFormat.Json, Path.Combine(_exportFolder!, "bench.json"));

    [Benchmark(Description = "Export the fixture to XLSX")]
    public Task ExportXlsxAsync() => ExportAsync(TransferFileFormat.Xlsx, Path.Combine(_exportFolder!, "bench.xlsx"));

    [IterationSetup(Target = nameof(ImportJsonAsync))]
    public void CreateImportTarget()
    {
        _importRoot = FixtureCopy.NewTempFolder();
        var created = SqliteDataset.CreateNewAsync(DataPaths.ForDirectory(_importRoot)).GetAwaiter().GetResult();
        _importTarget = created.Dataset ?? throw new InvalidOperationException($"Import target not created: {created.Error?.Code}");
    }

    [IterationCleanup(Target = nameof(ImportJsonAsync))]
    public void DeleteImportTarget()
    {
        _importTarget?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _importTarget = null;
        if (_importRoot is not null)
        {
            FixtureCopy.DeleteFolder(_importRoot);
        }
    }

    [Benchmark(Description = "Import the fixture JSON into an empty dataset")]
    public async Task<long> ImportJsonAsync()
    {
        var target = _importTarget!;
        var document = await TransferAdapters.CreateReader().ReadAsync(_jsonExport!, null, CancellationToken.None);
        if (!document.IsSuccess)
        {
            throw new InvalidOperationException(document.Error!.Code);
        }

        var import = new ImportTransfer(target.Coordinator, target.Snapshots, target.ImportState, TimeProvider.System);
        var plan = await import.PreviewAsync(document.Value, ImportMode.CreateOnly, CancellationToken.None);
        if (!plan.IsSuccess)
        {
            throw new InvalidOperationException(plan.Error!.Code);
        }

        var applied = await import.ApplyAsync(plan.Value, OperationId.New(), CancellationToken.None);
        return applied.IsSuccess ? applied.Value.Value : throw new InvalidOperationException(applied.Error!.Code);
    }

    private async Task ExportAsync(TransferFileFormat format, string path)
    {
        var dataset = _fixture!.Dataset;
        var export = new ExportTransfer(dataset.Snapshots, TransferAdapters.CreateWriter(), dataset.Id.Value, TimeProvider.System);
        var result = await export.ExportAsync(EntireDatasetScope.Instance, format, null, path, CancellationToken.None);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Error!.Code);
        }
    }
}
