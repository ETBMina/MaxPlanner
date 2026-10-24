using BenchmarkDotNet.Attributes;
using MaxPlanner.Application.Abstractions;

namespace MaxPlanner.Benchmarks;

/// <summary>
/// T-11 startup budget (≤3 s to a usable view, excluding migration), its non-UI part: one consistent read snapshot of the
/// fixture on a pooled WAL reader, and the projection store's initial load (snapshot plus read-model composition).
/// </summary>
[MemoryDiagnoser]
public class LoadBenchmarks
{
    private readonly FixedDayBoundarySource _days = new();
    private FixtureCopy? _fixture;

    [GlobalSetup]
    public async Task SetupAsync() => _fixture = await FixtureCopy.OpenAsync();

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [Benchmark(Description = "Read snapshot of the fixture")]
    public async Task<int> SnapshotLoadAsync()
    {
        var snapshot = await _fixture!.Dataset.Snapshots.LoadAsync(CancellationToken.None);
        return snapshot.IsSuccess ? snapshot.Value.Tasks.Count : throw new InvalidOperationException(snapshot.Error!.Code);
    }

    [Benchmark(Description = "Projection store initial load to Ready")]
    public async Task<int> StoreInitialLoadAsync()
    {
        using var store = await FixtureCopy.StartStoreAsync(_fixture!.Dataset, _days);
        return store.Tasks.Count;
    }
}
