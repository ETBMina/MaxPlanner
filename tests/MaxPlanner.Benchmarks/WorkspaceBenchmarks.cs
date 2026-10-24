using BenchmarkDotNet.Attributes;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Dashboard;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Store;

namespace MaxPlanner.Benchmarks;

/// <summary>A loaded projection store over a copy of the fixture, shared by the store-level benchmarks.</summary>
public abstract class StoreBenchmark
{
    private readonly FixedDayBoundarySource _days = new();
    private FixtureCopy? _fixture;

    private protected FixtureCopy Fixture => _fixture!;

    protected WorkspaceProjectionStore Store { get; private set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixture = await FixtureCopy.OpenAsync();
        Store = await FixtureCopy.StartStoreAsync(_fixture.Dataset, _days);
        OnLoaded();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        Store?.Dispose();
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    protected virtual void OnLoaded()
    {
    }
}

/// <summary>
/// T-11 interaction budgets measured below the UI on a loaded store: one ordinary save to its publication in the shared
/// projection store (p95 ≤ 200 ms includes the UI update on top) and a Dashboard recompute after a change (p95 ≤ 200 ms).
/// </summary>
[MemoryDiagnoser]
public class WorkspaceBenchmarks : StoreBenchmark
{
    private readonly List<TaskReadModel> _editable = [];
    private readonly Dictionary<TaskId, long> _revisions = [];
    private int _next;

    [Benchmark(Description = "Save one task edit and see it in the projection store")]
    public async Task CommitToPublicationAsync()
    {
        var task = _editable[_next++ % _editable.Count];
        var notes = "Benchmark edit " + _next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var draft = new TaskContentDraft(
            task.EffectiveTitle, task.Service, [.. task.Designers.Select(d => d.Id)], [.. task.Developers.Select(d => d.Id)], [.. task.Tags.Select(t => t.Id)], notes, task.CustomValues);
        var result = await new UpdateTaskContent(Fixture.Dataset.Coordinator).ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), task.Id, _revisions[task.Id], draft), CancellationToken.None);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Error!.Code);
        }

        _revisions[task.Id] = result.Value.Value;
        await FixtureCopy.WaitForSequenceAsync(Store, result.Value.CommitSequence);
    }

    [Benchmark(Description = "Dashboard recompute over the store")]
    public DashboardResult DashboardRecompute() =>
        DashboardCalculator.Calculate(
            new DashboardInput([.. Store.Tasks.Items], [.. Store.SubtaskCards.Items], [.. Store.ChangeRequests.Items], [.. Store.ReviewItems.Items], Store.CurrentWorkflow!),
            DashboardFilter.None,
            Store.Today,
            new DashboardLabels(ReadModelComposer.DefaultGeneralSquadLabel, "Unassigned"));

    // Active unlinked tasks: their own title and notes are editable through the Grid's UpdateTaskContent route.
    protected override void OnLoaded()
    {
        foreach (var task in Store.Tasks.Items.Where(t => !t.IsArchived && !t.IsTitleInherited).Take(200))
        {
            _editable.Add(task);
            _revisions[task.Id] = task.Revision;
        }
    }
}

/// <summary>T-11 search/filter budget (p95 ≤ 300 ms after the final keystroke, including the 150 ms debounce): one pass of
/// the shared predicate over every task and Subtask card, as Board and Grid apply it.</summary>
[MemoryDiagnoser]
public class FilterBenchmarks : StoreBenchmark
{
    public enum FilterShape
    {
        Search,
        Fields,
        SearchAndFields,
    }

    /// <summary>The filter shapes a user applies most: a search, field filters, and both together.</summary>
    [ParamsAllValues]
    public FilterShape Filter { get; set; }

    [Benchmark(Description = "Filter and search every task and Subtask card")]
    public int FilterApply()
    {
        var workflow = Store.CurrentWorkflow!;
        var spec = Filter switch
        {
            FilterShape.Search => TaskFilterSpec.Empty with { SearchText = "card" },
            FilterShape.Fields => TaskFilterSpec.Empty with
            {
                Statuses = [.. workflow.TaskStatuses.Take(2).Select(s => s.Id)],
                Priorities = [.. workflow.Priorities.Take(2).Select(p => p.Id)],
            },
            _ => TaskFilterSpec.Empty with
            {
                SearchText = "card",
                Statuses = [.. workflow.TaskStatuses.Take(2).Select(s => s.Id)],
                Squads = [.. workflow.Squads.Take(2).Select(s => (SquadId?)s.Id), null],
            },
        };
        var store = Store;
        var context = new TaskFilterContext(
            id => store.Tasks.Lookup(id) is { HasValue: true } task ? task.Value : null,
            id => store.ChangeRequests.Lookup(id) is { HasValue: true } cr ? cr.Value : null);
        var count = store.Tasks.Items.Count(t => !t.IsArchived && TaskFilterMatcher.Matches(spec, t, context));
        return count + store.SubtaskCards.Items.Count(c => TaskFilterMatcher.Matches(spec, c, context));
    }
}
