# Phase 2 evidence: vertical slice (2026-10-22)

Machine: Windows 11 Pro 10.0.26200, x64, 150% scale (2560x1600), .NET SDK 10.0.401, WinApp CLI 0.7.1.
Data: disposable copies of the Debug build under `%TEMP%\MaxPlanner Tëst UI\…` with the fixture from
`tools/MaxPlanner.FixtureGenerator` (2 CRs, 5 tasks, 3 Subtask cards, 6 checklist items). No user data.

## How the app was driven
`Run-SliceEvidence.ps1` (uses `Start-SliceApp.ps1`) drives only the app's own process through UI Automation
(`winapp ui` search, get-value, wait-for, set-value, invoke, focus) and resizes only the app's own window with
`SetWindowPos`. No keyboard, mouse, touch or pen input. Cell edits use the Grid cells' Value pattern, which
runs the same commit path as Enter; leaving a cell (UIA focus elsewhere) runs the same path as Tab/pointer.
The Debug-only `MAXPLANNER_DEBUG_HOOKS=1` triggers (`DebugCommitHooks`, not compiled into Release) held one
commit (saving state, and to make a commit genuinely stale) or returned the typed DiskFull failure for one.
Results: `slice-results.json` (16/16 PASS), app log: `slice-app.log`.

| Check | Result |
|---|---|
| Grid rows (tasks + Subtask cards), linked title read-only with "Inherited from CR-1067" | PASS |
| Title edit commits; status bar "Saved" | PASS |
| Status edit → In Progress; StartDate set (B-04) | PASS |
| Board: card in In Progress with the new title, gone from Backlog, no reload | PASS |
| Checklists: group header shows the new title | PASS |
| Add an item: blank → "Item text is required."; valid item added | PASS |
| Checking an item updates the group and the Board card ("Checklist 1/2", AC-06) | PASS |
| Disk-full commit: editor and draft kept, cell error, App InfoBar, Details link, other pages unchanged; retry saves | PASS |
| Saving state: "Saving…" with the editor open, then closed after the committed value arrived | PASS |
| Genuine stale revision (Checklists commit while the Grid commit was held): conflict text, "Saved now", "Your change", committed title unchanged; Use mine saves | PASS |
| 100 navigation cycles: 19 live store subscriptions on all 300 navigations; working set 245 → 252 MiB | PASS (numbers only) |
| Genuine relaunch on the same `data/`: edited title, status and second title shown | PASS |

## Screens and window sizing (restored-window rerun)
- **Black captures were a capture artifact, not rendering.** The first runs used a window covering the whole
  monitor, which Windows presents without composition (independent flip), so GDI/BitBlt captures return black.
  The owner confirmed the app renders normally. With a restored window smaller than the work area, captures
  are correct. Screens: `screens/` (`screens-notes.txt`, `Capture-SliceScreens.ps1`): Dark pages and Grid
  states, live updates, all three pages at 500/640/1008/1600 epx, Light at 1200 epx.
- **Display:** 2560x1600 at 150% (1707x995 epx work area). The earlier resize helper was not DPI-aware.
- **`PreferredMinimumWidth/Height` are physical pixels:** a 300x300 px request is held at 750x750 px
  (500 epx × 1.5), matching design §2.1.
- **Breakpoints follow resizes:** pane width 0 at 500 and 640 epx (LeftMinimal), 48 at 1008 (LeftCompact),
  320 at 1600 (expanded). At exactly 640 and 1008 outer-window width the mode is one step lower, because the
  invisible resize borders make the content narrower (T-08.1 outer-versus-visible width question).
- **Fixed from the screen review:** Grid header labels offset from their columns; Board first column touching
  the page edge; Checklists `UniformGridLayout` forcing equal group heights (now one virtualizing column,
  max 880 epx; Phase 8 needs a variable-height multi-column layout, design §4.5).
- **Found, not fixed:** caption buttons nearly invisible with the app in Light while Windows is Dark (also in
  the Phase 1 evidence); Board and Grid nav glyphs look almost identical (design §6.4 "to verify").
- UI validation beyond this point is deferred until all phases are developed (owner decision).

## Not exercised
- Keyboard paths (F2/Enter/Esc/Tab, typing to edit, arrows, Ctrl+1…5), Narrator and High Contrast were not
  exercised on the real app; their logic is covered by code review and the ViewModel tests only.
