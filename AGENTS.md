# Max Planner — Agent working guide

## 1. Purpose and authority

Build and maintain **Max Planner**, the native Windows application described in [Business Requirements.md](<Business Requirements.md>). This guide defines how to work; [design.md](design.md) defines the UI and interaction contract; [implementation-plan.md](implementation-plan.md) tracks work order and status; `docs/adr/` records decisions. Keep these files with the application repository.

Before work:

1. Read applicable agent instructions, the user's current request, and this guide.
2. Read the requirements, including relevant T-, B-, and AC- identifiers. Read the design before UI work.
3. Inspect the existing files, current changes, build scripts, and tests. Do not assume that a solution, tool, package, or planned document already exists.
4. Identify the affected requirements and the smallest complete, verifiable change. For a substantial implementation, create or update `implementation-plan.md` with dependencies, deliverables, acceptance checks, and actual progress.

Explicit user instructions take precedence over these project guidelines and skill defaults, subject to the agent host's higher-priority instructions. The requirements are the product authority; design, implementation plans, sketches, and skills must not silently change them. An ADR explains an implementation choice; it cannot waive a MUST requirement. If a genuine business ambiguity remains, ask a focused question and continue independent work. Use established design defaults without repeatedly asking for approval.

Scope work to the request. Documentation work does not require scaffolding, installing tools, or launching an app. Preserve unrelated files and user changes. Do not commit, publish, sign with production credentials, or install system tools merely because a sample workflow includes those steps.

## 2. Non-negotiable implementation rules

This is a reminder of high-risk requirements, not a substitute for reading the specification.

| Area | Required approach |
|---|---|
| Platform | C#, native WinUI 3 XAML, stable Windows App SDK 2.x, .NET 10 LTS (SDK 10.0.401 pinned by owner decision), SQLite via `Microsoft.Data.Sqlite`. Revalidate compatibility and pin exact versions deliberately. |
| Deployment | Unpackaged, self-contained x64 ZIP with both runtime families and native dependencies (including SQLite) bundled. No required installation, package registration, administrator rights, network, Office, or separately installed runtime. |
| Files | Resolve app-owned paths from `AppContext.BaseDirectory`; keep data, configuration, backups, logs, and temporary files under `data/`. No silent AppData fallback. |
| Architecture | Domain has no project/third-party dependencies; Application references Domain; Infrastructure and Presentation reference Application/Domain; Desktop composes them. UI never accesses SQLite directly. |
| Identity | Stable `Guid` IDs; configurable lookup IDs and semantic flags. Labels, Task Number, CR Number, and filenames are not task identities. |
| CR relationship | One CR has zero or more tasks; each task has zero or one CR. Links are **permanent**: created once for an unlinked task, never unlinked or reassigned. Deleting a CR cascades to its tasks after confirmation. |
| Inheritance | Linked task Title, CR Number, Task Number, Squad ("General" when none), and Owner resolve from the current CR; Subtask cards resolve them through their parent. No independently writable copies or task overrides. CR changes refresh every dependent record. |
| Dates | Task and Subtask card dates are actual dates, automated independently; CR dates are planned inclusive ranges. Date-only business values and UTC audit timestamps stay separate. Only incomplete CRs ending before local today are overdue (CRs are not archived). |
| Mutations | Use cases validate and commit complete logical operations with revision checks and stable operation IDs. One ordered SQLite writer; transaction delegates are synchronous—no `await` within a transaction. Reads use pooled WAL readers. |
| Reactive UI | Ordered committed changes feed one shared Presentation projection store. Snapshot/subscription initialization is race-free. Incremental UI updates preserve focus, selection, scroll, and drafts. |
| Integrity | Failures retain drafts and previously committed data. No silent partial imports, automatic empty-database recovery, guessed relationship repair, or blind retry after uncertain commit/publication. |
| Native appearance | Fluent 2: System/Light/Dark, system-controlled High Contrast, native Mica on the main window and Acrylic on transient surfaces with solid fallback, responsive layouts and Windows Snap support. English-only `.resw` strings. |
| Scope | Implement all release-1 requirements (including Subtask cards and Review items); do not introduce deferred cloud, account, sync, historical analytics, task deadlines, or extension features. |

