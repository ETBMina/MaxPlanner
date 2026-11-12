using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;

namespace MaxPlanner.Presentation.Workspace;

/// <summary>
/// The Application use cases the pages and editors call, grouped by feature (T-07: every edit goes through the same
/// command path). Use cases are stateless, so one instance of each serves every page of the open dataset. Settings and
/// data transfer have their own groups (<c>SettingsUseCases</c>, <c>DataServices</c>), given to the Settings page only.
/// </summary>
public sealed record WorkspaceUseCases(
    TaskUseCases Tasks,
    SubtaskCardUseCases SubtaskCards,
    ChecklistUseCases Checklists,
    GeneralTodoUseCases GeneralTodos,
    ChangeRequestUseCases ChangeRequests,
    ReviewUseCases Reviews)
{
    /// <summary>Every use case over one dataset's coordinator; the composition root and tests share this list.</summary>
    public static WorkspaceUseCases Create(IMutationCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return new(
            new TaskUseCases(
                new CreateTask(coordinator),
                new UpdateTask(coordinator),
                new UpdateTaskContent(coordinator),
                new MoveTask(coordinator),
                new CopyTask(coordinator),
                new ArchiveTask(coordinator),
                new RestoreTask(coordinator),
                new DeleteTask(coordinator),
                new LinkTaskToChangeRequest(coordinator)),
            new SubtaskCardUseCases(
                new CreateSubtaskCard(coordinator),
                new UpdateSubtaskCard(coordinator),
                new UpdateSubtaskCardContent(coordinator),
                new MoveSubtaskCard(coordinator),
                new DeleteSubtaskCard(coordinator)),
            new ChecklistUseCases(
                new AddChecklistItem(coordinator),
                new SetChecklistItemCompleted(coordinator),
                new RenameChecklistItem(coordinator),
                new MoveChecklistItem(coordinator),
                new DeleteChecklistItem(coordinator),
                new PopOutChecklistItem(coordinator)),
            new GeneralTodoUseCases(
                new AddGeneralTodo(coordinator),
                new SetGeneralTodoCompleted(coordinator),
                new RenameGeneralTodo(coordinator),
                new MoveGeneralTodo(coordinator),
                new DeleteGeneralTodo(coordinator)),
            new ChangeRequestUseCases(
                new CreateChangeRequest(coordinator),
                new UpdateChangeRequest(coordinator),
                new DeleteChangeRequest(coordinator),
                new ScheduleChangeRequest(coordinator),
                new ResizeChangeRequest(coordinator),
                new MoveChangeRequestToBacklog(coordinator)),
            new ReviewUseCases(
                new CreateReview(coordinator),
                new UpdateReview(coordinator),
                new SetReviewDone(coordinator),
                new MoveReview(coordinator),
                new DeleteReview(coordinator)));
    }
}

/// <summary>Task commands: create, edit, move, copy, archive, restore, delete, and the permanent CR link (B-02, B-05, B-06).</summary>
public sealed record TaskUseCases(
    CreateTask CreateTask,
    UpdateTask UpdateTask,
    UpdateTaskContent UpdateTaskContent,
    MoveTask MoveTask,
    CopyTask CopyTask,
    ArchiveTask ArchiveTask,
    RestoreTask RestoreTask,
    DeleteTask DeleteTask,
    LinkTaskToChangeRequest LinkTaskToChangeRequest);

/// <summary>Subtask card commands (B-16).</summary>
public sealed record SubtaskCardUseCases(
    CreateSubtaskCard CreateSubtaskCard,
    UpdateSubtaskCard UpdateSubtaskCard,
    UpdateSubtaskCardContent UpdateSubtaskCardContent,
    MoveSubtaskCard MoveSubtaskCard,
    DeleteSubtaskCard DeleteSubtaskCard);

/// <summary>A task's checklist item commands, including Pop out to card (B-09, B-16.3).</summary>
public sealed record ChecklistUseCases(
    AddChecklistItem AddChecklistItem,
    SetChecklistItemCompleted SetChecklistItemCompleted,
    RenameChecklistItem RenameChecklistItem,
    MoveChecklistItem MoveChecklistItem,
    DeleteChecklistItem DeleteChecklistItem,
    PopOutChecklistItem PopOutChecklistItem);

/// <summary>General to-do commands (B-09).</summary>
public sealed record GeneralTodoUseCases(
    AddGeneralTodo AddGeneralTodo,
    SetGeneralTodoCompleted SetGeneralTodoCompleted,
    RenameGeneralTodo RenameGeneralTodo,
    MoveGeneralTodo MoveGeneralTodo,
    DeleteGeneralTodo DeleteGeneralTodo);

/// <summary>CR commands, including the Calendar's schedule, resize, and move to backlog (B-03, B-08).</summary>
public sealed record ChangeRequestUseCases(
    CreateChangeRequest CreateChangeRequest,
    UpdateChangeRequest UpdateChangeRequest,
    DeleteChangeRequest DeleteChangeRequest,
    ScheduleChangeRequest ScheduleChangeRequest,
    ResizeChangeRequest ResizeChangeRequest,
    MoveChangeRequestToBacklog MoveChangeRequestToBacklog);

/// <summary>Review commands (B-17).</summary>
public sealed record ReviewUseCases(
    CreateReview CreateReview,
    UpdateReview UpdateReview,
    SetReviewDone SetReviewDone,
    MoveReview MoveReview,
    DeleteReview DeleteReview);
