# ADR 0025: Stored text uses LF line breaks

- **Status:** Accepted (owner decision, Nov 2026)
- **Requirements:** B-12, T-05, T-10. Line breaks in "preserve multiline text" now mean LF, not the exact CR/LF bytes that were entered.

## Context
WinUI `TextBox` returns line breaks as a bare carriage return (`\r`), and nothing normalized them, so stored notes held `\r`, `\r\n`, or `\n` depending on how they were entered. The XLSX writer emitted every line break as CR LF inside `<t>`, and XML end-of-line handling read each one back as `\n`. As a result, notes changed on an XLSX round trip, and merging the app's own export planned spurious note updates. Writing the carriage return exactly (`_x000D_` or `&#xD;`) would make the round trip exact, but Excel treats only LF (CHAR(10)) as a line break, so every multi-line note typed in the app would then show no line breaks in Excel.

## Options
1. Write `\r` exactly in XLSX. The round trip is exact, but notes lose their line breaks in Excel.
2. **Store LF only.** Normalize line breaks in the app and migrate existing data. XLSX then writes `\n`, which Excel shows as a line break.
3. Keep the lossy XLSX behavior and document that JSON is the only exact format.

## Decision
Option 2.
- **Domain:** `LineBreaks.Normalize` turns `\r\n` and a bare `\r` into `\n`. The multi-line value factories apply it: `LongText` (task and Subtask card notes, CR description, review comments), `ChecklistText` (checklist items and General to-dos), and `MultilineTextCustomValue`. Every route (editor, Grid, every import format, copy, loading from SQLite) therefore yields the same stored form. Single-line values keep their existing rules.
- **Editors:** the task, CR, Subtask card, review and custom-field editors and the Grid notes draft compare normalized text for dirty tracking. The bound text is not rewritten, so caret, selection and drafts are unaffected (ADR 0010). A note that has only been reloaded into a `TextBox`, which reports `\r`, is not dirty.
- **Storage:** migration `M0003_lf_line_breaks` (schema version 3) rewrites `\r\n` and `\r` to `\n` in `tasks.notes`, `subtask_cards.notes`, `change_requests.description`, `review_items.comments`, `checklist_items.text` and `general_todos.text`. Multi-line custom values in `custom_values_json` and field defaults in `default_value_json` are parsed and re-serialized rather than text-replaced, so escaped backslashes are safe. Only rows that contain a carriage return are written. Revisions and audit timestamps are unchanged, as in M0002. Preserved metadata (`preserved_json`: PreLinkTitle, unselected CR references, LegacyJson) and the operation ledger are left as received, because they record source content rather than editable text.
- **XLSX:** text cells use the OOXML `_xHHHH_` escapes in both directions. The reader decodes them, so an Excel-authored `_x000D_` imports and then normalizes to LF. The writer escapes every underscore that starts escape-like text as `_x005F_`, and writes characters XML 1.0 cannot hold (C0 controls other than tab, LF and CR, unpaired surrogates, U+FFFE, U+FFFF) as `_xHHHH_`, as Excel does. The reader decodes surrogates only as a pair and leaves escapes for U+0000, U+FFFE, U+FFFF and lone surrogates as literal text, so those few characters read back as their escape text. `\n` is serialized inside `<t>` and read back as `\n` by Excel and by the app's reader.
- **JSON and CSV:** the formats are unchanged. Imported CR and CRLF text is normalized.

## Consequences
- XLSX, CSV, and JSON round trips of multi-line business text are exact, and merging the app's own export plans no updates.
- Multi-line notes keep their line breaks in Excel (visible in the formula bar, and in the cell once Wrap Text is on).
- Text that deliberately contained a carriage return without a line feed becomes a line break. Backups taken before the upgrade keep the original bytes.
- Exports contain LF line breaks. Tools that expect CRLF see LF.
- Workbooks exported by earlier versions that contain literal `_xHHHH_` text now import with that text decoded.
