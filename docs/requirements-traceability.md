# Requirements traceability

Requirement ID → where it is implemented → how Phase 19 verifies it → status. Product rules are in `Business Requirements.md`; this table only points at code and checks. Paths are relative to `src/` unless they start with `tests/`, `tools/`, `scripts/` or `docs/`.

**Status values:** *Built* = code complete, build clean, not yet verified by the Phase 19 run; *Partial* = built with a recorded open item; *Verified* = covered by tests or evidence that already ran (Phases 0–4); *Open* = not built.

Test layers: **D** Domain tests, **A** Application tests (in-memory fake and real SQLite), **I** Infrastructure tests on real files, **P** Presentation ViewModel tests, **UI** `winapp ui` batches + screenshots, **M** manual check by the owner, **G** release-gate scripts (`scripts/verify-portable.ps1`, `scripts/run-performance.ps1`).

## Technical requirements
| ID | Implementation | Phase 19 verification | Status |
|---|---|---|---|
| T-01 Authority | `Business Requirements.md`, `design.md`, `docs/adr/` | Review | Built |
| T-02 Platform and pins | `global.json`, `Directory.Packages.props`, lock files, `eng/WinUI.Common.props` (ADR 0002, 0019) | `build.ps1 -Locked`; G: second publish leaves lock files unchanged | Verified (build) |
| T-03 Portable deployment | `MaxPlanner.Desktop.csproj`, `Program.cs` (probe, lock, exit codes), `Infrastructure/Storage/*`, `scripts/publish-portable.ps1`, `THIRD-PARTY-NOTICES.txt`, `eng/portable/README.txt` | G: verify-portable all gates on the dev machine, Sandbox, and the clean offline VMs (24H2, 25H2) | Partial (gates written, clean VM pending) |
| T-04 Clean Architecture | Five projects; `tests/MaxPlanner.Architecture.Tests` | Architecture tests | Verified |
| T-05 Data model | `Domain/*`, `Infrastructure/Persistence/Schema/M0001_initial.sql` | D, I | Built |
| T-06 Lifecycle, transactions | `SqliteMutationCoordinator`, `SqliteReadSnapshotSource`, `OperationLedger`, `App.xaml.cs` shutdown | I (fault injection, ledger replay), Desktop smoke tests | Verified (Phase 1 slices) |
| T-07 Reactive state | `Presentation/Common/Store/WorkspaceProjectionStore.cs`, `ProjectionView`, `CoalescingObservable` (Dashboard) | P store tests; AC-17 navigation cycles (G run-performance) | Verified (store); Built (Dashboard coalescing) |
| T-08 Native UI | `Shell/*`, `Themes/AppResources.xaml`, page views, `ThemeService`, `MainWindow` backdrop | UI screenshots L/M/S × Light/Dark; M: High Contrast, Narrator, touch, DPI, Snap, transparency off, RDP | Built |
| T-08.1 Responsive | `ApplyBreakpoint` in every page view, `CompactSearch`, `FilterChipsBar.IsCompact` | UI at 500/639/640/1007/1008/1600 epx | Built |
| T-09 Settings, backup, recovery | `Infrastructure/Backup/*`, `Preferences/*`, `StartupCoordinator`, `RecoveryView` | I restore tests; Desktop integration; UI recovery page | Built |
| T-10 Transfer validation | `Infrastructure/Transfer/*`, `Application/Transfer/*` | I transfer fixtures; security review | Built |
| T-11 Performance gates | `tools/MaxPlanner.FixtureGenerator` (benchmark), `tests/MaxPlanner.Benchmarks`, log events 5003/5016/5605/5010, `scripts/run-performance.ps1` | G run-performance on the documented machine | Built (not run) |
| T-12 Sequence, deliverables | `implementation-plan.md`, `docs/deployment-guide.md`, `docs/user-guide.md`, this file | Review | Built |
| T-13 Inherited data | `Application/ReadModels/ReadModelComposer.cs`, store recomposition | A, P (AC-04, AC-22) | Verified (store); Built (editors) |

## Business requirements
| ID | Implementation | Phase 19 verification | Status |
|---|---|---|---|
| B-01 Scope | Navigation and pages | Review | Built |
| B-02 Tasks and forms | `Application/Tasks/*`, `Presentation/Tasks/TaskEditorViewModel.cs` | A, P, UI editor runs | Built |
| B-03 CRs and links | `Application/ChangeRequests/*`, `CrLinkPolicy`, link-permanence trigger | D, A, I, UI | Built |
| B-04 Workflow, dates | `TaskDatePolicy`, `Application/Settings/*`, `SettingsPageViewModel` | D, A, P, UI | Built |
| B-05 Archive, delete | `ArchiveTask`, `RestoreTask`, `DeleteTask`, `DeleteChangeRequest`, `CrDeletionPolicy` | D, A (AC-30), UI | Built |
| B-06 Copy | `CopyTaskPolicy`, `CopyTask`, `CopyTaskViewModel` | D, A (AC-09), UI | Built |
| B-07 Board and Grid | `Presentation/Board/*`, `Presentation/Grid/*` (ADR 0004) | P, UI (drag, keyboard, Grid gate score sheet) | Built |
| B-08 Calendar | `CalendarLayoutPolicy`, `Presentation/Calendar/*` | D, P, UI (AC-11) | Built |
| B-09 Checklists | `Application/Checklists/*`, `Presentation/Checklists/*` | A, P, UI | Built |
| B-10 Search, filters | `Application/Filtering/*`, `Presentation/Common/Filters/*` | A, P, UI; G filter benchmark | Partial (People/Tags custom fields not filterable, allowed) |
| B-11 Settings, fields | `Application/Settings/*`, `Presentation/Settings/*` | A, P, UI | Partial (required-field backfill step not built) |
| B-12 Transfer | `ExportTransfer`, `ImportTransfer`, `ImportPlanner`, Data page wizards | A, I (AC-12, AC-13), UI; G export/import benchmark | Built |
| B-13 External import | `Application/Transfer/External/*`, `Infrastructure/Transfer/External/*` | A, I (AC-14), UI | Built |
| B-14 Acceptance scenarios | See the AC table | — | — |
| B-15 Dashboard | `DashboardCalculator`, `Presentation/Dashboard/*` | A (AC-18, AC-19), P (AC-21), UI (AC-20); G Dashboard benchmark | Built |
| B-16 Subtask cards | `SubtaskCard`, `Application/SubtaskCards/*`, `SubtaskCardEditorViewModel` | D, A (AC-32), P, UI | Built |
| B-17 Review items | `ReviewItem`, `Application/Reviews/*`, `ReviewsViewModel`, `ReviewEditorViewModel` | D, A (AC-33), P, UI | Built |