## 3. Orchestrating plugin agents and skills

Use the installed plugin **agents** and **skills** to extend what a single session can do: specialist agents bring focused WinUI/Windows expertise and run independent work in parallel, and skills bring tested working procedures and tools (WinApp CLI `find-ui`, `find-api`, `ui`, sandbox). Prefer them over improvising from memory whenever one covers the work. Skills and agents are complementary working procedures, not additional product requirements. Names below are discovery names; do not embed one developer's absolute installation path in scripts or documentation.

At the beginning of the relevant phase, inspect the session's available agents and skills, read the actual `SKILL.md`, and load only the references needed for the work. **Announce every agent or skill you use, briefly, before using it**, and report what it contributed. Check the installed tool version and help before relying on sample commands (WinApp CLI 0.7.1 is the current baseline; see `docs/dev-prerequisites.md`). Availability, prefixes, templates, and CLI options may differ between machines.

### 3.1 Plugin agents

| Agent | When to delegate | Brief it with |
|---|---|---|
| `winui:winui-dev` | WinUI 3 / Windows App SDK / XAML work that benefits from a dedicated specialist: planning a phase, implementing a self-contained page or control, diagnosing a XAML/build/startup failure, or reviewing a UI change against Fluent guidance | The relevant requirement IDs, `design.md` section, decisions/ADRs, target files, the "done" definition, and whether it may edit files or must stay read-only. Remind it that this guide and the requirements override generic skill defaults (unpackaged, no MSIX, pinned versions). |
| `winappcli:winapp` | Driving or inspecting the running app through UI Automation, Windows Sandbox runs, screenshots, Windows SDK/API lookups, and troubleshooting WinApp CLI behavior | The published or built EXE path, the isolated test dataset folder, the scenario steps and expected UIA states, and where to save evidence |
| `Explore` / `Plan` (built-in) | Read-only codebase sweeps and design alternatives when the answer is a conclusion, not a file dump | A specific question and breadth |

- Delegate only work with a clear boundary and result. Keep architecture decisions, requirement interpretation, and final verification in the coordinating session; an agent's report is evidence to check, not proof.
- Give each agent a complete brief; agents start without this conversation's context. Writers must not touch the same files concurrently; use isolated worktrees for parallel code-writing agents.
- An agent's suggestion never overrides the requirements, `design.md`, an ADR, or the user's instructions. Record material agent findings in the plan, an ADR, or evidence.

### 3.2 Skills

