# ADR 0003: Portable unpackaged self-contained deployment

- **Status:** Proposed. Phase 0 spike 1 must prove it on a clean offline machine.
- **Requirements:** T-03, AC-01, AC-02

## Context
The app must launch from an extracted ZIP with no installer, no package registration, no admin rights and no separately installed runtimes.

## Decision
The Desktop project sets:
- `WindowsPackageType=None`, `SelfContained=true`, `WindowsAppSDKSelfContained=true`, `RuntimeIdentifier=win-x64`
- `PublishSingleFile`, `PublishTrimmed`, `PublishAot` and (initially) `PublishReadyToRun` all `false`

A `Directory.Build.targets` guard fails the build if trimming, AOT or single-file is enabled.

**Publishing:** `scripts/publish-portable.ps1` runs `dotnet publish`, adds notices and `SHA256SUMS.txt`, and zips with the root folder `MaxPlanner/`. It asserts that no `data/` folder is shipped.

**Exit codes:** 0 ok or redirected, 2 data folder not writable, 3 dataset locked, 4 newer schema, 5 startup failure.

## Spike 1 results (2026-10-22, developer machine)
- **Component packages adopted** instead of the metapackage: `Microsoft.WindowsAppSDK.WinUI` 2.3.9, `Foundation` 2.3.12, `InteractiveExperiences` 2.1.9 and `Base` 2.0.4, the 2.5.1 release set. This removes unused AI/ML/Search/Widgets payload: 236 → 176 MB unpacked, 93.7 → 69.4 MB zipped.
- The self-contained unpackaged launch works with components only. The published EXE was launched from a path with a space and "ë" and an unrelated working directory, and showed a responsive window with resources resolved.
- `e_sqlite3.dll` ships in the output.
- Evidence: `docs/evidence/phase-0/2026-10-22/spikes-1-2.md`.

## Still open
- A clean offline machine (Windows Sandbox / VM) launch.
- Whether the VC++ runtime must be shipped app-locally (found by the clean-machine run).

## Consequences
MSIX is not produced. The WinApp CLI packaging/signing skills are out of scope unless the owner asks for an extra channel.

## Verification
- `scripts/verify-portable.ps1`.
- Sandbox evidence (interim).
- The Phase 18 clean offline VM gate.
