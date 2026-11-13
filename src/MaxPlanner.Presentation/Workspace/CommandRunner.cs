using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Workspace;

/// <summary>
/// Runs one write command with the shared save indicator (T-07, C-05): checks that editing is allowed, runs the use case
/// under an operation ID, ends the indicator with the actual result, announces what the caller asked for, and waits until
/// the commit is displayed. Use on the UI thread.
/// </summary>
/// <remarks>
/// For a non-null request, <see cref="RunAsync{T}"/> does not throw (a fatal <see cref="OutOfMemoryException"/> aside).
/// When the command, the display wait, the completion, the announcement or the busy flag throws, in this order: the
/// indicator leaves the in-flight state this run started, the failure is reported once through
/// <see cref="UiTasks.Report"/> (ADR 0029), the busy flag is cleared, and the caller gets an
/// <see cref="CommandRunStatus.Unexpected"/> result with the unexpected-error text, which it then shows inline in the same
/// UI-thread step. A cancellation ends the indicator but is not reported and has no text to show.
/// </remarks>
public interface ICommandRunner
{
    /// <summary>T-07: true while the store is not Ready or the app is closing; no command may start then.</summary>
    bool IsEditingBlocked { get; }

    Task<CommandRun<T>> RunAsync<T>(CommandRequest<T> request);
}

/// <summary>How a typed failure ends the save indicator.</summary>
public enum CommandFailureIndicator
{
    /// <summary><see cref="SaveStatusViewModel.Report"/>: a validation refusal is rejected, anything else fails.</summary>
    Report,

    /// <summary><see cref="SaveStatusViewModel.Fail"/> for every failure (the Board's moves).</summary>
    Fail,
}

/// <summary>The text that completes the save indicator in place of "Saved", with an optional follow-up link (design §4.5).</summary>
public sealed record CommandResultText(string Message, string? ActionText = null, Func<Task>? Action = null);

/// <summary>One write command and how its result is shown.</summary>
/// <typeparam name="T">The value the command returns.</typeparam>
/// <param name="RecordKey">Resource key of the record noun in failure texts, for example <c>Record_Task</c>.</param>
/// <param name="Execute">Runs the use case under the given operation ID.</param>
public sealed record CommandRequest<T>(string RecordKey, Func<OperationId, Task<AppResult<CommandOutcome<T>>>> Execute)
{
    /// <summary>
    /// T-05: the operation to reuse when the caller retries after an unknown outcome
    /// (<see cref="CommandRun{T}.RetryOperation"/>); null starts a new one.
    /// </summary>
    public OperationId? Operation { get; init; }

    public CommandFailureIndicator FailureIndicator { get; init; } = CommandFailureIndicator.Report;

    /// <summary>The text announced for a typed failure; null announces nothing (the caller shows or announces it).</summary>
    public Func<CommandFailure, string>? FailureAnnouncement { get; init; }

    /// <summary>Announce the editing-blocked text as a failure when editing is blocked; otherwise only return it.</summary>
    public bool AnnouncesBlocked { get; init; }

    /// <summary>Waits this long at most until the commit is displayed (T-07); null does not wait.</summary>
    public TimeSpan? DisplayTimeout { get; init; }

    /// <summary>Waits until the commit is displayed in the caller's own way; used instead of <see cref="DisplayTimeout"/>.</summary>
    public Func<CommandOutcome<T>, Task>? WhenDisplayed { get; init; }

    /// <summary>
    /// Completes the indicator with a result text in place of "Saved". It runs after the display wait, so it can name
    /// what the store now shows; without it the indicator completes before the wait.
    /// </summary>
    public Func<CommandOutcome<T>, CommandResultText>? Completion { get; init; }

    /// <summary>
    /// The caller's busy flag: set to true just before the command starts and to false when the run ends, on every path.
    /// Not set when editing is blocked.
    /// </summary>
    public Action<bool>? Busy { get; init; }
}

/// <summary>Request shapes several sites share.</summary>
public static class CommandRequest
{
    /// <summary>The announced failure text: the failure's own message.</summary>
    public static string FailureText(CommandFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Message;
    }

    /// <summary>
    /// A page command whose result is only announced (card and row menus, Calendar): the blocked text and the failure text
    /// are announced, a failure is <see cref="CommandFailureIndicator.Report"/>ed, and nothing waits for the display.
    /// </summary>
    public static CommandRequest<T> Announced<T>(string recordKey, Func<OperationId, Task<AppResult<CommandOutcome<T>>>> execute) =>
        new(recordKey, execute) { AnnouncesBlocked = true, FailureAnnouncement = FailureText };
}

public enum CommandRunStatus
{
    Succeeded,

    /// <summary>Editing was blocked, so nothing was sent.</summary>
    Blocked,

    /// <summary>The command returned a typed failure (<see cref="CommandRun{T}.Failure"/>).</summary>
    Failed,

    /// <summary>The run threw; the failure was reported (a cancellation was not).</summary>
    Unexpected,
}

/// <summary>The result of <see cref="ICommandRunner.RunAsync{T}"/>.</summary>
public sealed class CommandRun<T>
{
    private CommandRun(CommandRunStatus status, OperationId? operation, CommandOutcome<T>? outcome, CommandFailure? failure, string? message)
    {
        Status = status;
        Operation = operation;
        Outcome = outcome;
        Failure = failure;
        Message = message;
    }

    public CommandRunStatus Status { get; }

    public bool IsSuccess => Status == CommandRunStatus.Succeeded;

    /// <summary>The operation the command ran under; null when editing was blocked.</summary>
    public OperationId? Operation { get; }

