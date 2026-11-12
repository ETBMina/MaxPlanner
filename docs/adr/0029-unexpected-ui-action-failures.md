# ADR 0029: Unexpected failures of UI actions are logged and shown, and the app keeps running

- **Status:** Accepted (owner decision, Nov 2026)
- **Requirements:** AC-07 (a failed save keeps the draft), T-05 (idempotent retry). Arose from architecture review finding A-02.

## Context
Expected failures (validation, conflicts, storage errors) come back from use cases as typed errors and are shown by the editor or page that asked. This ADR is about the other kind: an exception nobody expected, thrown by an action the UI started and did not await.

Before review batch 1C, code-behind started such work with `_ = viewModel.XxxAsync()`. An exception was lost: nothing was logged, nothing was shown, and the app carried on, sometimes with an editor stuck in its "saving" state. Batch 1C replaced those calls with bound commands and a helper, which by default rethrow on the UI thread. `App.OnUnhandledException` logs and does not mark the exception handled, so the app would have closed and an unsaved draft would have been lost.

## Options
1. **Log the failure, show an error, keep running.**
2. Let the app close, with the cause in the log.
3. Keep losing the exception silently.

## Decision
Option 1.
- **One handler:** `MaxPlanner.Presentation.Common.UiTasks`. Async view-model commands run through `UiTasks.RunGuardedAsync`; work that code-behind must start itself (keys, drag and drop, menus built in code, row templates) ends with `.Forget()`. Both end in `UiTasks.Report`.
- **What the user sees:** the `Error_Unexpected` text in the save status with its InfoBar, and the same text as a screen-reader announcement. Open dialogs and drafts are left as they are.
- **What is recorded:** log event 4002 at Error, with the exception.
- **Not failures:** `OperationCanceledException` is ignored.
- **Never caught:** fatal exceptions (`OutOfMemoryException`), following the existing `catch (Exception ex) when (ex is not OutOfMemoryException)` pattern.
- **Editors and dialogs reset themselves first:** when a save throws, the editor ends its saving state, shows the text as its save error, keeps the draft, and can be closed. The retry reuses the same operation ID, because the outcome is unknown (T-05).
- **No workspace open** (startup, recovery): the failure is only logged.
- **`App.OnUnhandledException` is unchanged.** Exceptions that reach it (synchronous handler code, anything outside this handler) still end the app. The handler does not rely on it.

## Consequences
- An unexpected failure no longer costs the user an unsaved draft, and it is no longer invisible.
- The app may continue after a defect. The log entry is the signal to fix it; the handler is not a substitute for typed errors.
- `UiTasks` holds process-wide configuration (`Configure`, called once at launch). Tests that use it reset it and do not run in parallel with each other.
- Known limitation: if a save commits and the wait for it to be displayed then throws, the editor says the change is not saved although it is. The retry under the same operation ID is recognised by the ledger and does not write twice.
- New UI code starts unawaited work through a command or `.Forget()`, never with a bare discard.
