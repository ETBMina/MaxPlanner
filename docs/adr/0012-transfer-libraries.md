# ADR 0012: Transfer libraries

- **Status:** Accepted for implementation (Phase 13, 2026-10-23): the four pins restore and build on .NET 10 / win-x64 with a locked restore and publish. The memory, fixture and license checks below are still open (Phase 19 / Phase 18 notices).
- **Requirements:** T-02, T-10, B-12, B-13

## Decision (proposed)
| Need | Primary | Fallback | License |
|---|---|---|---|
| CSV | Sep 0.17.1 | CsvHelper 33.x | MIT / MS-PL or Apache-2.0 |
| XLSX | DocumentFormat.OpenXml 3.5.1, streaming SAX reader/writer | ClosedXML 0.105.x with SixLabors.Fonts pinned to an Apache-2.0 version | MIT |
| Markdown | Markdig 1.4.2 | — | BSD-2-Clause |
| YAML front matter | YamlDotNet 18.1.0 | — | MIT |
| JSON | System.Text.Json source generation | — | MIT (BCL) |

**Why these.** OpenXml SAX gives low memory use, explicit text cells (leading zeros survive), detection of the `date1904` date system, and rejection of formulas without ever evaluating them.

**Rejected.** MiniExcel was rejected for weaker cell-type control.

## Verification
- Fixtures for each format, including malformed files, formula-like text, 1900/1904 dates and zip bombs.
- Memory measured with the 50,000-record limit.

## Implementation (Phases 13–14, 2026-10-23)
- **Packages** (Central Package Management, referenced only by `MaxPlanner.Infrastructure`): Sep 0.17.1, DocumentFormat.OpenXml 3.5.1, Markdig 1.4.2, YamlDotNet 18.1.0. New transitive packages: csFastFloat 4.1.6 (Sep), DocumentFormat.OpenXml.Framework 3.5.1 and System.IO.Packaging 10.0.2 (OpenXml). All four direct packages and the transitive ones must appear in the Phase 18 third-party notices (the repository has no notices file yet).
- **JSON:** `Infrastructure/Transfer/TransferJson.cs` source-generates the Application DTOs (`Application/Transfer/TransferDocument.cs`); the format marker and version are checked on a `JsonDocument` probe (max depth 64) before binding. Schema: `docs/schemas/transfer-v1.schema.json`; populated fixture: `docs/samples/transfer-v1-sample.json`.
- **XLSX:** `XlsxWorkbook.cs` writes with `OpenXmlWriter` (inline strings for text and IDs, a `yyyy-mm-dd` date style, numeric and boolean cells) and reads with `OpenXmlReader` row by row. `date1904` is honored; any cell formula is rejected with its location; date cells with a time of day are rejected; the ZIP is checked first (entries, declared size, names).
- **CSV:** `CsvTable.cs` writes with the Sep writer (UTF-8 BOM, escaping, explicit header) and reads with the Sep reader (`Unescape`, auto-detected separator for external files). Formula-leading text cells get a leading apostrophe on export only. Strict UTF-8 decoding.
- **Markdown/YAML:** `MarkdownRecords.cs` uses Markdig (`UseYamlFrontMatter`, `UseTaskLists`) and the YamlDotNet representation model; only scalars and scalar lists are read, nested mappings are skipped (no alias expansion).
