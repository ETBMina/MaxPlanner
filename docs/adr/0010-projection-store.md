# ADR 0010: Reactive projection store and snapshot handoff

- **Status:** Accepted. The pipeline shape was verified by spike 5 on 2026-10-22 (`docs/evidence/phase-0/platform-controls/`).
- **Requirements:** T-07, AC-06, AC-07, AC-17

## Decision
**Ownership.** One Presentation `WorkspaceProjectionStore` owns DynamicData `SourceCache`s for tasks (archived included), Subtask cards, CRs, review items and General to-dos, plus a lookup subject. Only the store mutates them, on its own scheduler.

**Handoff.** Startup avoids losing or doubling changes made while loading:
1. Subscribe to the committed change feed into a buffer.
2. Load the snapshot. Data and `commit_sequence` come from one SQLite read transaction, which spike 2 proved consistent.
3. Apply the snapshot.
4. Drain the buffer, dropping sequences ≤ the snapshot sequence.
5. Go live.

**Failures.** A sequence gap or publication fault triggers Resyncing: editing is blocked and the store reloads through the same handoff.

**Recomposition.** Each change set recomposes only the affected read models. For example, a CR edit recomposes all of its linked tasks and their Subtask cards through a CR-to-tasks index. The store then emits one `Committed(seq)`, so Dashboard components update from a single consistent batch.

### View pipeline (confirmed by spike 5 on DynamicData 9.5.0 + System.Reactive 7.0.0)
```csharp
store.Connect()
     .Filter(predicate$)                                   // on the store scheduler
     .ObserveOn(dispatcherQueueScheduler)                  // BEFORE the inline update
     .TransformWithInlineUpdate(create, (vm, record) => vm.Update(record))
     .SortAndBind(out ReadOnlyObservableCollection<TViewModel> items, comparer$)
     .Subscribe(_ => selection.Restore());                 // see the selection rule below
```
- **`ObserveOn` comes before `TransformWithInlineUpdate`**, because the inline update mutates ViewModels that the UI is bound to.
- `DispatcherQueueScheduler : LocalScheduler` is a small Presentation class over the injected dispatcher, because Rx 7 ships no WinUI scheduler. ViewModels see only `IUiDispatcher`.
- **Default `SortAndBind` options:**
  - Keep Move notifications. Reporting a move as Remove+Insert keeps the selection but loses focus on a re-sorted focused row.
  - Leave `UseBinarySearch` off. Sort keys change in place, so a binary search could choose the wrong position; this is reasoned, not measured.
  - Converting Update to Refresh made no difference and is not used.
- **What spike 5 measured on 10,000 records** (focus, draft and scroll held across 150+ ticks):
  - The ViewModel, TextBox and container instances are preserved on in-place updates.
  - Store-to-UI latency averaged 1.7–2.6 ms, p95 4–8 ms.
  - The initial bind took 97–200 ms cold.
  - Disposing a pipeline releases every subscription, and no UI change follows disposal.
- **Selection rule:** the ListView clears the selection when another row moves above the selected row. Selection lives in ViewModel state and is restored after each change set (about 12 ms later); the transient null `SelectionChanged` is ignored.
- **Focused item re-sorting:** focus follows the item and its container automatically, but scroll does not. While the item has an open editor or unsaved draft, its position is held until editing ends. Otherwise the list scrolls minimally to bring it into view (`design.md` §7.7).

### Implementation decisions (slice 1e, 2026-10-22)
- **One store scheduler.** All cache and raw-map mutation runs on an owned `EventLoopScheduler`. The feed observer only hands change sets off to it; it never projects on the database writer thread.
- **Handoff generations.** Each handoff increments a generation number, and scheduled work from an abandoned subscription or load is ignored.
- **Resync triggers:**
  - a sequence gap (also between the snapshot and the buffer);
  - feed `OnError`;
  - `RequiresFullReload` (import and restore);
  - an exception while applying a change set.

  While resyncing, status is `Resyncing` and editing is blocked.
- **Faulted.** A snapshot that cannot be loaded or projected goes to `Faulted` and keeps the previous caches. It does not loop; `Reload()` retries.
- **`LinkedTaskCount`** counts every linked task, archived included, the same set the cascade-delete confirmation shows (B-05).
- **Day boundary.** A day signal uses the date it carries; snapshot loads use `TimeProvider.LocalToday()`. The Desktop day-boundary source (slice 1f) must use the same clock.
- **Selection.** A null selection from the list is treated as transient while the selected record is still in view; a deliberate deselect calls `Selection.Clear()`.

## Verification
- Presentation tests with a test scheduler covering load races, ordering, gaps, filter regrouping, selection restore, and 100-cycle subscription disposal.
- Spike 5 evidence: `docs/evidence/phase-0/platform-controls/` (20–25).
- Former open point (spike 5): working set rose 228 → 293 MB over 10 navigation cycles with the mutator running.
- **Phase 2 result (2026-10-22), settled for the slice pages:** page ViewModels are created on first visit and retained, so navigation adds no subscriptions.
  - Presentation test `SliceBehaviourTests.A_hundred_navigation_cycles_keep_the_live_subscription_count_stable_and_disposal_releases_it`: 100 Board → Grid → Checklists cycles keep `ActiveCacheSubscriptions` at 19 (Grid 2, Board 8 columns × 2, Checklists 1); disposing the workspace returns it to 0.
  - Real app (Debug build, fixture dataset of 5 tasks / 3 Subtask cards, `winapp ui` invoking the nav items 300 times): the log reported 19 live subscriptions on every navigation; working set 245 → 252 MiB (process) and 245 → 251 MiB (log) across the run. That is a 6–7 MiB rise on a small dataset, not a growth trend measured over several runs; it does not prove or rule out retention at the T-11 fixture size, which Phase 17 measures. Evidence: `docs/evidence/phase-2/2026-10-22/slice-results.json`.
