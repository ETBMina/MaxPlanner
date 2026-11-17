---
name: app-review
description: Run or continue the gated whole-app review of Max Planner (architecture and MVVM, consistency and shared components, UI/UX, performance), or one part of it. Use this whenever the user asks to review the app or a layer of it, audit architecture, MVVM, duplicate code, shared components, UI quality or performance, "repeat the review", "review part N", continue an earlier review, or turn review findings into approved implementation batches, even if they do not name this skill. Not for reviewing a single diff or pull request; use a code-review skill for that.
---

# App review

A repeatable review of the whole app, in parts, where the user approves every step. Review is read-only and done by the main session; code is written by a separate implementing agent; nothing is implemented, merged or committed without the user's say.

The state of a review lives in `docs/review/implementation-plan.md`. Read it first. If it exists, continue from its status table instead of starting again. If it does not, start at "Starting a review".

## Why it is shaped this way

- **Parts in a fixed order** (baseline, architecture, consistency, UI/UX, performance): structural changes move code, shared components should exist before they are restyled, and performance is measured last on the final structure against the baseline taken first.
- **Three gates per part:** a finding is an opinion until the user approves it, a batch can still be too risky to run now, and the next part should not start on code the user has not accepted.
- **Review and implementation are separate agents:** the reviewer has no stake in the diff it checks.
- **A living plan document:** a review spans several sessions; the conversation does not survive, the document does.

## Starting a review

1. Read `AGENTS.md` and follow it. It decides who writes code, what needs approval and what "done" means.
2. Settle these with the user before anything else, in one round of questions, and record the answers under "Rules" in the plan:
   - **Scope of rules:** which documents are open to challenge (`design.md`, requirements, ADRs) and which limits are fixed.
   - **App data:** the app is launched only from a disposable folder on a generated fixture, never on the user's own data.
   - **Git:** which branch, and that commits happen only when the user asks.
   - **Who does what:** which model reviews and which implements.
   - **What is out of scope** (for example business functionality).
3. Create the review branch and `docs/review/implementation-plan.md` from `references/plan-template.md`.
4. Run part 0.

If the user asks for one part only, do that part with the same gates; still take or reuse a baseline when the part is UI/UX or performance.

## The loop for every part

1. **Review, read-only.** Load the WinUI code-review skill and apply its checklist on top of the part's own scope in `references/part-checks.md`. Measure before judging: counts, diffs and file sizes turn "this feels duplicated" into evidence. Read the code that matters yourself; use a search agent only for wide sweeps.
2. **Findings.** Give each an ID (`A-nn` architecture, `C-nn` consistency, `U-nn` UI/UX, `P-nn` performance), severity, evidence with `file:line`, the proposed change, effort (S/M/L), risk, and any ADR or `design.md` section it departs from. Also report what already holds, what duplication is worth keeping and why, and what you did not read closely.
3. **Gate 1: the user approves findings by ID.** Recommend, including which findings to defer or drop.
4. **Update the plan.** Approved findings become batches. Group by files touched and by risk: small, no-behaviour-change moves first; one batch per coherent refactor; the riskiest last.
5. **Gate 2: the user approves each batch before it runs.**
6. **Implement** through the implementing agent, one batch per brief. Use `references/briefs.md`.
7. **Verify** before reporting: read the diff, run the build and the tests yourself, and for non-trivial batches start an independent read-only review. Decide which review findings to send back. An agent's report is a claim, not a result.
8. **Report** what changed, what was checked, what was not, manual test steps for anything the user must see in the app, and bugs noticed but not fixed.
   - **Launch the batch's build before asking about the merge.** Build the working branch in its worktree, put generated fixture data in that build's own `data/` folder (`tools/MaxPlanner.FixtureGenerator benchmark <build-folder> --scale 0.03`), start the app from there, and tell the user which window is the test build. Unit tests cannot see focus, shortcuts, enabled states or layout, and the first launch also proves the startup wiring. Only launch; the user does the testing. Merge after they say it passed.
9. **Gate 3: the user confirms before the next part's review starts.**
10. **Learn.** After each part, add what went wrong or what worked to `references/lessons.md`; workflow and agent-handling lessons belong there, not in `AGENTS.md`. Separately, propose `AGENTS.md` changes that would have prevented the architecture and design mistakes the part found: where code lives, layering, shared components, UI patterns. Tie each proposal to the finding it prevents, and apply it only after the user agrees.

## Rules that keep the review honest

- **Behaviour stays the same in a refactor.** If a finding can only be fixed by changing what the user sees or what happens on failure, stop and ask, with the options and a recommendation. Record an approved departure in an ADR or `design.md` in the same batch.
- **Bugs noticed in passing are listed, not fixed.** Put them under "Noticed in passing" in the plan. The exception is a bug that an approved decision makes unavoidable; say so plainly when that happens.
- **Say when a finding was wrong.** A new architecture test or a closer read can disprove an earlier claim. Correct it in the plan and tell the user.
- **Prefer no shared component over a wrong one.** State the cost of each consolidation: template depth on a hot path, indirection, fragile focus behaviour. Keep duplication where it is cheaper.
- **Tests are the safety net, so check the net.** Before a refactor, make sure the behaviour being moved is pinned by a test; if not, the brief says to pin it first. A test that would pass with the fix deleted proves nothing.
- **Do not launch the app, drive its UI or run tests that launch it unless the user allowed it for that part.** Announce each launch; the window appears on the user's desktop.

## Working alongside the user's own copy of the app

The user often has their own Debug build running, which locks the build output. Do not stop their process. Run implementing agents in their own worktree, and verify a merged commit either in a worktree or, when the merge is a fast-forward-shaped merge, by comparing tree hashes with the commit that was built and tested.

## References

| File | Read it when |
|---|---|
| `references/part-checks.md` | Starting the review of a part: what to measure and look for, with the commands that worked. |
| `references/briefs.md` | Writing a brief for the implementing agent or for an independent reviewer. |
| `references/plan-template.md` | Creating `docs/review/implementation-plan.md` for a new review. |
| `references/lessons.md` | Before every part. Update it after every part. |
