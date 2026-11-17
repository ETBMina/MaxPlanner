# Briefs

A brief is everything the agent knows. It has not seen the review, the plan or the conversation.

## Implementing agent (one batch)

Include, in this order:

1. **Who and where.** The project in one sentence, the repository path, the branch. If the agent runs in a worktree: the commit its base must be (`git log --oneline -1` must show it; otherwise `git reset --hard <commit>` before any work), and that it commits to its own worktree branch.
2. **Read `AGENTS.md` and follow it.**
3. **Goal.** The finding in plain words, the evidence with paths and approximate lines, and the target shape. Where a design choice is open, say so and let the agent choose after reading the code; ask it to explain the choice.
4. **Order of work** when it matters, for example "pin current behaviour with tests before moving logic".
5. **Constraints.**
   - Behaviour unchanged; list what that means for this batch (order of operations, messages, enabled states, focus, AutomationIds).
   - Files the agent must not touch (other running batches, later batches' territory).
   - No new dependencies or version changes; return a proposal instead.
   - If the right fix changes behaviour or departs from an ADR or `design.md`: do not implement that part; return what the document says, the alternative, the trade-offs and a recommendation.
   - Bugs noticed are recorded, not fixed. Name the known ones that must stay as they are.
   - No app launch, UI automation, screenshots or smoke tests.
   - Commit rules: message style, no attribution lines, no push.
6. **Checks.** Build and test commands, the test count before the change, and the self-review with the WinUI code-review skill.
7. **Report.** Ask for: commits; what changed per file or area; hooks or differences kept on purpose; anything where order, timing or enabled state could differ; tests added and which behaviour each pins; build and test counts; self-review findings and fixes; what could not be verified without running the app, as manual steps; bugs noticed; proposals needing the user.

For a large batch, add: "If you cannot finish every area with care, finish a correct subset, commit per area, and say exactly what is not done."

## Follow-up to the same agent

Send review fixes back to the agent that wrote the code; it has the context. Number the items, say which are must-fix, repeat what must not be changed, and ask for a further commit and the new counts. When passing on a user decision, quote the option the user chose and name the options they did not.

## Independent reviewer (read-only)

- Fresh agent, no edits, no build, no app launch.
- Name the diff exactly (`git diff <base> HEAD`, or the commit) and the worktree path.
- State the requirement the diff claims to meet, usually "behaviour unchanged", and what that covers.
- Tell it to use the WinUI code-review skill only.
- List the focus areas for this diff: behaviour equivalence path by path against the old code (`git show <base>:<path>`), threading, lifetime and disposal, layering, bindings and enabled states, exceptions, and whether the tests would fail if the behaviour broke.
- Ask it to check the author's own list of "unobservable" differences.
- Ask for findings most severe first, each with file and line, a concrete failure scenario, severity, and whether it is a regression or pre-existing; and for what it compared and what it did not check.

Review follow-up commits too. A fix written after the review has not been reviewed.
