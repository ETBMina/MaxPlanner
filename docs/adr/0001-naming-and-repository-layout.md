# ADR 0001: Product naming and repository layout

- **Status:** Accepted (2026-10-22)
- **Requirements:** T-03, T-04

## Context
The original specification used the working name "Portable Kanban". The repository held a Visual Studio template: a net8 WinUI project plus an MSIX packaging project (`.wapproj`) targeting x86/x64/ARM64 with Release trimming. Each of those conflicts with T-02/T-03.

## Options
1. Convert the template in place.
2. Replace it with the T-04 five-project layout.

## Decision
The product is named **Max Planner**. The template and packaging project are deleted. The new layout is:

- `src/MaxPlanner.{Domain,Application,Infrastructure,Presentation,Desktop}`
- `tests/MaxPlanner.*.Tests`
- `tools/`, `scripts/`, `docs/`

The executable is `MaxPlanner.exe` (Desktop `AssemblyName=MaxPlanner`), and the portable ZIP root folder is `MaxPlanner/`.

## Consequences
- The solution builds for x64 only.
- Converting in place would have dragged in packaged-app defaults, so it was rejected.

## Verification
Architecture tests enforce the project graph. The publish script asserts the EXE name and ZIP layout.
