# ADR 0028: Transfer format 2 — single Designer and Developer, readable task tables

- **Status:** Accepted (owner decision, Nov 2026)
- **Requirements:** B-12, B-02. Supersedes the **Transfers** bullet of ADR 0024.

## Context
ADR 0024 made Designer and Developer a single optional person, but kept the transfer format unchanged: `DesignerIds` and `DeveloperIds` stayed arrays with at most one element. The owner finds the list misleading, since only one person is allowed. The owner also wants the Tasks and Change Requests tables to be readable on their own: statuses, people, and tags must show names, with the IDs kept beside them.

The Change Requests table already has a name column next to every ID (Owner, Squad, Status, Priority). The Tasks table has names for Status, Priority, Squad, and CR Owner, but Designer, Developer, and Tags are IDs only.

## Options
1. **Single values in every format and a new `formatVersion` 2**, with name columns in the tables; files of version 1 still import.
2. Single values in XLSX and CSV only, JSON unchanged, no version change.
3. Keep the arrays and only add name columns.

## Decision
Option 1.
- **Format version:** `formatVersion` is 2. The app writes 2 and reads 1 and 2. Newer versions are still rejected (B-12).
- **JSON:** a task has `designerId` and `developerId`, each a single optional ID, in place of `designerIds` and `developerIds`.
- **Tasks table (XLSX sheet and CSV):** `DesignerIds` and `DeveloperIds` are replaced by `DesignerId` and `DeveloperId` (plain ID text), each followed by an informational name column, `Designer` and `Developer`. `TagIds` keeps its JSON-cell encoding and is followed by an informational `Tags` column: the tag names in task order, separated by `, `.
- **Change Requests table:** unchanged; it already shows names beside every ID.
- **Informational columns** are ignored on import, as before.
- **Reading version 1:** JSON `designerIds` and `developerIds`, and table columns `DesignerIds` and `DeveloperIds`, are still accepted. The first ID is kept and the others are dropped with the ADR 0024 warning. If a record has both the array and the single ID, the array wins. A legacy column that is present with an empty cell clears the role.
- Custom values keep their JSON-cell encoding (no per-field readable columns for now).

## Consequences
- Files written by this version cannot be imported by an older build of the app, which rejects `formatVersion` 2.
- Existing exports and backups of version 1 still import.
- `docs/schemas/transfer-v1.schema.json` keeps its file name and now describes versions 1 and 2. `docs/samples/transfer-v1-sample.json` stays a version 1 sample.
- Round-trip tests cover version 2, and fixtures of version 1 cover the compatibility path.
