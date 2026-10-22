# ADR 0008: Mica main window, Acrylic on transient surfaces

- **Status:** Accepted (owner decision, 2026-10-22). Behavior is verified by Phase 0 spike 7.
- **Requirements:** T-08, AC-24, AC-27

## Context
The earlier spec required DesktopAcrylic on the main window. Windows 11 Fluent 2 guidance uses Mica for long-lived app windows and Acrylic for transient, light-dismiss surfaces.

## Decision
- **Main window:** `Window.SystemBackdrop = new MicaBackdrop()` when `MicaController.IsSupported()` is true. Otherwise there is no backdrop and the root uses `SolidBackgroundFillColorBaseBrush`.
- **Content surfaces:** Card and Layer theme brushes with stroke borders, so hierarchy never depends on the material.
- **Transient surfaces:** Flyouts and menus use the platform's native Acrylic where the API supports it (confirmed with `winapp find-api`). `ContentDialog` keeps its native surface. No simulated materials.
- **Fallback:** when transparency is off, over Remote Desktop, or in power-saving conditions, the platform's own solid fallback applies.

## Verification
- **Spike 7 (2026-10-22):**
  - Runtime selection is implemented in `MainWindow.ApplyBackdrop`, and the Mica path is verified on build 26200 (screenshots in `docs/evidence/phase-0/2026-10-22/`).
  - The unsupported branch is defensive code; no Windows 11 build in the support matrix lacks Mica.
- **Still manual** (you perform these where Windows settings are involved): transparency effects off, Remote Desktop, battery saver, Light/Dark/High Contrast screenshots.
