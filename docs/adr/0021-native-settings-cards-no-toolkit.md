# ADR 0021: Native settings cards; no Community Toolkit WinUI packages

- **Status:** Accepted (owner decision, 2026-10-22)
- **Requirements:** T-08, B-11, B-02.6, B-09
- **Evidence:** `docs/evidence/phase-0/platform-controls/` (spike 4)

## Context
Spike 4 tested `CommunityToolkit.WinUI.Controls.SettingsControls` 8.2.251219 on our Windows App SDK 2.5.1 component packages.
- It renders and switches Light/Dark correctly, and adds only about 480 KiB.
- **But every CommunityToolkit.WinUI 8.2 package depends on the `Microsoft.WindowsAppSDK` 1.6.250108002 metapackage.** Built as-is, the project fails (MSB4011 duplicate imports, MSIX build-tools error). The workaround is a direct, asset-excluded reference to that 1.6 metapackage in every WinUI project, which puts an outdated package in the lock files.
- Clickable `SettingsCard`s (`IsClickEnabled`) report as Buttons but have no working UIA Invoke, and non-clickable cards are unnamed groups.

## Options
1. Adopt the Toolkit with the excluded 1.6 metapackage workaround and accessibility guardrails.
2. **Native cards:** Presentation styles over `Grid` and `Expander`.

## Decision
Option 2, chosen by the owner. Release 1 references **no Community Toolkit WinUI packages**. CommunityToolkit.Mvvm, which has no Windows App SDK dependency, is unaffected.

| Toolkit control | Replacement |
|---|---|
| `SettingsCard` | A `SettingsCard` style: a `Grid` card with icon/header/description/content slot, Card brushes, Light/Dark/HighContrast resources, `AutomationProperties.Name` = header. Actions are explicit `Button`s, never a click-enabled card. |
| `SettingsExpander` | A styled native `Expander` holding the rows |
| `TokenizingTextBox` | The **TokenPicker** Presentation control (`AutoSuggestBox` + removable chip `ItemsRepeater`) |
| `StaggeredLayout` | `UniformGridLayout` |

## Consequences
- About 1–2 days of styling and control work in Phase 9 (Settings) and Phase 4 (TokenPicker).
- The Windows App SDK graph stays free of 1.x packages.
- Accessibility (names, Invoke, keyboard) is under our control and tested with the feature.
- The `CommunityToolkit.WinUI.Controls.SettingsControls` pin stays in `Directory.Packages.props` only so `spikes/PlatformControls` keeps building. No `src` project may reference it; a later architecture test can enforce that.
- A future Toolkit release that targets Windows App SDK 2.x could reopen this through a new ADR.

## Verification
Phase 9 UI tests cover each card's UIA Name and Invoke, keyboard traversal, Light/Dark/HighContrast, and the 1000-epx width cap at all breakpoints.
