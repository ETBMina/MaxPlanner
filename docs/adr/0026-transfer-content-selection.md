# ADR 0026: Transfer content selection and configuration replace

- **Status:** Accepted (owner decision, Nov 2026)
- **Requirements:** B-04, B-12, T-10. Supersedes the B-12 rule "Import is additive/merge, never implicit replacement" for configuration only.

## Context
Export and import always moved configuration and business records together. The owner wants to move them separately: copy one dataset's workflow setup to another without its records, or move records between datasets that already share a setup.

## Decision
- **Content:** export and import offer **Configuration only**, **Data only**, or **Both** (default).
  - *Configuration* is the lookups (task statuses, CR statuses, priorities, squads, people, tags), the custom field definitions, and the business defaults (default task status, CR status, priority, CR Owner, calendar scheduling status, CR backlog status).
  - *Data* is tasks with their checklist items, Subtask cards, CRs, links, reviews, and General to-dos.
  - UI preferences are not part of either.
- **Export**
  - *Configuration only* writes a file with empty record collections. It is available for JSON, XLSX (definition sheets only), and the CSV bundle (`metadata.json` only). It is not available for a single CSV table.
  - *Data only* writes the records and only the lookups, field definitions, and options those records reference, so labels stay readable and an import can match values by name. It writes no defaults.
  - A Current view export is always data only.
- **Import:** the choice applies to Max Planner transfer files that carry definitions (JSON, XLSX, CSV bundle). A single CSV table and external sources (B-13) keep their existing behavior.
  - *Data only* never changes configuration. Each referenced value is matched by ID, then by normalized name. A value that matches nothing is an error, and the preview lists it. Apply is disabled until the value exists, for example after a configuration import.
  - *Configuration only* and *Both* **replace** the current configuration with the file's.
    - A file value is matched to an existing one by ID, then by normalized name or field key. A matched value is updated to the file's name, order, and flags, and keeps its ID, so records keep pointing to it.
    - A file value with no match is added.
    - An existing value that is not in the file is removed. If records or defaults use it, the wizard's Lookups step asks for a replacement from the file's values, following the B-04 removal rules: CR Owner and status/priority uses must be replaced; Designer, Developer, and review Developer may be cleared. Apply is disabled until every in-use removal is resolved.
    - Custom field definitions that are not in the file are deactivated, not deleted, so their values are kept (B-11). Removed options of a kept field that are in use are resolved like lookups.
    - Defaults are set to the file's.
  - In *Both*, the configuration is replaced first, then the data is imported with the chosen Create only / Merge mode.
  - Only a file with a complete configuration can replace one: an entire-dataset scope with defaults. A Current view export or a data-only export holds only the referenced subset. For such a file the wizard preselects *Data only*, and choosing *Configuration only* or *Both* is an error in the preview.
  - A replace needs an explicit confirmation in the wizard. It is one atomic transaction that publishes a full reload (ADR 0010). A safety backup is always taken first, even when the target has no records, because the configuration being replaced is data of its own.

## Consequences
- Configuration can now be replaced by an import as well as by Restore. Business records are still never deleted by an import.
- The import plan gains configuration removals and remappings. These are revalidated at commit like any other plan entry (T-10).
- The transfer format is unchanged (`formatVersion` 1). Files with empty record collections, or with partial lookups, were already valid.
