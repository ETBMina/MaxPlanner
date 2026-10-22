# ADR 0013: Single instance and dataset lock

- **Status:** Accepted (2026-10-22). Spike 8 verified unpackaged `AppInstance` redirection and the lock (`docs/evidence/phase-0/2026-10-22/spikes-6-9.md`).
- **Requirements:** T-06.5

## Decision
**Activation redirect (best effort).** In `Program.Main`:
- Compute a key from the full path of `data/`.
- Call `AppInstance.FindOrRegisterForKey(key)`.
- If another instance owns the key, redirect activation to it, allow it to take the foreground, and exit with code 0.

Two separate portable copies have different keys, so each opens its own dataset.

**Guarantee: the lock file.** The process opens `data/.lock` with `FileShare.None` for its whole lifetime, and Windows releases it if the process crashes. If the lock is unavailable, the app reports "This Max Planner folder is already open" and exits with code 3.

**When redirection fails** (for example, the owner is shutting down or hung), the launch shows the same "already open" message and exits with code 3. It never exits silently; spike 8 found and fixed this.

**Write probe.** Before the lock, `DataDirectoryProbe` checks that `data/` is writable. On failure the app explains how to move the folder and exits with code 2.

## Verification
- `DatasetLockTests` and `DataDirectoryProbeTests` run against real files.
- Spike 8 published-ZIP runs covered: second launch redirect (exit 0, one process), a separate copy running independently, a non-cooperating lock holder (message, exit 3), and a non-writable data location (message, exit 2).
- Automated process-level smoke tests in `MaxPlanner.Desktop.SmokeTests` arrive in Phase 1.
