# ADR 0006: Permanent CR links

- **Status:** Accepted (owner decision, 2026-10-22)
- **Requirements:** T-01, T-05, T-10, B-02.4, B-03.6–7, AC-29

## Context
Title inheritance (ADR 0005) makes unlinking ambiguous: a detached task would have no title. The owner chose to remove unlinking and reassignment entirely.

## Decision
- A link is created once, for an unlinked task (active or archived), after a confirmation that states the link is permanent and that the title will become the CR's title.
- No route offers unlink or reassign: editor, Grid, Board, import or merge.
- Link rows are insert-only. They are deleted only with the task or by CR cascade (ADR 0007).
- Import may link a currently unlinked task once. Any attempt to change or remove an existing link is reported as a conflict and applies nothing.

## Consequences
- A wrong link is corrected by deleting the task or by copying it (copies start unlinked).
- No unlink/reassign use case exists in the Application layer, so none can be called.

## Verification
- `CrLinkPolicy` tests.
- An import conflict fixture.
- A UI check that no unlink command exists.
- AC-29.