| Skill | When to use | Expected contribution and limits |
|---|---|---|
| OpenAI `winui-app` | WinUI planning, architecture/UI foundation, new-app bootstrapping, environment inspection, windowing, responsiveness, troubleshooting | Establish the native WinUI approach and official references; guide build/launch verification when code changes. Run setup/scaffold portions only for an authorized implementation/setup task. |
| Microsoft `winui:winui-design` | Before authoring or substantially changing XAML, selecting controls, reviewing visuals, accessibility, themes, or responsive layouts | Find relevant native samples, choose controls, plan layout/binding, and evaluate Fluent/accessibility behavior against `design.md`. |
| Microsoft `winui:winui-dev-workflow` | Creating the actual solution, building/running it, or diagnosing build/startup failures | Use compatible installed templates/build tools and analyzer integration; capture build and real launch results. A tool-assisted developer launch does not prove portability. |
| Microsoft `winui:winui-code-review` | After implementation/build verification and before declaring a feature ready; before a commit when a commit is requested | Review project boundaries, MVVM, bindings, resources, UI Automation, async code, security, and performance. Report actionable file/line findings and fix relevant defects. |
| Microsoft `winui:winui-ui-testing` | Verifying implemented interactions, keyboard behavior, persistence through the UI, and visual states | Prefer repeatable batch UI Automation checks against an isolated test dataset; inspect screenshots as well as assertions. Include responsive/theme coverage and genuine restart tests where required. |
| Microsoft `winui:winui-packaging` | Preparing release artifacts, deployment validation, or explicitly requested signing/distribution work | Use relevant self-contained/release guidance while retaining the required portable ZIP. Its MSIX, certificate trust, Store, and administrator steps are not this project's default release workflow. |
| Microsoft `winui:winui-setup` | Only when the user explicitly requests toolchain setup or repair | Install/repair prerequisites within that authorization. Missing tools during ordinary work mean report the missing prerequisite and continue unaffected work; do not invoke setup automatically. |
| Microsoft `winui:winui-wpf-migration` | Only for an actual user-requested WPF-to-WinUI migration | Not applicable to this from-scratch app by default. |
| Microsoft `winui:winui-session-report` | Only when explicitly asked for session diagnostics or feedback | Do not inspect agent session history automatically during normal development. |
| `winappcli:winapp-find-ui` | Before writing new XAML for a control or pattern | Pull real WinUI Gallery/Toolkit samples instead of inventing markup; adapt them to `design.md` and native resources. |
| `winappcli:winapp-find-api` | Before using any WinUI/WinRT type or member, and on CS0117/CS1061/CS0246 or unknown XAML member errors | Verify the API exists in the project's referenced metadata (`find-api check-property`, `members`, `enums`) rather than guessing. |
| `winappcli:winapp-ui-automation` | Inspecting or driving the running app during UI verification | Element trees, AutomationId checks, clicks, keys, drag, screenshots; complements `winui-ui-testing`. |
| `winappcli:winapp-sandbox` | Clean-environment launch of the published ZIP, disposable UI runs | Interim clean-machine evidence; it does not replace the T-11 clean offline VM gate. |
| `winappcli:winapp-troubleshoot` | WinApp CLI or Windows SDK errors | Diagnose before retrying; ignore its MSIX/signing remedies for this unpackaged app. |
| `windbg:*` triage skills | Native crashes, hangs, heap corruption, or dumps from the app or its native dependencies (SQLite, Windows App Runtime) | Evidence-first native diagnosis; managed/XAML exceptions go through `winui-dev-workflow` first. |
| `code-review`, `security-review`, `simplify` | Before declaring a phase ready and before any requested commit | General correctness, security (import parsing, ZIP handling, URL launching, SQL), and simplification passes alongside `winui-code-review`. |
| `dataviz` | Designing Dashboard charts | Chart form, accessible color, and labeling guidance; rendering stays native WinUI (T-08). |
| `winappcli:winapp-package`, `-signing`, `-manifest`, `-identity` | Not used by default | MSIX, certificates, manifests, and package identity contradict the portable unpackaged requirement; use only if the user explicitly asks for an additional distribution channel. |

### 3.3 Coordination and overlap

1. For a new-app implementation, use `winui-app` for the overall approach, `winui-design` for native UI choices, and `winui-dev-workflow` for the verified local build/run procedure. Delegate a bounded phase or page to the `winui:winui-dev` agent when a specialist pass helps, and use `winappcli:winapp` for UI automation runs. Do not run multiple scaffolding recipes over the same solution. Choose one compatible route after inspecting available templates, then record it.
2. For each UI feature, proceed through design/control selection, implementation/build/run, code review, and UI testing. Select only the skills needed by the change; a domain-only rule change need not trigger screen automation.
3. Keep project constraints above generic sample defaults. A packaged sample does not authorize switching this app to MSIX. Setting unpackaged deployment is intentional here, not a workaround for a broken packaged launch. Never count a runtime-installing/registration helper as proof that the published EXE works independently.
4. Preserve exact dependency pins. Do not opportunistically upgrade packages or adopt preview APIs because a skill sample says “latest.” Validate the chosen stable toolchain and update pins deliberately.
5. Preserve the required incremental projection pipeline even if a generic example clears and refills a collection. Similarly, a simple table example does not satisfy the editable Grid gate in T-08/B-07.
6. Use applicable analyzers where supported and record how they were loaded. Do not claim plain builds ran plugin-injected analyzers. Adapt development tooling to the unpackaged project; keep reproducible project build/publish scripts independent of a personal plugin cache.
7. If skills disagree, resolve the difference against the user's request, requirements, actual API/tool behavior, and official documentation. Document a material departure with evidence. Ask only when the unresolved choice would change product scope or behavior.
8. If a skill, agent, or tool is unavailable, state that limitation and continue with project scripts and current official Microsoft documentation, WinUI Gallery, and Windows App SDK samples where possible. Do not invent skill/agent use or successful checks, and do not automatically install optional plugins.
9. Use agents where they add capability—specialist WinUI depth, parallel independent work, or UI automation—not for work the coordinating session can do quickly itself. Never let two agents write the same files at once.

