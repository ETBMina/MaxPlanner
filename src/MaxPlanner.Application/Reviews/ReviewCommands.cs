using System.Diagnostics.CodeAnalysis;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Lookups;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Reviews;

/// <summary>Raw review editor values (B-17.1). Blank optional text means "no value"; blank services are ignored.</summary>
/// <param name="CrNumber">Free text, never a link or identity (B-17.3).</param>
/// <param name="DeveloperId">An existing person, or the preallocated ID of <c>NewDeveloper</c>; null for none.</param>
public sealed record ReviewDraft(
    string? Title,
    string? CrNumber,
    string? TaskNumber,
    IReadOnlyList<string> Services,
    PersonId? DeveloperId,
    string? Comments);

/// <param name="NewDeveloper">A person typed into the Developer picker that did not exist yet; created only by this save (B-02.6).</param>
/// <param name="IsDone">B-17.1: checking Done sets CompletedOn to local today.</param>
public sealed record CreateReviewCommand(OperationId OperationId, ReviewItemId NewId, ReviewDraft Draft, NewPerson? NewDeveloper, bool IsDone);

/// <summary>Creates a review item at the end of the Reviews list (B-17).</summary>
public sealed class CreateReview
{
    public const string OperationKind = "review.create";

    private readonly IMutationCoordinator _coordinator;

    public CreateReview(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The new review's revision; field errors are all reported at once (B-02.9).</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(CreateReviewCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = new ValidationCollector();
        var content = ReviewRules.Parse(command.Draft, command.NewDeveloper, validation);
        if (content is null)
        {
            return UseCaseResults.Failed<long>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            if (TransactionGuards.EnsureNewId(tx, EntityKind.ReviewItem, command.NewId.Value, tx.Reviews.Find(command.NewId) is not null) is { } idError)
            {
                return TxResult.Abort<long>(idError);
            }

            var resolved = ReviewRules.ApplyNewDeveloper(tx, command.NewDeveloper, content, out var lookupsChanged);
            if (ReviewRules.Validate(tx, resolved) is { } invalid)
            {
                return TxResult.Abort<long>(invalid);
            }

            var item = ReviewItem.Create(command.NewId, resolved, RankPolicy.After(tx.Reviews.MaxRank()), tx.UtcNow);
            item = item.SetDone(command.IsDone, tx.LocalToday, tx.UtcNow);
            tx.Reviews.Insert(item);
            tx.Changes.UpsertReviewItem(item);
            ReviewRules.Finish(tx, lookupsChanged);
            return TxResult.Commit(item.Revision);
        }, cancellationToken);
    }
}

/// <param name="ExpectedRevision">The revision the editor loaded; a different current revision is a conflict.</param>
public sealed record UpdateReviewCommand(OperationId OperationId, ReviewItemId ReviewId, long ExpectedRevision, ReviewDraft Draft, NewPerson? NewDeveloper, bool IsDone);

/// <summary>Saves the review editor draft (B-17, design §5.4) with a revision check.</summary>
public sealed class UpdateReview
{
    public const string OperationKind = "review.update";

    private readonly IMutationCoordinator _coordinator;

    public UpdateReview(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The review's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(UpdateReviewCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = new ValidationCollector();
        var content = ReviewRules.Parse(command.Draft, command.NewDeveloper, validation);
        if (content is null)
        {
            return UseCaseResults.Failed<long>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            if (!ReviewRules.TryLoad(tx, command.ReviewId, command.ExpectedRevision, out var item, out var error))
            {
                return TxResult.Abort<long>(error);
            }

            var resolved = ReviewRules.ApplyNewDeveloper(tx, command.NewDeveloper, content, out var lookupsChanged);
            if (ReviewRules.Validate(tx, resolved) is { } invalid)
            {
                return TxResult.Abort<long>(invalid);
            }

            // SetDone keeps an existing completion day when Done stays checked (B-17.1).
            var updated = item.Edit(resolved, tx.UtcNow).SetDone(command.IsDone, tx.LocalToday, tx.UtcNow);
            tx.Reviews.Update(updated);
            tx.Changes.UpsertReviewItem(updated);
            ReviewRules.Finish(tx, lookupsChanged);
            return TxResult.Commit(updated.Revision);
        }, cancellationToken);
    }
}

public sealed record SetReviewDoneCommand(OperationId OperationId, ReviewItemId ReviewId, long ExpectedRevision, bool IsDone);

/// <summary>
/// The row checkbox (B-17.1–2): checking sets CompletedOn to local today, unchecking clears it. Done is the
/// only review state; there is no status workflow (B-17.4).
/// </summary>
public sealed class SetReviewDone
{
    public const string OperationKind = "review.set-done";

    private readonly IMutationCoordinator _coordinator;

    public SetReviewDone(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The review's new revision (unchanged when it already had that state).</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(SetReviewDoneCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            if (!ReviewRules.TryLoad(tx, command.ReviewId, command.ExpectedRevision, out var item, out var error))
            {
                return TxResult.Abort<long>(error);
            }

            var updated = item.SetDone(command.IsDone, tx.LocalToday, tx.UtcNow);
            if (!ReferenceEquals(updated, item))
            {
                tx.Reviews.Update(updated);
                tx.Changes.UpsertReviewItem(updated);
            }

            return TxResult.Commit(updated.Revision);
        }, cancellationToken);
    }
}

/// <param name="AnchorId">The review the moved one is placed next to (its visible neighbour for Move up/down).</param>
public sealed record MoveReviewCommand(OperationId OperationId, ReviewItemId ReviewId, long ExpectedRevision, ReviewItemId AnchorId, bool PlaceAfter);

/// <summary>Reorders a review in the explicit list order (B-17.2).</summary>
public sealed class MoveReview
{
    public const string OperationKind = "review.move";

