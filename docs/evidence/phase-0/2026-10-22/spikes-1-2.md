# Phase 0 evidence: spike 1 (portable payload) and spike 2 (SQLite engine)

**Environment:** Lenovo Legion Pro 5 (developer machine), Windows 11 Pro 25H2 build 26200, x64, SSD. .NET SDK 10.0.401 / runtime 10.0.12. WinApp CLI 0.7.1.

## Spike 1: portable publish (ADR 0003)
| Build | Windows App SDK references | Files | Unpacked | ZIP |
|---|---|---|---|---|
| A | `Microsoft.WindowsAppSDK` 2.5.1 metapackage | 545 | 235.9 MB | 93.7 MB |
| **B (adopted)** | Components: `WinUI` 2.3.9, `Foundation` 2.3.12, `InteractiveExperiences` 2.1.9, `Base` 2.0.4 | 474 | **176.2 MB** | **69.4 MB** |

- **What build B drops:** `onnxruntime.dll` (20.7 MB), `DirectML.dll` (17.8 MB), and the Search, AI and Widgets components. None of them is used.
- **Launch check (both builds):** the ZIP was extracted to `%TEMP%\Max Planner Tëst …` (a space and a non-ASCII character in the path) and started with the working directory set to `%TEMP%`. A responsive top-level window appeared.
- **UI Automation (build B):** `Shell_TitleBar` shows "Max Planner", `Shell_StartupText` shows "Opening your planner…", and the native caption buttons are present. Screenshot: `component-packages-portable-launch.png`.
- `e_sqlite3.dll` is present in the published output; the publish script asserts it.

**Not yet proven:** a clean offline machine (Windows Sandbox or VM) and the VC++ runtime dependency. Every run so far was on the developer machine.

## Spike 2: SQLite engine behavior (ADR 0009)
Tests: `tests/MaxPlanner.Infrastructure.Tests/Persistence/SqliteEngineSpikeTests.cs`. All tests use real files in `%TEMP%\MaxPlanner Tëst DB\…`, with Microsoft.Data.Sqlite 10.0.13 and SQLitePCLRaw.bundle_e_sqlite3 2.1.13.

| Behavior | Result |
|---|---|
| Writer settings | `journal_mode=wal`, `synchronous=2` (FULL), `foreign_keys=1` |
| CR number uniqueness on the normalized value (`"  cr-105 "` vs `"CR-105"`) | Rejected with SQLITE_CONSTRAINT_UNIQUE (2067) |
| One CR, many tasks | Allowed |
| A second link for the same task | Rejected with SQLITE_CONSTRAINT_PRIMARYKEY (1555) |
| A link to a missing CR or task | Rejected with SQLITE_CONSTRAINT_FOREIGNKEY (787) |
| A failed statement inside a transaction | The whole logical operation rolled back |
| Read-only snapshot transaction while the writer commits 5 more | Rows and `commit_sequence` unchanged inside the snapshot, and equal to each other |
| Online backup (`BackupDatabase`) during continuous writes | Copy passes `integrity_check = ok` and is one consistent snapshot (180 of 271 commits captured) |
| Single-row commit latency, `synchronous=FULL`, 300 runs | p50 **1.21 ms**, p95 **2.70 ms**, max 5.52 ms |
| Writer process killed mid-stream (`Process.Kill`) | Reopen passes `integrity_check = ok`. Rows equal `commit_sequence` (no torn transaction). 267 ≥ 250 acknowledged commits survived. |

**Limits of this evidence**
- A process kill is not a power loss. Durability across OS crashes or power loss relies on SQLite's documented `synchronous=FULL` guarantees and the drive honoring flushes.
- Disk-full and file-lock fault injection are Phase 1 tests.