## 4. Code structure and conventions

- Organize by feature within the five required projects. Keep use cases focused; introduce abstractions for real boundaries, not hypothetical extensibility. Avoid generic repository frameworks, service locators, or an additional reporting database.
- Enable nullable reference types and follow checked-in formatting/analyzers. Use descriptive domain names, small methods, explicit lifetimes, and immutable command/result/read-model contracts where practical.
- ViewModels depend on application use cases and presentation services, not repositories. Code-behind handles only view mechanics such as focus, pointer capture, window APIs, and drag visuals.
- Use MVVM Toolkit for observable draft state and commands. Use asynchronous command paths with cancellation where meaningful; `async void` is limited to framework event handlers with error handling. Never block the UI with `.Wait()`, `.Result`, database calls, parsing, or large synchronous work.
- Keep semantic validation and Dashboard predicates testable outside XAML. Separate draft values from committed read models; never mutate shared cached entities as the first step in Save.
- Give changing compiled bindings explicit `OneWay`/appropriate `TwoWay` modes and templates the correct data type. Marshal collection notifications through the injected dispatcher; dispose subscriptions by ownership.
- Use native semantic resources and localized UI strings. Keep serialized identifiers/dates culture-independent while formatting visible dates and numbers for the user. Do not use display text as automation identity.
- Add stable AutomationIds, meaningful accessible names, keyboard paths, and focus behavior with the feature, not after the screen is finished.
- Use typed errors at boundaries. Report actionable failures without leaking notes, import bodies, secrets, or unnecessary personal data to logs. Catch exceptions where they can be handled or translated; do not swallow them.
- Respect cancellation/commit semantics, revision checks, and stable operation IDs across all routes. Imports, drag/drop, inline edits, and dialogs must not create separate business-rule implementations.

## 5. How and when to write code comments

Comments must explain information a maintainer cannot reliably infer from clear names and code. Write concise English sentences adjacent to the relevant decision. Prefer improving names, extracting a policy, or simplifying control flow over narrating confusing code.

### Write comments for

- Non-obvious business intent or exceptions, especially date automation, inclusive CR ranges, shared inherited values, permanent links and cascade deletion, Subtask card independence, filtered Board ordering, and import merge semantics. Cite a relevant requirement ID when it makes the rule traceable.
- Concurrency, thread affinity, ownership, disposal, transaction boundaries, commit/publication recovery, and race prevention. State which assumption must remain true.
- Algorithms whose correctness depends on ordering or boundary behavior, such as calendar lane allocation and snapshot/sequence handoff.
- Compatibility workarounds and measured performance decisions. Link an official issue/document or ADR, name the affected version/condition, and state when the workaround can be removed.
- XAML behavior that is intentionally non-obvious: a bounded viewport needed for virtualization, a special binding mode, a focus restoration hook, or a High Contrast fallback.
- Tests with a subtle fixture or counterintuitive expected result. Explain the scenario, not each assertion.

### XML documentation

Use `///` documentation on cross-project application ports/contracts and reusable APIs when semantics are not obvious from the signature. Document side effects, cancellation before versus after commit, conflict outcomes, thread/lifetime requirements, null/empty meaning, units, and date/time conventions where relevant. Use `<summary>`, `<param>`, `<returns>`, `<remarks>`, and `<exception>` only when they add useful contract information. Use `<inheritdoc/>` for unchanged implementation contracts rather than duplicating text.

Do not require a comment for every property, command, constructor, or private helper. A mechanically repeated method name is not useful API documentation.

### Examples of useful comments

These illustrate comment style; they are not a prescribed implementation.

