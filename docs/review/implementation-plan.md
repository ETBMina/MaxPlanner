# App review — implementation plan

Living document for the whole-app review (architecture and MVVM, consistency, UI/UX, performance). It is updated after every review part. Business functionality is out of scope.

## Rules

- Review is done by Fable with the `winui:winui-code-review` skill. Code is written by Opus through the `winui:winui-dev` agent.
- `design.md`, the requirements and the ADRs are open to challenge. Still fixed: portable unpackaged ZIP, pinned .NET / Windows App SDK versions, approval before any new library.
- Three gates per part: the user approves findings by ID, approves each batch before it is implemented, and confirms before the next part's review starts.
- Refactors keep behaviour unchanged. Approved departures from an ADR or `design.md` are recorded in the same batch.
- Branch: `review/app-review`. One commit per approved batch, only when the user asks.
- The app is launched only from a disposable folder on a generated fixture, never on the user's Debug data.

## Status

| # | Part | Review | Findings approved | Implementation |
|---|---|---|---|---|
| 0 | Baseline | Done 2026-11-11 | n/a | n/a |
| 1 | Architecture and MVVM | Done 2026-11-11 | All nine approved 2026-11-11 | 1A, 1B, 1C, 1D merged 2026-11-12 (1290 tests); awaiting the user's manual check in the app. A-03(b), A-04, A-06 moved to part 2. |
| 2 | Consistency and shared components | Not started | | |
| 3 | UI / UX | Not started | | |
| 4 | Performance | Not started | | |

## Part 0 — Baseline

| Check | Result |
|---|---|
| `./scripts/build.ps1` | Passed 2026-11-11 — 0 warnings, 0 errors |
| `./scripts/test.ps1` | Passed 2026-11-11 — 1156 tests, 0 failed, 0 skipped (smoke tests not included) |
| `scripts/run-performance.ps1` (startup, page-switch p95, working set, subscriptions) | Run 2026-11-11 on the full benchmark fixture — see table below. Raw results: `docs/review/baseline/perf/` |
| "Before" screenshots | 26 images in `docs/review/baseline/screens/` — see coverage below |

### Performance baseline (2026-11-11)

Fixture: 5,000 CRs, 10,000 tasks (2,000 archived), 2,000 Subtask cards, 50,000 checklist items, 2,000 reviews, 5,000 General to-dos. Machine: i7-13700HX, 24 logical cores, Windows 11, .NET 10.0.15. Release portable build of `main` at `dabacbc`.

| Measure | Baseline | Budget | Verdict |
|---|---|---|---|
| Startup to rendered, worst of 5 launches | 1,490 ms (1,365–1,490) | 3,000 ms | Pass |
| Page switch, 100 switches | p50 28 ms, p95 95 ms, max 99 ms | p95 250 ms | Pass |
| Idle working set after 20 navigation rounds | 597.4 MiB | 500 MiB | **Miss** |
| Live store subscriptions, first vs last rounds | 21 vs 21 | no growth | Pass |
| Save one task edit to projection store (benchmark) | 1.68 ms, 71 KB allocated | — | — |
| Dashboard recompute over the store (benchmark) | 3.19 ms, 3.9 MB allocated | — | — |

Not measured by the script: save-to-visible-UI, search-to-visible-UI, Dashboard-after-commit in the UI, UI-thread stalls during import.

### Screenshot coverage

Data: benchmark fixture at scale 0.03 (150 CRs, 300 tasks, 60 Subtask cards, 1,500 checklist items, 60 reviews, 150 to-dos).

| Set | Files | Pages |
|---|---|---|
| Dark, default window | `dark-default-*.png` | Dashboard, Board, Grid, Calendar, Checklists, Settings |
| Light, default window | `light-default-*.png` | same six |
| Dark, narrow window (collapsed navigation) | `dark-narrow-*.png` | same six |
| Dark, maximized | `dark-wide-*.png` | same six |
| Dialogs, dark | `dark-dialog-new-task.png`, `dark-dialog-new-cr.png` | New task, New CR |

