# ADR 0005: Task title inherited from the CR

- **Status:** Accepted (owner decision, 2026-10-22)
- **Requirements:** B-02.2–4, B-03.3, T-05, T-13, AC-28

## Context
The earlier spec made a task's title editable and only prefilled once from the CR. The owner's design shows linked tasks displaying the CR title, with Service telling sibling tasks apart.

## Decision
- **Linked tasks:** the effective title is always the CR's current Title. It is read-only on the task and updates everywhere when the CR title changes.
- **Unlinked tasks:** the task stores its own `OwnTitle`, which is required and editable.
- **Linking:** linking moves `OwnTitle` into `PreservedMetadata.PreLinkTitle`, which is never displayed but is exported in full JSON and backups.
- **Service is required on linked tasks** (ADR 0020 context). Copies prefill their own title from the source's effective title.

## Consequences
- Read models expose `Title` and `IsTitleInherited`.
- Grid Title cells are read-only for linked rows and navigate to the CR.
- Import reports conflicting task titles for linked tasks and never overrides the CR.

## Verification
- Domain tests for `TaskTitlePolicy`.
- Reactive tests for CR title edits.
- AC-28.
