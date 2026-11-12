using MaxPlanner.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Presentation.Common;

/// <summary>
/// The one place an unexpected failure of a UI action ends (A-02): the exception is logged, the user sees the
/// unexpected-error text, and the app keeps running. Async view-model commands run through
/// <see cref="RunGuardedAsync"/>, and work that code-behind starts itself through <see cref="Forget"/>. A cancellation
/// is not a failure and is ignored; a fatal exception (<see cref="OutOfMemoryException"/>) is never caught. Use on the
/// UI thread.
/// </summary>
public static partial class UiTasks
{
    private static ILogger? s_logger;
    private static Func<WorkspaceContext?>? s_currentContext;

    /// <summary>
    /// Called once at composition: where failures are logged, and the workspace whose save status and announcements
    /// show them (none during startup or recovery; the failure is then only logged).
    /// </summary>
    public static void Configure(ILogger logger, Func<WorkspaceContext?> currentContext)
    {
        s_logger = logger ?? throw new ArgumentNullException(nameof(logger));
        s_currentContext = currentContext ?? throw new ArgumentNullException(nameof(currentContext));
    }

    /// <summary>Runs an async UI action; an unexpected failure is reported (<see cref="Report"/>) instead of thrown.</summary>
    public static async Task RunGuardedAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Lets work that code-behind started run on, ending in <see cref="Report"/> if it fails. Prefer a view-model command
    /// wherever a control can bind one. Only a fatal exception leaves this method, on the UI thread.
    /// </summary>
    public static async void Forget(this Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Logs an unexpected failure at Error (event 4002) and shows <c>Error_Unexpected</c> in the save status, with its
    /// App InfoBar, and as a screen-reader announcement. A cancellation is ignored. Open dialogs and drafts are left as
    /// they are; an editor that catches its own failure resets its state first and passes its own context.
    /// </summary>
    /// <param name="context">The workspace to show the failure in; null uses the current one from <see cref="Configure"/>.</param>
    public static void Report(Exception exception, WorkspaceContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
        {
            return;
        }

        if (s_logger is { } logger)
        {
            Log.ActionFailed(logger, exception);
        }

        if ((context ?? s_currentContext?.Invoke()) is { } shown)
        {
            var message = shown.Strings.GetString("Error_Unexpected");
            shown.SaveStatus.ShowUnexpectedFailure(message);
            shown.Announce(message, isFailure: true);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 4002, Level = LogLevel.Error, Message = "A UI action failed unexpectedly; the error was shown and the app keeps running.")]
        public static partial void ActionFailed(ILogger logger, Exception exception);
    }
}
