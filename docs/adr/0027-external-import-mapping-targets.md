# ADR 0027: External import mapping targets and unmapped columns

- **Status:** Accepted (owner decision, Nov 2026)
- **Requirements:** B-11, B-13, T-05. Amends T-05's "Preserve unknown imported fields" rule for external imports.

## Context
External imports (B-13) map each source column to a built-in field chosen from a fixed list. A column with no matching field, such as an old app's `createdAt` or `subtasks`, can only be ignored. Its values are then kept as hidden preserved metadata, which nothing shows. The owner wants imports from any spreadsheet to work without a code change for each new column, and wants to choose whether unmapped data is kept.

## Decision
The mapping `ComboBox` of each column offers three new kinds of target, and each unmapped column gets a keep-or-discard choice.

- **Created** (tasks, CRs, reviews, General to-dos): the record's `CreatedAtUtc`.
  - Accepted values are an ISO date-time with an offset or `Z`, an ISO date-time without an offset (read as local time), an ISO date (local midnight), or a spreadsheet date cell.
  - A value later than the import time, or one that can't be read, is an Error finding for that record.
  - `UpdatedAtUtc` is the import time. In Merge, an existing record keeps its own created time (T-10).
  - The alias `created`, `created at`, `createdAt`, `created_at`, `created on` or `creation date` proposes it.
- **Checklist items** (tasks): each non-empty line of the cell becomes one checklist item, in order.
  - `[ ] text`, `[x] text` and `[X] text` set the done state, with an optional leading `-` or `*`.
  - A line without a checkbox becomes an item that is not done.
  - Empty lines are skipped. Line breaks are normalized to LF first (ADR 0025).
  - The alias `subtasks`, `checklist` or `checklist items` proposes it.
  - In Merge, a supplied cell replaces the task's checklist; an empty cell clears it (B-12 supplied-collection rule).
- **Custom field** (tasks, CRs): an existing active custom field of the table's entity, or **Create custom field…**.
  - A new field takes its label from the column header and a key derived from that label. Its kind is suggested from the column's values: Date if every value is an ISO date, Number if every value is numeric, Url if every value is an http(s) URL, MultilineText if any value spans lines, otherwise Text.
  - The user can change the label and pick another kind: Text, MultilineText, Number, Date, Url or Lookup. A Lookup field gets one option for each distinct value.
  - New fields are optional and are created in the same atomic apply as the records (B-11). The preview lists them.
  - A value that is invalid for the field's kind is an Error finding for that record. Values are never coerced.
- **Unmapped columns:** each column left on *Ignore* has a **Keep hidden** / **Discard** choice, defaulting to Keep hidden.
  - *Keep hidden* stores the values in the record's preserved metadata, as today (T-05).
  - *Discard* drops them; they are not stored anywhere.
  - The preview shows how many columns are kept and how many are discarded.

## Edge rules (settled during review)
- **Created limits and time zones:** a value whose UTC instant falls outside the `DateTimeOffset` range is invalid. A local time that falls in a daylight-saving gap or overlap takes the zone's standard offset.
- **Markdown checklists:** the Checklist items alias is not proposed for Markdown sources, because their checklists come from the body (B-13.3). If the user maps a front-matter column anyway and the body also has a checklist, the record gets an Error.
- **JSON checklist arrays:** a checklist array whose elements aren't text is an Error. It never becomes an empty checklist.
- **Reviews and General to-dos have no preserved metadata.** Their unmapped columns are always discarded, and the wizard says so instead of offering Keep hidden.
- **Keys for new custom fields:** when the key proposed from the label is empty, reserved, or already taken, a generated key (`field`, `field_2`, …) is used instead. The label is kept. Two tables that create the same field (same target, label and kind) in one import share one new field.
- **New Lookup fields** need at least one non-blank value and at most 200 distinct values. Outside that range the preview shows an Error that suggests Text.
- **Earlier hidden data:** Discard applies to the incoming data only. Hidden data that an earlier import kept stays on the record.

## Consequences
- Data from any spreadsheet can reach a visible, editable home without a code change: a built-in field, a custom field, or an explicit discard.
- T-05's preservation rule is now a default, not a guarantee. Discarded external columns can't be recovered from the dataset, only from the source file.
- Mapping to a custom field creates configuration during an external import. This is the additive behavior that ADR 0026 keeps for external sources.
- Board order is still not a mapping target. Imported tasks are ranked by the app.
