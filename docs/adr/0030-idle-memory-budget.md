# ADR 0030: Idle working set budget of 600 MiB on the benchmark fixture

- **Status:** Accepted (owner decision, Nov 2026)
- **Requirements:** T-11 (idle working set with fixture). Arose from performance review finding P-02.

## Context
The requirements set the idle working set on the benchmark fixture (5,000 CRs, 10,000 tasks, 2,000 Subtask cards, 50,000 checklist items) at 500 MiB or less. The budget was set before the shell kept every loaded page alive (T-11/AC-17): pages are collapsed instead of unloaded, so a page switch keeps realized items, scroll offsets, selection and drafts and takes no layout until the page is shown again.

Review part 4 measured, on a Release portable build, after 20 navigation rounds:

- 597 MiB at the start of the review and 628 MiB after part 3.
- 544 MiB after batch 4B (P-01) reduced the Board and Grid list realization cache.
- A managed heap snapshot after visiting every page: 517 MiB private bytes, of which the GC heap is 145 MB. The rest, about 370 MiB, is native: the WinUI visual trees of the five live pages, composition, the runtime and SQLite. A 300-task dataset starts at about 280 MiB.

## Options
1. **Raise the budget to 600 MiB** and keep every page alive.
2. Release the realized items of pages that have been hidden for a while. This would save native memory, but the first switch back to such a page would rebuild its items, which works against the page-switch budget and AC-17.
3. Shrink managed structures (per-page filtered views, read-model dictionaries). The snapshot shows each is a few hundred KB, so this cannot close the gap.

## Decision
Option 1. The idle working set budget on the benchmark fixture is **600 MiB**. `scripts/run-performance.ps1` checks this value.

## Consequences
- The release gate passes at the measured 544 MiB, with about 56 MiB of headroom for later features.
- Instant page switching stays (p95 60 ms after batch 4B, against a 250 ms budget).
- Memory still grows with the dataset. If a later change pushes the idle working set past 600 MiB, option 2 is the next step to measure.
