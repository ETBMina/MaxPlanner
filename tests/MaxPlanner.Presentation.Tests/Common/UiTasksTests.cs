using AwesomeAssertions;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Tests.Testing;
using MaxPlanner.Presentation.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>UiTasks holds process-wide configuration, so the tests that configure it never run beside other tests.</summary>
[CollectionDefinition(nameof(UiTasksConfiguration), DisableParallelization = true)]
public sealed class UiTasksConfiguration;

/// <summary>
/// The one place an unexpected UI-action failure ends: it is logged at Error (event 4002) and shown once with the
/// unexpected-error text, the caller does not see it, a cancellation is ignored, and a fatal exception is not caught.
/// </summary>
[Collection(nameof(UiTasksConfiguration))]
public sealed class UiTasksTests
{
    [Fact]
    public void A_failed_guarded_action_is_logged_and_shown_once_and_does_not_throw()
    {
        using var h = WorkspaceHarness.Create();
        var (log, announced) = Configure(h.Workspace.Context);
        try
        {
            h.Await(UiTasks.RunGuardedAsync(() => Task.FromException(new InvalidOperationException("defect"))));

            log.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Error, 4002, "defect"));
            announced.Should().Equal(new Announcement("Error_Unexpected", true));
            h.Workspace.Context.SaveStatus.FailureMessage.Should().Be("Error_Unexpected");
            h.Workspace.Context.SaveStatus.IsFailureNoticeOpen.Should().BeTrue();
            h.Workspace.Context.SaveStatus.IsFailed.Should().BeTrue();
        }
        finally
        {
            Reset();
        }
    }

    [Fact]
    public void Forget_reports_a_faulted_task_in_the_current_workspace_without_throwing()
    {
        using var h = WorkspaceHarness.Create();
        var (log, announced) = Configure(h.Workspace.Context);
        try
        {
            Task.FromException(new InvalidOperationException("defect")).Forget();

            log.Entries.Should().ContainSingle();
            announced.Should().ContainSingle();
        }
        finally
        {
            Reset();
        }
    }

    [Fact]
    public void A_cancellation_is_not_a_failure()
    {
        using var h = WorkspaceHarness.Create();
        var (log, announced) = Configure(h.Workspace.Context);
        try
        {
            h.Await(UiTasks.RunGuardedAsync(() => Task.FromException(new OperationCanceledException())));
            Task.FromCanceled(new CancellationToken(canceled: true)).Forget();

            log.Entries.Should().BeEmpty();
            announced.Should().BeEmpty();
            h.Workspace.Context.SaveStatus.IsFailed.Should().BeFalse();
        }
        finally
        {
            Reset();
        }
    }

    [Fact]
    public async Task An_out_of_memory_exception_is_not_swallowed()
    {
        using var h = WorkspaceHarness.Create();
        var (log, _) = Configure(h.Workspace.Context);
        try
        {
#pragma warning disable CA2201 // The test needs the fatal exception type itself.
            await FluentActions.Awaiting(() => UiTasks.RunGuardedAsync(() => Task.FromException(new OutOfMemoryException())))
#pragma warning restore CA2201
                .Should().ThrowAsync<OutOfMemoryException>();
            log.Entries.Should().BeEmpty();
        }
        finally
        {
            Reset();
        }
    }

    private static (RecordingLogger Log, List<Announcement> Announced) Configure(WorkspaceContext context)
    {
        var log = new RecordingLogger();
        var announced = new List<Announcement>();
        context.Announced += (_, a) => announced.Add(a);
        UiTasks.Configure(log, () => context);
        return (log, announced);
    }

    private static void Reset() => UiTasks.Configure(NullLogger.Instance, () => null);

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, int EventId, string? Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId.Id, exception?.Message));
    }
}