```csharp
// T-06: The writer connection belongs to the database worker. Keep this entire
// transaction synchronous on that worker; await only outside this boundary.

// B-15: PlannedEnd is inclusive, so a CR ending today becomes overdue tomorrow.

// B-07: Hidden cards still own ranks. A filtered status move appends to the
// full destination column rather than renumbering only visible cards.
```

```xml
<!-- Keep the card list in a bounded viewport so virtualization stays active. -->
```

### Avoid and maintain

- No line-by-line narration, decorative section banners, personal authorship notes, change-history diaries, commented-out implementations, or unsupported claims such as “thread-safe” or “atomic.”
- Do not paste large requirements/ADRs into source. Reference them and explain only the local consequence.
- Do not leave unexplained `TODO`, `FIXME`, or `HACK` markers. If unfinished work must remain, describe the missing behavior and reference a tracked issue or named implementation-plan item with a completion condition. It remains incomplete work.
- Update or remove stale comments in the same change as the code. Review comments for accuracy, including examples and XML docs. A comment never replaces validation, a test, or a working feature.

## 6. Implementation and verification workflow

Follow T-12 rather than building all screens over mocked persistence first:

1. **Foundation:** record stable version pins and project boundaries; prove unpackaged self-contained startup and SQLite read/write/reopen with the bundled native library. Resolve the native Grid control gate with an ADR and an executable spike before committing to its UI architecture.
2. **Integrity:** implement domain policies, schema/link constraints, revisions, transaction coordination, migrations, and recoverable startup. Use real temporary SQLite files for persistence tests.
3. **Vertical flow:** save a Grid task edit, persist it, and update Board/Checklists through shared changes. Verify failure, conflict, and subscription lifecycle behavior before adding more workflows.
4. **Feature completion:** implement every business screen, Dashboard, transfers, settings, recovery, accessibility, and responsive states. Keep an explicit requirement-to-test checklist; track partial and blocked items honestly.
5. **Release proof:** run the T-11 gates against the published Release ZIP, including the clean offline VM, relocation, restart, dependency absence, performance fixture, and recovery cases. Deliver the required source/scripts/docs/notices/schema/evidence with the artifact.

During implementation, create reproducible build, test, and portable publish scripts and document their actual commands in the repository. Discover current scripts before running commands; do not assume sample filenames exist. Keep development prerequisites separate from end-user requirements. Avoid single-file publishing, trimming, and AOT initially, as required by T-03.

Choose checks proportionate to the change, but complete all affected acceptance criteria. Domain tests verify policy boundaries; Application tests verify use cases; Infrastructure tests verify real durability/transactions; Presentation tests verify ordered reactive behavior; UI tests verify interaction and visual/accessibility behavior. Avoid tests that merely mirror implementation details.

For UI changes, build and launch on a supported Windows machine, verify a responsive real window, exercise the changed flow, and inspect its visual states. Test with disposable datasets and dedicated output folders; never use real user data for destructive automation. UI Automation alone does not prove visual quality or Narrator usability. Screenshots alone do not prove persistence. Restart/reopen checks must actually relaunch the app and reopen the SQLite database; do not substitute direct file reads for app behavior.

For documentation-only changes, check consistency, links, and requirement references; do not report app builds or runtime tests as performed. For unavailable checks, record the exact limitation and remaining acceptance work.

## 7. Documentation, decisions, and handoff

- Keep the requirements authoritative for product rules, `design.md` for interaction/layout, `implementation-plan.md` for work sequence/status, and `docs/adr/` for decisions and evidence. Create future files when needed; these references do not imply they exist today.
- An ADR records context, considered options, chosen approach, consequences, dependency/license implications, and verification. Key early decisions include Grid selection, chart rendering, transfer libraries, and version/deployment compatibility.
- If a design tool such as Google Stitch is used, treat output as a visual reference. Reconcile it with native controls and this specification; do not import a web UI as the application.
- Make changes concrete and reviewable. Summarize what changed, relevant checks and results, and material limitations. Distinguish completed work from proposed steps. Never call a feature complete while required persistence, responsive, accessibility, failure, or portable-deployment behavior remains a placeholder.
