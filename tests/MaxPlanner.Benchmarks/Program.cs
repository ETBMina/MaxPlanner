using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace MaxPlanner.Benchmarks;

/// <summary>
/// T-11 non-UI budgets on the benchmark fixture (Phase 17): snapshot load, projection store initial load, one commit to
/// its publication in the store, Dashboard recompute, filter apply, and export/import of the fixture.
/// </summary>
/// <remarks>
/// <para>Run from <c>scripts/run-performance.ps1</c>, or by hand:
/// <c>dotnet run -c Release --project tests/MaxPlanner.Benchmarks -p:Platform=x64 -- --filter *</c> with
/// <c>MAXPLANNER_FIXTURE</c> set to an app folder whose <c>data/data.db</c> holds the fixture
/// (<c>MaxPlanner.FixtureGenerator benchmark &lt;folder&gt;</c>). Every benchmark works on its own temporary copy of
/// that folder, so the fixture itself is never changed.</para>
/// <para>The in-process toolchain is used because the project is a WinUI-referencing, self-contained x64 project that
/// BenchmarkDotNet's generated child projects do not reproduce reliably. Results include the p95 column the T-11 budgets
/// are stated in.</para>
/// </remarks>
internal static class Program
{
    public static int Main(string[] args)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithId("InProcess"))
            .AddColumn(StatisticColumn.P95)
            .AddExporter(JsonExporter.Full);
        var artifacts = Environment.GetEnvironmentVariable("MAXPLANNER_BENCHMARK_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(artifacts))
        {
            config = config.WithArtifactsPath(artifacts);
        }

        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        return summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Any(r => !r.Success)) ? 1 : 0;
    }
}
