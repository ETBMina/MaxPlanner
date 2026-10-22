# Max Planner — Implementation Plan (WinUI 3 · .NET 10 · SQLite · portable)

Approved on 2026-10-22. This file tracks the work order and its actual status. Product rules live in `Business Requirements.md`, UI rules in `design.md`, and decisions in `docs/adr/`.

## Progress
Status values: `Done` (verified), `In progress`, `Pending`, `Blocked` (with a reason).

| Phase | Item | Status | Evidence / notes |
|---|---|---|---|
| 0 | WinApp CLI upgraded with `winui:winui-setup` | Done | 0.5.0 → 0.7.1 on 2026-10-22. Developer Mode was already enabled. .NET SDK stays at 10.0.401 (D12). |
| 0 | `git init`, `.gitignore`, `.gitattributes` | Done | Branch `main`, no commits (D9) |
| 0 | Old net8/MSIX scaffold removed | Done | `MaxPlanner/`, `MaxPlanner (Package)/` and `MaxPlanner.slnx` deleted |
| 0 | `implementation-plan.md` | Done | This file |
| 0 | Requirements and AGENTS.md rewritten for D1–D20 | Done | `Business Requirements.md` adds B-16, B-17 and AC-28–AC-33. AGENTS.md §3 now directs the use of plugin agents and skills. |
| 0 | `design.md` (winui-design) | Done | 771 lines, written by the `winui:winui-dev` agent with the `winui:winui-design` skill and `winapp find-ui`/`find-api`. Design questions Q1–Q6 and Q8 were resolved with the owner and folded into the requirements. Still open: verify the glyph codepoints marked † in §6.4. |
| 0 | ADRs 0001–0020 | Done | `docs/adr/`. 0003, 0004, 0012 and 0013 stay Proposed until their spikes run. |
| 0 | Solution scaffold, props, scripts, architecture tests | Done | Five projects plus 2 test projects. `scripts/build.ps1`: 0 warnings, 0 errors. `scripts/test.ps1`: 15/15 passed. The unpackaged dev launch shows the Mica window with resw strings (`docs/evidence/phase-0/2026-10-22/`). Not yet verified: publish-portable/verify-portable (spike 1). |
| 0 | Spike 1: portable publish | In progress | Switched to WinAppSDK component packages: ZIP 93.7 → 69.4 MB, unpacked 236 → 176 MB. The extracted ZIP launches with resources resolved, **on the dev machine only**. Still open: clean offline Sandbox/VM run and the CRT check. Evidence: `docs/evidence/phase-0/2026-10-22/spikes-1-2.md` |
| 0 | Spike 2: SQLite engine | Done | 8 real-file tests pass: WAL/FULL/FK; UNIQUE/PK/FK constraints; snapshot isolation; consistent online backup during writes; commit p95 2.70 ms; kill-mid-write keeps every acknowledged commit. `SqliteConnectionFactory` added. |
| 0 | Spike 3: Grid gate | Done | **Decision: custom ListView grid (ADR 0004).** TableView 1.5.1 failed virtualization memory (244 → 901 MB, leaking x:Bind template trackers). Its async commit needs internal APIs, and its column headers are missing from UIA. The ListView fallback passed editing, async commit and memory with public APIs. Evidence: `docs/evidence/phase-0/grid-gate/` |
| 0 | Spike 5: Rx/DynamicData focus | Done | Pipeline confirmed (ADR 0010): focus, draft and scroll are kept; selection is restored from ViewModel state; p95 update 4–8 ms. Focus and selection rules added to design.md §7.7. |
| 0 | Spike 4: Toolkit SettingsControls | Done | **Owner decision: native Expander cards (ADR 0021).** The Toolkit needed the WinAppSDK 1.6 metapackage, and its clickable cards lack UIA Invoke. No Community Toolkit WinUI packages in release 1; TokenPicker and `UniformGridLayout` replace TokenizingTextBox and StaggeredLayout. |
| 0 | Spike 6: x:Uid in Presentation library | Done | The published build resolves Presentation `.resw` strings (ADR 0014) |
| 0 | Spike 7: Mica fallback | Partial | Runtime Mica/solid selection is implemented and the Mica path verified. Manual checks remain: transparency off, RDP, battery saver (ADR 0008). |
| 0 | Spike 8: single instance + lock | Done | Redirect, separate copies, locked (exit 3) and not-writable (exit 2) all verified on the published ZIP. A silent-exit defect on failed redirect was found and fixed (ADR 0013). |
| 0 | Spike 9: Presentation.Tests | Done | ViewModel tests run with no XAML runtime; 33/33 tests pass (ADR 0016) |
| 1–18 | Feature phases | Pending | See "Phases" below |

## Context
Build **Max Planner**, a native, offline, portable Windows 11 planner similar to Microsoft Planner. It covers Change Requests (CRs), tasks, Subtask cards, checklists, reviews, a Dashboard, a Calendar and data transfer.

