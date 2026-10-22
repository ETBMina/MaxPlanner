# ADR 0018: Review items

- **Status:** Accepted (owner decision, 2026-10-22)
- **Requirements:** B-17, B-09, B-15, AC-33

## Context
The owner reviews other people's work and wants a record of it without creating a CR and a task each time.

## Options
1. A "Review" task type.
2. A separate review log.
3. A container CR per period.
4. General to-dos.

The owner chose a **separate entity shown in Checklists**.

## Decision
**Fields.** `ReviewItem`: CR number text, Title, Task number text, Services (a list of text), Developer (Person), Review comments, IsDone with `CompletedOn`, and Order.

**Checklists page.** Review items appear in a Reviews section, one line per item, with a "⋯" menu for View/Edit and Delete.

**CR number.** It is text, not a link. A matching normalized CR number only enables an "Open CR" affordance.

**Dashboard**
- A Reviews card: open, completed this week, completed this month.
- An "Open reviews by developer" chart.
- Drill-down opens Checklists › Reviews.

## Consequences
- No Board, Grid or Calendar presence.
- Included in backup and every transfer format.

## Verification
Domain/application tests for completion dates and week/month boundaries (the week starts Sunday), Dashboard agreement tests, and AC-33.
