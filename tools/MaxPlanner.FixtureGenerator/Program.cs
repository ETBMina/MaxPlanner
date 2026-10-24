using System.Globalization;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.FixtureGenerator;

/// <summary>
/// Creates disposable datasets for UI evidence, manual checks, and the T-11 performance gate. Every record is created
/// through the real Application use cases on a real SQLite file, so the data passes the same validation, date
/// automation (B-04), and link rules (B-03) as data entered in the app.
/// </summary>
/// <remarks>
/// <para>Usage:</para>
/// <list type="bullet">
/// <item><c>MaxPlanner.FixtureGenerator &lt;app-folder&gt; [--extra-tasks N]</c>: the small demonstration dataset.</item>
/// <item><c>MaxPlanner.FixtureGenerator benchmark &lt;app-folder&gt; [--scale F] [--seed N]</c>: the T-11 benchmark
/// dataset (10,000 tasks, 5,000 CRs, 50,000 checklist items, 2,000 Subtask cards, 2,000 reviews, 5,000 General
/// to-dos, 20% of tasks archived). <c>--scale 0.1</c> makes a tenth of it for a quick dry run.</item>
/// <item><c>MaxPlanner.FixtureGenerator set-schema-version &lt;app-folder&gt; &lt;version&gt;</c>: writes
/// <c>PRAGMA user_version</c> of an existing dataset, so a release gate can prove the newer-schema refusal (T-03.8, exit
/// code 4). Use it only on a disposable copy.</item>
/// </list>
/// <para>The dataset is created at <c>&lt;app-folder&gt;/data/data.db</c>, the layout the portable EXE uses beside
/// itself; the folder must not contain a dataset yet. Never point the tool at real user data.</para>
/// </remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["benchmark", var folder, .. var options] => await RunBenchmarkAsync(folder, options),
                ["set-schema-version", var folder, var version] => SetSchemaVersion(folder, int.Parse(version, CultureInfo.InvariantCulture)),
                [var folder] when !folder.StartsWith('-') => await RunDemoAsync(folder, 0),
                [var folder, "--extra-tasks", var count] => await RunDemoAsync(folder, int.Parse(count, CultureInfo.InvariantCulture)),
                _ => Usage(),
            };
        }
        catch (FormatException)
        {
            return Usage();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  MaxPlanner.FixtureGenerator <app-folder> [--extra-tasks N]");
        Console.Error.WriteLine("  MaxPlanner.FixtureGenerator benchmark <app-folder> [--scale F] [--seed N]");
        Console.Error.WriteLine("  MaxPlanner.FixtureGenerator set-schema-version <app-folder> <version>");
        return 2;
    }

    private static async Task<int> RunDemoAsync(string folder, int extraTasks)
    {
        var paths = DataPaths.ForDirectory(Path.GetFullPath(folder));
        var created = await SqliteDataset.CreateNewAsync(paths);
        if (created.Dataset is not { } dataset)
        {
            await Console.Error.WriteLineAsync($"Could not create the dataset: {created.Status} {created.Error?.Code}");
            return 1;
        }

        await using (dataset)
        {
            await DemoDataset.CreateAsync(dataset, extraTasks);
            await PrintSummaryAsync(dataset, paths);
        }

        return 0;
    }

    private static async Task<int> RunBenchmarkAsync(string folder, string[] options)
    {
        var scale = 1.0;
        var seed = 20261023;
        for (var i = 0; i + 1 < options.Length; i += 2)
        {
            switch (options[i])
            {
                case "--scale":
                    scale = double.Parse(options[i + 1], CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = int.Parse(options[i + 1], CultureInfo.InvariantCulture);
                    break;
                default:
                    return Usage();
            }
        }

        if (scale is <= 0 or > 1)
        {
            await Console.Error.WriteLineAsync("--scale must be greater than 0 and at most 1.");
            return 2;
        }

        var paths = DataPaths.ForDirectory(Path.GetFullPath(folder));
        var created = await SqliteDataset.CreateNewAsync(paths);
        if (created.Dataset is not { } dataset)
        {
            await Console.Error.WriteLineAsync($"Could not create the dataset: {created.Status} {created.Error?.Code}");
            return 1;
        }

        await using (dataset)
        {
            var counts = BenchmarkCounts.Scaled(scale);
            var started = System.Diagnostics.Stopwatch.StartNew();
            await new BenchmarkDataset(dataset.Coordinator, new Random(seed), counts, DateOnly.FromDateTime(DateTime.Today)).CreateAsync(dataset.Snapshots);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Generated in {started.Elapsed.TotalSeconds:F1} s."));
            await PrintSummaryAsync(dataset, paths);
        }

        return 0;
    }

    /// <summary>T-03.8 gate helper: marks a disposable dataset as written by a newer app.</summary>
    private static int SetSchemaVersion(string folder, int version)
    {
        var paths = DataPaths.ForDirectory(Path.GetFullPath(folder));
        if (!File.Exists(paths.DatabaseFile))
        {
            Console.Error.WriteLine($"No dataset at {paths.DatabaseFile}.");
            return 1;
        }

        using var connection = new SqliteConnection($"Data Source={paths.DatabaseFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {version};");
        command.ExecuteNonQuery();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Set user_version = {version} in {paths.DatabaseFile}."));
        return 0;
    }

    private static async Task PrintSummaryAsync(SqliteDataset dataset, DataPaths paths)
    {
        var final = (await dataset.Snapshots.LoadAsync(CancellationToken.None)).Value;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Created {paths.DatabaseFile}: {final.ChangeRequests.Count} CRs, {final.Tasks.Count} tasks ({final.Tasks.Count(t => t.IsArchived)} archived), "
            + $"{final.SubtaskCards.Count} Subtask cards, {final.Tasks.Sum(t => t.ChecklistItems.Count)} checklist items, {final.ReviewItems.Count} reviews, "
            + $"{final.GeneralTodos.Count} General to-dos."));
    }
}