    /// <summary>The committed outcome; also set when the command committed and the display wait threw.</summary>
    public CommandOutcome<T>? Outcome { get; }

    /// <summary>The typed failure, when the status is <see cref="CommandRunStatus.Failed"/>.</summary>
    public CommandFailure? Failure { get; }

    /// <summary>
    /// The text to show inline: the blocked text, the failure text, or the unexpected-error text; null on success and
    /// after a cancellation (nothing to show). Test <see cref="IsSuccess"/>, not this, for success.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// T-05: the operation a retry of the same intent must reuse, because the outcome is unknown (an unknown commit
    /// outcome, or a throw); null when a retry is a new intent.
    /// </summary>
    public OperationId? RetryOperation =>
        Status == CommandRunStatus.Unexpected || Failure is { RequiresSameOperationOnRetry: true } ? Operation : null;

    internal static CommandRun<T> Succeeded(OperationId operation, CommandOutcome<T> outcome) => new(CommandRunStatus.Succeeded, operation, outcome, null, null);

    internal static CommandRun<T> Blocked(string message) => new(CommandRunStatus.Blocked, null, null, null, message);

    internal static CommandRun<T> Failed(CommandFailure failure) => new(CommandRunStatus.Failed, failure.OperationId, null, failure, failure.Message);

    internal static CommandRun<T> Unexpected(OperationId operation, CommandOutcome<T>? outcome, string? message) =>
        new(CommandRunStatus.Unexpected, operation, outcome, null, message);
}

/// <summary>The workspace's <see cref="ICommandRunner"/>: its save status, announcements, and display wait.</summary>
internal sealed class CommandRunner(WorkspaceContext context) : ICommandRunner
{
    public bool IsEditingBlocked => context.IsEditingBlocked;

    public async Task<CommandRun<T>> RunAsync<T>(CommandRequest<T> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var strings = context.Strings;
        if (context.IsEditingBlocked)
        {
            var blocked = strings.GetString("Error_EditingBlocked");
            if (request.AnnouncesBlocked)
            {
                Try(() => context.Announce(blocked, isFailure: true));
            }

            return CommandRun<T>.Blocked(blocked);
        }

        var status = context.SaveStatus;
        var operation = request.Operation ?? OperationId.New();

        // The indicator's in-flight count: this run owns one from the moment Begin is called (Begin counts first, before
        // it raises any notification) until an end call is made (each end call uncounts first). The flags change before
        // the calls, so a change handler that throws inside a call never makes the catch end the indicator twice and take
        // another command's count.
        var ownsInFlight = false;
        var isReported = false;
        CommandOutcome<T>? outcome = null;
        try
        {
            request.Busy?.Invoke(true);
            ownsInFlight = true;
            status.Begin();
            var result = await request.Execute(operation).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                var failure = CommandFailure.From(result.Error!, operation, request.RecordKey, strings);
                ownsInFlight = false;
                if (request.FailureIndicator == CommandFailureIndicator.Fail)
                {
                    status.Fail(failure);
                }
                else
                {
                    status.Report(failure);
                }

                if (request.FailureAnnouncement?.Invoke(failure) is { } announcement)
                {
                    context.Announce(announcement, isFailure: true);
                }

                return CommandRun<T>.Failed(failure);
            }

            outcome = result.Value;
            if (request.Completion is null)
            {
                ownsInFlight = false;
                status.Complete();
            }

            await WhenDisplayedAsync(request, outcome).ConfigureAwait(true);
            if (request.Completion is { } completion)
            {
                var text = completion(outcome);
                ownsInFlight = false;
                status.Complete(text.Message, text.ActionText, text.Action);
            }

            return CommandRun<T>.Succeeded(operation, outcome);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            isReported = ex is not OperationCanceledException;
            return Unexpected(operation, outcome, ex, ownsInFlight);
        }
        finally
        {
            EndBusy(request, isReported);
        }
    }

    /// <summary>
    /// Ends a run that threw: the indicator leaves its in-flight state when this run still owns a count, then the failure
    /// is reported once. A cancellation is neither reported nor announced and shows nothing inline. Nothing here throws: a failure while ending is dropped,
    /// because the original failure is the one being reported.
    /// </summary>
    private CommandRun<T> Unexpected<T>(OperationId operation, CommandOutcome<T>? outcome, Exception exception, bool ownsInFlight)
    {
        if (ownsInFlight)
        {
            Try(context.SaveStatus.Reject);
        }

        if (exception is OperationCanceledException)
        {
            return CommandRun<T>.Unexpected(operation, outcome, message: null);
        }

        var message = context.Strings.GetString("Error_Unexpected");
        Try(() => UiTasks.Report(exception, context));

        return CommandRun<T>.Unexpected(operation, outcome, message);
    }

    /// <summary>
    /// Clears the caller's busy flag. If that throws, the failure is reported, unless this run already reported one: the
    /// user then sees one failure, and the run's result stays as it was.
    /// </summary>
    private void EndBusy<T>(CommandRequest<T> request, bool isReported)
    {
        try
        {
            request.Busy?.Invoke(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!isReported)
            {
                Try(() => UiTasks.Report(ex, context));
            }
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Dropped on purpose: see the callers.
        }
    }

    private Task WhenDisplayedAsync<T>(CommandRequest<T> request, CommandOutcome<T> outcome) =>
        request.WhenDisplayed is { } wait ? wait(outcome)
        : request.DisplayTimeout is { } timeout ? context.WhenDisplayedAsync(outcome.CommitSequence, timeout)
        : Task.CompletedTask;
}