## Acceptance scenarios
| ID | Implementation | Phase 19 verification | Status |
|---|---|---|---|
| AC-01 Clean offline launch, persist | Portable publish; first-run dataset creation | G verify-portable Launch; M clean VM create/close/reopen | Partial (dev-machine launch verified Phase 0–2) |
| AC-02 Move folder | `DataPaths.ForApplication`, single-instance key per folder | G Relocation (`-SecondDrive`); M clean VM | Built |
| AC-03 Concurrent linking | Writer ordering, `cr_task_links` PK | I concurrent link tests | Built |
| AC-04 CR edits propagate | `UpdateChangeRequest`, store recomposition | P reactive test; UI | Built |
| AC-05 Date rules per route | `TaskDatePolicy` via every move/edit use case | D, A per route | Built |
| AC-06 Checklist toggle updates | Shared store, `ChecklistItemViewModel` | P, UI | Verified (Phase 2 UIA) |
| AC-07 Failed save / stale revision | Revision guard, conflict review in editors and Grid | I, P, UI | Verified (Phase 2) |
| AC-08 Rename/remove status | `RemoveLookup`, `LookupRemovalDialog` | A, UI | Built |
| AC-09 Copy archived linked task | `CopyTask` | A, UI | Built |
| AC-10 Archive/restore linked task | `ArchiveTask`/`RestoreTask`, store `IsParentArchived` | A, P, UI | Built |
| AC-11 Calendar boundaries | `ScheduleChangeRequest`, `ResizeChangeRequest`, `CalendarSurfaceBuilder` | D, A, P, UI keyboard | Built |
| AC-12 JSON round trip | Transfer builder/writer/reader/planner | I | Built |
| AC-13 Invalid import, no partial change | `ImportTransfer.ApplyAsync` stale checks | A, I | Built |
| AC-14 External import under one CR | `ExternalImportPlanner` | A, I | Built |
| AC-15 Restore, corrupt prefs, interrupted migration | `RestoreService`, `JsonUiPreferencesStore`, `SchemaMigrator` | I, Desktop integration, UI recovery | Built |
| AC-16 Keyboard, Narrator, contrast, 200% | Keyboard maps, AutomationIds/names, HC dictionary, F6 cycle, Ctrl+F | UI keyboard batches; M Narrator, HC, 200% | Built |
| AC-17 Benchmark navigation, no leaks | Retained page VMs, subscription logging (5010), page timing (5605) | G run-performance; P subscription counts | Built (not run) |
| AC-18 Dashboard totals | `DashboardCalculator` | A | Built |
| AC-19 Overdue rule | `OverduePolicy` | D, A | Built |
| AC-20 Drill-down and return | `DrillDownNavigator`, `WorkspaceViewModel`, focus return (`ShellView`) | P, UI | Built |
| AC-21 Day change refresh | `LocalDayBoundarySource`, `SystemDayChangeMonitor`, Dashboard per batch | P, M (time-zone change) | Built |
| AC-22 Inheritance after changes | Store recomposition | P, A | Verified (store end-to-end) |
| AC-23 Theme modes | `ThemeService`, theme-following styles | UI Light/Dark; M live Windows theme change | Built |
| AC-24 Material fallback | `MainWindow.ApplyBackdrop`, card strokes | M transparency off, RDP, battery saver | Partial (Mica path verified Phase 0) |
| AC-25 Snap layouts | Breakpoint code in every page, compact search | UI at Snap widths; M Snap | Built |
| AC-26 Snap/DPI keeps state | Retained views, `CompactSearch` focus keep, `WindowPlacementTracker` | UI resize while editing; M mixed DPI | Built |
| AC-27 Themes × breakpoints × input | As AC-16, AC-23, AC-25 | UI + M matrix | Built |
| AC-28 Link moves own title | `TaskTitlePolicy`, `PreservedMetadata.PreLinkTitle` | D, A, I export | Built |
| AC-29 No unlink/reassign | No route; import conflict | A, UI | Built |
| AC-30 Cascade delete | `DeleteChangeRequest`, `CrDeletionPolicy` | A fault injection, UI | Built |
| AC-31 Default CR Owner | `SetDefaultCrOwner`, `RemoveLookup` replacement | A, UI | Built |
| AC-32 Pop out, independent dates | `PopOutChecklistItem`, `SubtaskCard.Update` | D, A, UI | Built |
| AC-33 Reviews | Review use cases, Dashboard review metrics | A, P, UI | Built |
