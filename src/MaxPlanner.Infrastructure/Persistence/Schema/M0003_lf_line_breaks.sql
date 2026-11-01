-- M0003 (ADR 0025): multiline text is stored with LF line breaks only. CR LF and bare CR become LF in the plain
-- multiline columns; custom values in JSON columns are rewritten in C# (M0003LfLineBreaks). Revisions and audit
-- timestamps are unchanged: the text means the same, only its line-break form changes.
UPDATE tasks SET notes = REPLACE(REPLACE(notes, char(13) || char(10), char(10)), char(13), char(10)) WHERE instr(notes, char(13)) > 0;
UPDATE checklist_items SET text = REPLACE(REPLACE(text, char(13) || char(10), char(10)), char(13), char(10)) WHERE instr(text, char(13)) > 0;
UPDATE subtask_cards SET notes = REPLACE(REPLACE(notes, char(13) || char(10), char(10)), char(13), char(10)) WHERE instr(notes, char(13)) > 0;
UPDATE change_requests SET description = REPLACE(REPLACE(description, char(13) || char(10), char(10)), char(13), char(10)) WHERE instr(description, char(13)) > 0;
UPDATE review_items SET comments = REPLACE(REPLACE(comments, char(13) || char(10), char(10)), char(13), char(10)) WHERE instr(comments, char(13)) > 0;
UPDATE general_todos SET text = REPLACE(REPLACE(text, char(13) || char(10), char(10)), char(13), char(10)) WHERE instr(text, char(13)) > 0;
