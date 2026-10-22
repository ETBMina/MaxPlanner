namespace MaxPlanner.Application.Common;

/// <summary>
/// Identifies one user intent (a Save click, a drop, a cell commit) across retries.
/// </summary>
/// <remarks>
/// T-05: callers create the ID once per intent and reuse it when retrying after an
/// uncertain outcome, so the operation ledger can return the recorded result instead
/// of applying the change twice.
/// </remarks>
public readonly record struct OperationId(Guid Value)
{
    public static OperationId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
