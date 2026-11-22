# ADR 0032: License Max Planner under PolyForm Internal Use 1.0.0

- **Status:** Accepted (owner decision, Nov 2026)

## Context
Max Planner is released free to the public as a portable ZIP. The owner wants the source to be readable, redistribution not allowed, private modification allowed, free use at work for now, and the right to sell or relicense later. Open-source licenses (MIT, Apache 2.0, GPL) allow redistribution and were rejected. Among ready-made licenses, PolyForm Internal Use 1.0.0 is the closest fit.

## Decision
- The repository is licensed under **PolyForm Internal Use License 1.0.0**, text unchanged in `LICENSE`, with the line `Required Notice: Copyright (c) 2026 Mina Talaat Hakim` above it.
- The license allows use for the internal business operations of the user and their company, and changes for that use. It forbids distributing the software.
- The owner keeps copyright and may offer other terms to anyone, including future versions (the license's "No Other Rights" section).
- Outside contributions are not accepted (`CONTRIBUTING.md`), so no CLA is needed and the owner holds all rights.

## Known gap
The only permitted purpose is internal business use. Purely personal use by an individual is not clearly covered. If that matters, the owner can add a plain statement of intent to the README or move to a custom license.

## Follow-ups
- Ship `LICENSE` and a third-party notices file in the portable ZIP (publish script and `verify-portable.ps1`), and show the license in Settings › About.
- Collect license notices for the bundled dependencies (SQLite, Windows App SDK, .NET and others).
