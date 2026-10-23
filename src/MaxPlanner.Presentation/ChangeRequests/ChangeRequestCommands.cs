using System.Globalization;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.ChangeRequests;

/// <summary>
/// The CR commands every page and editor offers (B-03, B-08, design §4.4, §5.3): open, create, schedule,
/// resize, move to backlog, delete, and the editor's follow-ups. Dialogs never stack (design §5.0), so a
/// follow-up opens only after the CR editor closed. Use on the UI thread.
/// </summary>
public sealed class ChangeRequestCommands
{
    private readonly WorkspaceContext _context;

    internal ChangeRequestCommands(WorkspaceContext context) => _context = context;

    /// <summary>Opens the CR editor (Calendar bars and records, Board/Grid CR references, the task editor's CR link).</summary>
    public Task OpenAsync(ChangeRequestId id) => RunEditorAsync(id, null, startDelete: false);

    /// <summary>New CR (Calendar accent button, B-03.1); <paramref name="plannedDay"/> is the day flyout's "New CR on this date".</summary>
    public Task NewAsync(DateOnly? plannedDay = null) => RunEditorAsync(null, plannedDay, startDelete: false);

    /// <summary>
    /// "Open linked tasks" (B-08.3): the CR editor, whose Linked tasks section lists every linked task with the
    /// count and opens each one (design §5.3 group 8).
    /// </summary>
    public Task OpenLinkedTasksAsync(ChangeRequestId id) => OpenAsync(id);

    /// <summary>"Delete…" from the Calendar menus: the CR editor opens on its cascade step (design §5.7, AC-30).</summary>
    public Task DeleteAsync(ChangeRequestId id) => RunEditorAsync(id, null, startDelete: true);

    /// <summary>
    /// B-08.5: PlannedStart = PlannedEnd = <paramref name="day"/> and the scheduling status, in one command. The
    /// caller previewed and confirmed replacing existing dates; the revision it showed is sent with the command.
    /// </summary>
    public async Task<bool> ScheduleAsync(ChangeRequestReadModel cr, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(cr);
        var outcome = await _context.Tasks.RunAsync("Record_ChangeRequest", op => _context.UseCases.ScheduleChangeRequest.ExecuteAsync(
            new ScheduleChangeRequestCommand(op, cr.Id, cr.Revision, day), CancellationToken.None)).ConfigureAwait(true);
        if (outcome is not null)
        {
            _context.Announce(_context.Strings.Format("Calendar_Scheduled", cr.Number, LongDate(day)));
        }

        return outcome is not null;
    }

    /// <summary>
    /// B-08.8: changes one endpoint (bar edge or keyboard step) or both (Edit dates flyout). A failure keeps the
    /// committed dates, which the Calendar keeps showing, and is reported in the save status.
    /// </summary>
    public async Task<bool> ResizeAsync(ChangeRequestReadModel cr, DateOnly? newStart, DateOnly? newEnd)
    {
        ArgumentNullException.ThrowIfNull(cr);
        var outcome = await _context.Tasks.RunAsync("Record_ChangeRequest", op => _context.UseCases.ResizeChangeRequest.ExecuteAsync(
            new ResizeChangeRequestCommand(op, cr.Id, cr.Revision, newStart, newEnd), CancellationToken.None)).ConfigureAwait(true);
        if (outcome is not null && (newStart ?? cr.PlannedStart) is { } start && (newEnd ?? cr.PlannedEnd) is { } end)
        {
            _context.Announce(_context.Strings.Format("Calendar_Rescheduled", cr.Number, LongDate(start), LongDate(end)));
        }

        return outcome is not null;
    }

    /// <summary>B-08.11: clears both planned dates and sets the backlog status.</summary>
    public async Task<bool> MoveToBacklogAsync(ChangeRequestReadModel cr)
    {
        ArgumentNullException.ThrowIfNull(cr);
        var outcome = await _context.Tasks.RunAsync("Record_ChangeRequest", op => _context.UseCases.MoveChangeRequestToBacklog.ExecuteAsync(
            new MoveChangeRequestToBacklogCommand(op, cr.Id, cr.Revision), CancellationToken.None)).ConfigureAwait(true);
        if (outcome is not null)
        {
            _context.Announce(_context.Strings.Format("Calendar_MovedToBacklog", cr.Number));
        }

        return outcome is not null;
    }

    private static string LongDate(DateOnly day) => day.ToString("M", CultureInfo.CurrentCulture);

    private async Task RunEditorAsync(ChangeRequestId? id, DateOnly? plannedDay, bool startDelete)
    {
        // The editor needs the loaded workflow (lookups and defaults); before the first load nothing opens.
        if (_context.Store.CurrentWorkflow is null)
        {
            return;
        }

        var editor = new CrEditorViewModel(_context, id, plannedDay);
        using (editor)
        {
            if (startDelete)
            {
                editor.BeginDelete();
            }

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
                await RunEditorAsync(other, null, startDelete: false).ConfigureAwait(true);
                break;
            case CrEditorFollowUp.MoveToBacklog when editor.SavedId is { } crId && _context.Store.ChangeRequests.Lookup(crId) is { HasValue: true } cr:
                // The committed CR, never the discarded draft, supplies the revision.
                await MoveToBacklogAsync(cr.Value).ConfigureAwait(true);
                break;
            default:
                break;
        }
    }
}
