-- M0001: initial Max Planner schema (docs/schemas/sqlite-schema-v1.md).
-- Encodings (ADR 0009): IDs are 16-byte BLOBs in Guid.ToByteArray() order, business dates are
-- 'yyyy-MM-dd' TEXT, UTC timestamps are INTEGER .NET ticks, flexible values are JSON TEXT.
-- Runs inside the migration transaction; the C# step seeds the B-04 defaults and app_metadata.

CREATE TABLE task_statuses (
    id               BLOB    NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    name             TEXT    NOT NULL,
    name_norm        TEXT    NOT NULL UNIQUE,
    sort_order       INTEGER NOT NULL,
    is_completed     INTEGER NOT NULL CHECK (is_completed IN (0, 1)),
    sets_start_date  INTEGER NOT NULL CHECK (sets_start_date IN (0, 1)),
    is_backlog       INTEGER NOT NULL CHECK (is_backlog IN (0, 1))
) STRICT;

CREATE TABLE cr_statuses (
    id            BLOB    NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    name          TEXT    NOT NULL,
    name_norm     TEXT    NOT NULL UNIQUE,
    sort_order    INTEGER NOT NULL,
    is_completed  INTEGER NOT NULL CHECK (is_completed IN (0, 1)),
    is_backlog    INTEGER NOT NULL CHECK (is_backlog IN (0, 1))
) STRICT;

CREATE TABLE priorities (
    id          BLOB    NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    name        TEXT    NOT NULL,
    name_norm   TEXT    NOT NULL UNIQUE,
    sort_order  INTEGER NOT NULL
) STRICT;

CREATE TABLE squads (
    id          BLOB    NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    name        TEXT    NOT NULL,
    name_norm   TEXT    NOT NULL UNIQUE,
    sort_order  INTEGER NOT NULL
) STRICT;

CREATE TABLE people (
    id         BLOB NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    name       TEXT NOT NULL,
    name_norm  TEXT NOT NULL UNIQUE
) STRICT;

CREATE TABLE tags (
    id         BLOB NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    name       TEXT NOT NULL,
    name_norm  TEXT NOT NULL UNIQUE
) STRICT;

CREATE TABLE field_definitions (
    id                  BLOB    NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    target              TEXT    NOT NULL CHECK (target IN ('Task', 'ChangeRequest')),
    field_key           TEXT    NOT NULL,
    key_norm            TEXT    NOT NULL,
    label               TEXT    NOT NULL,
    kind                TEXT    NOT NULL CHECK (kind IN ('Text', 'MultilineText', 'Number', 'Date', 'Lookup', 'People', 'Tags', 'Url')),
    is_required         INTEGER NOT NULL CHECK (is_required IN (0, 1)),
    default_value_json  TEXT    NULL,
    display_order       INTEGER NOT NULL,
    options_json        TEXT    NOT NULL DEFAULT '[]',
    is_active           INTEGER NOT NULL CHECK (is_active IN (0, 1)),
    UNIQUE (target, key_norm)
) STRICT;

-- One row. Defaults reference their lookups, so a referenced status/priority/person cannot be removed
-- without first choosing a replacement (B-04).
CREATE TABLE app_metadata (
    id                             INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    schema_version                 INTEGER NOT NULL,
    dataset_id                     BLOB    NOT NULL CHECK (length(dataset_id) = 16),
    commit_sequence                INTEGER NOT NULL CHECK (commit_sequence >= 0),
    default_task_status_id         BLOB    NOT NULL REFERENCES task_statuses(id),
    default_cr_status_id           BLOB    NOT NULL REFERENCES cr_statuses(id),
    default_priority_id            BLOB    NOT NULL REFERENCES priorities(id),
    default_cr_owner_id            BLOB    NULL     REFERENCES people(id),
    calendar_scheduling_status_id  BLOB    NOT NULL REFERENCES cr_statuses(id),
    cr_backlog_status_id           BLOB    NOT NULL REFERENCES cr_statuses(id),
    migration_history_json         TEXT    NOT NULL
) STRICT;

