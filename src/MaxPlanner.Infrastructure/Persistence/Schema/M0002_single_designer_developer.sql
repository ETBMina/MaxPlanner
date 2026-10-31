-- M0002 (ADR 0024): a task has at most one Designer and at most one Developer.
-- For each task and role, the row with the lowest position is kept (ties by person_id); the others are deleted.
-- The owner approved dropping the extras; the migration-safety backup taken before this step still holds them.
DELETE FROM task_people
WHERE EXISTS (
    SELECT 1
    FROM task_people AS kept
    WHERE kept.task_id = task_people.task_id
      AND kept.role = task_people.role
      AND (kept.position < task_people.position
           OR (kept.position = task_people.position AND kept.person_id < task_people.person_id)));

CREATE UNIQUE INDEX ux_task_people_task_role ON task_people (task_id, role);
