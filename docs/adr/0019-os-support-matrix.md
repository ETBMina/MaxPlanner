# ADR 0019: Windows support matrix

- **Status:** Accepted
- **Requirements:** T-02

## Context
The earlier spec set the compatibility floor at build 22000 (21H2), which is out of servicing. The development machine runs build 26200 (25H2).

## Decision
| Level | Builds |
|---|---|
| Tested (supported) | Windows 11 24H2 (26100) and 25H2 (26200), x64 |
| Untested-compatible floor | 22H2 (22621), set through `TargetPlatformMinVersion`/`SupportedOSPlatformVersion` 10.0.22621.0 |
| Below 22621 | Unsupported |

Windows editions tested (Pro, Enterprise) are listed in the release evidence.

## Verification
The release evidence lists the OS edition/build for each test run.
