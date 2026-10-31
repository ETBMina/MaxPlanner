# Development prerequisites and commands

These are **developer** prerequisites. End users need only Windows 11 and the portable ZIP (T-03); see `deployment-guide.md` once it exists.

## Toolchain (recorded 2026-10-22)
| Tool | Version | Notes |
|---|---|---|
| Windows | 11 Pro 25H2, build 26200 | Development machine |
| .NET SDK | 10.0.401 (runtime 10.0.12) | Pinned in `global.json` (`latestPatch`). The owner chose not to upgrade (ADR 0002). |
| WinApp CLI | 0.7.1 | Upgraded from 0.5.0 with the `winui:winui-setup` skill (`winget upgrade --id Microsoft.WinAppCli`) |
| WinUI template pack | 0.0.6-alpha | Installed on demand by `winapp new`; used only for a reference scaffold outside the repo |
| Developer Mode | Enabled | Not strictly required for the unpackaged dev loop |
| Visual Studio | Optional | If it is open on the old solution, it restores the deleted `MaxPlanner/` and `MaxPlanner (Package)/` projects. Open `MaxPlanner.slnx` instead. |

Package versions are pinned in `Directory.Packages.props` (ADR 0002). Lock files (`packages.lock.json`) are committed.

## Scripts (PowerShell 7)
```powershell
./scripts/build.ps1                     # restore + build (Debug, x64); warnings are errors
./scripts/build.ps1 -Configuration Release -Locked
./scripts/test.ps1                      # all test projects except the smoke tests, via Microsoft Testing Platform; TRX in artifacts/test-results
./scripts/test.ps1 -IncludeSmoke        # also the Desktop smoke tests, which launch the app
./scripts/publish-portable.ps1 -Version 0.1.0   # self-contained win-x64 ZIP + SHA-256 in artifacts/
./scripts/verify-portable.ps1 -Zip artifacts/MaxPlanner-0.1.0-win-x64.zip   # T-03/T-11.6 release gates (-Gates to select)
./scripts/run-performance.ps1                   # T-11 fixture + BenchmarkDotNet suite + app runs on the published ZIP
./tools/Generate-Notices.ps1                    # regenerate THIRD-PARTY-NOTICES.txt after a dependency change
dotnet run --project tools/MaxPlanner.FixtureGenerator -p:Platform=x64 -- benchmark <app-folder> [--scale 0.1]
```
End-user prerequisites and the release procedure are in [deployment-guide.md](deployment-guide.md).

## Running the app during development
```powershell
winapp run src/MaxPlanner.Desktop/MaxPlanner.Desktop.csproj -p Platform=x64 --detach --json
```
WinApp CLI 0.7.1 project mode builds and launches the **unpackaged** app directly and prints its PID. The app is unpackaged by requirement (T-03, ADR 0003), not as a workaround. A developer launch does not prove portability; use the published ZIP checks.

## WinApp CLI commands used by the skills
| Command | Purpose |
|---|---|
| `winapp new --list` | Lists templates: winui, winui-lib, winui-mvvm, winui-navview, winui-tabview, winui-unittest (pack 0.0.6-alpha) |
| `winapp find-ui "<query>"` / `--id <id>` | WinUI Gallery / Toolkit sample lookup before writing XAML |
| `winapp find-api <query> --project-dir src/MaxPlanner.Presentation` | Confirms an API exists in the referenced metadata; sub-verbs `members`, `check-property`, `enums` |
| `winapp ui list-windows -a <pid>` / `inspect` / `screenshot` / `invoke` / `send-keys` / `drag` | UI Automation for verification and evidence |
| `--on sandbox` | Runs a command in the WinApp-managed Windows Sandbox (clean-environment checks) |

## Evidence
Evidence goes in `docs/evidence/<phase>/<date>/`. The first scaffold launch is `phase-0/2026-10-22/scaffold-first-launch.png`.