Not captured yet (done at the start of part 3): editors opened on existing records, Subtask card and review editors, filter and column flyouts, context menus, expanded Settings sections, the Data page and import wizard, Calendar Week and Records tabs, dialogs in light theme. Some images carry a tooltip strip on the right from the capture tool.

## Findings

Finding IDs: `A-nn` architecture, `C-nn` consistency, `U-nn` UI/UX, `P-nn` performance. Each records severity, evidence, proposed change, effort (S/M/L), risk, any ADR or `design.md` section it departs from, and the user's decision.

### Part 1 — Architecture and MVVM (reviewed 2026-11-11, all nine findings approved 2026-11-11)

Verdict: the layering holds and MVVM hygiene is good. The findings are about where logic sits inside Presentation and Desktop, not about broken boundaries.

What already holds (no action):

- Project references follow Domain ← Application ← Infrastructure/Presentation ← Desktop, enforced by `tests/MaxPlanner.Architecture.Tests`.
- No view model file references a WinUI type; the 29 non-view files that do are controls, panels, automation peers and view services.
- XAML uses `x:Bind` only (1,322 uses, 0 `{Binding}`); all 407 observable properties use the partial-property form; no `.Result`/`.Wait()` blocking calls.
- Writes go through use cases and one coordinator; reads go through read models and the projection store.

| ID | Severity | Finding | Evidence | Proposed change | Effort | Risk | Decision |
|---|---|---|---|---|---|---|---|
| A-01 | High | The four editor view models each re-implement the same editor state machine: about 40 members with identical names (save, dirty-close prompt, conflict review, reload, follow-up, retry) and no shared base. | `Tasks/TaskEditorViewModel.cs:60`, `ChangeRequests/CrEditorViewModel.cs:72`, `SubtaskCards/SubtaskCardEditorViewModel.cs:48`, `Reviews/ReviewEditorViewModel.cs:30` | Extract an `EditorViewModelBase` (or a composed `EditorSession`) that owns save, dirty-close, conflict and reload; each editor keeps only its fields and commands. | L | Medium; covered by `Presentation.Tests/Workspace` | Approved |
| A-02 | Medium | Actions are public methods called from code-behind handlers instead of commands: about 170 XAML event handlers against 83 `Command=` bindings, and 96 fire-and-forget `_ = …Async()` calls in code-behind. The editors have no `[RelayCommand]` at all. Two `async void` methods outside plain event handlers. | `Tasks/TaskEditorDialog.xaml.cs:195-346`, `ChangeRequests/CrEditorDialog.xaml.cs`, `Grid/GridPageView.xaml.cs:663`, `Checklists/ChecklistsPageView.xaml.cs:175` | Turn VM actions into `[RelayCommand]` (async where needed) bound with `x:Bind`; replace manual `SelectionChanged` handlers with `TwoWay` binds. Handlers stay only for focus, drag and keyboard work. | M–L | Low–Medium | Approved |
| A-03 | Medium | The Grid page code-behind (1,458 lines) is the grid control itself. It also parses typed and UI Automation values into drafts and raises hardcoded English error text. | `Grid/GridPageView.xaml.cs:722-844` (parsing, 8 English literals), whole file for the control logic | (a) Move value parsing to `GridRowViewModel` with `.resw` strings and tests. (b) Extract a `TaskGrid` control (header, scroll sync, cell host, editor flyouts) so the page is thin. | (a) S, (b) L | (a) Low, (b) Medium–High: focus behaviour is fragile and has no automated UI tests | Approved, (a) and (b) |
| A-04 | Low | Board drag/drop, insertion line and auto-scroll (about 340 lines) live in the page code-behind, as does page preloading in the shell view. Legitimate view logic, but not reusable or testable where it is. | `Board/BoardPageView.xaml.cs:513-850`, `Shell/ShellView.xaml.cs:297-470` | Move each into its own view-side class (`BoardDragController`, `PagePreloader`). | M | Medium | Approved |
| A-05 | Medium | Startup, recovery and restore orchestration lives in the Desktop executable and calls Infrastructure statics directly. No unit test references it; only the smoke tests reach it. The coordinator also receives the whole `IServiceProvider`. | `Desktop/Startup/StartupCoordinator.cs:47-240`, `Desktop/Maintenance/DataMaintenanceService.cs`, `Desktop/Composition/DatasetSession.cs` | Put the open/create/restore flow behind Application ports implemented by Infrastructure, move the coordinator where a test project can drive it with fakes, pass a session factory instead of the provider. Desktop keeps wiring and window code only. | M–L | Medium; this is the data-safety path, so tests come first | Approved |
| A-06 | Low | `WorkspaceContext` is a service bag handed to every view model: store, all 36 use cases, scheduler, preferences, dialogs, announcements, plus lazily created commands and filter scopes. Dependencies of a view model are not visible from its constructor. | `Workspace/WorkspaceContext.cs:26-241` | Group the use cases by feature and expose narrow interfaces (`IDialogService`, `IAnnouncer`) the context implements; view models take what they use. | M | Low | Approved |
| A-07 | Low | Seven views and controls create their own static `ResourceStrings` instead of using the injected `ILocalizedStrings`. | `Board/BoardPageView.xaml.cs:23`, `Grid/GridPageView.xaml.cs:51`, `Common/Controls/TagPill.cs:55`, 4 more | One shared view-side accessor, or take strings from the view model. | S | Low | Approved |
| A-08 | Low | Nothing enforces the Presentation rules that currently hold by discipline (view models free of WinUI types). | `tests/MaxPlanner.Architecture.Tests/LayerDependencyTests.cs` covers Domain and Application only | Add an architecture test: `*ViewModel` types and the Store/Workspace namespaces must not depend on `Microsoft.UI.Xaml`. | S | Low | Approved |
| A-09 | Low | Several files hold many types or have grown very large, which hides structure. | `Infrastructure/Persistence/Stores/SqliteStores.cs` (673), `Workspace/WorkspaceContext.cs` (5 types), `Application/Transfer/ImportPlanner.cs` (1,446), `External/ExternalImportPlanner.cs` (1,342) | One type per file; split the planners by record kind. No behaviour change. | S–M | Very low | Approved |

