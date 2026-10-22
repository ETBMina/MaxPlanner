# ADR 0017: Subtask cards and the checklist-item rename

- **Status:** Accepted (owner decision, 2026-10-22)
- **Requirements:** B-16, B-01, B-04, B-07, B-15, AC-32

## Context
New work is sometimes needed on a task that is already completed. Reopening the task would rewrite its history (status and dates). The owner wants a separate card that stays linked to the task.

## Options
1. A full task with a `ParentTaskId`.
2. A **lightweight card**, chosen by the owner.

## Decision
**The entity.** A `SubtaskCard` table with ParentTaskId, Subtitle (required), Status (task statuses), Start/End dates, Notes, Order and revision. It shows the parent's effective Title and Service read-only, and the inherited CR values through the parent's link.

**Dates.** B-04 date automation applies to the card's own dates and never to the parent's.

**Creation**
- "Add Subtask card" from a task.
- "Pop out to card" from a checklist item: one transaction removes the item and creates the card.

**Lifecycle.** The card follows its parent for archive visibility and deletion, including the CR cascade.

**Rename.** The old "subtasks" (checkbox items inside a task) are renamed **Checklist items** to avoid two meanings of "subtask".

## Consequences
- The Board shows two card kinds.
- The Grid gains a Kind column and filter.
- The Dashboard counts Subtask cards separately and never in task totals.

## Verification
Domain tests for independent dates, the pop-out atomicity test, and AC-32.
