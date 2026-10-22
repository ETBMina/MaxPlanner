# ADR 0007: CR deletion cascades to linked tasks

- **Status:** Accepted (owner decision, 2026-10-22)
- **Requirements:** B-05, T-06.3, T-13, AC-30

## Context
The earlier spec detached linked tasks when a CR was deleted. Links are now permanent (ADR 0006), so detachment is no longer possible.

## Decision
- Deleting a CR deletes, in one transaction: the CR, its links, every linked task (archived included), and their checklist items and Subtask cards. Tombstones are written for every deleted ID.
- The confirmation shows the CR number and the counts.
- The command carries the exact set of task IDs that was confirmed. If the current set differs at commit time, the use case returns `CascadeScopeChanged` and the UI asks again.

## Consequences
- There is no Archive alternative, because CRs are not archived (ADR 0020).
- Tombstones stop stale ViewModels and imports from re-creating deleted records.

## Verification
Infrastructure tests with fault injection mid-transaction (nothing deleted on failure), the scope-change test, and AC-30.
