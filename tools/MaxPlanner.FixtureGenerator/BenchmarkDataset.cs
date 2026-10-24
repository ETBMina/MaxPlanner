using System.Globalization;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.FixtureGenerator;

/// <summary>The T-11 fixture sizes. Checklist items are per task, so 10,000 tasks give 50,000 items.</summary>
internal sealed record BenchmarkCounts(
    int People,
    int Tags,
    int ExtraSquads,
    int ChangeRequests,
    int Tasks,
    int ChecklistItemsPerTask,
    int SubtaskCards,
    int ArchivedTasks,
    int Reviews,
    int GeneralTodos)
{
    /// <summary>T-11: 10,000 tasks, 5,000 CRs, 50,000 checklist items, 2,000 Subtask cards, 2,000 reviews, 5,000 General to-dos, 20% archived.</summary>
    public static BenchmarkCounts Full { get; } = new(40, 20, 4, 5_000, 10_000, 5, 2_000, 2_000, 2_000, 5_000);

    public static BenchmarkCounts Scaled(double scale)
    {
        int S(int value) => Math.Max(1, (int)Math.Ceiling(value * scale));
        var full = Full;
        return new BenchmarkCounts(
            full.People, full.Tags, full.ExtraSquads, S(full.ChangeRequests), S(full.Tasks), full.ChecklistItemsPerTask,
            S(full.SubtaskCards), S(full.ArchivedTasks), S(full.Reviews), S(full.GeneralTodos));
    }
}

/// <summary>
/// Builds the T-11 benchmark dataset through the use cases. Independent creates are submitted concurrently (bounded),
/// which keeps the single ordered writer busy; the commit order of independent records carries no meaning.
/// </summary>
/// <remarks>
/// Values are "realistic short" (T-11): short titles and notes, 0–2 people and tags, 60% of tasks linked to a CR (several
/// tasks share a CR), 70% of CRs scheduled around today (some overdue), and a spread over every configured status.
/// </remarks>
internal sealed class BenchmarkDataset(IMutationCoordinator coordinator, Random random, BenchmarkCounts counts, DateOnly today)
{
    private const int Concurrency = 32;

    private static readonly string[] Verbs = ["Update", "Fix", "Add", "Review", "Migrate", "Remove", "Document", "Test", "Refactor", "Design"];
    private static readonly string[] Nouns = ["card limits", "login flow", "statement export", "fee engine", "push alerts", "KYC checks", "payee list", "audit log", "rate table", "onboarding"];
    private static readonly string[] Services = ["Cards API", "Mobile App", "Payments", "Platform", "Web Banking", "Core Ledger", "Notifications", "Identity"];
    private static readonly string[] Notes = ["Waiting on the vendor sandbox.", "Agreed with the squad lead.", "Needs a security sign-off.", "Copy is final.", "Retest after the hotfix."];

    private readonly FixtureBuilder _builder = new(coordinator);

    public async Task CreateAsync(IReadSnapshotSource snapshots)
    {
        Console.WriteLine("Lookups…");
        var people = new List<PersonId>();
        for (var i = 1; i <= counts.People; i++)
        {
            people.Add(await _builder.PersonAsync(string.Create(CultureInfo.InvariantCulture, $"Person {i:D2}")));
        }

        var tags = new List<TagId>();
        for (var i = 1; i <= counts.Tags; i++)
        {
            tags.Add(await _builder.TagAsync(string.Create(CultureInfo.InvariantCulture, $"tag-{i:D2}")));
        }

        for (var i = 1; i <= counts.ExtraSquads; i++)
        {
            await _builder.SquadAsync(string.Create(CultureInfo.InvariantCulture, $"Squad {i:D2}"));
        }

        var workflow = (await snapshots.LoadAsync(CancellationToken.None)).Value.Workflow;

        Console.WriteLine("Change requests…");
        var crs = await ParallelAsync(counts.ChangeRequests, i => _builder.CrAsync(CrSpecFor(i, workflow, people)));

        Console.WriteLine("Tasks and checklist items…");
        var tasks = await ParallelAsync(counts.Tasks, i => _builder.TaskAsync(TaskSpecFor(i, workflow, people, tags, crs)));

        // Cards go under the tasks that stay active and archiving takes the last ones, so an archive always runs at
        // the revision its task was created with.
        Console.WriteLine("Subtask cards…");
        var parents = tasks.Take(Math.Max(1, tasks.Count - counts.ArchivedTasks)).ToList();
        await ParallelAsync(counts.SubtaskCards, i =>
            _builder.CardAsync(parents[i % parents.Count], Title(i, "Card"), random.Next(3) == 0 ? Pick(Notes) : null, Pick(workflow.TaskStatuses).Id));

        Console.WriteLine("Archiving…");
        await ParallelAsync(Math.Min(counts.ArchivedTasks, tasks.Count), i => Done(_builder.ArchiveAsync(tasks[tasks.Count - 1 - i])));

        Console.WriteLine("Reviews…");
        await ParallelAsync(counts.Reviews, i => Done(_builder.ReviewAsync(ReviewFor(i, people), random.Next(10) < 4)));

        Console.WriteLine("General to-dos…");
        await ParallelAsync(counts.GeneralTodos, i => Done(_builder.GeneralTodoAsync(Title(i, "To-do"), random.Next(10) < 3)));
    }

