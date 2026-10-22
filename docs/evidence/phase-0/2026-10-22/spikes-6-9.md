# Phase 0 evidence: spikes 6–9

**Environment:** the developer machine (Windows 11 Pro 25H2, build 26200, x64). .NET SDK 10.0.401. WinApp CLI 0.7.1. Each run used the published portable ZIP extracted under `%TEMP%`, in folders whose names contain a space and "ë", and was launched with the working directory set to `%TEMP%`. Only non-intrusive `winapp ui` commands were used (inspect, search, get-value, list-windows, invoke, screenshot). The computer-use skill was not used.

## Spike 6: `x:Uid` in the Presentation class library (ADR 0014)
- `src/MaxPlanner.Presentation/Shell/StartupView.xaml` uses `x:Uid="Shell_StartupText"`, and the string lives in `src/MaxPlanner.Presentation/Strings/en-US/Resources.resw`.
- In the published unpackaged build, `winapp ui get-value Shell_StartupText` returned "Opening your planner…", so the class library's resources resolve.
- Desktop-level strings (window title, pre-XAML error messages) stay in `src/MaxPlanner.Desktop/Strings/en-US/Resources.resw` and are read with `ResourceLoader`.
- **Result: PASS.** Page and control strings live with their views in Presentation.

## Spike 7: Mica and solid fallback (ADR 0008)
- `MainWindow.ApplyBackdrop` sets `MicaBackdrop` (Kind Base) when `MicaController.IsSupported()` returns true. Otherwise the root grid gets `SolidBackgroundFillColorBaseBrush`.
- On this machine Mica is supported and renders (screenshots `scaffold-first-launch.png`, `component-packages-portable-launch.png`).
- **Result: PARTIAL.** The supported path is verified. Not exercised:
  - The unsupported branch: every Windows 11 build in the support matrix supports Mica, so it is defensive code.
  - Platform fallbacks you must check by hand: transparency effects off, Remote Desktop, and battery saver.

## Spike 8: single instance and dataset lock (ADR 0013)
Code: `src/MaxPlanner.Desktop/Program.cs`, `NativeMethods.cs`, `src/MaxPlanner.Infrastructure/Storage/DatasetLock.cs` and `DataDirectoryProbe.cs`. Tests: `DatasetLockTests` and `DataDirectoryProbeTests`, run against real files.

| Scenario | Observed | Result |
|---|---|---|
| First launch | Window shown; `data/temp/` and `data/.lock` created beside the EXE | PASS |
| Second launch, same folder | Exit code 0 within 15 s, still one process, first window in the foreground (`AppInstance` redirect + `AllowSetForegroundWindow`) | PASS |
| Second portable copy in another folder | Runs as its own instance (different instance key and lock) | PASS |
| Lock held by a non-cooperating process | Native error box: "This Max Planner folder is already open in another window…"; it stays open until OK, then exit code 3 (`spike8-dataset-locked.png`) | PASS |
| Data location not writable (a file named `data`) | Error box with the data path and instructions to move the folder; exit code 2; no database opened, no AppData fallback | PASS |
| Redirect to an owner that is shutting down | **Defect found and fixed:** the app exited with code 3 without telling the user. It now shows the "already open" message before exiting. | Fixed |

Harness note: `winapp ui screenshot` against the native error box caused that box to close. Untouched, the box stays open for more than 15 s. This is a test-harness interaction, not app behavior.

## Spike 9: Presentation.Tests without a XAML runtime (ADR 0016)
- `tests/MaxPlanner.Presentation.Tests` imports the WinUI props to reference the WinUI Presentation library, and runs self-contained (no installed Windows App Runtime needed).
- 5 `ShellViewModel` tests run in-process with no `DispatcherQueue`, XAML Application or WinRT activation.
- **Result: PASS.** No sixth "Presentation.Core" project is needed.

## Totals after spikes 6–9
`scripts/build.ps1`: 0 warnings, 0 errors. `scripts/test.ps1`: 33/33 passed.
