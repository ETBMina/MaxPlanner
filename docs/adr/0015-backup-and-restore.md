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
