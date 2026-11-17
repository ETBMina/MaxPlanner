# App review — implementation plan

Living document for the whole-app review. It is updated after every review part.

## Rules

- Who reviews, who implements, and with which review skill.
- Which documents are open to challenge and which limits are fixed.
- Three gates per part: findings by ID, each batch before it runs, confirmation before the next part.
- Refactors keep behaviour unchanged. Approved departures are recorded in the same batch.
- Branch and commit rule.
- Where and on what data the app may be launched.

## Status

| # | Part | Review | Findings approved | Implementation |
|---|---|---|---|---|
| 0 | Baseline | Not started | n/a | n/a |
| 1 | Architecture and MVVM | Not started | | |
| 2 | Consistency and shared components | Not started | | |
| 3 | UI / UX | Not started | | |
| 4 | Performance | Not started | | |

## Part 0 — Baseline

| Check | Result |
|---|---|
| Build | |
| Tests | |
| Performance script | |
| "Before" screenshots | |

### Performance baseline

Fixture, machine, build, commit. Then one row per measure: baseline, budget, verdict.

### Screenshot coverage

What was captured, and what was not.

## Findings

Finding IDs: `A-nn` architecture, `C-nn` consistency, `U-nn` UI/UX, `P-nn` performance.

### Part N — Title (reviewed DATE, state)

Verdict in two sentences. What already holds. Method and what was not launched or read closely.

| ID | Severity | Finding | Evidence | Proposed change | Effort | Risk and cost | Decision |
|---|---|---|---|---|---|---|---|

Duplication to keep, and why. Not reviewed in depth.

## Implementation batches

| Batch | Findings | Why here | Approved | Status |
|---|---|---|---|---|

Status records: commits, where they are merged, build and test counts, which commits had an independent review, and whether the batch was run in the app.

## Early performance notes

Observations from earlier parts that belong to the performance part.

## Noticed in passing

Bugs and issues outside the review's scope: listed for the user, not acted on. Mark an item when a later decision fixes it.
