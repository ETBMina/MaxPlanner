# App review — implementation plan

Living document for the whole-app review (architecture and MVVM, consistency, UI/UX, performance). It is updated after every review part. Business functionality is out of scope.

## Rules

- Review is done with the `winui:winui-code-review` skill. Code is written by Opus through the `winui:winui-dev` agent. Parts 0–2A were reviewed by Fable; from batch 2B on (2026-11-13, user's decision) Fable is no longer used, and the main session and the independent reviews run on Opus.
- Before a batch is merged, its build is launched on fixture data so the user can test it.
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
| 2 | Consistency and shared components | Done 2026-11-12 | Approved as proposed 2026-11-12 | 2A–2E merged 2026-11-13, each tested by the user in the app |
| 3 | UI / UX | Done 2026-11-13 | All fourteen decided 2026-11-13 (four with changes, see the table) | Not started; each batch awaits approval |
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

### Part 2 — Consistency and shared components (reviewed 2026-11-12, awaiting decisions)

Verdict: the visual building blocks are already shared well (one theme dictionary with 404 keys, no colour literals outside it, shared `StateView`, `ExpandingSearchBox`, `FilterButton`, `TokenPicker`, `TagPill`, avatars). The duplication is in scaffolding: page and dialog shells, the save sequence in view models, and shared types that live in feature folders.

Method: file inventory, pattern counts and side-by-side diffs of the page views, the editor dialogs and the view models, on `review/app-review` after batches 1A–1D. The app was not launched.

| ID | Severity | Finding | Evidence | Proposed change | Effort | Risk and cost | Decision |
|---|---|---|---|---|---|---|---|
| C-01 | Medium | The page scaffold is repeated on six pages: the header block (title, subtitle, padding `32,24,32,12`), the `IPageView` members, and the size-changed guard that calls `ApplyBreakpoint`. The breakpoints 640 and 1008 are written as literals about 25 times in 12 files, while `PagePadding`, `BoardLayout` and `EditorDialogSizing` each define them again. | `Board/`, `Grid/`, `Calendar/`, `Checklists/`, `Dashboard/`, `Settings/` `*PageView.xaml(.cs)`; `Calendar/CalendarPageView.xaml.cs:220-253`; `Common/PagePadding.cs:16-22` | (a) One `Breakpoints` type with the two widths and a width-class helper. (b) A `PageHeader` control with title, subtitle and content slots, and a small page-view helper for the size-changed guard. | (a) S, (b) M | (a) Very low. (b) Low–Medium: header layout differs per page at narrow widths. Not on a hot path, so one extra control level costs nothing measurable. | |
| C-02 | High | The editor dialog shell is copied across the four editors (and partly Copy task and the field dialog): identical `OnPrimaryClick`, `OnClosing`, `OnSaveAccelerator`, `OnBackAccelerator` in code-behind; the title row with the More menu; the validation and error InfoBars; and the conflict-review and reload-confirm steps (71 lines of XAML that differ in 2 lines between Subtask card and CR, 10 against Task). The matching step logic (`ReviewConflict`, `ApplyConflictReview`, `BeginReload`, `ConfirmReload`, `StepBack`) is still repeated in three view models after batch 1B. | `SubtaskCards/SubtaskCardEditorDialog.xaml:265-335`, `ChangeRequests/CrEditorDialog.xaml:513-583`, `Tasks/TaskEditorDialog.xaml:832-902`; the four `*EditorDialog.xaml.cs` | A `ConflictReviewPanel` and an `EditorMessageBars` user control; extend `EditorDialogLayout.Attach` to wire the four shared handlers; move the conflict step state into `EditorViewModelBase`. | M–L | Medium. Dialogs only, so the extra user-control level has no hot-path cost. AutomationIds must stay as they are. | |
| C-03 | Low–Medium | The clearable date field (picker, clear button, helper text) is repeated six times in three editors, about 22 lines each. First day of week is forced to Sunday on the Dashboard and Calendar pickers (3 places) and left to the system setting in the editors and the month-view flyout. | `Tasks/TaskEditorDialog.xaml:457-520`, `ChangeRequests/CrEditorDialog.xaml:265-305`, `SubtaskCards/SubtaskCardEditorDialog.xaml:200-240`; `Dashboard/DashboardPageView.xaml:261,268`, `Calendar/CalendarPageView.xaml:418` | A `ClearableDateField` control; one first-day-of-week rule for every date picker. | S–M | Low. Which first day to use is a visible choice and needs the user's decision. | |
| C-04 | Medium | Shared types live in feature folders, so `Common` depends on `Tasks` and features reference each other sideways (12 such references): `Tasks/Pickers.cs` (the view model of `Common/Controls/TokenPicker`), `Tasks/FieldError` (used by 8 files), `Tasks.ConflictRowViewModel` (3 editors), `Grid.StatusOption` (used by Tasks and SubtaskCards), `PriorityOption` inside `TaskEditorViewModel.cs`, `SquadOption` inside `CrEditorViewModel.cs`. | `Common/Controls/TokenPicker.xaml.cs`, `Grid/GridRowViewModel.cs:19`, `Tasks/TaskEditorViewModel.cs:30`, `ChangeRequests/CrEditorViewModel.cs:31` | Move them to `Common` (pickers, fields, options), one type per file, and add an architecture test: `Common` must not reference a feature namespace. | S | Very low: moves and namespace updates only. | |
| C-05 | Medium | The "run one command" sequence (editing-blocked check, new operation ID, save status begin, execute, map the failure, report, announce, wait until displayed) is written 11 times. Three are near-identical private `RunAsync` helpers. Each handles a thrown exception differently, which is why busy flags and the "Saving…" indicator can stick (noticed in batch 1C). | `Tasks/TaskCommands.cs:206-240`, `Checklists/ChecklistItemViewModel.cs:196-220`, `Settings/SettingsPageViewModel.cs:593-617`, `Board/BoardPageViewModel.cs:442-480`, `Grid/GridPageViewModel.cs:209-235`, plus 6 more | One command runner on the workspace context that owns the sequence and the exception handling. This is where A-06 fits: the context exposes narrow interfaces (`ICommandRunner`, `IAnnouncer`, `IDialogService`) and the use cases are grouped by feature. | M | Medium: every write path goes through it, but each has tests. Fixes the stuck-indicator bug in one place, which is a behaviour change to approve. | |
| C-06 | Low | The three Data wizards copy the same `StepTemplate` and `StepListStyle` verbatim. | `Settings/DataPage/ExportWizardView.xaml`, `ImportWizardView.xaml`, `RestoreWizardView.xaml` (resources) | Shared resources or a small `WizardHost` control for the step list and footer. | S | Low. | |
| C-07 | Low–Medium | Dates are formatted at about 40 call sites with five formats (`d`, `D`, `MMM d`, `MMM d, yyyy`, `MMM yyyy`) and no shared formatter. `MMM d` fixes the month-day order regardless of the user's culture. | View models and controls across Board, Calendar, Checklists, Dashboard, Grid | One `DateText` helper with named formats (short, long, compact). Which format each surface shows is decided in part 3. | S | Low. Replacing `MMM d` with a culture-aware pattern changes text in some regions. | |
| C-08 | Low | Context menus built in code repeat the same local `Item(key, id, action, enabled)` helper (3 copies) and 18 `new MenuFlyout…` sites in 5 files. | `Grid/GridPageView.xaml.cs:380,1337`, `Settings/SettingsPageView.xaml.cs:307`, Board and Calendar builders | One small menu-builder helper. | S | Very low. | |
| C-09 | Low | Small XAML repeats: the accent "New …" button with icon, text and Ctrl+N (4 pages); a flat `ListViewItem` style (3+ places); 330 `FontIcon` size literals (14: 177, 12: 125, 16: 28). | Page views and dialogs | Named styles and icon-size resources in `AppResources.xaml`. | S | Very low; resource lookups only. | |
| C-10 | Low | In-place rename is implemented twice, each with its own key handling. | `Checklists/ChecklistsPageView.xaml.cs`, `Settings/SettingsPageView.xaml.cs`, `Settings/SettingsRows.cs` | A shared inline-rename helper. Optional. | S–M | Low–Medium: focus handling is fragile. | |
| C-11 | Medium (UX) | Two filter experiences: the Dashboard uses inline combo boxes with From/To date pickers; Board, Grid and Calendar use the Filters button with chips and a custom range calendar. | `Dashboard/DashboardPageView.xaml:36-80`, `Common/Filters/FilterButton.xaml` | Decide in part 3 with mock-ups; no code proposal here. | — | — | |

**Decision (user, 2026-11-12): approved as proposed.** C-01 to C-10 are approved for batches 2A–2E; C-11 and the visible choices from C-03 and C-07 go to part 3; A-04 is dropped; A-03(b) is narrowed as described below; A-06 is done inside C-05.

Where the deferred part 1 findings land:

- **A-06** (`WorkspaceContext`): becomes part of C-05.
- **A-04** (Board drag controller, shell page preloader): no shared component comes out of it. Only the Board has auto-scroll and an insertion line; Calendar, Settings, the task editor checklist and the Grid header use simpler drops or the built-in `ListView` reorder. Recommendation: lowest priority, Board-only, or drop it.
- **A-03(b)** (`TaskGrid` control): the Grid already has its own controls (`Grid/GridControls.cs`, 14 types in 1,009 lines) and nothing else reuses a grid. Recommendation: narrow it to (1) one type per file for `GridControls.cs` and (2) moving the header menu, column chooser, row menu and editor flyout builders out of `GridPageView.xaml.cs` into view-side classes. No new control.

Duplication to keep:

- `EditorChecklistItemViewModel` (a draft row in the task editor) and `ChecklistItemViewModel` (a live row that commits each change) look alike but follow different save rules.
- Board card, Calendar record and Dashboard row templates: each is on a list hot path with different content. A shared card control would add template depth per item for little gain.
- The two filter scopes already share `FilterScopeViewModel`.

Not reviewed in depth: `Settings/DataPage/ImportWizardViewModel.cs` (918 lines) and the bodies of the Calendar and Settings view models were surveyed for structure and patterns, not read line by line. Visual consistency on screen belongs to part 3.

Batches for part 2. The findings are approved; each batch still needs the user's go-ahead before it is implemented:

| Batch | Findings | Why together |
|---|---|---|
| 2A | C-04, C-06, C-08, C-09, C-01(a), C-07 (helper only, same output) | Moves and small helpers; no behaviour or visual change. Approved 2026-11-12. Implemented and verified 2026-11-12 (build clean, 1295 tests): seven commits `057cdc7`..`55b04d3`, merged into `review/app-review` as `f4387ee` (same tree as the tested commit) after the user tested the branch build on fixture data on 2026-11-13. Reviewed by the main session only (moves and mechanical replacements). Skipped inside C-09: the flat list-item style, because no two sites were identical. The New button is shared by the user's decision: the Grid's icon goes from 16 to 14 to match the other pages. |
| 2B | C-05 with A-06 | One change to how view models run commands. Approved 2026-11-13. Implemented and verified 2026-11-13 (build clean, 1356 tests, independent review on Opus found no regression; its fixes and the user's three decisions applied). Seven commits `39779c3`..`c133790` on `refs/heads/worktree-agent-adeb3e7f08af76001`; merged into `review/app-review` as `ab8a437` (same tree as the tested commit) after the user tested the branch build on 2026-11-13. |
| 2C | C-02, C-03 | Editor dialog shell and its fields. Approved 2026-11-13. Implemented and verified 2026-11-13 (build clean, 1374 tests, independent review on Opus found no regression; its follow-up pins the x:Uid resource lookups). Eight commits `1d38c59`..`1eec611` on `refs/heads/worktree-agent-a9788fd047eb64d50`. The user's first test found both editor message bars open and empty (x:Bind turns a null string into ""); fixed in `1eec611` (1380 tests); merged into `review/app-review` as `893305c` (same tree as the tested commit) after the user tested it on 2026-11-13. |
| 2D | C-01(b), C-10 | Page header control; inline rename. Approved 2026-11-13. Implemented and verified 2026-11-13 (build clean, 1420 tests, independent review on Opus found no regression and no fixes needed). Four commits `310e697`..`8731079`, merged into `review/app-review` as `7328dde` (same tree as the verified commit) on the user's instruction on 2026-11-13. Inline rename: only the key handling and first focus are shared; the view-model rules differ in five ways and stay per page. |
| 2E | A-03(b) narrowed | Grid code-behind. Approved 2026-11-13. Implemented and verified 2026-11-13 (build clean, 1439 tests, independent review on Opus found no regression). Three commits `1db804e`..`fe637f4`, merged into `review/app-review` as `0d50f2e` (same tree as the tested commit) after the user tested it on 2026-11-13. |
| Part 3 | C-11, date format choices from C-07, first day of week from C-03 | Visible choices, decided with mock-ups |

### Part 3 — UI / UX (reviewed 2026-11-13, decided 2026-11-13)

Verdict: the app already looks like a Windows 11 app: Mica, the navigation pane, theme brushes everywhere, a High Contrast dictionary, accent buttons used once per page, InfoBars for status. The findings are about focus on open, three editors that each introduce themselves differently, empty values styled as real values, a few loud or cramped spots, and two table styles.

Method: the current build (`0d50f2e`) launched on the scale-0.03 fixture; all six pages, the Task, CR and Subtask card editors on existing records, the Filters flyout and a sub-menu, the Grid column chooser, Calendar Week and Records, and light theme. Screenshots in `docs/review/part3/screens/`. Not captured: the review editor on an existing record, the Data page wizards, context menus (the capture tool did not show the popup), narrow windows after part 2 (the part 0 set covers narrow before it).

| ID | Severity | Finding | Evidence | Proposed change | Effort | Risk | Decision |
|---|---|---|---|---|---|---|---|
| U-01 | High | Editors do not reliably land focus where they mean to. WinUI places its own first focus after `Opened`, which overrides the editor: the task editor's tag field takes focus and flickers (seen by the user), the Subtask card editor ends on its ⋯ button instead of Subtitle, and a task with a read-only title opens with a focus ring on the Close (×) button. The CR editor happens to keep its focus. | `editor-subtask-existing.png`, `editor-task-existing.png`; `TaskEditorDialog.OnOpened`, `SubtaskCardEditorDialog.xaml.cs:23`, `CrEditorDialog.xaml.cs:27`, `ReviewEditorDialog.xaml.cs:18` | One initial-focus rule in `EditorDialogShell`: each editor names its first field, the shell sets it before the dialog's own focus pass (and never on a chrome button). | S–M | Low || Approved (2026-11-13), then changed by the user while testing 3A: editors open with no visible focus (focus on the content, no caret or highlight); the first Tab goes to the first field. |
| U-02 | Medium | The three editors introduce themselves three ways. Task: a tag search strip on top, the CR title as heading, a facts bar (CR number, Task number, Squad, Owner). CR: "Change request" heading, Created, ⋯. Subtask card: "Subtask card" heading, a parent caption, ⋯, then a "Parent task" card with five facts in a different layout. | `editor-task-existing.png`, `editor-cr-existing.png`, `editor-subtask-existing.png` | One header pattern: record kind as a caption, the record's title as the heading, ⋯ at the right, then one read-only facts bar in the same style for every editor. Tags move below the heading on the task editor. Mock-up 1. | M | Medium (XAML in 3 editors, AutomationIds kept) || Approved with changes (2026-11-13): build on the task editor, the most recent one; keep tags at the top but show the tag search only on demand, not always; put the Created date in the info (facts) box. |
| U-03 | Medium | Empty values look like values: "No task number" in the bold value style, an orange person icon on an empty Designer ("Choose a person"), and the date pickers' default lower-case "select a date". | `editor-task-existing.png`, `editor-subtask-existing.png` | Empty facts as a secondary "None" (or an em dash); a neutral outline person icon for an empty picker; the date placeholder from `.resw` ("Pick a date"). | S | Low || Approved as proposed (2026-11-13) |
| U-04 | Medium | Overdue CR bars on the Calendar carry a warning glyph, the word "Overdue" and a full caution-colour outline. In dark the outline is the loudest thing on the page; in light the yellow outline on a pale fill is faint. | `page-calendar.png`, `light-calendar.png`; `Themes/AppResources.xaml:894-898` | Keep the glyph and text; replace the full outline with a 3 px caution accent on the bar's leading edge, as completed bars use a neutral one. Mock-up 2. | S | Low || Approved as proposed (2026-11-13) |
| U-05 | Medium | Dashboard cards: the big numbers do not line up across cards (a longer caption pushes "29" up), the Reviews card sits alone on a second row with its own layout and says "30 done this week" twice, and a full-width paragraph explains the filters. | `page-dashboard.png` | Fixed card anatomy: label at the top, number on one baseline, caption at the bottom; Reviews uses the same anatomy (number plus one caption line); the filter explanation moves into an info button tooltip. Mock-up 3. | S–M | Low || Approved as proposed (2026-11-13) |
| U-06 | Low–Medium | Board cards spend a line on a lone checklist glyph when the card has no ID chips, and a lone avatar on its own bottom line. | `page-board.png` | Checklist glyph with its count ("2/5") on the service line, avatar at the end of the same line; cards get shorter and scan better. Mock-up 4. | S | Design approved by the user (2026-11-13) in a forked design session: see "U-06 design" below. |
| U-07 | Low–Medium | Two table styles: the Grid (row dividers, 44 px rows, header cells) and Calendar Records (no dividers, bold first column, different header). | `page-grid.png`, `calendar-records.png` | Records uses the Grid's header and row styles. | S–M | Low || Approved as proposed (2026-11-13) |
| U-08 | Low | Grid Kind column shows "Subtask c…" truncated with the row ⋯ inside the same cell; every Subtask card title has a lock icon. | `page-grid.png` | Kind as an icon with its name as tooltip and accessible name, ⋯ in its own narrow column; the lock only on hover or in the tooltip. | S–M | Medium (Grid cell layout) || Approved as proposed (2026-11-13) |
| U-09 | Low | Checklists: on two-line review rows the checkbox is centred while the text is top-aligned; "Open task" on task cards looks like plain text. | `page-checklists.png` | Top-align the checkbox with the first line; "Open task" as a hyperlink button. | S | Low || Approved as proposed (2026-11-13) |
| U-10 | Low | The status bar's left text repeats the page subtitle on Board and Checklists ("300 cards in 8 buckets" twice). | `page-board.png`, `page-checklists.png` | Status bar left side shows only transient status (filter result counts when filtered); subtitles keep the summary. | S | Low || Approved as proposed (2026-11-13) |
| U-11 | Medium (from C-11) | Two filter experiences: the Dashboard filters inline with combo boxes and From/To pickers; Board, Grid and Calendar use the Filters button with chips. | `page-dashboard.png`, `flyout-filters-status.png` | Dashboard uses the same Filters button and chips (its four filters become rows in the same flyout). | M | Medium (Dashboard filter wiring) || Approved as an experiment (2026-11-13). Tried in 3E and rejected (2026-11-17): the Dashboard keeps its inline filters. |
| U-12 | Low (from C-07, C-03) | Dates mix "Created 11/13/2026", "Started 11/13/2026" and "Nov 2026"; first day of the week is forced to Sunday on Dashboard and Calendar pickers but follows the system in the editors. | screenshots; `Dashboard/DashboardPageView.xaml:261,268`, `Calendar/CalendarPageView.xaml:418` | Record dates as "Nov 13" (with the year only when not this year) through `DateText`; one first-day rule for every picker, taken from the workweek setting. | S | Low || Approved with change (2026-11-13): show the year too, for example "Nov 13, 2026". |
| U-13 | Low (held from 2D) | The Grid toolbar never moves Show archived, Columns and Export into "…"; at the narrowest windows it clips. | `Grid/GridPageView.xaml` | Give the CommandBar a constrained width so dynamic overflow works. | S | Low || Approved as proposed (2026-11-13) |
| U-14 | Medium (held from 2E, a bug) | A Grid flyout editor opened by F2, double-tap or a page switch never saves on an outside click (the double-click guard overflows). | `Grid/GridEditorFlyout.cs` | Fix the guard's "not opened by tap" value. | S | Low || Approved as proposed (2026-11-13) |

#### U-06 design (approved 2026-11-13)

The Subtask card uses the task card's base layout. Designed with the user in a forked session; the variants shown were "parent line above the title" (A) and "parent title as the heading, subtitle in the footer" (B); the user chose B, without dates.

Rows, top to bottom, the same for both kinds (`Board/BoardPageView.xaml`, `CardTemplate`):

| Row | Task card (unchanged) | Subtask card (new) |
|---|---|---|
| 1. Tag strip with ⋯ | Task's tags | Parent's tags (as today) |
| 2. ID row | Priority glyph, CR chip, Task chip; ⋯ at the end when there is no strip | CR chip and Task chip only; no priority glyph and no Subtask list glyph (`Board_SubtaskGlyph` removed). ⋯ at the end when there is no strip |
| 3. Title | Task title | The parent task's title, in the same title style. It is the link to the parent (replaces the "Subtask of …" line) |
| 4. Chip row | Service chip, Squad chip | Service chip and Squad chip (the Squad chip is new on Subtask cards) |
| 5. Footer, below a divider | Checklist progress, avatars on the right | "↳ " + the card's own subtitle on one line (trimmed with an ellipsis), avatar on the right |

What differs per kind, and nothing else: row 2's priority glyph (task only), row 3's text and link target, and row 5's left side. The "Subtask of …" line and the dates line (`DatesText`, "Started …") are no longer shown on the card; the dates stay in the Subtask card editor.

Rules for the implementation:
- No element per card is added: the Subtask glyph, the parent-link line and the separate dates footer go away; the Subtask card reuses the task card's footer row with different content.
- When a Subtask card has neither tags nor ID chips, ⋯ sits at the end of the title row, never on a line of its own.
- Without colour, the kind is still clear: the ↳ subtitle in the footer, and the card's accessible name keeps "Subtask card …" (`Board_SubtaskCardName`-style name including the subtitle and the parent).
- AutomationIds that must stay: `Board_SubtaskCard_{id}` (card), `Board_CardTagStrip_`, `Board_CardMenuButtonStrip_`, `Board_CardMenuButton_`, `Board_CardCrChip_`, `Board_CardTaskChip_`, and `Board_CardParentLink_` (now on the title link). `Board_CardPriority_` stays task-only. Add `Board_CardSubtitle_{id}` for the footer subtitle text.
- The tooltip on the title shows the full parent title; the footer subtitle shows the full subtitle as its tooltip.
- `design.md` §4.2 (Board card anatomy) is updated in the same batch.

Not proposed: the CR editor's pre-checked "Make Person the default CR Owner" appears only when no default owner exists; that is product rule B-11, out of scope.

## Implementation batches

Part 1. All nine findings are approved; each batch still needs the user's go-ahead before it is implemented. Batches run in this order, one at a time on `review/app-review`, because 1B, 1C, 1E, 1F and 1G touch the same Presentation files.

| Batch | Findings | Why here | Approved | Status |
|---|---|---|---|---|
| 1A | A-03(a), A-07, A-08, A-09 | Small, low risk, no behaviour change. A-08's test guards the later batches. | 2026-11-11 | Implemented and verified 2026-11-12 (build clean, 1176 tests). Committed as `11c61c1`; awaiting the user's manual check. Also removed the one violation A-08 found: `ShellViewModel` used `InfoBarSeverity`. |
| 1B | A-01 | One refactor across the four editors | 2026-11-12 | Implemented and verified 2026-11-12 (build clean, 1257 tests, independent review found no regression). Commits `286ab98` and `b0c86bc`, merged into `review/app-review` as `1d3b78c` (same tree as the tested commit). |
| 1C | A-02 | After 1B, so commands are added once in the shared editor base | 2026-11-12 | Implemented and verified 2026-11-12 (build clean, 1290 tests). Five commits `d163f08`..`9aa8257`, merged into `review/app-review` as `965f24e` (same tree as the tested commit). Independent review covered the first three commits only; the failure handling (ADR 0029, commits `f764ce7`, `9aa8257`) was reviewed by the main session only. |
| 1D | A-05 | Desktop/Infrastructure only, tests first. Touches no Presentation file, so it may run in its own worktree beside 1A, 1B or 1C. | 2026-11-11 | Implemented and verified 2026-11-12 (build clean, 1203 tests, independent review done, one regression fixed). Commits `461a63e` and `55368fb`, merged into `review/app-review` as `8d8342d` (combined result: build clean, 1223 tests). ADR 0015 and `docs/requirements-traceability.md` (T-09) updated. Not launched in the app. |

Moved to part 2 on 2026-11-12 (user's decision). The findings stay approved; they are folded into the part 2 batches where they fit instead of running as standalone refactors:

| Finding | Was | Why it waits for part 2 |
|---|---|---|
| A-06 `WorkspaceContext` split | 1E | The grouping depends on what the Calendar, Settings and Filter view models need, which part 2 reads closely. |
| A-04 Board drag controller, shell page preloader | 1F | Part 2 decides whether Board, Calendar and Checklists share a drag component. |
| A-03(b) `TaskGrid` control | 1G | Part 2 decides which grid pieces become shared components. Riskiest item: focus and keyboard behaviour is covered only by manual testing. |

Part 2 reviews the code as batches 1A–1D leave it.

Part 3. Each batch still needs the user's go-ahead. They run one at a time in this order; 3E runs last on its own branch and is merged only if the user keeps it.

| Batch | Findings | Why together | Approved | Status |
|---|---|---|---|---|
| 3A | U-01, U-03, U-04, U-12, U-13, U-14 | Small, contained fixes: editor first focus, empty-value styles, overdue bars, dates with the year and first day of week, Grid toolbar overflow, Grid flyout light-dismiss bug | 2026-11-13 | Implemented and verified 2026-11-13 (build clean, 1492 tests, independent review on Opus: no errors, four fixes applied). Nine commits `1ee1554`..`6044e22`, merged into `review/app-review` as `4c7e5d5` (same tree as the tested commit) after the user tested it on 2026-11-13; `design.md` updated in the main checkout. |
| 3B | U-02 | The editor header and facts box, built on the task editor (tags on demand, Created in the facts box) | 2026-11-15 | Implemented and verified 2026-11-15 (build clean, 1535 tests, independent review on Opus: no errors, five fixes applied; Board crash repro passes). Eight commits `88f5887`..`4e19e91` on `refs/heads/worktree-agent-a1a549b6c64273642` (the last three are the user's feedback: Subtask editor per U-06, Copied hint, one-field tag line; then two tag-field fixes found by the user and by an on-screen check: the input was never laid out on reveal, and Esc/pick reopened the list; 1564 tests); `design.md` updated in the main checkout; merged into `review/app-review` as `39eb625` (same tree as the tested commit) after the user tested it on 2026-11-16. |
| 3C | U-06, U-05, U-09, U-10 | Board card (U-06 design), Dashboard cards, Checklists alignment and link, status bar text | 2026-11-16 | Implemented and verified 2026-11-16 (build clean, 1592 tests, independent review on Opus: no errors; its fixes applied; checked on screen in light and dark; Board scroll repro passes). Six commits `b50ec4e`..`17c3cb8` on `refs/heads/worktree-agent-a8a18e6cfebc3dc11`; a follow-up (`45f93d8`) hid every card title and was caught by the screen check, fixed in `17c3cb8`; `design.md` updated in the main checkout; merged into `review/app-review` after the user tested it on 2026-11-16. |
| 3D | U-07, U-08 | Tables: Calendar Records adopts the Grid styles; Grid Kind column and lock icon | 2026-11-16 | Merged 95240de after user test |
| 3E | U-11 | Experiment on its own branch: Dashboard uses the Filters button; the user tests and decides | 2026-11-16 | Rejected by the user after testing (2026-11-17); not merged. Branch worktree-agent-a860b89961ebdc9f0 (a4379b9) kept for reference. Its review's shared-filter fixes (empty chips bar gap on Grid/Board/Calendar, open menu detached on dispose) merged separately as 6d2e632 |

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
  1. `ChecklistCommandQueue.RunAfterAsync` awaits the previous command's task, so after one checklist action throws, every later action on that card rethrows the same exception. **Fixed in batch 2B.**
  2. Actions that predate batch 1C leave their busy flag (`IsAdding`) and the save status in-flight count set after a throw, so the status bar can keep showing "Saving…". **Fixed in batch 2B** (command runner).
- Found during batch 2B, not fixed:
  1. Pop out on the Checklists page sets its inline error from `SaveStatus.FailureMessage`, so a blocked or validation-refused pop-out can show a stale message from an earlier failure, or nothing (`Checklists/ChecklistGroupViewModel.cs`, around line 135).
  2. If editing is blocked when a Board card is dropped, or the reorder is refused, the card keeps `IsMoving` and stays dimmed.
  4. Seen by the user while testing 2C (pre-existing, not changed by 2C): opening the task editor, the tag field takes focus first and looks selected, flickering for a few seconds, before focus moves to the title. The dialog focuses its first control (the tag field), and `TaskEditorDialog.OnOpened` moves focus to the title only at low dispatcher priority, which waits while other work (page preloading, the Debug build) runs. **User's decision (2026-11-13): fix in part 3** (set the initial focus directly instead of moving it after a delay).
  3. The task editor's Add Subtask card sends its command even while editing is blocked (T-07). **Fixed in batch 2B** (user approved 2026-11-13).
  3. The recovery page's four commands still end the app on an unexpected exception; no workspace is open there to show the error (ADR 0029).
- Found during batch 2D, not fixed:
  1. The Grid header's CommandBar sits in an Auto column, so it always gets unlimited width and its dynamic overflow never moves Show archived, Columns or Export into "…"; at the narrowest windows the actions row is clipped instead (`Grid/GridPageView.xaml`). Pre-existing; a candidate for part 3.
  2. Settings rename: Esc while a rename is saving moves focus to the row's ⋯ button although the box stays open.
  3. `Settings/DataPage/DataPageView` keeps its own header markup and has no width-0 guard; a candidate for the shared `PageHeader`.
- Found during batch 2E (pre-existing, confirmed in the code before 2E): a Grid flyout editor opened by F2, double-tap, UI Automation or the reopen after a page switch never commits on light dismiss. The double-click guard uses `long.MinValue` as "not opened by tap", so `now - long.MinValue` overflows negative and every outside click counts as the second click of a double-click (`Grid/GridEditorFlyout.cs`, formerly `GridPageView.xaml.cs` ~901). One-line fix proposed. **User's decision (2026-11-13): fix in part 3.**
- Found while testing batch 3A (user, 2026-11-13): scrolling the Board's In Progress bucket with the mouse wheel in a maximized window crashed with `LayoutCycleException`. Reproduced by script; caused by 3A's longer card dates ("Started Nov 13, 2026"); the build before 3A and 3A with that one line reverted do not crash. The Board card layout loops when the dates text changes length; fixed in 3A commit `95e91bd`: the avatar row used a virtualizing `StackLayout`, so its width followed the card's viewport while the bucket scrolled and the wrapping dates text beside it changed height; it now uses a non-virtualizing `CardPeopleLayout` and the dates stay on one line (ellipsis + tooltip). The scripted repro no longer crashes.
- User feedback while testing batch 3B (2026-11-15), sent back to the 3B agent: (1) the Subtask card editor must follow the U-06 card design: the parent task's title as the heading (the Open parent link), the card's own subtitle under it marked ↳ as the editable field, the parent's facts and Created in the info box; (2) copy buttons for CR and task numbers show a short "Copied" hint; (3) the tag field has no visible "Add tag" button: the whole tag line looks and acts as one clickable field, and the "Add tag" placeholder shows only once the input is revealed by a click or key.
- Batch 3C decision (user, 2026-11-16): on a Subtask card the parent-title link is kept even though a drag cannot start from it; the card is dragged by its chips, footer or edge.
