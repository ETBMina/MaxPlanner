# ADR 0004: Grid control selection

- **Status:** Accepted (2026-10-22). Decided by the Phase 0 spike 3 score sheet.
- **Requirements:** T-08 (Grid selection gate), B-07

## Context
The Community Toolkit DataGrid is deprecated. The owner limited candidates to open-source (OSI-licensed) controls.

## Options
1. **WinUI.TableView 1.5.1 (MIT).** Maintained, with a net10 asset. Built against WinUI 1.8; NuGet unified it to WinUI 2.3.9 without warnings.
2. **Custom grid:** a virtualized `ListView` with a header row, a column model, cell editors and custom UIA Grid/Table automation peers.
3. Commercial grids were excluded by the owner.

## Score sheet
Every row had to pass for option 1. A failure on rows 2, 3, 5 or 6 selects option 2.

| # | Criterion | TableView 1.5.1 | Custom ListView grid (spike fallback) |
|---|---|---|---|
| 1 | Builds, publishes and runs unpackaged on WinAppSDK 2.5.1 components | PASS | PASS |
| 2 | F2 / double-click edit; Enter commits; Esc cancels; Tab commits then moves only on success | PARTIAL: ComboBox editors swallow Enter; the library's Enter on an unchanged cell moves down and starts editing; a UIA select during an edit orphans the editor | PASS with public APIs |
| 3 | Editor stays open during an async commit and reopens with the error on failure | PARTIAL: keeping it open works (cancel `CellEditEnding`), but closing it after success needs **internal** APIs via `[UnsafeAccessor]`; there is no public commit/cancel API | PASS with public APIs |
| 4 | Template editors bind to row-ViewModel drafts | PASS | PASS |
| 5 | 10,000 rows scroll without memory growth | **FAIL:** PropertyChanged subscribers 10,871 → 32,581 and private memory 244 → 901 MB over 5 scroll cycles, not released by GC; average frame gap 54–82 ms | PASS: about 165 MB stable, about 20 subscribers, 11.8 ms average frame gap (8 columns) |
| 6 | UI Automation: Grid/Table patterns, cell names, AutomationIds | PARTIAL: Grid/Table/GridItem patterns work, but column headers are missing from UIA and header/body horizontal scroll desyncs | PARTIAL: List/ListItem only; custom peers needed |
| 7 | Column layout observable and restorable | PASS | Built with the custom grid |
| 8 | Light/Dark/High Contrast | PARTIAL: Light/Dark verified; HC dictionaries present, live HC not checked | Uses theme resources |
| 9 | `x:Bind` in cell templates | PASS, but this is the configuration that leaks in row 5 | PASS |

## Decision
**Option 2: a custom virtualized grid** (`ListView` rows plus a synchronized header) in Presentation.

The deciding failures:
- unbounded memory growth with the `x:Bind` template columns that rows 4 and 9 require;
- async commit possible only through internal APIs that a package update can silently break;
- column headers missing from UI Automation, which blocks keyboard header sort/resize and screen-reader table navigation (design §7.4, §8).

## Consequences
- **Work to build:**
  - Column model: key, header, width, order, visibility, sort, editor kind, read-only predicate.
  - Header row with resize/reorder/sort, mouse and keyboard.
  - Horizontal header/body scroll sync.
  - Cell focus and editing state machine (F2/Enter/Esc/Tab/Ctrl+Enter, tunnelling key handling for ComboBox editors).
  - Layout persistence.
  - Custom `AutomationPeer`s exposing Grid/Table/GridItem/TableItem and column headers.
- TableView's MIT-licensed automation-peer design may be used as a reference. Any copied code keeps its license header and is listed in THIRD-PARTY-NOTICES.
- `WinUI.TableView` stays pinned only for `spikes/GridGate`. No `src` project references it.
- Fixing these upstream (public commit/cancel edit or deferral, template-column binding leak, UIA column headers, header scroll sync) could reopen this decision in a later release, but would need a new ADR.

## Verification
- **Evidence:** `docs/evidence/phase-0/grid-gate/` (`00-index.txt` maps files to criteria), produced by the `winui:winui-dev` agent with the `winui-dev-workflow`, `winui-design` and `winui-ui-testing` skills.
- **Spike source:** `spikes/GridGate/` (`GridGate.exe --fallback` runs the custom grid prototype).
- **Still needs manual checks:** Ctrl+Enter, header pointer gestures, live High Contrast, Narrator, clean-VM run.
- **Limitations:** runs were on the developer machine (Windows Sandbox failed with E_ACCESSDENIED). A TableView variant with `{Binding}` instead of `x:Bind` templates was not tried. That would not fix rows 3 or 6.