    private static async Task<bool> Done(Task task)
    {
        await task;
        return true;
    }

    private static async Task<List<T>> ParallelAsync<T>(int count, Func<int, Task<T>> create)
    {
        var results = new T[count];
        using var gate = new SemaphoreSlim(Concurrency);
        var pending = new List<Task>(count);
        for (var i = 0; i < count; i++)
        {
            await gate.WaitAsync();
            pending.Add(RunAsync(i));
            if ((i + 1) % 1000 == 0)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {i + 1}/{count}"));
            }
        }

        await Task.WhenAll(pending);
        return [.. results];

        // create runs synchronously up to its first await, so every Random draw happens on this loop's thread.
        async Task RunAsync(int index)
        {
            try
            {
                results[index] = await create(index);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private CrSpec CrSpecFor(int i, WorkflowSnapshot workflow, List<PersonId> people)
    {
        var scheduled = random.Next(10) < 7;
        var start = today.AddDays(random.Next(-120, 121));
        var end = start.AddDays(random.Next(0, 21));
        return new CrSpec(
            string.Create(CultureInfo.InvariantCulture, $"CR-{10000 + i}"),
            Title(i, "CR"),
            Pick(people),
            random.Next(10) == 0 ? null : Pick(workflow.Squads).Id,
            random.Next(4) == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{random.Next(1, 99999):D5}"),
            Pick(workflow.Priorities).Id,
            Pick(workflow.CrStatuses).Id,
            scheduled ? start : null,
            scheduled ? end : null,
            random.Next(3) == 0 ? Pick(Notes) : null);
    }

    private TaskSpec TaskSpecFor(int i, WorkflowSnapshot workflow, List<PersonId> people, List<TagId> tags, List<ChangeRequestId> crs)
    {
        var linked = random.Next(10) < 6;
        var checklist = Enumerable.Range(1, counts.ChecklistItemsPerTask)
            .Select(n => (string.Create(CultureInfo.InvariantCulture, $"Step {n}: {Pick(Verbs).ToLowerInvariant()} {Pick(Nouns)}"), random.Next(10) < 4))
            .ToList();
        return new TaskSpec(
            linked ? null : Title(i, "Task"),
            Pick(Services),
            linked ? Pick(crs) : null,
            Pick(workflow.Priorities).Id,
            Pick(workflow.TaskStatuses).Id,
            Some(people, 2),
            Some(people, 2),
            Some(tags, 2),
            random.Next(2) == 0 ? Pick(Notes) : null,
            checklist);
    }

    private ReviewDraft ReviewFor(int i, List<PersonId> people) => new(
        Title(i, "Review"),
        random.Next(2) == 0 ? string.Create(CultureInfo.InvariantCulture, $"CR-{10000 + random.Next(counts.ChangeRequests)}") : null,
        random.Next(2) == 0 ? string.Create(CultureInfo.InvariantCulture, $"{random.Next(1, 9999):D4}") : null,
        [.. Enumerable.Range(0, random.Next(1, 3)).Select(_ => Pick(Services)).Distinct()],
        random.Next(10) < 8 ? Pick(people) : null,
        random.Next(2) == 0 ? Pick(Notes) : null);

    private string Title(int i, string kind) => string.Create(CultureInfo.InvariantCulture, $"{Pick(Verbs)} {Pick(Nouns)} ({kind} {i + 1})");

    private T Pick<T>(IReadOnlyList<T> values) => values[random.Next(values.Count)];

    private List<T> Some<T>(List<T> values, int max) => [.. Enumerable.Range(0, random.Next(max + 1)).Select(_ => Pick(values)).Distinct()];
}
