# ADR 0022: Concurrency stamps for workflow settings

- **Status:** Proposed (Phase 10, pending owner review)
- **Requirements:** T-05 (revisions and stable operation IDs), T-06, T-07, B-04, B-11, AC-07, AC-08

## Context
Records (tasks, Subtask cards, CRs, reviews, General to-dos) carry a `revision` column, and every command sends the revision the editor saw. Lookups (statuses, priorities, squads, people, tags), field definitions, and the workspace defaults in `app_metadata` have no revision column in schema v1. The Settings use cases still need a check that refuses a change prepared against settings that changed meanwhile (another Settings action, or a person added by a task editor).

## Options
1. Add a `workflow_revision` column with migration M0002.
2. **Stamps:** each Settings command carries a culture-independent text stamp of the part of the workflow it depends on (one lookup value, the order of one lookup kind, one field definition, the field order of one target, or the defaults). The use case recomputes the stamp inside the write transaction and refuses with `WorkflowChangedError` when it differs.

## Decision
Option 2 (`Application/Settings/WorkflowStamps`). It needs no schema change, and its scope is narrower than one global revision: an unrelated change (for example a person added by a task editor) does not make a rename of a status fail. Lookup removal additionally compares the confirmed usage counts with the counts inside the transaction (design §5.9) and refuses with `lookup-usage-changed`.

Every Settings command also uses a stable operation ID, so a retry after an uncertain outcome replays the ledger entry (results are `long`, an existing ledger result type).

## Consequences
- No migration; schema v1 stays as is.
- Stamps are recomputed from the workflow read inside the transaction, so they are exact for the single-writer design (T-06).
- A future migration may add row revisions to lookups; the stamps can then be replaced without changing the commands' meaning.

## Verification
Deferred to Phase 19 (owner decision): Application tests for every Settings use case on the in-memory fake and on real SQLite, including a stale stamp, a stale usage count, and ledger replay.
