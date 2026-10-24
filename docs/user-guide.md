# Max Planner — user guide

Max Planner is an offline planner for Change Requests (CRs), tasks, Subtask cards, checklists and reviews. It runs from a folder without installation; see `README.txt` in that folder (or [deployment-guide.md](deployment-guide.md)) for starting, moving, backing up and updating it.

> This guide describes the release-1 build as implemented. Its behaviour has not yet been through the Phase 19 UI validation, so details may still change.

## 1. Concepts
| Term | Meaning |
|---|---|
| **Change Request (CR)** | A planned piece of work with a unique number, title, required **Owner**, optional squad, status, priority, planned start/end dates (inclusive), task number, email subject, solution design link and description. A CR can have any number of tasks. |
| **Task** | A unit of work with a Service, status, priority, designers, developers, tags, notes, actual start/end dates and **checklist items**. |
| **Link** | A task can be linked to one CR, **once and permanently**. A linked task shows the CR's title, CR number, task number, squad ("General" when the CR has none) and owner, read-only; they change everywhere when the CR changes. A linked task needs a Service. Links are never removed or moved to another CR. |
| **Subtask card** | A light card under one task (subtitle, status, own dates, notes). It shows its parent's title, service and CR values. Moving a card never changes its parent. |
| **Checklist item** | A checkbox line inside a task. **Pop out to card** turns an item into a Subtask card in one step. |
| **General to-do** | A checklist item that belongs to no task. |
| **Review** | A record for reviewing someone else's work: title, optional CR number and task number (plain text), services, developer, comments, and Done with its completion day. |
| **Archive** | Hides a task (and its Subtask cards) from active views without deleting it. Restore brings it back. CRs are not archived. |
| **Overdue CR** | An incomplete CR whose planned end is before today. Tasks have no deadline. |

Dates follow the workflow (Settings): moving a task or Subtask card into a status that starts work sets its start date, and moving it into a completed status sets its end date. Each card's dates are its own.

## 2. Window and navigation
- The left navigation has **Dashboard, Board, Grid, Calendar, Checklists** and **Settings**. In a narrow window it collapses to icons, and at the smallest width it opens from the button in the title bar.
- The bottom bar shows a page summary on the left and the save status on the right: "Saving…", "Saved", "All changes saved", or "Couldn't save — Details". Only real save results are shown.
- Messages that affect the whole app (for example a failed backup) appear under the title bar.

## 3. Pages
### Dashboard
Cards for Active tasks, Completed tasks, Unscheduled tasks, Overdue CRs, Active Subtask cards and Reviews (open, done this week and this month; weeks start on Sunday); charts of tasks by status, tasks by squad and open reviews by developer, each with a **Table** view; lists of overdue CRs and unlinked tasks. Filter by squad, priority and CR owner. Select a card, bar or list to **drill down**: tasks open in the Grid, overdue CRs in Calendar › Records, reviews in Checklists › Reviews, with the matching filter shown as "From Dashboard". **Return to Dashboard**, the title-bar Back button, or Alt+Left go back with the Dashboard's filters and scroll position.

### Board
One column per task status, with task cards and Subtask cards. Drag a card to another column or position, or use the keyboard (below). While a search or filter is active, a card moved to another column goes to the end of that column and reordering within a column is off. Each card's **⋯** menu (also right-click or Shift+F10) has Open, Move to, Move up/down, Copy…, Add Subtask card, Open CR, Archive and Delete…. In a narrow window the Board shows one column at a time with a column picker.

### Grid
A spreadsheet of tasks and Subtask cards. Edit a cell with F2, a double-click, or by typing; Enter commits, Esc cancels, Tab commits and moves to the next editable cell. Inherited CR values are read-only (edit them on the CR). Sort by a column header; resize, reorder and hide columns (Columns button; Reset column layout in the overflow). **Show archived** includes archived tasks. **Export…** exports the rows shown. The row ⋯ menu adds Link to CR… and Restore.

### Calendar
**Month** and **Week** show scheduled CRs as bars on a Sunday–Thursday workweek (Friday–Saturday items are listed in the outside-workdays strip). Drag a CR from the **Backlog** onto a day to schedule it, drag a bar's edges to change its dates, or use the keyboard. **Records** lists all CRs in a sortable table. **New CR** creates a CR; a day's menu offers "New CR on this date".

### Checklists
**Checklists** shows General to-dos first and then a card per active task with its checklist items; completed items collapse under "Completed (n)". Add items with the Add box (Enter), rename with F2, reorder with Alt+Shift+Up/Down, and pop an item out to a Subtask card. **Add to task…** starts a checklist on any task. **Reviews** shows one line per review with a Done checkbox; ⋯ opens View/Edit and Delete; completed reviews collapse under "Completed (n)".