CREATE TABLE tasks (
    id                  BLOB    NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    own_title           TEXT    NULL,
    service             TEXT    NULL,
    status_id           BLOB    NOT NULL REFERENCES task_statuses(id),
    priority_id         BLOB    NOT NULL REFERENCES priorities(id),
    start_date          TEXT    NULL CHECK (start_date IS NULL OR start_date GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'),
    end_date            TEXT    NULL CHECK (end_date IS NULL OR end_date GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'),
    notes               TEXT    NULL,
    is_archived         INTEGER NOT NULL CHECK (is_archived IN (0, 1)),
    rank                INTEGER NOT NULL,
    custom_values_json  TEXT    NULL,
    preserved_json      TEXT    NULL,
    created_at_utc      INTEGER NOT NULL,
    updated_at_utc      INTEGER NOT NULL,
    revision            INTEGER NOT NULL CHECK (revision >= 1),
    CHECK (start_date IS NULL OR end_date IS NULL OR start_date <= end_date)
) STRICT;
CREATE INDEX ix_tasks_status_archived ON tasks (status_id, is_archived);

-- Owned by the task: deleted with it.
CREATE TABLE checklist_items (
    id            BLOB    NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    task_id       BLOB    NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    text          TEXT    NOT NULL,
    is_completed  INTEGER NOT NULL CHECK (is_completed IN (0, 1)),
    rank          INTEGER NOT NULL
) STRICT;
CREATE INDEX ix_checklist_items_task ON checklist_items (task_id);

-- Owned by the task. position keeps the list order exactly as saved.
CREATE TABLE task_people (
    task_id    BLOB    NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    person_id  BLOB    NOT NULL REFERENCES people(id),
    role       TEXT    NOT NULL CHECK (role IN ('Designer', 'Developer')),
    position   INTEGER NOT NULL,
    PRIMARY KEY (task_id, role, person_id)
) STRICT;
CREATE INDEX ix_task_people_person ON task_people (person_id);

CREATE TABLE task_tags (
    task_id   BLOB    NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    tag_id    BLOB    NOT NULL REFERENCES tags(id),
    position  INTEGER NOT NULL,
    PRIMARY KEY (task_id, tag_id)
) STRICT;
CREATE INDEX ix_task_tags_tag ON task_tags (tag_id);

-- Not cascaded: deleting a task must delete its Subtask cards explicitly (B-05, B-16.7), so a task
-- delete that forgot them fails instead of silently dropping cards.
CREATE TABLE subtask_cards (
    id              BLOB    NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    parent_task_id  BLOB    NOT NULL REFERENCES tasks(id),
    subtitle        TEXT    NOT NULL,
    status_id       BLOB    NOT NULL REFERENCES task_statuses(id),
    start_date      TEXT    NULL CHECK (start_date IS NULL OR start_date GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'),
    end_date        TEXT    NULL CHECK (end_date IS NULL OR end_date GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'),
    notes           TEXT    NULL,
    rank            INTEGER NOT NULL,
    created_at_utc  INTEGER NOT NULL,
    updated_at_utc  INTEGER NOT NULL,
    revision        INTEGER NOT NULL CHECK (revision >= 1),
    CHECK (start_date IS NULL OR end_date IS NULL OR start_date <= end_date)
) STRICT;
CREATE INDEX ix_subtask_cards_parent ON subtask_cards (parent_task_id);
CREATE INDEX ix_subtask_cards_status ON subtask_cards (status_id);

CREATE TABLE change_requests (
    id                   BLOB    NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    number               TEXT    NOT NULL,
    number_norm          TEXT    NOT NULL UNIQUE,
    title                TEXT    NOT NULL,
    owner_id             BLOB    NOT NULL REFERENCES people(id),
    squad_id             BLOB    NULL     REFERENCES squads(id),
    status_id            BLOB    NOT NULL REFERENCES cr_statuses(id),
    priority_id          BLOB    NOT NULL REFERENCES priorities(id),
    planned_start        TEXT    NULL CHECK (planned_start IS NULL OR planned_start GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'),
    planned_end          TEXT    NULL CHECK (planned_end IS NULL OR planned_end GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'),
    email_subject        TEXT    NULL,
    solution_design_url  TEXT    NULL,
    description          TEXT    NULL,
    task_number          TEXT    NULL,
    custom_values_json   TEXT    NULL,
    preserved_json       TEXT    NULL,
    created_at_utc       INTEGER NOT NULL,
    updated_at_utc       INTEGER NOT NULL,
    revision             INTEGER NOT NULL CHECK (revision >= 1),
    -- B-04: unscheduled (both null) or an inclusive range with start <= end.
    CHECK ((planned_start IS NULL) = (planned_end IS NULL)),
    CHECK (planned_start IS NULL OR planned_start <= planned_end)
) STRICT;
CREATE INDEX ix_change_requests_planned ON change_requests (planned_start, planned_end);

-- T-05: the only authority for the CR/task relationship. The primary key allows at most one CR per
-- task; the non-unique cr_id index allows many tasks per CR.
CREATE TABLE cr_task_links (
    task_id        BLOB    NOT NULL PRIMARY KEY REFERENCES tasks(id),
    cr_id          BLOB    NOT NULL REFERENCES change_requests(id),
    linked_at_utc  INTEGER NOT NULL
) STRICT;
CREATE INDEX ix_cr_task_links_cr ON cr_task_links (cr_id);

-- ADR 0006: links are permanent. Rows are inserted once and removed only by task deletion or CR
-- cascade deletion; there is no unlink or reassign, so updating a row is always a defect.
CREATE TRIGGER tr_cr_task_links_permanent BEFORE UPDATE ON cr_task_links
BEGIN
    SELECT RAISE(ABORT, 'cr_task_links rows are permanent');
END;

CREATE TABLE review_items (
    id                BLOB    NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    cr_number_text    TEXT    NULL,
    title             TEXT    NOT NULL,
    task_number_text  TEXT    NULL,
    developer_id      BLOB    NULL REFERENCES people(id),
    comments          TEXT    NULL,
    is_done           INTEGER NOT NULL CHECK (is_done IN (0, 1)),
    completed_on      TEXT    NULL CHECK (completed_on IS NULL OR completed_on GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'),
    rank              INTEGER NOT NULL,
    created_at_utc    INTEGER NOT NULL,
    updated_at_utc    INTEGER NOT NULL,
    revision          INTEGER NOT NULL CHECK (revision >= 1),
    -- B-17.1: done items have a completion day, open ones do not.
    CHECK (is_done = (completed_on IS NOT NULL))
) STRICT;

CREATE TABLE review_services (
    review_id  BLOB    NOT NULL REFERENCES review_items(id) ON DELETE CASCADE,
    position   INTEGER NOT NULL,
    service    TEXT    NOT NULL,
    PRIMARY KEY (review_id, position)
) STRICT;

CREATE TABLE general_todos (
    id              BLOB    NOT NULL PRIMARY KEY CHECK (length(id) = 16),
    text            TEXT    NOT NULL,
    is_completed    INTEGER NOT NULL CHECK (is_completed IN (0, 1)),
    rank            INTEGER NOT NULL,
    created_at_utc  INTEGER NOT NULL,
    updated_at_utc  INTEGER NOT NULL,
    revision        INTEGER NOT NULL CHECK (revision >= 1)
) STRICT;

-- T-05: committed operations, so a retry after an uncertain outcome replays the recorded result.
CREATE TABLE operation_ledger (
    operation_id      BLOB    NOT NULL PRIMARY KEY CHECK (length(operation_id) = 16),
    kind              TEXT    NOT NULL,
    commit_sequence   INTEGER NOT NULL UNIQUE,
    committed_at_utc  INTEGER NOT NULL,
    result_json       TEXT    NOT NULL
) STRICT;

-- B-05: deleted identities, so stale editors and imports cannot re-create them.
CREATE TABLE tombstones (
    entity_kind     TEXT    NOT NULL CHECK (entity_kind IN ('Task', 'ChecklistItem', 'SubtaskCard', 'ChangeRequest', 'ReviewItem', 'GeneralTodo')),
    id              BLOB    NOT NULL CHECK (length(id) = 16),
    deleted_at_utc  INTEGER NOT NULL,
    PRIMARY KEY (entity_kind, id)
) STRICT, WITHOUT ROWID;

-- B-13: external source identities of imported records. Empty until transfers arrive.
CREATE TABLE import_ledger (
    namespace    TEXT NOT NULL,
    entity_kind  TEXT NOT NULL,
    source_key   TEXT NOT NULL,
    record_id    BLOB NOT NULL CHECK (length(record_id) = 16),
    PRIMARY KEY (namespace, entity_kind, source_key)
) STRICT, WITHOUT ROWID;
