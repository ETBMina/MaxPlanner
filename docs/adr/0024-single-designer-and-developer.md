# ADR 0024: One Designer and one Developer per task

- **Status:** Accepted (owner decision, Oct 2026)
- **Requirements:** B-02, B-04, B-09, B-10, B-12; replaces the "Designer and Developer each allow zero or more people" design default in the data model section

## Context
Release 1 modelled Designer and Developer as lists of people, edited with token pickers in the task editor and Grid. The owner decided that a task has at most one Designer and at most one Developer. The editing control should behave like a single choice, but keep the case-insensitive suggestions and the add-new-from-the-box behavior of the people pickers.

## Options
1. Keep lists in storage and the model, and limit the UI to one person.
2. **Make both fields a single optional person** in the Domain, read models, use cases, UI, and transfers, and migrate existing data.

## Decision
Option 2.
- **Domain and Application:** a task has a nullable `DesignerId` and a nullable `DeveloperId`. Commands, drafts, and read models carry single values.
- **Storage:** a schema migration keeps, for each task and role, the first person by the current `position` and deletes the other rows. The schema then allows at most one person per task and role.
- **UI:** the task editor and the Grid use the single-person picker (§5.0 of `design.md`, the same control as CR Owner and Review developer): a person dropdown that shows the chosen person in the box, with suggestions while typing and an "Add 'name'" entry; a new person is created on Save.
- **Filters** keep multi-select: choosing several people matches tasks whose Designer (or Developer) is any of them.
- **Transfers:** the file formats are unchanged; Designer and Developer stay arrays with at most one element on export. An import that finds more than one person keeps the first and reports a warning naming what was dropped.

## Consequences
- Data with more than one Designer or Developer per task loses the extra people on upgrade (owner approved: keep the first, drop the rest). Backups taken before the upgrade still hold them.
- Grid shows one name with one avatar in each column; the columns are named Designer and Developer.
- Removing a person still offers replace or clear for these fields (B-04).
