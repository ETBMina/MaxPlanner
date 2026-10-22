using MaxPlanner.Application.Changes;

namespace MaxPlanner.Application.Abstractions;

/// <summary>
/// The committed change sets of one open dataset, in commit order (T-07, ADR 0010). Presentation's
/// projection store is its subscriber; nothing else re-queries the database to stay current.
/// </summary>
/// <remarks>
/// <para><b>Ordering and thread.</b> Each change set is delivered once, on the database writer worker,
/// immediately after its transaction committed and before the next queued operation starts. Sequences
/// therefore arrive strictly increasing. Subscribers must hand the work off quickly (for example to the
/// store's own scheduler) and must never block on, or start, another mutation from inside
/// <see cref="IObserver{T}.OnNext"/>: that would stall or deadlock the single writer.</para>
/// <para><b>Gaps.</b> A sequence can be skipped when a commit's publication was lost (for example the
/// storage could not confirm the commit, <see cref="Common.CommitOutcomeUnknownError"/>). Subscribers that
/// see a gap resynchronize from a <see cref="IReadSnapshotSource"/> snapshot instead of guessing.</para>
/// <para><b>Subscriber faults.</b> If a subscriber throws from <see cref="IObserver{T}.OnNext"/>, the commit
/// result is unaffected (the operation still reports success, T-07). The fault is logged, that subscriber
/// receives <see cref="IObserver{T}.OnError"/> with the thrown exception and is unsubscribed, and it must
/// resubscribe and reload a snapshot (Resyncing). Other subscribers keep receiving change sets.</para>
/// <para><b>Handoff.</b> Subscribe first, then load a snapshot, then drop buffered change sets whose
/// sequence is at or below <see cref="ReadSnapshot.CommitSequence"/>. A change set committed before the
/// snapshot was read is always included in it, because publication happens after commit.</para>
/// <para><b>End.</b> Subscribers receive <see cref="IObserver{T}.OnCompleted"/> when the dataset closes.</para>
/// </remarks>
public interface IChangeFeed : IObservable<CommittedChangeSet>
{
    /// <summary>
    /// The sequence of the most recent change set handed to subscribers, or the dataset's commit sequence
    /// when it was opened if nothing has been published since. Readable from any thread.
    /// </summary>
    long LastPublishedSequence { get; }
}
