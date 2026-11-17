using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Domain.Identifiers;
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

/// <summary>
/// Commands that existed before A-02 run through the same guard, one test per pattern: a generated command that
/// disables itself while running, a hand-built row command, and a command whose action comes from elsewhere.
/// </summary>
[Collection(nameof(UiTasksConfiguration))]
public sealed class GuardedExistingCommandTests
{
    [Fact]
    public void A_generated_command_that_throws_is_reported_and_still_disables_itself_while_running()
    {
        using var h = WorkspaceHarness.Create();
        var announced = Configure(h.Workspace.Context);
        try
        {
            var general = h.Workspace.Checklists.General;
            general.NewItemText = "Call the bank";
            var hold = h.Coordinator.HoldNext();
            h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

            var run = general.AddItemCommand.ExecuteAsync(null);
            general.AddItemCommand.CanExecute(null).Should().BeFalse("a command without concurrent runs still disables its button while it runs");
            hold.SetResult();
            h.Await(run);

            announced.Should().ContainSingle().Which.Should().Be(new Announcement("Error_Unexpected", true));
            general.AddItemCommand.IsRunning.Should().BeFalse();
        }
        finally
        {
            Reset();
        }
    }

    [Fact]
    public void A_hand_built_row_command_that_throws_is_reported()
    {
        using var h = WorkspaceHarness.Create(async s =>
        {
            foreach (var title in (string[])["First", "Second"])
            {
                (await new CreateReview(s.Coordinator).ExecuteAsync(
                    new CreateReviewCommand(OperationId.New(), new ReviewItemId(Guid.NewGuid()), new ReviewDraft(title, null, null, [], null, null), null, false), default))
                    .IsSuccess.Should().BeTrue();
            }
        });
        var announced = Configure(h.Workspace.Context);
        try
        {
            var reviews = h.Workspace.Checklists.Reviews;
            h.PumpUntil(() => reviews.Open.Count == 2);
            var second = reviews.Open[1];
            second.MoveUpCommand.CanExecute(null).Should().BeTrue();
            h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

            h.Await(second.MoveUpCommand.ExecuteAsync(null));

            announced.Should().Contain(new Announcement("Error_Unexpected", true));
        }
        finally
        {
            Reset();
        }
    }

    [Fact]
    public void A_command_whose_action_throws_is_reported_in_the_current_workspace()
    {
        using var h = WorkspaceHarness.Create();
        var announced = Configure(h.Workspace.Context);
        try
        {
            var status = h.Workspace.Context.SaveStatus;
            status.Complete("Moved", "Open card", () => Task.FromException(new InvalidOperationException("defect")));

            h.Await(status.RunResultActionCommand.ExecuteAsync(null));

            announced.Should().ContainSingle().Which.Should().Be(new Announcement("Error_Unexpected", true));
            status.IsFailureNoticeOpen.Should().BeTrue();
        }
        finally
        {
            Reset();
        }
    }

    private static List<Announcement> Configure(WorkspaceContext context)
    {
        var announced = new List<Announcement>();
        context.Announced += (_, a) => announced.Add(a);
        UiTasks.Configure(NullLogger.Instance, () => context);
        return announced;
    }

    private static void Reset() => UiTasks.Configure(NullLogger.Instance, () => null);
}