### Settings
Appearance (Use system setting, Light, Dark); workflow lists (task statuses with their flags, CR statuses, priorities, squads); people and tags; defaults (task status, CR status, priority, **Default CR Owner**, scheduling and backlog statuses); custom fields for tasks and CRs; and **Data**: the data folder, Back up now, Manage backups, and the Data page with **Export**, **Import** and **Restore**.

Removing a status, priority, squad, person or tag that records use asks for a replacement (or, where allowed, to clear it) and shows how many records change.

## 4. Editors
Task, Subtask card, CR and review editors open as dialogs. Required fields are marked "*". **Save** (or Ctrl+Enter) saves; errors appear under the fields and focus moves to the first one. Closing with unsaved changes — Esc, Cancel, or closing the app window — asks **Save**, **Discard** or **Continue editing**. If someone else's change was saved first (for example in another page), the editor shows both values and lets you keep yours or reload.

- **Link to CR…** (unlinked tasks): pick a CR and confirm; the link is permanent. The task's own title is kept in the data but no longer shown.
- **Copy…**: creates a new unlinked task from the chosen parts of the source.
- **Delete…** always asks first. Deleting a CR also deletes its tasks, their checklist items and Subtask cards; the confirmation shows the counts.

## 5. Search and filters
Board, Grid, Calendar and Checklists have a search box (Ctrl+F); in a narrow window, the Search button opens it on its own row. **Filters** (Ctrl+Shift+F) edits a draft — nothing changes until **Apply** (or Ctrl+Enter). Active filters show as chips; select a chip to remove it, or **Clear all filters** (Ctrl+Shift+L). Board and Grid share one task filter. Filters are remembered between sessions; a saved filter that no longer matches a value (for example a removed status) is cleared with a message.

## 6. Import, export, backup and restore
- **Export** (Settings › Data, or Grid › Export…): the whole dataset or the current Grid rows, as JSON, Excel (.xlsx), one CSV table, or a ZIP of CSV tables.
- **Import**: Max Planner's own files, or external CSV, Excel, JSON, and Markdown files or folders, with a mapping step for external data. A preview shows what will be created, updated, skipped or refused; nothing changes until you apply, and the whole import succeeds or nothing is written. Importing into a dataset that already has records makes a safety backup first. Existing CR links are never changed by an import.
- **Backups**: one automatic backup per day (the last 7 are kept), plus manual and safety backups in `data\backups`.
- **Restore** (Settings › Data › Restore): choose a backup, review the checks, confirm, and the app reloads with the restored data. A safety backup of the current data is made first.
- If the data is missing or damaged at start, a recovery page explains the options; the app never replaces your data with an empty dataset on its own.

## 7. Keyboard
| Keys | Action |
|---|---|
| Ctrl+1 … Ctrl+5, Ctrl+, | Dashboard, Board, Grid, Calendar, Checklists, Settings |
| Ctrl+F | Focus the page search |
| Ctrl+Shift+F / Ctrl+Shift+L | Open Filters / Clear all filters |
| Ctrl+N | The page's New (task, CR, to-do) |
| F6 / Shift+F6 | Move focus between navigation, page header, content and status bar |
| Alt+Left | Back to the Dashboard after a drill-down; Back on an editor step |
| Shift+F10 or the Menu key | Actions for the focused card, row or item |
| Enter / Delete / Ctrl+D | Open / Delete… / Copy… the focused record |
| **Board** Alt+Shift+Left/Right | Move the card to the previous/next status |
| **Board** Alt+Shift+Up/Down | Move the card up/down (not while filtered) |
| **Board** Ctrl+Left/Right | Go to the next column |
| **Grid** F2, Enter, Esc, Tab | Edit, commit, cancel, commit and move |
| **Grid** Alt+Left/Right, Ctrl+Shift+Left/Right (on a header) | Resize / move the column |
| **Calendar** arrows, PageUp/PageDown, Ctrl+T | Move between days, periods, today |
| **Calendar** Shift+Left/Right, Ctrl+Shift+Left/Right (on a bar) | Planned end / start ±1 day |
| **Calendar** Ctrl+E, Ctrl+Shift+B, Ctrl+Shift+S | Edit dates, move to backlog, schedule a backlog CR |
| **Checklists** Space, F2, Alt+Shift+Up/Down, Ctrl+Shift+P | Toggle, rename, reorder, pop out to card |
| **Editors** Ctrl+Enter, Esc | Save, close (asks when there are unsaved changes) |

## 8. Accessibility and appearance
Every command has a keyboard route and an accessible name; status is never shown by color alone. The app follows Windows High Contrast themes and text size, works from 500 effective pixels wide (Snap layouts), and uses Mica with a solid fallback when transparency is off. Theme changes apply at once.
