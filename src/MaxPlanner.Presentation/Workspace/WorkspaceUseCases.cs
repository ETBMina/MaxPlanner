using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;

namespace MaxPlanner.Presentation.Workspace;

/// <summary>
/// The Application use cases the pages and editors call (T-07: every edit goes through the same command path).
/// Use cases are stateless, so one instance of each serves every page of the open dataset.
/// </summary>
public sealed record WorkspaceUseCases(
    CreateTask CreateTask,
    UpdateTaskContent UpdateTaskContent,
    MoveTask MoveTask,
    UpdateSubtaskCardContent UpdateSubtaskCardContent,
    MoveSubtaskCard MoveSubtaskCard,
    AddChecklistItem AddChecklistItem,
    SetChecklistItemCompleted SetChecklistItemCompleted,
    UpdateTask UpdateTask,
    CopyTask CopyTask,
    ArchiveTask ArchiveTask,
    RestoreTask RestoreTask,
    DeleteTask DeleteTask,
    LinkTaskToChangeRequest LinkTaskToChangeRequest,
    CreateSubtaskCard CreateSubtaskCard,
    DeleteSubtaskCard DeleteSubtaskCard,
    PopOutChecklistItem PopOutChecklistItem,
    UpdateSubtaskCard UpdateSubtaskCard,
    CreateChangeRequest CreateChangeRequest,
    UpdateChangeRequest UpdateChangeRequest,
    DeleteChangeRequest DeleteChangeRequest,
    ScheduleChangeRequest ScheduleChangeRequest,
    ResizeChangeRequest ResizeChangeRequest,
    MoveChangeRequestToBacklog MoveChangeRequestToBacklog,
    RenameChecklistItem RenameChecklistItem,
    MoveChecklistItem MoveChecklistItem,
    DeleteChecklistItem DeleteChecklistItem,
    AddGeneralTodo AddGeneralTodo,
    SetGeneralTodoCompleted SetGeneralTodoCompleted,
    RenameGeneralTodo RenameGeneralTodo,
    MoveGeneralTodo MoveGeneralTodo,
    DeleteGeneralTodo DeleteGeneralTodo,
    CreateReview CreateReview,
    UpdateReview UpdateReview,
    SetReviewDone SetReviewDone,
    MoveReview MoveReview,
    DeleteReview DeleteReview)
{
    /// <summary>Every use case over one dataset's coordinator; the composition root and tests share this list.</summary>
    public static WorkspaceUseCases Create(IMutationCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return new(
            new CreateTask(coordinator),
            new UpdateTaskContent(coordinator),
            new MoveTask(coordinator),
            new UpdateSubtaskCardContent(coordinator),
            new MoveSubtaskCard(coordinator),
            new AddChecklistItem(coordinator),
            new SetChecklistItemCompleted(coordinator),
            new UpdateTask(coordinator),
            new CopyTask(coordinator),
            new ArchiveTask(coordinator),
            new RestoreTask(coordinator),
            new DeleteTask(coordinator),
            new LinkTaskToChangeRequest(coordinator),
            new CreateSubtaskCard(coordinator),
            new DeleteSubtaskCard(coordinator),
            new PopOutChecklistItem(coordinator),
            new UpdateSubtaskCard(coordinator),
            new CreateChangeRequest(coordinator),
            new UpdateChangeRequest(coordinator),
            new DeleteChangeRequest(coordinator),
            new ScheduleChangeRequest(coordinator),
            new ResizeChangeRequest(coordinator),
            new MoveChangeRequestToBacklog(coordinator),
            new RenameChecklistItem(coordinator),
            new MoveChecklistItem(coordinator),
            new DeleteChecklistItem(coordinator),
            new AddGeneralTodo(coordinator),
            new SetGeneralTodoCompleted(coordinator),
            new RenameGeneralTodo(coordinator),
            new MoveGeneralTodo(coordinator),
            new DeleteGeneralTodo(coordinator),
            new CreateReview(coordinator),
            new UpdateReview(coordinator),
            new SetReviewDone(coordinator),
            new MoveReview(coordinator),
            new DeleteReview(coordinator));
    }
}
