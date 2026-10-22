using MaxPlanner.Application.Common;

namespace MaxPlanner.Presentation.Common.Store;

public enum ProjectionStatus
{
    /// <summary>The first snapshot is loading; nothing is displayed as data yet (B-15.7).</summary>
    Loading,

    Ready,

    /// <summary>A gap, feed fault, or bulk change is being repaired by reloading a snapshot (T-07).</summary>
    Resyncing,

    /// <summary>Loading a snapshot failed; the previous projection (if any) is kept but no longer updated.</summary>
    Faulted,
}

/// <summary>The store's lifecycle state.</summary>
/// <param name="Error">The typed snapshot failure when <see cref="Status"/> is <see cref="ProjectionStatus.Faulted"/>.</param>
/// <param name="Exception">
/// The unexpected exception that caused a resync or fault (an inconsistent change set, a throwing
/// subscriber, a feed fault), for diagnostics; null otherwise.
/// </param>
public sealed record ProjectionState(ProjectionStatus Status, AppError? Error = null, Exception? Exception = null)
{
    public static ProjectionState Loading { get; } = new(ProjectionStatus.Loading);

    /// <summary>
    /// T-07: while not Ready the displayed data may be behind the database, so editors must not start a
    /// command from it.
    /// </summary>
    public bool IsEditingBlocked => Status != ProjectionStatus.Ready;
}

public enum ProjectionCommitReason
{
    /// <summary>A snapshot replaced the whole projection (startup, resync, or full reload).</summary>
    Snapshot,

    /// <summary>One committed change set was applied.</summary>
    ChangeSet,

    /// <summary>The local day changed and date-relative read models were recomposed (B-15.9).</summary>
    DayChanged,
}

/// <summary>
/// Raised once after every cache has been updated for one batch, so consumers such as the Dashboard can
/// recompute from a consistent state (ADR 0010).
/// </summary>
/// <param name="Sequence">The commit sequence the projection now reflects.</param>
/// <param name="Today">The local day the date-relative read models were composed for.</param>
public sealed record ProjectionCommit(long Sequence, ProjectionCommitReason Reason, DateOnly Today);
