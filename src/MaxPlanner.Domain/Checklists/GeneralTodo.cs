using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Checklists;

/// <summary>An independent to-do in Checklists › General to-dos; no task, CR, or status (B-09).</summary>
public sealed record GeneralTodo
{
    private GeneralTodo()
    {
    }

    public GeneralTodoId Id { get; private init; }

    public ChecklistText Text { get; private init; } = null!;

    public bool IsCompleted { get; private init; }

    public long Rank { get; private init; }

    public DateTimeOffset CreatedAtUtc { get; private init; }

    public DateTimeOffset UpdatedAtUtc { get; private init; }

    public long Revision { get; private init; }

    public static GeneralTodo Create(GeneralTodoId id, ChecklistText text, long rank, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new GeneralTodo { Id = id, Text = text, Rank = rank, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc, Revision = 1 };
    }

    /// <summary>Rebuilds a stored to-do exactly as persisted, without a revision bump; audit values are checked.</summary>
    public static DomainResult<GeneralTodo> Rehydrate(
        GeneralTodoId id,
        ChecklistText text,
        bool isCompleted,
        long rank,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(text);
        return StoredRecord.CheckAudit(createdAtUtc, updatedAtUtc, revision) is { } audit
            ? DomainResult.Failure<GeneralTodo>(audit)
            : DomainResult.Success(new GeneralTodo
            {
                Id = id,
                Text = text,
                IsCompleted = isCompleted,
                Rank = rank,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = updatedAtUtc,
                Revision = revision,
            });
    }

    public GeneralTodo Rename(ChecklistText text, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (this with { Text = text }).Touch(nowUtc);
    }

    public GeneralTodo SetCompleted(bool isCompleted, DateTimeOffset nowUtc) => (this with { IsCompleted = isCompleted }).Touch(nowUtc);

    public GeneralTodo MoveTo(long rank, DateTimeOffset nowUtc) => (this with { Rank = rank }).Touch(nowUtc);

    private GeneralTodo Touch(DateTimeOffset nowUtc) => this with { UpdatedAtUtc = nowUtc, Revision = Revision + 1 };
}