Not reviewed in depth in part 1: Calendar, Settings and Filter view models (metrics only); they are read closely in part 2.

## Implementation batches

Part 1. All nine findings are approved; each batch still needs the user's go-ahead before it is implemented. Batches run in this order, one at a time on `review/app-review`, because 1B, 1C, 1E, 1F and 1G touch the same Presentation files.

| Batch | Findings | Why here | Approved | Status |
|---|---|---|---|---|
| 1A | A-03(a), A-07, A-08, A-09 | Small, low risk, no behaviour change. A-08's test guards the later batches. | 2026-11-11 | Implemented and verified 2026-11-12 (build clean, 1176 tests). Committed as `11c61c1`; awaiting the user's manual check. Also removed the one violation A-08 found: `ShellViewModel` used `InfoBarSeverity`. |
| 1B | A-01 | One refactor across the four editors | 2026-11-12 | Implemented and verified 2026-11-12 (build clean, 1257 tests, independent review found no regression). Commits `286ab98` and `b0c86bc`, merged into `review/app-review` as `1d3b78c` (same tree as the tested commit). Not run in the app. |
| 1C | A-02 | After 1B, so commands are added once in the shared editor base | 2026-11-12 | Implemented and verified 2026-11-12 (build clean, 1290 tests). Five commits `d163f08`..`9aa8257`, merged into `review/app-review` as `965f24e` (same tree as the tested commit). Independent review covered the first three commits only; the failure handling (ADR 0029, commits `f764ce7`, `9aa8257`) was reviewed by the main session only. Not run in the app. |
| 1D | A-05 | Desktop/Infrastructure only, tests first. Touches no Presentation file, so it may run in its own worktree beside 1A, 1B or 1C. | 2026-11-11 | Implemented and verified 2026-11-12 (build clean, 1203 tests, independent review done, one regression fixed). Commits `461a63e` and `55368fb`, merged into `review/app-review` as `8d8342d` (combined result: build clean, 1223 tests). ADR 0015 and `docs/requirements-traceability.md` (T-09) updated. Not launched in the app. |

