# SQLite schema v1 (migration M0001)

- **Source of truth:** `src/MaxPlanner.Infrastructure/Persistence/Schema/M0001_initial.sql` (DDL, embedded in the assembly) and `Persistence/Migrations/M0001InitialSchema.cs` (seed). This page explains them; if they disagree, the code wins and this page is the defect.
- **Requirements:** T-05, T-06, T-09, B-03, B-04, B-05, B-07, B-16, B-17. **Decisions:** ADR 0006, 0007, 0009, 0010, 0015.
- **File:** `data/data.db` beside the executable (T-03). Connection settings: `journal_mode=WAL`, `synchronous=FULL`, `foreign_keys=ON`, `busy_timeout=5000` (`SqliteConnectionFactory`).

## Encodings

| Kind of value | Stored as | Notes |
|---|---|---|
| IDs (`Guid`) | `BLOB`, exactly 16 bytes, `CHECK (length(id) = 16)` | Byte layout is `Guid.ToByteArray()` (little-endian first three fields), read back with `new Guid(bytes)`. Microsoft.Data.Sqlite binds a `Guid` as TEXT by default, so code always binds the bytes explicitly (`SqlValues`); STRICT tables reject a TEXT ID. |
| ID ordering | never `ORDER BY id` | SQLite compares BLOBs byte by byte, which differs from `Guid.CompareTo`. Ties on rank are broken by the ID **in C#** after reading (B-07). |
| Business dates (`DateOnly`) | `TEXT` `yyyy-MM-dd`, `CHECK (… GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]')` | Invariant culture; never converted through UTC (T-05). |
| Audit timestamps (`DateTimeOffset`) | `INTEGER` UTC ticks (`UtcTicks`) | Read back with a zero offset. |
| Booleans | `INTEGER` 0/1 with `CHECK (x IN (0, 1))` | |
| Revisions | `INTEGER`, `CHECK (revision >= 1)` | Start at 1; each committed change increments (T-05). |
| Custom values, preserved metadata, field options and defaults, migration history, ledger results | `TEXT` JSON | Written and read with the source-generated `StorageJsonContext` (no reflection). Property names are part of the format. |
| Enum-like values (entity kind, field target/kind, person role) | `TEXT` with a `CHECK` list | Spelled explicitly in `StorageNames`, not taken from C# member names. |

Every table is `STRICT`, so a value of the wrong storage class is rejected rather than stored.

### JSON shapes

- **Custom values** (`custom_values_json`, NULL when empty): `{"<field-id D-format>": {"Kind": "Text|MultilineText|Number|Date|Lookup|People|Tags|Url", "Text"?: string, "Number"?: number, "Date"?: "yyyy-MM-dd", "Option"?: guid, "Ids"?: [guid]}}`. Values of deactivated fields are kept (B-11).
- **Preserved metadata** (`tasks.preserved_json` and `change_requests.preserved_json`, NULL when empty): `{"PreLinkTitle"?: string, "UnselectedCrReferences"?: [string], "LegacyJson"?: string}`. `LegacyJson` holds unknown imported content verbatim (T-05). `PreLinkTitle` is task-only.
- **Field options** (`options_json`): `[{"Id": guid, "Label": string, "Order": int}]`; **default value** (`default_value_json`): one custom value object.
- **Migration history** (`app_metadata.migration_history_json`): `[{"Version": int, "Name": string, "AppliedAtUtc": ISO-8601}]`.
- **Ledger result** (`operation_ledger.result_json`): the use case's result. Supported types are registered in `StorageJson.LedgerResultTypes` and `StorageJsonContext`: `TaskId`, `ChangeRequestId`, `SubtaskCardId` (`{"Value": guid}`), `long`, and `CascadeDeletionSummary`. Any other type is refused before its transaction starts.

Any stored value that fails to parse, or that a domain `Rehydrate` factory rejects, is reported as `StorageFailureError(Corrupt)`. Rows are never skipped or repaired.

## Tables

### Workflow lookups

| Table | Columns | Constraints |
|---|---|---|
| `task_statuses` | `id`, `name`, `name_norm`, `sort_order`, `is_completed`, `sets_start_date`, `is_backlog` | `name_norm` UNIQUE |
| `cr_statuses` | `id`, `name`, `name_norm`, `sort_order`, `is_completed`, `is_backlog` | `name_norm` UNIQUE |
| `priorities` | `id`, `name`, `name_norm`, `sort_order` (0 = highest) | `name_norm` UNIQUE |
| `squads` | `id`, `name`, `name_norm`, `sort_order` | `name_norm` UNIQUE |
| `people` | `id`, `name`, `name_norm` | `name_norm` UNIQUE |
| `tags` | `id`, `name`, `name_norm` | `name_norm` UNIQUE |
| `field_definitions` | `id`, `target` (`Task`/`ChangeRequest`), `field_key`, `key_norm`, `label`, `kind`, `is_required`, `default_value_json`, `display_order`, `options_json`, `is_active` | UNIQUE (`target`, `key_norm`) |

