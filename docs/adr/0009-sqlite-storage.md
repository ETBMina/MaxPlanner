# ADR 0009: SQLite storage instead of LiteDB

- **Status:** Accepted (owner approved the plan on 2026-10-22). Engine behavior was verified by spike 2 on 2026-10-22 (`docs/evidence/phase-0/2026-10-22/spikes-1-2.md`). Pins: Microsoft.Data.Sqlite 10.0.13 with SQLitePCLRaw.bundle_e_sqlite3 2.1.13, the pair Microsoft.Data.Sqlite declares.
- **Requirements:** T-02, T-05, T-06, T-09, T-11.2

## Context
The original spec required LiteDB:
- Its latest stable release, 5.0.21, is from July 2024, and v6 is still prerelease.
- Its per-thread transaction tracking shaped the worker design.
- Its backup story was "checkpoint, close, copy".

The owner's criteria were instant UI updates, efficient performance, and unchanged portability.

## Options
| Option | Assessment |
|---|---|
| LiteDB 5.0.21 | Pure managed, document model. Aging, thread-affine transactions, no relational constraints. |
| **SQLite via Microsoft.Data.Sqlite** | Maintained by the .NET team (MIT). WAL concurrency, real UNIQUE/FK/CHECK constraints, online backup API, mature durability. Needs a bundled native `e_sqlite3.dll`. |
| SQLite via EF Core | Migration tooling and LINQ, at the cost of more reflection and startup work. Unnecessary for a single-user app with hand-tuned queries. |

## Decision
Use **SQLite via Microsoft.Data.Sqlite** with hand-written parameterized SQL in Infrastructure.

**Connection settings**
- `journal_mode=WAL`
- `synchronous=FULL`, so a reported success is durable
- `foreign_keys=ON`
- `busy_timeout`

**Concurrency**
- One writer connection owned by an ordered, bounded worker queue, using `BEGIN IMMEDIATE` transactions.
- The transaction delegate is synchronous by type, so nothing can `await` inside a transaction.
- Pooled read-only connections serve the snapshot (all tables plus `commit_sequence` in one read transaction), exports and previews.

**Encodings:** GUIDs as BLOB(16), business dates as ISO text with `CHECK`, UTC timestamps as integer ticks, custom and legacy values as JSON text.

**Versioning:** schema version in `PRAGMA user_version` plus `app_metadata`.

**Instant UI does not depend on the engine.** The UI is updated from the in-process committed change feed (ADR 0010), never by re-querying.

## Consequences
- The native SQLite library is part of the portable payload and the notices.
- Unique and relationship rules are enforced twice: by application validation (friendly errors) and by database constraints (last line of defense).

## Verification
- **Spike 2 (done 2026-10-22):**
  - Single-row commit under `synchronous=FULL`: p50 1.21 ms, p95 2.70 ms.
  - UNIQUE, PK and FK constraints are enforced.
  - A read transaction stays on one snapshot while the writer continues.
  - An online backup taken during writes is a consistent valid copy.
  - After the writer process is killed, every acknowledged commit survives with no torn transaction.
  - Connection settings live in `SqliteConnectionFactory`.
- **Phase 1:** fault injection (disk full, file lock) and the production schema/migrations with real temporary-file integration tests.
