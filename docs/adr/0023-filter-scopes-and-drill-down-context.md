# ADR 0023: Filter scopes, shared predicates, and the drill-down context stack

- **Status:** Proposed (Phases 11–12, pending owner review)
- **Requirements:** B-07 (filtered Board), B-08.12, B-10, B-15, B-16.8, AC-18–AC-21, T-07

## Context
B-10 asks for search and advanced filters on Board/Grid (one shared task scope), Calendar (a CR scope), and Checklists (its own search), each persisted independently, edited through a draft, and cleared visibly when a saved value no longer resolves. B-15.6 asks that every Dashboard card, chart category, and list drill down with *exactly* its predicate, that the destination's filters be replaced rather than intersected, and that the previous context be restorable (AC-20).

## Options
1. Filter logic in each page ViewModel (the Phase 3 and Phase 7 search boxes), with the Dashboard computing its own counts.
2. **One Application filter model and matcher per record kind**, used by the pages, by `DashboardCalculator`, and by the drill-down specs it emits; a Presentation scope ViewModel per filter scope; a small navigator that stacks replaced contexts.

## Decision
Option 2.
- **Application `Filtering`:** immutable `TaskFilterSpec` and `CrFilterSpec` (search text plus fields; values within a field are OR, fields are AND; lookups by stable ID; Squad uses `null` for "General"); `ReviewFilterSpec` for review drill-downs. `TaskFilterMatcher`, `CrFilterMatcher`, and `ReviewFilterMatcher` are the only predicates. Date ranges are inclusive with explicit undated inclusion; the Calendar planned range uses interval overlap. `FilterPersistence` maps specs to the existing `ViewFilterPreference` (GUIDs, ISO dates, invariant numbers), so `configuration.json` needs no format change. `FilterValidation` removes values that no longer resolve and reports what was removed.
- **Dashboard metrics** are part of the task spec (`DashboardTaskMetric`) or the CR spec (`DashboardOverdue`) and are never persisted. `DashboardCalculator` counts with the same matchers and returns each metric's drill-down spec, so a count and its drill-down cannot disagree. The overdue rule is `OverduePolicy` (Domain), the review week starts on Sunday.
- **Presentation `Common/Filters`:** `TaskFilterScopeViewModel` (shared by Board and Grid, key `tasks`) and `CrFilterScopeViewModel` (Calendar, key `calendar`) hold the applied spec, the draft flyout, chips, the badge count, the invalid-value notice, and the Dashboard context. Predicates run on the store scheduler over current read models (ADR 0010); a Subtask card reads its parent from the store there.
- **`DrillDownNavigator`** keeps one entry per destination page whose context a drill-down replaced. Back (TitleBar, Alt+Left) and Return pop an entry, restore that page's previous filters, and show the Dashboard, whose retained ViewModel keeps its filters and scroll offset.

## Consequences
- Board, Grid, Calendar, the Dashboard and its drill-downs share one implementation per rule; the earlier page-local search code was removed.
- Because Board and Grid share the task scope, a task drill-down is visible on the Board too (with reordering off while active).
- A future filter field needs a spec member, a matcher clause, a persistence key, a validation rule, a draft field, and a chip; all are in two files.
- No new dependency and no schema or preferences-format change.

## Verification
Deferred to Phase 19 (owner decision): Application tests for the matchers, persistence round trips, validation, and `DashboardCalculator` (AC-18, AC-19, AC-21 boundaries); Presentation tests for the scopes, the draft, chips, drill-down replacement and restore, and the navigator; UI checks of the flyout, chips, and Back/Return.
