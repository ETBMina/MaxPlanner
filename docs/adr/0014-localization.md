# ADR 0014: English-only resw localization

- **Status:** Accepted (owner decision, 2026-10-22). The class-library resource lookup is verified by spike 6.
- **Requirements:** T-08

## Decision
- Release 1 ships en-US only.
- Every user-visible string, including accessible names, lives in `src/MaxPlanner.Presentation/Strings/en-US/Resources.resw`. XAML uses `x:Uid`; code uses the Windows App SDK `ResourceLoader`.
- Visible dates and numbers are formatted with the current culture. Serialization uses invariant formats.

## Spike 6 result (2026-10-22)
`x:Uid` resources in the Presentation class library resolve in the published unpackaged build: `Shell_StartupText` showed "Opening your planner…".

- **Page and control strings** live in `src/MaxPlanner.Presentation/Strings/en-US/Resources.resw`.
- **Desktop-level strings** (window title and errors shown before XAML starts) live in `src/MaxPlanner.Desktop/Strings/en-US/Resources.resw`.
- No qualified Uids are needed.

## Consequences
Adding a language later is a translation and resource task only.
