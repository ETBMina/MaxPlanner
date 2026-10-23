# ADR 0015: Backup and restore protocol

- **Status:** Accepted
- **Requirements:** T-09, AC-15

## Decision
**Backup**
- The SQLite online backup API copies `data.db` into `backups/<yyyyMMddTHHmmssZ>-<kind>/`, together with `configuration.json` and `manifest.json`. The manifest holds the format/app/schema versions, dataset ID, kind and a SHA-256 for each file.
- Backup kinds: `daily`, `manual`, `migration-safety`, `restore-safety`, `import-safety`.

**Retention**
- 7 daily backups.
- Safety backups are retained separately.
- Manual backups are never pruned.
- A backup failure is reported in an InfoBar and the log.

**Restore**
1. Extract to `temp/restore-<id>/staging`.
2. Verify checksums.
3. Open staging read-only and run `integrity_check`, `foreign_key_check`, the schema version check (migrating staging if older) and domain validation.
4. Take a restore-safety backup.
5. Drain the writer and close.
6. Write `restore.pending.json` with phase `swapping`.
7. Move current files to `previous/` and staging into `data/`.
8. Set the phase to `committed`, clean up, reopen, and rebuild projections.

**Interrupted restore.** A marker found at startup rolls forward if staging is complete and verified, or rolls back from `previous/` otherwise. The result is always reported. The app never substitutes an empty database.

## Verification
Fault tests that interrupt the restore at each phase.

## Implementation (Phase 15, 2026-10-23)
- `Infrastructure/Backup/RestoreService.cs`: `Validate` copies `data.db` to `data/temp/restore-<id>/staging` and checks the manifest, SHA-256 and length, schema version (older is accepted and migrated by the normal open, newer refused), `integrity_check`, `foreign_key_check`, and loads every record through the storage mappers (domain `Rehydrate` rules) with link and Subtask card parent checks. `Swap` (dataset closed, pools cleared) writes `restore.pending.json` (phase `swapping`, staging path, `previous/` path, expected SHA-256), moves the current `data.db`/`-wal`/`-shm` to `previous/`, moves the staged copy in, verifies the checksum, sets `committed`, removes the marker and the restore folder; a failure rolls back from `previous/` and removes the marker only after a verified rollback. `RecoverInterrupted` rolls forward when `data/data.db` or the staged copy verifies against the marker's checksum, otherwise rolls back from `previous/`, otherwise leaves the marker (Unresolved). No path creates an empty database.
- Settings › Data restore (`StartupCoordinator.RestoreAsync`): restore-safety backup with the online API first (no backup, no restore), then the T-06.6 close of the session, the swap, and a normal reopen (full projection load). The recovery page offers **Finish the interrupted restore** (InterruptedRestore) and **Restore from a backup…** (Corrupt, DatabaseMissing, MigrationFailed; a raw checksummed copy of the current files is kept as a restore-safety backup because the dataset cannot be opened).
- **Interim decision:** a restore replaces `data.db` only; the running app's `configuration.json` (UI preferences) is kept, and the backup's copy stays in its folder. Saved filters that refer to lookups missing from the restored data are cleared visibly by the existing B-10 validation.
- Manual backups: `BackupMaintenance.Create` (kinds `manual` and `import-safety` from the Data page) and `DeleteManual` (manual kind only, directly under `backups/`). `BackupCatalog.List` reports kind, date, size and a cheap manifest state (present, parsable, file sizes); full checksums are verified only by restore.
