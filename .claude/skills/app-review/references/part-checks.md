# What each part covers

Run searches from `src/` and exclude `obj/` and `bin/`. Record the numbers in the plan; they are the "before" for the next review.

## Part 0: Baseline

Purpose: numbers and screenshots before anything changes.

- `./scripts/build.ps1` and `./scripts/test.ps1` on the starting commit. Record warnings, errors and the test count. If the build fails with MSB3027/MSB3021, the user's own copy of the app is running: ask them to close it or build in a worktree.
- Performance (needs the user's go-ahead, it launches the app):
  - `tools/MaxPlanner.FixtureGenerator benchmark <app-folder>` for the full fixture; `--scale 0.03` for a screenshot-sized one. The demo fixture is too sparse to judge UI.
  - `./scripts/publish-portable.ps1`, then `./scripts/run-performance.ps1`. It exits 1 when a budget is missed; that is a result, not a script failure.
  - Record startup, page-switch p50/p95, idle working set, subscription counts, and the benchmark allocations.
- Screenshots with `winapp ui`, from a disposable copy of the published app: every page in light and dark, default, narrow and maximized, plus the main dialogs. Notes that save time:
  - `winapp ui screenshot -w <hwnd> --output <file>`; delete an existing file first, there is no overwrite flag.
  - In a narrow window the navigation pane collapses, so invoke by shortcut (`winapp ui send-keys "ctrl+2" --via send-input`) instead of by nav item.
  - Change the theme by expanding the theme combo and invoking the list item, not by setting a value.
  - List what was not captured, so part 3 can fill the gaps.

## Part 1: Architecture and MVVM

Look for where logic sits, not only whether layer references are legal.

- Layer references and what the architecture tests enforce; which rules hold only by discipline.
- View models that reference UI types. Search for fully qualified names too (`Microsoft.UI.Xaml.`), not only `using` lines; an architecture test is the reliable check.
- Code-behind size and content: largest `.xaml.cs` files first. Count XAML event handlers against `Command=` bindings, unawaited async calls (`_ = …Async()`), and `async void` outside event handlers.
- View models that repeat the same member names: list member names per editor or page and count how many files share each.
- Hardcoded user-facing strings in code (`"…"` passed to errors or UI), which belong in `.resw`.
- Orchestration in the executable project that no test can reach.
- Service-bag objects handed to every view model.
- Files holding many types or grown very large.
- Binding style: `x:Bind` against `{Binding}`, observable property form, blocking waits.

## Part 2: Consistency and shared components

Compare siblings side by side. The duplication is usually in scaffolding, not in the obvious controls.

- Inventory: files by size; keys in the theme dictionary; per XAML file the count of local styles, keys, font sizes, colour literals, corner radius literals and templates.
- Page scaffolds: the header block, the page-view interface members, size-change handling, breakpoint numbers written as literals.
- Dialog shells: diff the same region of sibling dialogs (`diff` on line ranges); count handler names shared across code-behind files; count AutomationId suffixes shared across dialogs.
- Repeated field patterns (date with clear button, field error, message bars).
- Repeated view-model plumbing: count the calls that make up one "run a command" sequence per file.
- Cross-feature references: which feature folder uses another, and whether the shared folder depends on a feature.
- Formatting: date and number format strings and where they are written.
- Menus and flyouts built in code, and local helper functions copied between files.
- For every proposed shared component, state the cost and name the duplication worth keeping.
- Visible inconsistencies (two filter experiences, two date-range inputs) are recorded here and decided in part 3.

## Part 3: UI / UX

- Launch the fixture copy and capture what part 0 missed: editors on existing records, flyouts, context menus, expanded sections, wizards, dialogs in light theme.
- Review against Fluent and Windows 11 conventions: hierarchy, spacing and type ramp, density, alignment, colour and contrast, states (hover, focus, selected, disabled), keyboard flow, accessible names, narrow-window behaviour, motion.
- Load the WinUI design skill; check that a proposed control or property exists in the pinned Windows App SDK before proposing it.
- Show each visual proposal as a before/after mock-up and get approval before it enters the plan.

## Part 4: Performance

- Static: virtualization and recycling, template depth in list items, binding cost, layout thrash, UI-thread work, allocations in projection updates, SQLite queries and indexes, subscription leaks, startup path.
- Measured: rerun the benchmarks and `run-performance.ps1` and compare with part 0. Start from the "Early performance notes" collected during parts 1 to 3.
- Report with before/after charts.
- When memory is over budget, take a managed heap snapshot (`dotnet-gcdump collect -p <pid>`, installed globally) after visiting every page, and split GC heap from native before proposing fixes.
- Check `CacheLength` on list panels of pages that stay alive.