- **Product authority:** `Business Requirements.md`, revised by your decisions below. Phase 0 rewrites the spec before any code, so the spec stays the authority.
- **Working rules:** `AGENTS.md` (Clean Architecture with MVVM, the T-12 order, the WinUI skills).
- **Visual reference:** `Designs/Max Planner.html`, adapted to native Fluent 2.
- **Current repo:** a net8 Visual Studio template with an MSIX `.wapproj`, x86/x64/ARM64 and trimming. All of it conflicts with the spec and will be replaced. `design.md` is missing and the folder is not a git repo.
- **Toolchain on this machine:** .NET SDK 10.0.401 (runtime 10.0.12), WinApp CLI 0.7.1 (upgraded from 0.5.0 in Phase 0), Windows 11 build 26200.
- **How this plan was made:** the `winui:winui-dev` agent drafted it after reading the SKILL.md files for `winui-design`, `winui-dev-workflow`, `winui-code-review`, `winui-ui-testing`, `winui-packaging`, `winui-setup` and OpenAI `winui-app`, and checking versions on NuGet and MS Learn. I merged its draft with your decisions.

## Decisions (they override the current spec)
| # | Decision |
|---|---|
| D1 | Naming: **Max Planner**. Projects `MaxPlanner.{Domain,Application,Infrastructure,Presentation,Desktop}`, exe `MaxPlanner.exe`, ZIP root `MaxPlanner/`. |
| D2 | Replace the old scaffold with a `src/` + `tests/` layout: .NET 10, unpackaged, self-contained, **win-x64 only**, no trimming, AOT or single-file. |
| D3 | **Task Title is inherited from the linked CR** and read-only. An unlinked task has its own editable title. |
| D4 | **Links are permanent.** No unlink and no reassign. An unlinked task may be linked **once**, with a confirmation. Its own title moves into hidden `PreservedMetadata.PreLinkTitle`, which is exported. |
| D5 | **Deleting a CR cascades** to its links, tasks, Checklist items and Subtask cards. A confirmation shows the counts, everything happens in one transaction, and tombstones prevent replay. |
| D6 | **Mica** on the main window with a native solid fallback. DesktopAcrylic only on transient surfaces where natively supported. |
| D7 | Grid: **custom virtualized ListView grid**. WinUI.TableView (MIT) was spiked and failed the gate (ADR 0004). |
| D8 | English only, with all strings in en-US `.resw`. |
| D9 | Upgrade WinApp CLI with `winui:winui-setup`. Run `git init`; **no commits** unless you ask. |
| D10 | **Service is required on linked tasks**, because it is what tells sibling tasks apart. |
| D11 | **CRs cannot be archived.** Archived tasks can be linked normally. |
| D12 | Stay on **SDK 10.0.401 / runtime 10.0.12**. No upgrade. |
| D13 | **CR records** become a Calendar mode (Month / Week / Records). Import, Export and Restore are a **page** under Settings › Data. |
| D14 | **CR Owner:** a **required** Person on each CR. New CRs prefill it from a Settings default. Linked tasks and their Subtask cards show it **read-only**; unlinked tasks show none. It is filterable on Board, Grid, Calendar and Dashboard. |
| D15 | A null squad is **displayed as "General"** (label only). The Checklists "General" group is renamed **"General to-dos"** to avoid confusion. |
| D16 | **Renames:** the new card type is called **"Subtask"** (a Subtask card). The old checkbox items inside a task are renamed **"Checklist items"**. |
| D17 | **Subtask card**, a lightweight entity under one parent task (one level only). See below. |
| D18 | **Review item**, a separate entity shown in its own section of the Checklists page. See below. |
| D19 | **Storage moves from LiteDB to SQLite**, as recommended. Approving this plan confirms it. |
| D20 | **Modernization pass** (your point 1), applied to the spec and the architecture. See below. |

### D17 — Subtask card
**Fields**
- Own and editable: **Subtitle** (required, ≤250), **Status** (task statuses), **StartDate/EndDate** (date-only), **Notes**, Rank, timestamps, Revision.
- Read-only, shown from the parent: the parent's **effective Title**, the parent's **Service**, and the CR number, Task Number, Squad ("General") and CR Owner that come through the parent's link.

**Rules**
- B-04 date automation applies to the card's **own** dates only. The parent's status and dates are never touched, so work on a closed task can reopen as a card without changing the task's history.
- A parent may have any number of Subtask cards.

**Creating cards**
- From the task card menu or the task editor: **"Add Subtask card"**.
- From a Checklist item: **"Pop out to card"**. This **moves** the item: in one transaction the item is removed and a card is created with Subtitle = item text and the item's done state mapped to the Completed status.

**Lifecycle**
- Archiving the parent hides its cards; restoring the parent shows them again.
- Deleting the parent deletes its cards after a confirmation with the count. The CR cascade includes them.

