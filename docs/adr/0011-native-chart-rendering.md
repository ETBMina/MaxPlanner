# ADR 0011: Native chart rendering

- **Status:** Accepted
- **Requirements:** T-08 (charts), B-15

## Context
The Dashboard needs only horizontal bar charts: tasks by status, tasks by squad, and open reviews by developer. Each needs labels, counts, keyboard drill-down and an equivalent table.

## Options
1. A charting library (for example LiveCharts2/SkiaSharp). This adds a native renderer, payload and accessibility gaps.
2. Bars composed from native controls.

## Decision
Use option 2. Each bar row is a focusable `Button` with `AutomationProperties.Name` set to "<category>, <count> tasks". Bar length comes from a star-sized `Grid` column ratio, and the count is always visible as text. A Table toggle shows the same data in a list. Colors use theme resources, and High Contrast uses system colors.

## Consequences
- No chart dependency is added.
- Charts beyond horizontal bars would need a new ADR.

## Implementation (Phase 12)
`Presentation/Dashboard/BarChart` with `BarChartViewModel`: an `ItemsRepeater` of `Button` rows (label in a 35% column, a `BarTrack` panel that gives the bar its share of the largest count, at least 2 epx above zero and none at zero, and the count as text); a single accent hue (`AccentFillColorDefaultBrush`, one series, so no legend) with a 4-epx rounded data end; row names "In Progress, 6 tasks, 19 percent of 32. Opens matching tasks in Grid."; a Table `ToggleButton` showing the same rows as Category | Count | Share. The `dataviz` skill guided form, single-hue color, and labeling only; rendering is native WinUI.

## Verification
UIA names and keyboard drill-down tests, plus theme screenshots. Not run yet: deferred to Phase 19 (owner decision).
