using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Common;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Tests.Common;
using MaxPlanner.Presentation.Tests.Testing;
using MaxPlanner.Presentation.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// The one "run a write command" sequence (C-05): editing blocked, the operation ID, the save indicator, the typed
/// failure, the announcements, the display wait, the busy scope, and an exception reported once (ADR 0029). The tests
/// configure <see cref="UiTasks"/>, so they run in its collection.
/// </summary>
[Collection(nameof(UiTasksConfiguration))]
[Trait("Req", "T-07")]
public sealed class CommandRunnerTests
{
    private static readonly StorageFailureError DiskFull = new(StorageFailureKind.DiskFull);

    [Fact]
    public void A_committed_command_completes_the_indicator_before_waiting_for_the_display()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        SaveState? stateWhileWaiting = null;
        var request = probe.Request(Ok(42)) with
        {
            WhenDisplayed = _ =>
            {
                stateWhileWaiting = h.Workspace.SaveStatus.State;
                return Task.CompletedTask;
            },
        };

        var run = h.Await(probe.Runner.RunAsync(request));

        run.IsSuccess.Should().BeTrue();
        run.Outcome!.Value.Should().Be(42);
        run.Message.Should().BeNull();
        run.RetryOperation.Should().BeNull();
        run.Operation.Should().Be(probe.Operations.Single());
        stateWhileWaiting.Should().Be(SaveState.Saved);
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Saved);
        probe.Busy.Should().Equal(true, false);
        probe.Announced.Should().BeEmpty();
    }

    [Fact]
    public void A_completion_text_ends_the_indicator_after_the_display_wait()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var steps = new List<string>();
        var request = probe.Request(Ok(1)) with
        {
            WhenDisplayed = _ =>
            {
                steps.Add("wait:" + h.Workspace.SaveStatus.State);
                return Task.CompletedTask;
            },
            Completion = _ =>
            {
                steps.Add("complete");
                return new CommandResultText("Moved", "Open card", () => Task.CompletedTask);
            },
        };

        h.Await(probe.Runner.RunAsync(request)).IsSuccess.Should().BeTrue();

        steps.Should().Equal("wait:Saving", "complete");
        h.Workspace.SaveStatus.Text.Should().Be("Moved");
        h.Workspace.SaveStatus.ResultActionText.Should().Be("Open card");
    }

    [Fact]
    public void The_display_timeout_waits_until_the_store_shows_the_commit()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var request = probe.Request(Ok(1, sequence: h.Store.Sequence + 1)) with { DisplayTimeout = TimeSpan.FromSeconds(30) };

        var run = probe.Runner.RunAsync(request);
        h.Settle();

        run.IsCompleted.Should().BeFalse("the store has not reached the sequence yet");
        h.External(u => u.GeneralTodos.AddGeneralTodo.ExecuteAsync(
            new Application.Checklists.AddGeneralTodoCommand(OperationId.New(), new Domain.Identifiers.GeneralTodoId(Guid.NewGuid()), "Next"), CancellationToken.None));
        h.Await(run).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_typed_failure_is_reported_so_a_validation_refusal_is_rejected_and_announced_as_asked()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var request = probe.Request(Fails(new ValidationFailedError(new DomainError(DomainErrorCodes.Required, "Title")))) with
        {
            FailureAnnouncement = f => "Announced: " + f.Message,
        };

        var run = h.Await(probe.Runner.RunAsync(request));

        run.Status.Should().Be(CommandRunStatus.Failed);
        run.Failure!.Error.Should().BeOfType<ValidationFailedError>();
        run.Message.Should().Be("Field_Title is required.");
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Idle, "Report rejects a validation refusal");
        probe.Announced.Should().Equal(new Announcement("Announced: Field_Title is required.", true));
        probe.Busy.Should().Equal(true, false);
    }

    [Fact]
    public void A_typed_failure_with_fail_fails_the_indicator_even_for_a_validation_refusal_and_announces_nothing_by_default()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var request = probe.Request(Fails(new ValidationFailedError(new DomainError(DomainErrorCodes.Required, "Title")))) with
        {
            FailureIndicator = CommandFailureIndicator.Fail,
        };

        h.Await(probe.Runner.RunAsync(request)).Status.Should().Be(CommandRunStatus.Failed);

        h.Workspace.SaveStatus.State.Should().Be(SaveState.Failed);
        probe.Announced.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void While_editing_is_blocked_nothing_is_sent_and_the_text_is_returned(bool announces)
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        h.Workspace.Context.IsEditingBlocked = true;

        probe.Runner.IsEditingBlocked.Should().BeTrue();
        var run = h.Await(probe.Runner.RunAsync(probe.Request(Ok(1)) with { AnnouncesBlocked = announces }));

        (run.Status, run.Message, run.Operation).Should().Be((CommandRunStatus.Blocked, "Error_EditingBlocked", (OperationId?)null));
        probe.Operations.Should().BeEmpty();
        probe.Busy.Should().BeEmpty();
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Idle);
        probe.Announced.Should().Equal(announces ? new[] { new Announcement("Error_EditingBlocked", true) } : Array.Empty<Announcement>());
    }

    [Fact]
    [Trait("Req", "T-05")]
    public void A_given_operation_is_reused_and_only_an_unknown_outcome_asks_for_it_again()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var operation = OperationId.New();

        var unknown = h.Await(probe.Runner.RunAsync(probe.Request(Fails(new CommitOutcomeUnknownError(operation))) with { Operation = operation }));
        var final = h.Await(probe.Runner.RunAsync(probe.Request(Fails(DiskFull)) with { Operation = operation }));

        probe.Operations.Should().Equal(operation, operation);
        unknown.RetryOperation.Should().Be(operation);
        final.RetryOperation.Should().BeNull("a storage failure is final, so a retry is a new intent");
    }

    [Fact]
    public void A_thrown_command_ends_the_indicator_and_the_busy_scope_and_is_reported_once()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var log = Configure(h);
        try
        {
            var run = h.Await(probe.Runner.RunAsync(probe.Request(_ => throw new InvalidOperationException("defect"))));

            run.Status.Should().Be(CommandRunStatus.Unexpected);
            run.Message.Should().Be("Error_Unexpected");
            run.Outcome.Should().BeNull();
            run.RetryOperation.Should().Be(probe.Operations.Single(), "a throw leaves the outcome unknown (T-05)");
            probe.Busy.Should().Equal(true, false);
            var status = h.Workspace.SaveStatus;
            (status.IsSaving, status.IsFailed, status.IsFailureNoticeOpen).Should().Be((false, true, true));
            probe.Announced.Should().Equal(new Announcement("Error_Unexpected", true));
            log.Should().Equal("defect");
        }
        finally
        {
            Reset();
        }
    }

    [Fact]
    public void A_faulted_command_task_is_handled_like_a_throw()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var log = Configure(h);
        try
        {
            var run = h.Await(probe.Runner.RunAsync(probe.Request(_ => Task.FromException<AppResult<CommandOutcome<int>>>(new InvalidOperationException("defect")))));

            run.Status.Should().Be(CommandRunStatus.Unexpected);
            h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
            log.Should().ContainSingle();
        }
        finally
        {
            Reset();
        }
    }

    [Fact]
    public void A_display_wait_that_throws_after_the_commit_is_reported_once_and_keeps_the_outcome()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var log = Configure(h);
        try
        {
            var request = probe.Request(Ok(7)) with { WhenDisplayed = _ => throw new InvalidOperationException("wait") };

            var run = h.Await(probe.Runner.RunAsync(request));

            run.Status.Should().Be(CommandRunStatus.Unexpected);
            run.Outcome!.Value.Should().Be(7, "the command committed before the wait failed");
            run.RetryOperation.Should().Be(run.Operation, "the retry is recognised by the ledger (T-05)");
            probe.Busy.Should().Equal(true, false);
            (h.Workspace.SaveStatus.IsSaving, h.Workspace.SaveStatus.IsFailed).Should().Be((false, true));
            probe.Announced.Should().ContainSingle();
            log.Should().Equal("wait");
        }
        finally
        {
            Reset();
        }
    }

    [Fact]
    public void A_completion_that_throws_still_ends_the_indicator()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var request = probe.Request(Ok(1)) with { Completion = _ => throw new InvalidOperationException("completion") };

        var run = h.Await(probe.Runner.RunAsync(request));

        run.Status.Should().Be(CommandRunStatus.Unexpected);
        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
        probe.Announced.Should().ContainSingle();
    }

    [Fact]
    public void A_cancellation_ends_the_indicator_and_is_not_reported()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var log = Configure(h);
        try
        {
            var run = h.Await(probe.Runner.RunAsync(probe.Request(_ => throw new OperationCanceledException())));

            run.Status.Should().Be(CommandRunStatus.Unexpected);
            probe.Busy.Should().Equal(true, false);
            (h.Workspace.SaveStatus.IsSaving, h.Workspace.SaveStatus.IsFailed).Should().Be((false, false));
            probe.Announced.Should().BeEmpty();
            log.Should().BeEmpty();
        }
        finally
        {
            Reset();
        }
    }

    [Fact]
    public void A_throw_ends_only_its_own_command_while_another_is_in_flight()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var hold = new TaskCompletionSource<AppResult<CommandOutcome<int>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = probe.Runner.RunAsync(probe.Request(_ => hold.Task));

        h.Await(probe.Runner.RunAsync(probe.Request(_ => throw new InvalidOperationException("defect"))));
        h.Workspace.SaveStatus.IsSaving.Should().BeTrue("the held command is still in flight");

        hold.SetResult(AppResult.Success(new CommandOutcome<int>(1, 0, false)));
        h.Await(held).IsSuccess.Should().BeTrue();
        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
    }

    [Fact]
    public void A_change_handler_that_throws_while_the_indicator_completes_does_not_end_another_commands_count()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var status = h.Workspace.SaveStatus;
        h.Await(probe.Runner.RunAsync(probe.Request(Fails(DiskFull))));
        status.FailureMessage.Should().NotBeNull("completing will clear it, which raises a notification");
        var hold = new TaskCompletionSource<AppResult<CommandOutcome<int>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = probe.Runner.RunAsync(probe.Request(_ => hold.Task));
        var armed = true;
        status.PropertyChanged += (_, _) =>
        {
            if (armed)
            {
                armed = false;
                throw new InvalidOperationException("handler");
            }
        };

        var run = h.Await(probe.Runner.RunAsync(probe.Request(Ok(1))));

        run.Status.Should().Be(CommandRunStatus.Unexpected);
        status.IsSaving.Should().BeTrue("the held command still owns its in-flight count");
        hold.SetResult(AppResult.Success(new CommandOutcome<int>(2, 0, false)));
        h.Await(held).IsSuccess.Should().BeTrue();
        status.IsSaving.Should().BeFalse();
    }

    [Fact]
    public void A_change_handler_that_throws_while_the_indicator_begins_still_ends_this_runs_count()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var status = h.Workspace.SaveStatus;
        var armed = true;
        status.PropertyChanged += (_, _) =>
        {
            if (armed)
            {
                armed = false;
                throw new InvalidOperationException("handler");
            }
        };

        var run = h.Await(probe.Runner.RunAsync(probe.Request(Ok(1))));

        run.Status.Should().Be(CommandRunStatus.Unexpected);
        probe.Operations.Should().BeEmpty("Begin threw before the command was sent");
        status.IsSaving.Should().BeFalse();
        var hold = new TaskCompletionSource<AppResult<CommandOutcome<int>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = probe.Runner.RunAsync(probe.Request(_ => hold.Task));
        status.IsSaving.Should().BeTrue();
        hold.SetResult(AppResult.Success(new CommandOutcome<int>(2, 0, false)));
        h.Await(held);
        status.IsSaving.Should().BeFalse();
    }

    [Fact]
    public void A_busy_flag_that_throws_when_set_is_reported_once_and_nothing_is_sent()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var log = Configure(h);
        try
        {
            var cleared = 0;
            var request = probe.Request(Ok(1)) with
            {
                Busy = busy => _ = busy ? throw new InvalidOperationException("busy") : ++cleared,
            };

            var run = h.Await(probe.Runner.RunAsync(request));

            run.Status.Should().Be(CommandRunStatus.Unexpected);
            probe.Operations.Should().BeEmpty();
            cleared.Should().Be(1);
            h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
            log.Should().Equal("busy");
        }
        finally
        {
            Reset();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_busy_flag_that_throws_when_cleared_never_escapes_and_is_reported_once(bool commandThrows)
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);
        var log = Configure(h);
        try
        {
            var request = probe.Request(commandThrows ? _ => throw new InvalidOperationException("command") : Ok(1)) with
            {
                Busy = busy => _ = busy ? 0 : throw new InvalidOperationException("clear"),
            };

            var run = h.Await(probe.Runner.RunAsync(request));

            run.IsSuccess.Should().Be(!commandThrows, "a failure clearing the flag does not change the run's result");
            log.Should().Equal(commandThrows ? "command" : "clear");
            probe.Announced.Should().ContainSingle();
        }
        finally
        {
            Reset();
        }
    }

    [Fact]
    public void A_cancellation_has_no_text_to_show_and_is_not_announced()
    {
        using var h = WorkspaceHarness.Create();
        var probe = new Probe(h);

        var run = h.Await(probe.Runner.RunAsync(probe.Request(_ => throw new OperationCanceledException())));

        (run.Status, run.Message).Should().Be((CommandRunStatus.Unexpected, (string?)null));
        probe.Announced.Should().BeEmpty();
        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
    }

    private static Func<OperationId, Task<AppResult<CommandOutcome<int>>>> Ok(int value, long sequence = 0) =>
        _ => Task.FromResult(AppResult.Success(new CommandOutcome<int>(value, sequence, false)));

    private static Func<OperationId, Task<AppResult<CommandOutcome<int>>>> Fails(AppError error) =>
        _ => Task.FromResult(AppResult.Failure<CommandOutcome<int>>(error));

    private static List<string?> Configure(WorkspaceHarness h)
    {
        var log = new List<string?>();
        UiTasks.Configure(new ListLogger(log), () => h.Workspace.Context);
        return log;
    }

    private static void Reset() => UiTasks.Configure(NullLogger.Instance, () => null);

    /// <summary>Records what the runner did around a scripted command: the operations it sent, the busy flag, the announcements.</summary>
    private sealed class Probe
    {
        public Probe(WorkspaceHarness h)
        {
            Runner = h.Workspace.Context.Commands;
            h.Workspace.Context.Announced += (_, a) => Announced.Add(a);
        }

        public ICommandRunner Runner { get; }

        public List<OperationId> Operations { get; } = [];

        public List<bool> Busy { get; } = [];

        public List<Announcement> Announced { get; } = [];

        public CommandRequest<int> Request(Func<OperationId, Task<AppResult<CommandOutcome<int>>>> execute) =>
            new("Record_Task", op =>
            {
                Operations.Add(op);
                return execute(op);
            })
            {
                Busy = Busy.Add,
            };
    }

    private sealed class ListLogger(List<string?> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Add(exception?.Message);
    }
}
