# ADR 0016: Test stack

- **Status:** Accepted
- **Requirements:** T-11

## Decision
| Area | Choice |
|---|---|
| Framework | xUnit v3 on the Microsoft Testing Platform |
| Assertions | AwesomeAssertions (Apache-2.0). FluentAssertions 8+ is commercially licensed and is not used. |
| Architecture | TngTech.ArchUnitNET core, plus a project-graph test that parses the `.csproj` files |
| Time | `FakeTimeProvider` |
| Persistence | Real temporary SQLite files |
| Benchmarks | BenchmarkDotNet |
| UI | WinApp CLI `winapp ui` batches through `winui-ui-testing` / `winappcli:winapp` against an isolated fixture dataset; persistence is verified by relaunching the app |

Tests are tagged with requirement IDs (`[Trait("Req", "B-04")]`) to generate `docs/requirements-traceability.md`.

## Spike 9 result (2026-10-22)
`tests/MaxPlanner.Presentation.Tests` imports `eng/WinUI.Common.props` and is self-contained, so it can reference the WinUI Presentation library without an installed Windows App Runtime. ViewModel tests run in-process with no `DispatcherQueue`, XAML Application or WinRT activation, so no sixth "Presentation.Core" project is needed.

The rule that follows: ViewModels depend on `IUiDispatcher`, never directly on `DispatcherQueue`.
