# Lessons from past reviews

Read before each part. After each part, add what went wrong or worked, in one or two sentences with the reason. Remove an entry when a rule in `AGENTS.md` or a test now enforces it.

## Review

- **A text search is not proof.** "No view model references a WinUI type" was wrong: one used a fully qualified name. The architecture test found it. Prefer a test or a compiler-level check to a grep when stating that a rule holds.
- **Measure, then read.** Counting handler names, AutomationId suffixes and call patterns across sibling files found the duplication faster than reading each file, and gave the user numbers to decide on.
- **Diff siblings.** Diffing the same region of two dialogs showed 71 shared lines with 2 differences; that is stronger evidence than "these look alike".
- **A shared component is not always the answer.** Drag and drop looked duplicated in five places; only one had auto-scroll and an insertion line, the rest used the built-in list reorder. Check what the copies actually do before proposing one component.
- **Recommend deferring** findings whose right shape depends on a later part. The user agreed each time, and the later part changed or dropped them.

## Implementation

- **Worktrees may start from the wrong commit.** Agent worktrees were cut from `main`, not from the review branch. Every brief now states the expected base commit and what to do if it differs.
- **A skipped test is not a done state.** An agent left a new architecture test skipped because it found a violation. Send it back: fix the violation or return a proposal.
- **"Make failures visible" can change behaviour.** Asking that exceptions not be lost silently turned lost exceptions into app crashes. Any instruction about failure handling needs the user's choice among: close, log and continue, stay silent.
- **One decision can make a deferred bug mandatory.** "Keep running after a failure" required fixing the editor stuck in "saving". Say so to the user instead of fixing it quietly.
- **Apply a decision everywhere or say where it stops.** After the failure-handling decision, older commands still crashed and one view still dropped exceptions. Ask the agent to list every call site covered and every one left out, with the reason.
- **Suggested fixes can be wrong.** A reviewer's suggested fix would have blocked the UI thread; the implementing agent explained why and did it differently. Ask agents to push back with reasons.

## Verification

- **The user's running app locks the build.** Build and test in a worktree; never stop their process.
- **Tree hashes prove a merge.** When the merged commit has the same tree as the commit that was built and tested, a rebuild adds nothing.
- **Independent review finds what the author's review does not**: a lifetime regression, a test that would pass with the fix deleted, a timing-dependent test. Run it for anything beyond moves and renames.
- **Follow-up commits need review too.** Commits written after the independent review were only checked by the main session; say so in the report.
- **Ask what the tests would catch.** Several gaps were tests that pinned the fakes, or passed whether or not the behaviour held.

- **Merging before the user ran the app stacked up risk.** Four batches were merged on tests and diff review alone. The user then asked for the working branch to be launched for their own testing before every merge; the loop now does that.
- **An agent skips a sub-item when "no visible change" forbids it.** The shared New button was skipped because one page differed. Report such skips with the exact difference, so the user can approve the small visible change if they want the consistency.

## Working with the user

- Lead with the result and what is not verified; give manual test steps per batch, since unit tests cannot see focus, shortcuts or enabled states.
- Put decisions as a short list of options with a recommendation.
- Keep a list of bugs noticed and not fixed; the user decides when they are worth a batch.
