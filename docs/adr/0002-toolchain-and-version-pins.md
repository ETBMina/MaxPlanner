# ADR 0002: Toolchain and version pins

- **Status:** Accepted (2026-10-22). Each pin marked ⚠ is confirmed or replaced by a Phase 0 spike, and this ADR is updated.
- **Requirements:** T-02, T-11.8

## Context
T-02 requires exact pins through `global.json`, central package management and lock files. Versions were checked on NuGet and MS Learn on 2026-10-21.

## Decision
| Item | Pin | Notes |
|---|---|---|
| .NET SDK | 10.0.401 (`rollForward: latestPatch`, no prerelease) | The owner chose not to upgrade (runtime 10.0.12). Runtime 10.0.13 is a security release; adopting it before release is an owner decision. |
| Target framework | `net10.0-windows10.0.26100.0`, `TargetPlatformMinVersion` 10.0.22621.0 | See ADR 0019 |
| Windows App SDK 2.5.1 (Stable, serviced until 2027-04-29) | Component packages: WinUI 2.3.9, Foundation 2.3.12, InteractiveExperiences 2.1.9, Base 2.0.4 | Chosen over the metapackage in spike 1 (ADR 0003) |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | |
| Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer | 0.7.1 | Loaded as a NuGet analyzer in Presentation/Desktop, so plain `dotnet build` runs it |
| CommunityToolkit.Mvvm | 8.4.2 | |
| DynamicData / System.Reactive | 9.5.0 / 7.0.0 | ⚠ binary compatibility (transitive pin) |
| Microsoft.Data.Sqlite / SQLitePCLRaw.bundle_e_sqlite3 | 10.0.13 / 2.1.13 | The pair Microsoft.Data.Sqlite declares; SQLitePCLRaw 3.x is not adopted (ADR 0009) |
| WinUI.TableView | 1.5.1 | ⚠ Grid gate; see ADR 0004 |
| CommunityToolkit.WinUI.Controls.SettingsControls | 8.2.251219 | Rejected (ADR 0021); pinned only for `spikes/PlatformControls` |
| DynamicData / System.Reactive compatibility | — | Verified together at runtime in spike 5 (ADR 0010) |
| Sep / DocumentFormat.OpenXml / Markdig / YamlDotNet | 0.17.1 / 3.5.1 / 1.4.2 / 18.1.0 | See ADR 0012 |
| Microsoft.Extensions.DependencyInjection | 10.x | No Generic Host |
| Serilog / Serilog.Sinks.File / Serilog.Extensions.Logging | 4.4.0 / 7.0.0 / 10.0.0 | |
| Tests | xunit.v3 4.0.2, AwesomeAssertions 9.6.0, TngTech.ArchUnitNET 0.13.4, BenchmarkDotNet 0.15.8 | See ADR 0016 |

**Developer tooling:** WinApp CLI was upgraded from 0.5.0 to 0.7.1 through the `winui:winui-setup` skill on 2026-10-22. Developer Mode was already enabled. The template pack seen by `winapp new --list` is 0.0.6-alpha. The scaffold route is to generate a reference app in a scratch folder only, then hand-author the five projects. No template is run over the repository.

## Consequences
- Upgrades are deliberate edits to `Directory.Packages.props` with this ADR updated.
- `RestoreLockedMode` is enabled in CI or locked builds.

## Verification
- `scripts/build.ps1 -Locked` restores with lock files and builds with warnings as errors.
- Spike evidence goes in `docs/evidence/phase-0/`.
