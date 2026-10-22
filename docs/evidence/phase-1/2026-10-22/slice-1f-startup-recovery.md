# Slice 1f evidence: startup, recovery, preferences, logging, daily backup (2026-10-22)

Machine: Windows 11 Pro 10.0.26200, x64, 150% display scale (2560x1600 primary), .NET SDK 10.0.401, WinApp CLI 0.7.1.
All runs used disposable copies of the build output under `%TEMP%\MaxPlanner Tëst …` (space and "ë"); no user data.

## How the UI was driven
Non-intrusive `winapp ui` only, against the launched process's own top-level window (PID and HWND confirmed with
`list-windows` first): `wait-for`, `search`, `get-value`, `get-focused`, `focus`, `invoke`, `screenshot`.
No click, drag, hover, touch, or `send-input`. Day-boundary messages were posted only to the app's own HWND
(`PostMessage WM_TIMECHANGE`, `SendMessageTimeout WM_SETTINGCHANGE "intl"`). The app was closed with
`Process.CloseMainWindow` (WM_CLOSE to its own window) or the page's Exit button through UIA Invoke.
Results: `ui-evidence-results-run1.json` (all cases), `-run2-theme.json`, `-run3-responsive.json`.

| # | Screenshot | State | Checked |
|---|---|---|---|
| 01 | `01-first-run-ready-system-theme.png` | First run, ready (System = Dark on this machine, Mica) | `Shell_ReadyTitle`, counts 0/0/0; data.db, daily backup created; config written at close; no `-wal` after close |
| 02 | `02-recovery-corrupt.png` | Garbage `data.db` | Title/message; only Open data folder + Exit; Exit focusable; Exit → exit code 5; file unchanged |
| 03–05 | `03-recovery-missing.png`, `04-…-confirm.png`, `05-recovery-created-ready.png` | `data.db` missing, `backups/` has a manual backup | Create offer; Begin → confirmation InfoBar and focus on `Recovery_ConfirmCreateButton`; no file before confirm; Cancel creates nothing; Confirm → ready; manual backup untouched; exit 0 |
| 06 | `06-recovery-newer-schema.png` | `user_version = 99` | Warning page names 99 and 1; close → exit 4 |
| 07 | `07-recovery-interrupted-restore.png` | `restore.pending.json` present | Dataset not opened; exit 5 |
| 08/09 | `08-ready-light.png`, `09-ready-dark.png` | Saved theme Light / Dark | Applied at startup to the whole window incl. title bar; persisted value in `configuration.json` |
| 10 | `10-recovery-corrupt-500epx.png` | Recovery at 500x700 epx (saved bounds 750x1050 px) | Small breakpoint stacks the actions; text wraps; nothing clipped |
| 11/12 | `11-recovery-missing-light.png`, `12-recovery-missing-dark.png` | Config present but no database, Light / Dark | Recovery (not first run) because the folder was used; Light/Dark on recovery |

Run 1 found that `AdaptiveTrigger` states inside `RecoveryView` did not follow the window at 500 epx (actions stayed on
one row with Large padding). Fixed by applying the breakpoint from the page width in code-behind; run 3 shows the fix.

## Day boundary
The log of the Dark run shows `Window message WM_TIMECHANGE received.` and `Window message WM_SETTINGCHANGE(intl) received.`
The date logic itself (midnight timer, time-zone change, resume after sleeping past midnight, DST) is covered by
`LocalDayBoundarySourceTests` with `FakeTimeProvider`. Resume from real sleep was not exercised (manual).

## Startup timing (log lines "Window shown" / "Startup ready", measured from `Program.Main` entry)
| Build | Launch | Window shown | Ready |
|---|---|---|---|
| Release publish, fresh copy | 1 (first run, creates dataset) | 1533 ms | 1970 ms |
| Release publish | 2–5 | 424–625 ms | 604–807 ms |
| Debug build, fresh copy | 1 (first run) | 2085 ms | 2617 ms |

Empty dataset only; the T-11 fixture benchmark (10k tasks …) is still open. Time before `Main` (runtime host start) is not included.

## Still manual
Keyboard Tab traversal (posted keys are ignored by XAML controls; only `focus`/`get-focused` were checked), Narrator,
High Contrast, live Windows theme change while System is selected, resume from real sleep, real time-zone change,
mixed-DPI monitor moves and the 500-epx window minimum (needs pointer resizing), the resync InfoBar (needs a fault
injection that the release build does not expose), and Open data folder (opens Explorer; not invoked).