`name_norm` is `LookupName.Normalized` (trimmed, ordinal upper-case). People and tags are listed by `name_norm`; the others by `sort_order`, then ID.

Field definitions load through `FieldDefinition.Rehydrate`. It restores every stored value (including `is_active` and the stored option order) without re-running creation rules: a key that a later version reserves still loads. Structural checks report the row as Corrupt: key format, options present exactly for Lookup kinds, distinct option IDs, and a default value of the field's kind that names an existing option.

### `app_metadata` (exactly one row, `id = 1`)

`schema_version`, `dataset_id`, `commit_sequence`, `default_task_status_id`, `default_cr_status_id`, `default_priority_id`, `default_cr_owner_id` (NULL until chosen), `calendar_scheduling_status_id`, `cr_backlog_status_id`, `migration_history_json`. Every default is a foreign key to its lookup, so a lookup in use as a default cannot be removed without a replacement (B-04).

### Business records

| Table | Columns | Constraints and indexes |
|---|---|---|
| `tasks` | `id`, `own_title`, `service`, `status_id`, `priority_id`, `start_date`, `end_date`, `notes`, `is_archived`, `rank`, `custom_values_json`, `preserved_json`, `created_at_utc`, `updated_at_utc`, `revision` | FKs to `task_statuses`, `priorities`; `start_date <= end_date` when both are set; index `ix_tasks_status_archived (status_id, is_archived)` |
| `checklist_items` | `id`, `task_id`, `text`, `is_completed`, `rank` | FK `task_id` **ON DELETE CASCADE**; index on `task_id` |
| `task_people` | `task_id`, `person_id`, `role` (`Designer`/`Developer`), `position` | PK (`task_id`, `role`, `person_id`); FK `task_id` ON DELETE CASCADE; FK `person_id`; index on `person_id` |
| `task_tags` | `task_id`, `tag_id`, `position` | PK (`task_id`, `tag_id`); FK `task_id` ON DELETE CASCADE; FK `tag_id`; index on `tag_id` |
| `subtask_cards` | `id`, `parent_task_id`, `subtitle`, `status_id`, `start_date`, `end_date`, `notes`, `rank`, `created_at_utc`, `updated_at_utc`, `revision` | FK `parent_task_id` (**not** cascaded); FK `status_id` → `task_statuses`; date order CHECK; indexes `ix_subtask_cards_parent`, `ix_subtask_cards_status` |
| `change_requests` | `id`, `number`, `number_norm`, `title`, `owner_id`, `squad_id`, `status_id`, `priority_id`, `planned_start`, `planned_end`, `email_subject`, `solution_design_url`, `description`, `task_number`, `custom_values_json`, `preserved_json`, `created_at_utc`, `updated_at_utc`, `revision` | `number_norm` UNIQUE (`CrNumber.Normalized`: trimmed, ordinal upper-case); `owner_id` NOT NULL FK `people`; `squad_id` nullable FK (NULL displays "General"); planned dates both-or-none and `planned_start <= planned_end` (inclusive, B-04); index `ix_change_requests_planned (planned_start, planned_end)`. `preserved_json` holds `ChangeRequest.Preserved` (T-05 legacy content; NULL when empty). It is kept unchanged by edits, and a stored `PreLinkTitle` is rejected as Corrupt because only tasks are linked. |
| `cr_task_links` | `task_id`, `cr_id`, `linked_at_utc` | PK `task_id` (zero or one CR per task); FKs to `tasks` and `change_requests` (not cascaded); index `ix_cr_task_links_cr (cr_id)` (many tasks per CR). Trigger `tr_cr_task_links_permanent` aborts any UPDATE (ADR 0006). |
| `review_items` | `id`, `cr_number_text`, `title`, `task_number_text`, `developer_id`, `comments`, `is_done`, `completed_on`, `rank`, `created_at_utc`, `updated_at_utc`, `revision` | `developer_id` nullable FK `people`; `is_done = (completed_on IS NOT NULL)` (B-17.1). CR and task numbers are free text, not links. |
| `review_services` | `review_id`, `position`, `service` | PK (`review_id`, `position`); FK ON DELETE CASCADE |
| `general_todos` | `id`, `text`, `is_completed`, `rank`, `created_at_utc`, `updated_at_utc`, `revision` | |

Owned child rows (checklist items, task people/tags, review services) cascade with their parent. Subtask cards and links do not: task deletion and CR cascade deletion remove them explicitly in the same transaction, and the foreign keys reject a delete that forgot them (B-05, B-16.7, ADR 0007). The task's link is not stored on the task row; `TaskItem.ChangeRequestId` is loaded from `cr_task_links` (T-05).

