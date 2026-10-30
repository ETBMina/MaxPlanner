# Max Planner — agent guide

Max Planner is a native Windows planning app (CRs, tasks, Board, Grid, Calendar, Checklists, Dashboard) built with C#, WinUI 3 (Windows App SDK), .NET 10 and SQLite. It ships as a portable ZIP.

## Where things live

| Path | Use |
|---|---|
| `docs/adr/` | Accepted decisions. Read the ADRs for the area you change. |
| `design.md` | UI and interaction reference. |
| `Business Requirements.md` | Original product specification, kept as a reference. |
| `implementation-plan.md` | Work order and current status. |
| `docs/dev-prerequisites.md` | Toolchain, scripts, and WinApp CLI usage. |
| `src/` | `Domain`, `Application`, `Infrastructure`, `Presentation`, `Desktop` |
| `tests/` | One test project per layer, plus architecture, smoke, and benchmark tests |

`design.md`, the requirements, and the plan are large. Search for the relevant section instead of reading them whole.

## How work is done

- Delegate all code work, in any layer, to the `winui:winui-dev` agent. The main session clarifies the request, writes the brief, reviews the agent's diff, runs the checks, and reports to the user.
- A brief states the goal, the relevant context (files, ADRs, design sections, the user's own words), constraints, and what "done" means, and tells the agent to follow this file. It does not say which skills to use: the agent chooses and combines its own skills for the task.
- The main session edits documentation, ADRs, the plan, and plain text (`.resw` strings, comments) directly, and says so.
- An agent's report is a claim. Check the diff and run the checks before telling the user the work is done.
- Parallel code-writing agents work in separate worktrees and never touch the same files.
- If `winui:winui-dev` is unavailable, say so and ask the user before writing code in the main session.
- The user tests UI changes themselves. Do not launch the app, drive its UI (UI automation, simulated mouse or keyboard input), or take screenshots of the desktop unless the user asks for it in that request. The user is usually working on the same PC. Briefs to `winui:winui-dev` repeat this.

## Decisions

- Precedence: the user's instructions, then accepted ADRs, then engineering judgment. `design.md` and the requirements are references, not contracts.
- Before implementing anything that departs from `design.md`, the requirements, or an ADR, stop and ask the user. Give what the document says (with its section), the proposed alternative, the trade-offs (user experience, effort, data or migration impact, risk), and a recommendation. `winui:winui-dev` returns such proposals to the main session, which asks the user.
- After the user approves a departure, update `design.md` or the requirements, or add or supersede an ADR, in the same change.
- Settle routine implementation choices without asking.

## Hard constraints

- Unpackaged, self-contained x64 portable ZIP: no MSIX, installer, package identity, administrator rights, or separately installed runtime (ADR 0003). Ignore skill or sample defaults that assume packaging.
- App-owned paths resolve from `AppContext.BaseDirectory`; data, settings, backups, logs, and temporary files stay under `data/` beside the EXE.
- Versions are pinned in `global.json` and `Directory.Packages.props` (ADR 0002). Do not upgrade or adopt preview APIs without the user's approval.
- Layering: Domain has no dependencies; Application references Domain; Infrastructure and Presentation reference Application and Domain; Desktop composes them. The UI never touches SQLite. `MaxPlanner.Architecture.Tests` enforces this.
- One ordered SQLite writer. Transaction bodies are synchronous: no `await` inside a transaction (ADR 0009).
- Committed changes reach every page through the shared projection store, and incremental updates keep focus, selection, scroll, and drafts (ADR 0010).
- UI strings live in `.resw` files. Interactive elements get stable AutomationIds and accessible names.
- Automated and destructive tests use disposable datasets, never real user data.

## Commands

```powershell
./scripts/build.ps1                     # restore + build (Debug, x64); warnings are errors
./scripts/test.ps1                      # all test projects
winapp run src/MaxPlanner.Desktop/MaxPlanner.Desktop.csproj -p Platform=x64 --detach --json   # build and launch (only when the user asks)
./scripts/publish-portable.ps1 -Version 0.1.0   # portable ZIP in artifacts/
./scripts/verify-portable.ps1 -Zip artifacts/MaxPlanner-0.1.0-win-x64.zip   # release gates
```

## Done means

- `./scripts/build.ps1` succeeds and `./scripts/test.ps1` passes. New behavior has tests in the layer that owns the rule.
- A UI change ends with short manual test steps for the user: what to open, what to do, and what to expect, including light and dark themes and a narrow window where they matter.
- The diff has been reviewed for correctness and simplicity, and the findings are fixed or explained.
- The report says what changed, what was checked, and what was not checked.
- Commit only when the user asks.
