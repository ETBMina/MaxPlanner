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

## Verification
UIA names and keyboard drill-down tests, plus theme screenshots.