    private readonly IMutationCoordinator _coordinator;

    public MoveReview(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The moved review's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(MoveReviewCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, MoveReviewCommand command)
    {
        if (!ReviewRules.TryLoad(tx, command.ReviewId, command.ExpectedRevision, out var moving, out var error))
        {
            return TxResult.Abort<long>(error);
        }

        var ordered = ExplicitOrder.Sort(tx.Reviews.ListOrder().Select(r => (r.Id.Value, r.Rank)));
        if (ExplicitOrder.Place(ordered, command.ReviewId.Value, command.AnchorId.Value, command.PlaceAfter) is not { } changes)
        {
            return TxResult.Abort<long>(TransactionGuards.NotFound(tx, EntityKind.ReviewItem, command.AnchorId.Value));
        }

        var revision = moving.Revision;
        foreach (var (id, rank) in changes)
        {
            // A renumber touches neighbours too; each is loaded in this transaction before it is written.
            var item = id == moving.Id.Value ? moving : tx.Reviews.Find(new ReviewItemId(id))!;
            var updated = item.MoveTo(rank, tx.UtcNow);
            tx.Reviews.Update(updated);
            tx.Changes.UpsertReviewItem(updated);
            revision = id == moving.Id.Value ? updated.Revision : revision;
        }

        return TxResult.Commit(revision);
    }
}

public sealed record DeleteReviewCommand(OperationId OperationId, ReviewItemId ReviewId, long ExpectedRevision);

/// <summary>Deletes a review after the flyout confirmation (B-17.2, design §4.5). Its ID is tombstoned.</summary>
public sealed class DeleteReview
{
    public const string OperationKind = "review.delete";

    private readonly IMutationCoordinator _coordinator;

    public DeleteReview(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The deleted review's last revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(DeleteReviewCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            if (!ReviewRules.TryLoad(tx, command.ReviewId, command.ExpectedRevision, out var item, out var error))
            {
                return TxResult.Abort<long>(error);
            }

            tx.Reviews.Delete(item.Id);
            TransactionGuards.Tombstone(tx, EntityKind.ReviewItem, item.Id.Value);
            tx.Changes.DeleteReviewItem(item.Id);
            return TxResult.Commit(item.Revision);
        }, cancellationToken);
    }
}

/// <summary>The field rules the review use cases share (B-17.1, B-17.6 limits).</summary>
internal static class ReviewRules
{
    /// <returns>The content, or null when any field is invalid (every error is in <paramref name="validation"/>).</returns>
    public static ReviewContent? Parse(ReviewDraft draft, NewPerson? newDeveloper, ValidationCollector validation)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var title = validation.Check(TitleText.Create(draft.Title));
        var crNumber = validation.Check(TaskNumber.CreateOptional(draft.CrNumber, "CrNumber"));
        var taskNumber = validation.Check(TaskNumber.CreateOptional(draft.TaskNumber));
        var comments = validation.Check(LongText.CreateOptional(draft.Comments, "Comments"));
        var services = new List<ServiceText>();
        foreach (var raw in draft.Services ?? [])
        {
            if (validation.Check(ServiceText.CreateOptional(raw, "Services")) is { IsSuccess: true, Value: { } service })
            {
                services.Add(service);
            }
        }

        if (newDeveloper is not null)
        {
            validation.Check(LookupName.Create(newDeveloper.Name, "Developer"));
        }

        return validation.HasErrors
            ? null
            : new ReviewContent(title.Value, crNumber.Value, taskNumber.Value, services, draft.DeveloperId, comments.Value);
    }

    public static bool TryLoad(
        IWriteTransaction tx,
        ReviewItemId id,
        long expectedRevision,
        [NotNullWhen(true)] out ReviewItem? item,
        [NotNullWhen(false)] out AppError? error)
    {
        item = tx.Reviews.Find(id);
        if (item is null)
        {
            error = TransactionGuards.NotFound(tx, EntityKind.ReviewItem, id.Value);
            return false;
        }

        error = item.Revision == expectedRevision ? null : new ConcurrencyConflictError(EntityKind.ReviewItem, id.Value, expectedRevision, item.Revision);
        return error is null;
    }

    /// <summary>
    /// Creates the typed new developer when the draft uses it; a person with the same name created meanwhile is
    /// used instead of a duplicate (B-02.6 trim and deduplicate).
    /// </summary>
    public static ReviewContent ApplyNewDeveloper(IWriteTransaction tx, NewPerson? newDeveloper, ReviewContent content, out bool inserted)
    {
        inserted = false;
        if (newDeveloper is null || content.DeveloperId != newDeveloper.Id)
        {
            return content;
        }

        var name = LookupName.Create(newDeveloper.Name, "Developer").Value;
        if (tx.Workflow.Read().People.FirstOrDefault(p => p.Name.Normalized == name.Normalized) is { } existing)
        {
            return content with { DeveloperId = existing.Id };
        }

        tx.Lookups.InsertPerson(new PersonDefinition(newDeveloper.Id, name));
        inserted = true;
        return content;
    }

    /// <summary>B-04: references always resolve, so the developer must be a current person.</summary>
    public static ValidationFailedError? Validate(IWriteTransaction tx, ReviewContent content) =>
        content.DeveloperId is { } developer && tx.Workflow.Read().FindPerson(developer) is null
            ? new ValidationFailedError(new DomainError(DomainErrorCodes.UnknownReference, "Developer"))
            : null;

    /// <summary>Publishes the workflow when the save added a person, so pickers everywhere see the new name.</summary>
    public static void Finish(IWriteTransaction tx, bool lookupsChanged)
    {
        if (lookupsChanged)
        {
            tx.Changes.ReplaceWorkflow(tx.Workflow.Read());
        }
    }
}