**Views**
- **Board:** cards sit in their status column with a "Subtask of …" badge, drag and drop and keyboard moves.
- **Grid:** shown together with tasks and identified by a Kind column (no Kind filter, owner decision 2026-10-22); only the card's own fields are editable.
- **Dashboard:** a separate **"Active subtask cards"** metric. Task cards and charts never count them.
- **Search:** covers the Subtitle plus the parent's title and CR reference.

### D18 — Review item (for reviewing other people's work without a CR or task)
**Fields:** CR number (free text, optional), Title (required), Task number (text), **Services** (a list of text chips), **Developer** (a Person; picked from People with add-new), **Review comments** (multiline), **IsDone** plus `CompletedOnLocal` (date-only, set when checked), timestamps, Revision.

**Checklists page**
- A **"Reviews"** section is shown as **one line per review**: a checkbox, the title, and a CR/task reference.
- An **"⋯" button** opens View/Edit (a dialog with all properties) and Delete.
- Completed reviews go to a collapsed **"Completed (n)"** list.
- Search covers all text fields.

**Linking:** a typed CR number is **free text and not a link**. If it matches an existing CR's normalized number, an "Open CR" affordance is shown. It is never used as an identity.

**Dashboard**
- A **Reviews** card: Open, plus completed this week and this month, calculated from `CompletedOnLocal` with the week starting Sunday.
- A **"Open reviews by developer"** bar chart.
- Drill-down opens Checklists › Reviews with the matching filter.

**Transfer:** reviews are included in JSON/CSV/XLSX (a `Reviews` sheet or table), backup and restore.

### D19 — Why SQLite instead of LiteDB (your criteria: instant UI, performance, portability)
- **Instant UI is engine-independent.** The UI updates from the in-process committed-change feed and the projection store (T-07), never by re-querying the database.
- **Performance:** a single-row commit in WAL mode takes under 1 ms on an SSD. Readers never block the writer, so snapshot loading and exports run in parallel with edits. Prepared row reads of 10k tasks plus 50k checklist items load fast enough for the 3-second startup budget.
- **Integrity:** real `UNIQUE`/`FOREIGN KEY`/`CHECK` constraints (the link table has `task_id PRIMARY KEY` and an FK to the CR), atomic multi-table transactions, `PRAGMA integrity_check` and `foreign_key_check`, and a built-in **online backup API**.
- **Portability:** the native `e_sqlite3.dll` ships in the ZIP through SQLitePCLRaw. There is still no install, no admin rights and no network. It is proven in the Phase-0 clean-sandbox spike.

