using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.ChangeRequests;

/// <summary>
/// The CR commands every page and editor offers (B-03, design §5.3): open a CR, create one, and the editor's
/// follow-ups (create a task from the CR, open a linked task, open the CR that already uses a number). Dialogs
/// never stack (design §5.0), so a follow-up opens only after the CR editor closed. Use on the UI thread.
/// </summary>
public sealed class ChangeRequestCommands
{
    private readonly WorkspaceContext _context;

    internal ChangeRequestCommands(WorkspaceContext context) => _context = context;

    /// <summary>Opens the CR editor (Board/Grid CR references, the task editor's CR link).</summary>
    public Task OpenAsync(ChangeRequestId id) => RunEditorAsync(id);

    /// <summary>
    /// New CR. Calendar is its home (B-03.1, Phase 7); until then the Board offers it in its overflow menu.
    /// </summary>
    public Task NewAsync() => RunEditorAsync(null);

    private async Task RunEditorAsync(ChangeRequestId? id)
    {
        // The editor needs the loaded workflow (lookups and defaults); before the first load nothing opens.
        if (_context.Store.CurrentWorkflow is null)
        {
            return;
        }

        var editor = new CrEditorViewModel(_context, id);
        using (editor)
        {
            await _context.ShowDialogAsync(editor).ConfigureAwait(true);
        }

        switch (editor.FollowUp)
        {
            case CrEditorFollowUp.CreateTask when editor.SavedId is { } saved:
                // T-13: the task editor previews the saved CR by ID; no unsaved CR value is used.
                await _context.Tasks.NewLinkedAsync(saved).ConfigureAwait(true);
                break;
            case CrEditorFollowUp.OpenTask when editor.FollowUpTaskId is { } taskId:
                await _context.Tasks.OpenAsync(taskId).ConfigureAwait(true);
                break;
            case CrEditorFollowUp.OpenOtherCr when editor.DuplicateId is { } other:
                await RunEditorAsync(other).ConfigureAwait(true);
                break;
            default:
                break;
        }
    }
}
