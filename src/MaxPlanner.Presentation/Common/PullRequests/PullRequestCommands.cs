using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Common.PullRequests;

/// <summary>
/// "Create pull request…" from every entry point (B-18, design §5.11): the editors' follow-ups, the Board card menu and
/// the Grid row menu. The dialog always starts from the committed (saved) record. Use on the UI thread.
/// </summary>
public sealed class PullRequestCommands
{
    private readonly WorkspaceContext _context;

    internal PullRequestCommands(WorkspaceContext context) => _context = context;

    /// <summary>Settings › Integrations › Azure DevOps is on: the menu items are shown (design §5.11).</summary>
    public bool IsAvailable => _context.IsAzureDevOpsEnabled;

    public Task CreateForTaskAsync(TaskId id) =>
        _context.Store.Tasks.Lookup(id) is { HasValue: true } task ? ShowAsync(PullRequestSource.From(task.Value)) : Task.CompletedTask;

    /// <summary>A Subtask card uses its parent's CR and task numbers (B-16.2).</summary>
    public Task CreateForSubtaskCardAsync(SubtaskCardId id) =>
        _context.Store.SubtaskCards.Lookup(id) is { HasValue: true } card ? ShowAsync(PullRequestSource.From(card.Value)) : Task.CompletedTask;

    public Task CreateForChangeRequestAsync(ChangeRequestId id) =>
        _context.Store.ChangeRequests.Lookup(id) is { HasValue: true } cr ? ShowAsync(PullRequestSource.From(cr.Value)) : Task.CompletedTask;

    /// <summary>A Board card or Grid row: a task or a Subtask card.</summary>
    public Task CreateAsync(WorkItemRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record switch
        {
            { Task: { } task } => CreateForTaskAsync(task.Id),
            { Card: { } card } => CreateForSubtaskCardAsync(card.Id),
            _ => Task.CompletedTask,
        };
    }

    private async Task ShowAsync(PullRequestSource source)
    {
        var dialog = new CreatePullRequestViewModel(
            source, _context.Preferences.Current.AzureDevOps, _context.AzureDevOps, _context.Strings, _context, _context.UrlLauncher);
        await _context.ShowDialogAsync(dialog).ConfigureAwait(true);

        // Dialogs never stack (design §5.0): Settings opens only after the dialog closed.
        if (dialog.IsOpenSettingsRequested)
        {
            _context.RequestAzureDevOpsSettings();
        }
    }
}