### D20 — Modernization (spec rewrites, with a reason for each)
1. **LiteDB → SQLite** (D19). This rewrites T-02, T-05, T-06, T-09 and T-11.2.
2. **Windows App SDK 2.x** (2.5.1 Stable) replaces 1.x assumptions. Use the in-box `TitleBar` control and Mica (D6).
3. **OS matrix:** build 22000 (21H2) is out of servicing. The new **tested** matrix is Windows 11 **24H2 (26100) and 25H2 (26200)**. The compatibility floor becomes **22621 (22H2)** and is marked "untested-compatible", following T-02's own split between compatibility and support.
4. **`TimeProvider`** (the BCL abstraction) replaces a custom clock port. A tiny `LocalToday()` extension is added, and tests use `FakeTimeProvider`.
5. **Source generators everywhere they help:** MVVM Toolkit `[ObservableProperty]` on **partial properties** (C# 14), System.Text.Json `JsonSerializerContext`, `[LoggerMessage]`, and `[GeneratedRegex]` for import aliases. Reflection serialization is avoided.
6. **Reads and writes are separated.** One writer connection on a single ordered worker keeps commit order and idempotency. **Pooled read-only WAL connections** serve the snapshot, exports and previews. The LiteDB-era thread-affinity rule is dropped; the rule kept is "no `await` inside a transaction" (the delegate is synchronous by type).
7. **Tests:** xUnit v3 on the Microsoft Testing Platform, AwesomeAssertions (FluentAssertions 8 is commercially licensed), and ArchUnitNET architecture rules.
8. **Fluent 2 visual language:** Segoe Fluent Icons, the type ramp, Card/Layer brushes and `InfoBadge`. Hand-styled pills are replaced with native visuals. The Board drag uses native `ListView` drag and drop.
9. **Unchanged, because they are still current:** CommunityToolkit.Mvvm 8.4, DynamicData 9.5 with Rx 7 for keyed incremental projections, plain `ServiceCollection` (no Generic Host), and no mediator.

## Version pins (Central Package Management + lock files; "⚠" = prove in Phase 0)
| Area | Packages |
|---|---|
| Windows App SDK | **Microsoft.WindowsAppSDK 2.5.1** (⚠ metapackage vs component packages, to keep AI/ML out of the ZIP), **SDK.BuildTools 10.0.28000.2705**, **WinUIAnalyzer 0.7.1** |
| TFM and SDK | TFM `net10.0-windows10.0.26100.0`, minimum version **10.0.22621.0**; `global.json` 10.0.401 with `latestPatch` |
| Storage | **Microsoft.Data.Sqlite 10.0.x** + **SQLitePCLRaw.bundle_e_sqlite3** (⚠ exact pins and portable native load) |
| MVVM and reactive | CommunityToolkit.Mvvm 8.4.2, DynamicData 9.5.0, System.Reactive 7.0.0 (compatibility verified in spike 5) |
| Controls | No control packages: custom grid (TableView rejected, ADR 0004); native Expander settings cards, TokenPicker and `UniformGridLayout` (Toolkit WinUI rejected, ADR 0021) |
| Transfer | Sep 0.17.1 (CSV), DocumentFormat.OpenXml 3.5.1 (streaming XLSX), Markdig 1.4.2 + YamlDotNet 18.1.0 |
| DI and logging | Microsoft.Extensions.DependencyInjection 10.x, Serilog 4.4.0 + Sinks.File 7.0.0 (5 × 5 MiB rolling) |
| Tests | xunit.v3 4.0.2, AwesomeAssertions 9.6.0, ArchUnitNET 0.13.4, TimeProvider.Testing, BenchmarkDotNet 0.15.8 |

## Solution layout
```text
global.json  Directory.Build.props/.targets  Directory.Packages.props  .editorconfig  NuGet.config  MaxPlanner.slnx (x64)
design.md  implementation-plan.md  AGENTS.md  Business Requirements.md  Designs/
src/   MaxPlanner.Domain (net10.0, zero deps) → MaxPlanner.Application (net10.0)
       MaxPlanner.Infrastructure (net10.0: Sqlite, Serilog, Sep, OpenXml, Markdig)   MaxPlanner.Presentation (WinUI lib)
       MaxPlanner.Desktop (WinExe, AssemblyName=MaxPlanner; composition root)
tests/ Domain|Application|Infrastructure|Presentation|Architecture .Tests, Desktop.SmokeTests (published EXE), Benchmarks, ui/, fixtures/transfer
tools/ MaxPlanner.FixtureGenerator (benchmark dataset via real use cases), Generate-Notices.ps1
scripts/ build.ps1  test.ps1  publish-portable.ps1  verify-portable.ps1  run-ui-tests.ps1
docs/  adr/  schemas/transfer-v1.schema.json  samples/  deployment-guide.md  user-guide.md  dev-prerequisites.md  requirements-traceability.md  evidence/
```
**Shared props:** nullable, warnings as errors, `AnalysisLevel=latest-recommended`, deterministic builds, locked restore, and a guard target that rejects trim, AOT or single-file.

**Architecture tests:** they enforce the T-04 graph. Domain and Application may not use Sqlite, WinUI, Rx or file I/O. Presentation may not use Infrastructure. ViewModels may not use store ports.

## Architecture
**Domain** (immutable aggregates with `With…` transitions and strongly typed Guid IDs)
- **Aggregates**
  - `TaskItem`: `OwnTitle?`, Service, Status, Priority, people, actual dates, tags, notes, IsArchived, Rank, `ChecklistItem`s, custom values, `PreservedMetadata`.
  - `SubtaskCard`: ParentTaskId, Subtitle, Status, dates, Notes, Rank.
  - `ChangeRequest`: Number, Title, **OwnerId (required)**, Squad?, Status, Priority, `PlannedRange?`, EmailSubject, URL, Description, TaskNumber. **No archive.**
  - `CrTaskLink`: create-only.
  - `ReviewItem` and `GeneralTodo`.
  - Lookups: task and CR statuses with flags, priorities, squads, people, tags; plus `FieldDefinition` and `WorkspaceDefaults`, which gain **DefaultCrOwnerId**.
- **Policies** (pure and fully tested)
  - **Inheritance:** `TaskTitlePolicy`, plus inheritance resolution for Title, CR number, Task number, Squad and Owner through the parent link for Subtask cards.
  - **Links and deletion:** `CrLinkPolicy` (link once), `CrDeletionPolicy` (the cascade plan must equal the confirmed ID set).
  - **Rules from the spec:** `TaskDatePolicy` (B-04, applied to tasks and Subtask cards independently, never during import or restore), `OverduePolicy` (incomplete and End < today), `RankPolicy` (renumber the whole column, hidden cards included).
  - **Other rules:** `CopyTaskPolicy`, `LookupRemovalPolicy`, `CustomValuePolicy`, `ReviewCompletionPolicy`.
  - **Calendar:** `CalendarLayoutPolicy` clips to the visible Sun–Thu days, sorts by duration desc, start asc, ID, assigns first-fit lanes, emits "+N" overflow, continuation flags and the outside-workdays list.

**Application**
- **Mutations:** `IMutationCoordinator.ExecuteAsync(opId, kind, Func<IWriteTransaction, TxResult<T>>, ct)`. The delegate is synchronous by type. Cancellation counts only before commit.
- **Reads:** `IReadSnapshot` runs on pooled WAL readers.
- **Change feed:** `CommittedChangeSet` carries a **gap-free sequence written in the same transaction** and is published in commit order.
- **Idempotency:** an operation ledger.
- **Errors:** typed `AppError`s (Validation, Conflict, NotFound, DuplicateCrNumber, TaskAlreadyLinked, CascadeScopeChanged, Storage, CommitOutcomeUnknown, UnsupportedSchema).
- **Use cases** (one per intent): tasks, links, copy, moves, checklist items, Subtask cards (create, pop out, edit, move, delete), CRs (incl. schedule, resize endpoint, move to backlog, cascade delete), reviews, General to-dos, lookups/defaults/fields, transfer, backup.
- **Read-model composers** resolve every inherited value from current relationships.
- **`DashboardCalculator`** returns cards, charts and lists **with the drill-down filter specs**, so drill-downs reuse exactly the same predicate.

**Infrastructure (SQLite)**
- **Connection settings:** `data/data.db`; `journal_mode=WAL`, `synchronous=FULL` (success means durable), `foreign_keys=ON`, `busy_timeout`; schema version in `PRAGMA user_version` plus an `app_metadata` row.
- **Writing:** one writer connection on a dedicated ordered worker (bounded `Channel`), using `BEGIN IMMEDIATE` → work → commit/rollback. Results are fully materialized.
- **Reading:** read-only pooled connections. A snapshot reads all tables **and** `commit_sequence` in **one read transaction**, which makes the handoff race-free.
- **Schema**
  - Main tables: `tasks`, `subtask_cards(parent_task_id FK)`, `checklist_items(task_id FK, ord)`, `change_requests(number_norm UNIQUE, owner_id FK NOT NULL)`, `cr_task_links(task_id PK FK, cr_id FK)` with an index on `cr_id`, `task_people(task_id, person_id, role)`, `task_tags`, `review_items`, `review_services`, `general_todos`.
  - Lookups: lookup tables with `name_norm UNIQUE`, plus `field_definitions`.
  - Flexible content: `custom_values` and `legacy` stored as JSON TEXT.
  - Bookkeeping: `operation_ledger`, `import_ledger`, `tombstones`.
  - Encodings: GUIDs as BLOB(16), business dates as ISO `TEXT CHECK(...)`, UTC timestamps as INTEGER ticks.
- **Migrations:** numbered, embedded SQL plus C# steps, preceded by an online-backup safety copy. The app refuses a newer schema.
- **Startup:**
  - A probe checks that `data/` is writable, with no AppData fallback.
  - Single instance uses `AppInstance` redirection plus `data/.lock` opened with `FileShare.None`.
  - When data is missing or corrupt, show a recovery page and never create an empty DB silently.
- **Preferences:** `configuration.json` (UI only) via a temp file plus `File.Replace` with a last-good copy.
- **Backup:** the **SQLite online backup API** writes a manifest with SHA-256. Keep 7 dailies; safety backups are separate; manual backups are never pruned.
- **Restore:** staging, then `integrity_check` + `foreign_key_check` + domain validation, then a safety backup, then a swap through a `restore.pending.json` marker with roll-forward/back.
- **Logs:** bounded, with no content bodies.
- **Transfer adapters:** enforce the T-10 limits and the zip-slip guard, neutralize formulas in CSV, handle XLSX date1904 and reject formulas, and parse Markdown with Markdig.

**Presentation**
- **Projection store:** `WorkspaceProjectionStore` holds `SourceCache`s for tasks (archived included), Subtask cards, CRs, reviews and General to-dos, plus lookups.
  - **Handoff:** subscribe to the feed first, then load the snapshot, then drop changes with sequence ≤ the snapshot's.
  - A sequence gap or publication fault switches to Resyncing: editing is blocked and the store reloads.
  - The store emits one `Committed(seq)` per batch.
- **View pipelines:** `Filter → TransformWithInlineUpdate → SortAndBind → DispatcherQueueScheduler` preserves focus, selection and scroll.
- **ViewModels:** page ViewModels are retained singletons that own their subscriptions. Editor ViewModels are transient drafts with `IsDirty`, `IsSaving`, field errors and conflict review.
- **Services:**
  - `DialogService`: serialized, sets `XamlRoot` and theme, confirms with inline steps.
  - `NavigationService`: drill-down filter-context stack with a "From Dashboard" chip.
  - `ThemeService`: System/Light/Dark plus caption colors.
  - `BackdropService`: Mica, or the solid fallback.
- **Responsive:** `AdaptiveTrigger` breakpoints at 0, 640 and 1008 epx.
- **AutomationIds:** `Area_NameSuffix`, with record-keyed IDs for repeated items.

**Desktop**
- Custom `Program.Main`: single-instance check, then `Application.Start`.
- **StartupCoordinator:** show the window early, then probe, lock, configure logs and prefs, handle the recovery marker, run the daily backup, open the DB, migrate, and load the store.
- **Composition:** `ServiceCollection`.
- **Day boundaries:** midnight timer, `WM_TIMECHANGE`/`WM_SETTINGCHANGE`, resume and activation.
- **Shutdown:** drain the worker, dispose subscriptions, flush prefs, run a WAL checkpoint, release the lock.
- **Exit codes:** 0, 2 not writable, 3 locked, 4 newer schema, 5 failure.

**Native control map (adapted from the design)**
| Area | Controls |
|---|---|
| Shell | `TitleBar` + `NavigationView` (Settings in the footer) |
| Page commands | `CommandBar` with `AutoSuggestBox` + `InfoBadge` |
| Dashboard | Native accessible bar charts with Table toggles. **Cards:** Active tasks, Completed tasks, Unscheduled tasks, Overdue CRs, **Active subtask cards**, **Reviews**. **Charts:** by status, by squad ("General"), **Open reviews by developer**. **Lists:** Overdue CRs and Unlinked tasks. **Filters:** Squad, Priority and **CR Owner**. |
| Board | Virtualized `ListView` columns with drag and drop and keyboard moves. Task and Subtask cards; Subtask cards show the parent title, service and subtitle. |
| Grid | Custom virtualized ListView grid with header row and Grid/Table automation peers (ADR 0004); Kind, Owner and Title (read-only when linked) columns |
| Calendar | Custom Month/Week views with `Thumb` resize, `SplitView` backlog, "+N more" flyout and a **Records** mode; Owner shown on bars and records |
| Checklists | `ItemsRepeater` cards for **General to-dos**, task groups (Checklist items, each with a "Pop out to card" action) and **Reviews**, plus collapsed Completed sections |
| Settings | `SettingsExpander`: theme, workflow, priorities, squads, people/tags, custom fields, **Default CR Owner** among the defaults, and Data (location, backup, Import/Export/Restore page) |
| Editors | `ContentDialog`s. **Task:** inherited Title/Number/Squad/Owner shown read-only; "Link to CR…" for unlinked tasks only; Service marked required when linked; Checklist items; Subtask cards list with "Add". **Subtask card:** parent title and service read-only, Subtitle, Status, dates, Notes. **CR:** Owner (required) and Task number. **Review:** all fields. **Cascade-delete:** shows the counts. |

## Phases (T-12 order; each UI phase uses winui-design → build/run with winui-dev-workflow → winui-code-review → winui-ui-testing plus a screenshot review)
**Phase 0. Tooling, docs, scaffold and gates.**
- *Tooling:* `winui:winui-setup` upgrades WinApp CLI to ≥0.7. Record the real `winapp new/run/find-ui/find-api/ui --help` output in `docs/dev-prerequisites.md`.
- *Repo:* `git init` with ignore files; delete the old scaffold.
- *Documents:* **rewrite `Business Requirements.md` and `AGENTS.md` for D1–D20** (clause list below). Write `design.md` with `winui:winui-design` (all screens, including the new Subtask card, Review, Owner, cascade, Records, Import page, Loading/Empty/Error states, keyboard map, AutomationIds). Write `implementation-plan.md` with a status column, and the ADRs (naming, toolchain/pins, portable deployment, Grid gate, title inheritance, permanent links, cascade, backdrop, **SQLite**, projection store, charts, transfer libraries, single instance, localization, backup, test stack, Subtask card, Review item, OS matrix).
- *Scaffold:* build a reference scaffold in the scratchpad, then hand-author the five projects, the scripts and the architecture tests.
- *Spikes, each with evidence:*
  1. Portable ZIP launched from a path with spaces and Unicode, plus an offline Windows Sandbox run, **including the native e_sqlite3 load**, CRT check and payload size.
  2. SQLite WAL with `synchronous=FULL` commit latency, constraint enforcement, online backup during writes, and kill-and-reopen.
  3. **Grid gate score-sheet** (F2/Enter/Esc/Tab-commit, keep the editor open during an async commit, 10k-row virtualization, UIA, column persistence, High Contrast).
  4. Toolkit SettingsControls on WinAppSDK 2.5.1.
  5. Rx 7 + DynamicData focus preservation.
  6. `x:Uid` in a class library when published.
  7. Mica and its fallback.
  8. Unpackaged single instance.
  9. Presentation.Tests loads without WinRT activation.
- *Exit:* green build (analyzers, warnings as errors, locked restore), green architecture tests, and the gate decided.

**Phase 1. Integrity.**
- *Build:* Domain and policies (incl. Subtask card, Review, Owner, cascade); Application ports, errors, change sets and ledger; the SQLite writer/readers, schema, constraints, migration M0001 with seed defaults, tombstones, startup probe/lock/recovery, preferences, logging and daily backup; the projection store handoff and resync.
- *Test:* use **real temp SQLite files** for uniqueness and FKs, rollback, ledger replay, conflicts, cascade atomicity under fault injection, migration failure, newer schema, corrupt file and load-time races.
- *Slices (branch `phase-1/integrity`), each ending with a green build and tests:*

| Slice | Content | Status |
|---|---|---|
| 1a | Domain: text values and limits (B-02.10), dates/ranges, lookups with semantic flags, entities (Task, ChecklistItem, SubtaskCard, ChangeRequest, CrTaskLink, ReviewItem, GeneralTodo), and policies: dates (B-04), overdue (B-15), title inheritance (D3), link-once (D4), cascade scope (D5), rank (B-07), review completion (B-17) | Done: 40 Domain tests; full suite 78/78, 0 warnings. Custom values are deferred to 1b. |
| 1b | Domain: calendar lane layout (B-08.6–9), copy-task policy (B-06), lookup removal (B-04), custom values (B-11) | Done. Built by the `winui:winui-dev` agent, verified here: 189/189 tests, 0 warnings. The agent found and fixed a calendar lane bug (lanes not reused). Owner follow-ups are in: no chevron for hidden-day spill, long-item pop-out (shortened Subtitle + full Notes), copy "Keep current status". Domain `Rehydrate` factories with corrupt-row checks are ready for 1d. |
| 1c | Application: ports, typed errors, `CommittedChangeSet`, operation ledger contract, read-model composers, first use cases (create/update/move task, create CR, link, cascade delete, checklist and Subtask card operations) | Done (agent-built, reviewed here). An in-memory coordinator fake is the executable spec for 1d. Not yet implemented: Copy use case, lookup-removal and field-definition use cases, new people/tags on save (B-02.6), default-owner offer (B-11), change-feed port (1e). |
| 1d | Infrastructure: schema M0001 + seed (B-04 defaults), ordered writer worker, readers, row mappers, ledger, tombstones, migration runner, newer-schema refusal; fault-injection tests. **Must:** implement the 1c ports to pass the same contract the in-memory fake encodes, and order Board rank ties with the .NET `Guid` comparison (SQLite's byte-wise BLOB order differs), so renumbering never swaps tied cards. | Done (agent-built, verified here: 237/237, 0 warnings). Delivered: STRICT schema M0001 with B-04 seed; link-permanence trigger; migrator with safety backup and newer/corrupt refusal; no implicit DB creation; ordered writer coordinator with ledger replay, gap-free sequence, typed storage errors, CommitOutcomeUnknown + `IOperationStatusQuery`; `IChangeFeed`; consistent read snapshot; real disk-full test. Follow-ups done (253/253, 0 warnings, verified here): the in-memory fake now enforces the same revision guard as SQLite, with mirrored contract tests; CR preserved metadata is stored in `preserved_json`; `FieldDefinition.Rehydrate` with corrupt-row checks. |
| 1e | Presentation projection store: snapshot/sequence handoff, resync on gap, composer integration | Pending |
| 1f | Desktop startup coordinator, recovery page for missing/corrupt data, `configuration.json` preferences, bounded logging, daily backup | Pending |

**Phase 2. Vertical slice.**
- *Build:* the shell (Mica, theme) and a minimal Grid edit → persist → Board card and Checklists update without reload.
- *Test:* disk-full and stale-revision fault paths, persistence checked by a genuine relaunch, and 100 navigation cycles with a stable subscription count (AC-06, AC-07).

**Phases 3–15. Features.**
| # | Feature | Requirements |
|---|---|---|
| 3 | Board: drag and drop and keyboard moves for tasks **and Subtask cards**, filtered-append rule, responsive | B-07, AC-05 |
| 4 | Task editor: TokenPicker, Checklist items, link-once flow, Service required when linked, copy, archive/restore/delete | B-02, B-05, B-06 |
| 5 | **Subtask cards:** create, pop out, edit, own date automation, parent archive/delete effects | new B-16 |
| 6 | CR editor: Owner, default owner, linked tasks, Create task (save first), cascade delete | B-03, T-13 |
| 7 | Calendar: Month/Week, scheduling, resize with keyboard, Records mode | B-08, AC-11 |
| 8 | Checklists: General to-dos, Checklist items, **Reviews section** with one-line rows, the ⋯ dialog and completion | B-09, new B-17 |
| 9 | Full Grid: Kind, Owner and inherited columns, typed editors, layout persistence | B-07 |
| 10 | Settings, workflow, custom fields, Default CR Owner | B-04, B-11 |
| 11 | Search and filters (incl. Owner), drill-down context stack | B-10 |
| 12 | Dashboard incl. Subtask-card and Review metrics, Owner filter | B-15, AC-18–21 |
| 13 | JSON/CSV/XLSX transfer, incl. Subtask cards, Reviews, Owner and link permanence | B-12, AC-12/13 |
| 14 | External import wizard, incl. Markdown and the import ledger | B-13, AC-14 |
| 15 | Backup, restore and recovery UI | T-09, AC-15 |

**Phases 16–18. Hardening and release proof.**
- **16 Accessibility, responsive and theme pass:** screenshots at 500, 639, 640, 1007, 1008 and 1600 epx in System, Light and Dark. Manual checks, which you perform, cover HC, Narrator, touch, scaling, mixed DPI, Snap, transparency off and RDP.
- **17 Performance:** 10k-task fixture (plus Subtask cards and reviews), T-11 budgets.
- **18 Release proof** on the **published ZIP**: clean offline VM, relocation, second launch, protected folder, upgrade preserving `data/`, notices and checksums. `winui-packaging` is used for its self-contained guidance only, with no MSIX. **After this, we package the portable ZIP.**

## Spec rewrite list (Phase 0)
| Decision | Clauses |
|---|---|
| **D1** | Title, T-03 tree, T-04 tree, AGENTS.md |
| **D3** | T-05, T-07, T-13, B-02.2/.3/.4/.10, B-03.3/.10, B-06, B-07, B-12, B-13.7, AC-04/22, **new AC-28** |
| **D4** | T-01, T-05, T-07, T-10, T-13, B-02.4, B-03.6, B-03.7 (replaced), B-06, B-07, B-12, B-13.6, **new AC-29** |
| **D5** | T-06.3, T-13, B-05 (CR delete replaced), B-15.7, **new AC-30** |
| **D6** | T-08 backdrop bullets, T-11.5, AC-24/27, AGENTS.md "Native appearance" |
| **D10** | B-02.2 |
| **D11** | T-05 CR `IsArchived` removed; B-05, B-08.1/.4/.12, B-15, B-03.4, AC-10/19 |
| **D12** | T-02 runtime note |
| **D13** | B-08.4 |
| **D14** | T-05 CR, B-03.1/.3, B-04 defaults table, B-07 Board/Grid, B-10 filters, B-11, B-12, B-15 filters, T-13 inherited set, **new AC-31** |
| **D15** | All "Unset" → "General"; B-09 group renamed "General to-dos" |
| **D16/D17** | Global rename "Subtask" → "Checklist item"; **new B-16 Subtask cards**, **new AC-32** (pop-out atomicity, independent dates) |
| **D18** | **New B-17 Review items**; B-09, B-12, B-15 Review metrics, **new AC-33** |
| **D19/D20** | T-02 (storage and OS matrix), T-05 (relational schema and constraints), T-06 (writer/readers, WAL, FULL sync), T-09 (online backup), T-11.2 ("real temporary SQLite files"), AGENTS.md platform/mutation rows |

## Critical files
- `MaxPlanner.slnx`, `global.json`, `Directory.*.props`
- `src/MaxPlanner.Desktop/{Program.cs, App.xaml, MainWindow.xaml, Composition/*}`
- `src/MaxPlanner.Infrastructure/Persistence/{SqliteWriter.cs, SqliteReadPool.cs, Schema/*.sql, Migrations/*}`
- `src/MaxPlanner.Application/{Abstractions/IMutationCoordinator.cs, Changes/CommittedChangeSet.cs, Tasks/, SubtaskCards/, Reviews/, ChangeRequests/, Dashboard/}`
- `src/MaxPlanner.Domain/{Entities/, Policies/}`
- `src/MaxPlanner.Presentation/{Shared/Store/WorkspaceProjectionStore.cs, Shared/Services/, Board|Grid|Calendar|Checklists|Dashboard|Settings|Transfer/, Strings/en-US/Resources.resw}`
- `scripts/*.ps1`, `docs/adr/*`, `design.md`, `implementation-plan.md`
- **To delete:** `MaxPlanner/`, `MaxPlanner (Package)/`, the old `.slnx`

## Verification
- **Build and test:** `scripts/build.ps1 -Locked` and `scripts/test.ps1`. Tests are tagged with requirement IDs, which generates `docs/requirements-traceability.md`.
- **Persistence:** **real temp SQLite files**, plus fault, kill and restore-interruption tests.
- **UI:** `scripts/run-ui-tests.ps1 -Target local|sandbox` runs `winapp ui` batches on an isolated fixture, with screenshots reviewed visually. Persistence is checked by relaunching the app.
- **Portability:** `publish-portable.ps1` then `verify-portable.ps1`, which extracts to a path with spaces and Unicode, launches, and checks that a responsive window appears and `data/` is created beside the EXE. Phase 18 repeats this on a clean offline VM.
- **Evidence:** saved under `docs/evidence/<phase>/<date>/`. Manual or blocked checks are recorded honestly, never claimed.
