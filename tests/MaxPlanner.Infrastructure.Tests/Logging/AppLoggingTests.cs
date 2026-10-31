using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Infrastructure.Backup;
using MaxPlanner.Infrastructure.Logging;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Storage;
using MaxPlanner.Infrastructure.Tests.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace MaxPlanner.Infrastructure.Tests.Logging;

[Trait("Req", "T-09")]
public sealed partial class AppLoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MaxPlanner Tëst Logs", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_bound_is_five_files_of_five_mebibytes()
    {
        AppLogging.FileSizeLimitBytes.Should().Be(5 * 1024 * 1024);
        AppLogging.RetainedFileCount.Should().Be(5);
    }

    [Fact]
    public void Logs_are_written_under_data_logs()
    {
        var paths = DataPaths.ForDirectory(_root);
        using (var factory = AppLogging.CreateFactory(paths))
        {
            var logger = factory.CreateLogger("Test");
            Probe(logger, 42);
        }

        var file = Directory.GetFiles(paths.LogsDirectory).Should().ContainSingle().Subject;
        Path.GetFileName(file).Should().MatchRegex(@"^maxplanner-\d{8}\.log$");
        File.ReadAllText(file).Should().Contain("Probe 42");
    }

    [Fact]
    public void Rolling_on_size_never_keeps_more_than_five_files()
    {
        var paths = DataPaths.ForDirectory(_root);
        const long limit = 2048;
        using (var factory = AppLogging.CreateFactory(paths, limit, LogEventLevel.Information))
        {
            var logger = factory.CreateLogger("Test");
            for (var i = 0; i < 400; i++)
            {
                Filler(logger, i);
            }
        }

        var files = Directory.GetFiles(paths.LogsDirectory);
        files.Length.Should().Be(AppLogging.RetainedFileCount);
        // The sink rolls once a file has reached the limit, so a file may exceed it by at most one event.
        files.Should().AllSatisfy(f => new FileInfo(f).Length.Should().BeLessThanOrEqualTo(limit + 256));
    }

    [Fact]
    public async Task Record_content_written_through_use_cases_never_reaches_the_log()
    {
        const string title = "Confidential Ωmega launch 7731";
        const string notes = "Private note body 5519";
        var paths = DataPaths.ForDirectory(_root);
        var factory = AppLogging.CreateFactory(paths);
        var time = new TestClock(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
        var created = await SqliteDataset.CreateNewAsync(paths, new SqliteDatasetOptions(time, factory));
        var dataset = created.Dataset!;
        try
        {
            var draft = new TaskContentDraft(title, null, null, null, [], notes, CustomValue.NoValues);
            var create = await new CreateTask(dataset.Coordinator).ExecuteAsync(
                new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), draft), Ct);
            create.IsSuccess.Should().BeTrue();

            // A rejected command is logged as a failure category only.
            var invalid = new TaskContentDraft(new string('x', 10_000), null, null, null, [], notes, CustomValue.NoValues);
            (await new CreateTask(dataset.Coordinator).ExecuteAsync(
                new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), invalid), Ct)).IsSuccess.Should().BeFalse();

            (await new DailyBackupService(dataset, time, "test", factory.CreateLogger<DailyBackupService>()).RunAsync(Ct))
                .Outcome.Should().Be(DailyBackupOutcome.Created);
        }
        finally
        {
            await dataset.DisposeAsync();
            factory.Dispose();
            SqliteConnection.ClearAllPools();
        }

        var log = string.Concat(Directory.GetFiles(paths.LogsDirectory).Select(File.ReadAllText));
        log.Should().Contain("Applied schema migration 1").And.Contain("Daily backup");
        log.Should().NotContain(title).And.NotContain("Ωmega").And.NotContain(notes);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Probe {Value}")]
    private static partial void Probe(ILogger logger, int value);

    [LoggerMessage(Level = LogLevel.Information, Message = "Filler line {Index} to make the files roll over quickly")]
    private static partial void Filler(ILogger logger, int index);
}
