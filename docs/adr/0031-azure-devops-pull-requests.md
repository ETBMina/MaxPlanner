# ADR 0031: Create Azure DevOps pull requests from tasks and CRs

- **Status:** Accepted (owner decisions, Nov 2026)
- **Requirements:** Adds B-18. A narrow exception to the Deferred list in `Business Requirements.md` (external integration, encrypted storage). Tasks stay in Jira; nothing is read from or written to Azure DevOps work items.
- **Design:** `design.md` §5.11 (dialog, entry points, results, accessibility) and §4.6 Integrations (settings).

## Context
The owner's team promotes code with an Azure DevOps pipeline that creates pull requests (`ace_bank-pr`). From a Max Planner task, CR or Subtask card the owner wants to create one pull request for that work, with the task number as the source branch. The server is a custom enterprise **Azure DevOps Server 2020 Update 1.2**, which supports REST **api-version 6.0**. Authentication is a personal access token (PAT).

## Decision
- **Direct REST calls, no pipeline.** The app calls the pull request API itself: the pipeline cannot report PR numbers back, needs a build agent, and its parameters are currently not wired into its script. The app uses the built-in `HttpClient`: **no new dependency**. Certificate validation stays on; the app never disables it. On Windows `HttpClient` uses Schannel and the system proxy, like `curl.exe`, which works against the server (Python's OpenSSL client timed out in the TLS handshake).
- **Scope.** Create a pull request, and optionally set auto-complete. No work item access, no PR description, no saved PR links (nothing to migrate, nothing in export or import), no PR listing.
- **On/off switch.** Settings › Integrations › Azure DevOps has an **Enable** switch, off by default. When off, no pull request command is shown; values and token are kept.
- **Settings** are **per-PC preferences**: stored with the UI preferences but **not** reset by Restore default UI preferences, and not part of the dataset, export or import.
  - Collection URL (`https://<server>/<collection>`; must start with `https://`; an address with `/_git/` or `/_apis/` gets an inline hint), Project, Repository (plain names).
  - Default target branch (`uat`), auto-complete default (on).
  - Title template, default `PR {CRNum} {TaskNum} into {Target}`. Placeholders `{CRNum}`, `{TaskNum}`, `{Title}`, `{Source}`, `{Target}` reuse the Copy ID format's placeholder matching (case-insensitive). Unlike Copy ID, an empty value does **not** drop the text around it: only the placeholder and the extra spaces go, so `PR {CRNum} {TaskNum} into {Target}` without a CR number gives `PR 12345 into uat` (owner decision: the words make a sentence). An empty template falls back to the default.
  - PAT with **Change…**, **Remove** (confirmed) and **Test connection** (server, token, project and repository).
- **PAT storage: Windows Credential Manager** through `PasswordVault`, one entry for the app, readable only by the current Windows user. Never written under `data/`, never logged, exported or backed up. This is an **exception to the rule that app-owned data stays under `data/`** (ADR 0003 portable layout): a copied portable folder asks for the PAT again, and the entry survives deleting the folder (hence **Remove**). If `PasswordVault` does not work in this unpackaged, self-contained app, the implementing agent returns to the owner with findings instead of choosing a fallback.
- **Entry points.** "Create pull request…" just above Delete… in the More menus of the task, CR and Subtask card editors, the Board card menu, and the Grid row menu. Disabled only for a never-saved record. Enabled for archived tasks and when editing is blocked, because nothing is written. A Subtask card uses its parent's CR and task numbers. Dialogs never stack: from an editor the item is a follow-up like Copy…, so the editor closes first (dirty-close confirmation if needed) and the dialog uses the **saved** values.
- **Dialog.** An in-app dialog built from `EditorDialogShell` (not a separate window). Fields: source branch (default the task number, empty if none), target branch (default setting), title (from the template; rebuilt when source or target changes until the user edits it), auto-complete checkbox (default setting). Branches are typed, not picked from a server list. Git branch-name validation inline.
- **Flow** (api-version 6.0, Basic authentication with an empty user name and the PAT; 30 s timeout per request):
  1. `GET {collection}/_apis/connectionData` gives the user id (needed for auto-complete).
  2. Both branches exist: `GET .../git/repositories/{repo}/refs?filter=heads/{name}`.
  3. The source has commits the target lacks: `GET .../diffs/commits` with `baseVersion={target}`, `targetVersion={source}`, both `branch`, `diffCommonCommit=true`, `$top=1`. If `aheadCount` is 0 the app **does not create the PR**, with no override: the server would accept an empty PR (confirmed), but the owner does not want one.
  4. `POST .../pullrequests` with `sourceRefName`, `targetRefName`, `title`.
  5. If auto-complete is ticked, `PATCH .../pullrequests/{id}` with `autoCompleteSetBy: {id}` and `completionOptions: {deleteSourceBranch: false}` (a separate request, as in the pipeline). A failure here still reports the created PR.
- **Results** (in the dialog; full wording in `design.md` §5.11).
  - Success: "Pull request #{id} created.", where #{id} links to `{collection}/{project}/_git/{repo}/pullrequest/{id}`. The full address is not shown.
  - 409 with `typeKey` `GitPullRequestExistsException` (confirmed: `TF401179`): already exists, with **Open existing PR**, which finds the active PR by source and target. Matching uses the status code and `typeKey`, never the message wording.
  - 401/403 and incomplete setup: an **Open Settings** button closes the dialog and opens Settings with the Azure DevOps expander expanded (no editor sits behind the dialog).
  - Missing setup, missing branch, no new commits, network or server errors.
  - POST is never retried automatically; **Try again** appears only when nothing was created.
  - When the POST may have reached the server without a readable answer (timeout, connection lost after it was sent, or an unreadable 2xx), the outcome is unknown: the dialog says the pull request may exist and offers **Open existing PR** and **Try again**. Try again first looks for an active PR from the source to the target and reports "already exists" instead of posting again when it finds one.
- **Layering.** Application owns the ports (pull request client, token store), the title formatter (sharing the Copy ID placeholder core) and the pure rules (branch names, URLs, setup completeness). The HTTP client is implemented in Infrastructure (`net10.0`). The `PasswordVault` token store is implemented in **Desktop**, the only layer with WinRT platform adapters (like `ShellUrlLauncher`); Infrastructure keeps its plain target. The settings are a nested `AzureDevOps` slice of `UiPreferences`, so a restore of default UI preferences keeps them. Presentation sees only the ports. This is not a dataset write, so it does **not** go through `ICommandRunner` (ADR 0029) and does not use the save indicator or the editing-blocked check. Unexpected failures still follow ADR 0029 reporting.

## Consequences
- The app makes its first outbound network call, only on a user action.
- `AGENTS.md`'s `data/` rule gains a pointer to this exception.
- The test script `scripts/ado-pr-test.py` (not shipped) confirmed the server's behavior: 409 `GitPullRequestExistsException` for a duplicate; an empty PR is accepted; auto-complete via PATCH returns 200.
- Transfer parity (ADR 0026): nothing to change; no records or dataset configuration are added.
