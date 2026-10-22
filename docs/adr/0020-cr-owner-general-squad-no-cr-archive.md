# ADR 0020: CR Owner, "General" squad label, no CR archive, Records mode

- **Status:** Accepted (owner decisions, 2026-10-22)
- **Requirements:** B-03, B-04, B-05, B-08, B-09, B-10, B-11, B-15, T-05, AC-19, AC-31

## Decisions
1. **CR Owner.** Every CR has a required Owner (a Person). New CRs prefill it from a "Default CR Owner" setting stored in `app_metadata`. Linked tasks and their Subtask cards show the owner read-only, while unlinked tasks show none. It is filterable on Board, Grid, Calendar and Dashboard. Removing a person who owns CRs requires choosing a replacement.
2. **Service is required on linked tasks.** Sibling tasks under one CR share the CR's title, so Service is what tells them apart.
3. **"General" squad label.** A null squad is displayed as "General". This is a label, not a squad record. The Checklists "General" group is renamed "General to-dos" to avoid confusion.
4. **CRs are not archived.** CRs have no `IsArchived`. The overdue predicate drops its archive clause. Archived tasks can still be linked to a CR.
5. **Records mode.** The CR records list is a Calendar view mode (Month / Week / Records). Import, Export and Restore are a page under Settings › Data.

## Consequences
- The Dashboard gains an Owner filter.
- Transfer formats carry `ownerId`.
- Import proposes the default owner when a source CR has none.

## Verification
AC-19 and AC-31; a filter test for "General"; owner propagation in the reactive tests.
