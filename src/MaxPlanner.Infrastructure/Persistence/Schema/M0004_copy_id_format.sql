-- M0004 (design §4.6): the Copy ID format, a workspace default. NULL means the built-in default "{CRNum} - {TaskNum}".
ALTER TABLE app_metadata ADD COLUMN copy_id_format TEXT NULL;