### Bookkeeping

| Table | Columns | Purpose |
|---|---|---|
| `operation_ledger` | `operation_id` (PK), `kind`, `commit_sequence` (UNIQUE), `committed_at_utc`, `result_json` | One row per committed operation, written in the operation's transaction. A retry with the same operation ID returns the recorded result with `WasAlreadyApplied` and changes nothing (T-05). |
| `tombstones` | `entity_kind`, `id`, `deleted_at_utc`; PK (`entity_kind`, `id`); `WITHOUT ROWID` | Deleted identities, so stale editors and imports cannot re-create them (B-05). Recording the same ID twice keeps the first time. |
| `import_ledger` | `namespace`, `entity_kind`, `source_key`, `record_id`; PK (`namespace`, `entity_kind`, `source_key`); `WITHOUT ROWID` | External source identities (B-13). Empty until transfers are implemented. |

## Writes

- One writer connection on a dedicated thread (`SqliteMutationCoordinator`), fed by a bounded queue in enqueue order. Each operation is one `BEGIN IMMEDIATE` transaction whose body is synchronous (T-06.2).
- At commit, the same transaction increments `app_metadata.commit_sequence` (`UPDATE … RETURNING`) and inserts the ledger row. A rollback therefore consumes no sequence, and committed sequences are gap-free (T-07).
- UPDATEs are guarded with `WHERE id = $id AND revision = $previous`, where `$previous` is the revision the transaction loaded. Zero affected rows is a defect, not a user conflict; use cases report conflicts before writing.
- Checklist items are diffed on update, so toggling one item writes one row.
- Board columns (`ITaskStore.ListBoardColumn`/`MaxBoardRank`) contain the non-archived tasks in the status plus the Subtask cards of non-archived parents in the status, ordered by rank and then by .NET `Guid` comparison.

## Reads

`SqliteReadSnapshotSource` opens a pooled read-only connection and reads `commit_sequence` and then every table in **one** deferred read transaction, so the snapshot reflects exactly the commits up to its sequence (ADR 0010 handoff).

## Versioning and migrations

- The schema version is kept in both `PRAGMA user_version` and `app_metadata.schema_version`, written in the same transaction as each migration step, with a history entry. Supported version: **1**.
- Migrations are numbered consecutively (`SchemaMigrations.All`). Each runs in its own `BEGIN IMMEDIATE` transaction: embedded SQL and/or C# step, then history, `schema_version`, and `user_version`.
- **Opening** (`SqliteDataset.OpenExistingAsync`):
  1. A missing `data.db` returns `Missing`. Nothing is created (T-09, AC-15).
  2. The header is inspected before anything is written. Unreadable or not a database (`SQLITE_NOTADB`/`CORRUPT`) returns `StorageFailureError(Corrupt)`. `user_version` 0 means not a Max Planner dataset, which is also Corrupt and is never initialized. `user_version` above the supported version returns `UnsupportedSchemaError`. In all three cases the file is left byte-for-byte unchanged.
  3. `PRAGMA quick_check` must return `ok`; otherwise Corrupt.
  4. An older dataset is first copied with the online backup API to `data/backups/<yyyyMMddTHHmmssZ>-migration-safety/data.db`, then migrated. A failed step rolls back and returns `MigrationFailedError(currentVersion, failedVersion)` with the safety backup path. The dataset stays at its previous version.
  5. `app_metadata.schema_version` must equal `user_version`.
- **Creating** (`SqliteDataset.CreateNewAsync`) is a separate explicit call. It refuses when `data.db` or `data.db-wal` exists, runs M0001, and removes its own partial file if creation fails.

## M0001 seed (B-04 design defaults)

New random IDs are generated per dataset at creation; they never change afterwards.

| Lookup | Rows in order | Flags |
|---|---|---|
| Task statuses | Backlog, Blocked, In Progress, Review, Unit Testing, Completed, Designing, Developing | Backlog `is_backlog`; In Progress, Designing, Developing `sets_start_date`; Completed `is_completed` |
| CR statuses | Backlog, In Progress, Completed | Backlog `is_backlog`; Completed `is_completed` |
| Priorities | Urgent, High, Medium, Low | `sort_order` 0–3, highest first |
| Squads | Retail Finance, Retail Cards | No squad is NULL, shown as "General" |
| People, tags, field definitions | none | |

**Defaults:**
- Task status Backlog, CR status Backlog, priority Medium.
- Calendar scheduling status: CR In Progress. CR backlog status: CR Backlog.
- Default CR owner: NULL.
- `commit_sequence` 0, and a new `dataset_id`.