Moved to part 2 on 2026-11-12 (user's decision). The findings stay approved; they are folded into the part 2 batches where they fit instead of running as standalone refactors:

| Finding | Was | Why it waits for part 2 |
|---|---|---|
| A-06 `WorkspaceContext` split | 1E | The grouping depends on what the Calendar, Settings and Filter view models need, which part 2 reads closely. |
| A-04 Board drag controller, shell page preloader | 1F | Part 2 decides whether Board, Calendar and Checklists share a drag component. |
| A-03(b) `TaskGrid` control | 1G | Part 2 decides which grid pieces become shared components. Riskiest item: focus and keyboard behaviour is covered only by manual testing. |

Part 2 reviews the code as batches 1A–1D leave it.

## Early performance notes

Observations made during parts 1–3 that belong to part 4.

- Idle working set is 597 MiB on the benchmark fixture, over the 500 MiB budget (part 0 baseline). Part 4 looks at what holds the memory: all pages kept alive, per-row view models for 10k+ records, projection snapshots.
- Dashboard recompute allocates 3.9 MB per run with Gen2 collections.

## Noticed in passing

Out of scope for this review; listed for the user and not acted on.

- `scripts/publish-portable.ps1` currently fails its notices gate: `THIRD-PARTY-NOTICES.txt` lists `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` but the payload now carries `10.0.15`. `tools/Generate-Notices.ps1` regenerates it. The baseline used the published folder directly, without the ZIP gate.
- Startup and restore path, found during batch 1D and its independent review. All existed before the refactor and were kept as they are, because fixing them changes behaviour:
  1. Closing the window while the dataset is still opening, or between the close and reopen of a Settings restore, can leave a dataset that nobody closes (`Application/Startup/StartupCoordinator.cs`, `ShutdownAsync` versus `OpenAsync`).
  2. Recovery-page restore: an I/O failure in validation or the raw safety copy is logged as a failed swap, and the staged copy under `data/temp` is not discarded.
  3. `FinishInterruptedRestoreAsync` calls `RecoverInterrupted` outside the guard, so an unexpected exception reaches the recovery page's command.
  4. If `DatasetSession.Create` throws, the opened dataset is not closed (pinned by a test).
  5. A Settings restore whose swap throws returns `RolledBack` with no notice, although the marker is kept and the app reopens into the interrupted-restore recovery.
  6. `RestoreService` has no fault tests per phase, which ADR 0015's Verification section asks for.
- Editors, found during batch 1B. Both existed before the refactor:
  1. If a save command throws instead of returning an error, `IsSaving` stays true and the editor cannot be closed (`Common/Dialogs/EditorViewModelBase.cs`, `SaveDraftAsync`). **Fixed in batch 1C** as part of ADR 0029.
  2. In the review editor a later non-conflict failure clears the conflict flag; the other three editors keep it.
- Found during batch 1C, not fixed:
  1. `ChecklistCommandQueue.RunAfterAsync` awaits the previous command's task, so after one checklist action throws, every later action on that card rethrows the same exception.
  2. Actions that predate batch 1C leave their busy flag (`IsAdding`) and the save status in-flight count set after a throw, so the status bar can keep showing "Saving…".
  3. The recovery page's four commands still end the app on an unexpected exception; no workspace is open there to show the error (ADR 0029).
