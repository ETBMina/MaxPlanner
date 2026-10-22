# ADR 0012: Transfer libraries

- **Status:** Proposed. Confirmed by a compatibility, memory and license spike before Phase 13.
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
