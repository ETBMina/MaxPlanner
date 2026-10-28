# Max Planner — UI and interaction contract

This file defines how Max Planner looks and behaves. [Business Requirements.md](<Business Requirements.md>) is the product authority. When this file and the requirements disagree, the requirements win. Record the gap in §11 and do not silently change either file. Work order is tracked in [implementation-plan.md](implementation-plan.md) and decisions in `docs/adr/`.

- **Visual reference:** `Designs/Max Planner.html`, a light-only web mock. It is an inventory, not a specification. Every element is mapped to a native control here, and departures are listed in §10.
- **How it was grounded:**
  - The `winui:winui-design` skill (SKILL.md, `layout-review.md`, `theme-accessibility.md`, `brushes-and-icons.md`).
  - `winapp find-ui` 0.7.1 for samples; the sample ids are cited inline as `[id]`.
  - `winapp find-api --project sdk` against the machine index (WindowsSDK 10.0.28000 and WinAppSdkRuntime 2.5), which confirmed `TitleBar`, `SelectorBar`, `NavigationView` thresholds, `FlyoutBase.SystemBackdrop`, `OverlappedPresenter.PreferredMinimumWidth`, `Microsoft.Windows.Storage.Pickers` and the `AutomationProperties` heading/landmark/live members.
  - Re-run `find-api --project-dir` against the restored Presentation project before you code against any of these APIs.
- **Breakpoints (T-08):** **S** = 0–639 epx, **M** = 640–1007 epx, **L** = ≥1008 epx, measured on the app window and never on the screen. Design and test at widths down to 500 epx.

---

## 1. Principles

| # | Rule |
|---|---|
| P1 | Use **Fluent 2 and native WinUI 3 controls only**. Prefer built-in WinUI controls over custom ones. Community Toolkit controls are not used in release 1 (ADR 0021). Build a custom control only when nothing fits, and then it must specify its keyboard behavior, UIA peer, every visual state, High Contrast, and responsive behavior (Board column host, Calendar surface, bar chart). |
| P2 | Use **theme resources only**. Reference `{ThemeResource}` at the usage site and name brushes by purpose (§3). No hex literals, `White` or `Black` in XAML or code. |
| P3 | **Never use color alone.** Every state shown in color also carries text, a glyph or a shape: priority, status, overdue, completed, archived, selected, error, drop target (T-08, AC-27). |
| P4 | **Icons are Segoe Fluent Icons** through `{ThemeResource SymbolThemeFontFamily}`, defined once as named `FontIconSource` resources (`Icon.Add` …). Glyph table: §6.4. A button that shows only an icon must have `AutomationProperties.Name` and a tooltip that includes its shortcut. |
| P5 | Use the **system type ramp**: `TitleTextBlockStyle` for page titles, `SubtitleTextBlockStyle` for section headings, `BodyStrongTextBlockStyle` for card titles, `BodyTextBlockStyle`, and `CaptionTextBlockStyle` for metadata. Use the platform spacing grid (4/8/12/16/24/32) and `ControlCornerRadius`/`OverlayCornerRadius`. |
| P6 | **All strings come from `en-US` `.resw` files through `x:Uid`.** This includes AutomationProperties.Name, tooltips and InfoBar text. Format dates and numbers with the user's culture, and never parse displayed text (T-08). |
| P7 | **Show what was committed and be explicit about drafts.** Shared views show committed state only (T-07). Editors show draft state, and a draft is never saved implicitly by resizing, navigating or snapping (T-08, AC-26). |
| P8 | **One route per rule.** Dialogs, inline Grid edits, drag/drop, keyboard moves and imports call the same use cases. The UI never re-implements validation; it only displays it. |

## 2. App shell

### 2.1 Window
| Item | Contract |
|---|---|
| Backdrop | `Window.SystemBackdrop = new MicaBackdrop()` (Kind `Base`) `[gallery-systembackdrops-1, -2]`. See §3. |
| Size | First run: 1280×800 epx, centered on the primary work area. Later runs restore the bounds saved in `configuration.json` and clamp them to a visible work area (to handle a monitor removed or a DPI change). `OverlappedPresenter.PreferredMinimumWidth`/`Height` = 500×500 epx, converted with `GetDpiForWindow`. Keep the native Snap Layouts, caption buttons and system menu. |
| Title bar | The in-box **`TitleBar`** control `[gallery-titlebar-1, -2, -3]` with `ExtendsContentIntoTitleBar = true` and `SetTitleBar(titleBar)`.<br>• `IconSource` = app icon; `Title` = "Max Planner" (resw).<br>• `IsBackButtonVisible` is bound to the drill-down return stack (§2.9).<br>• `IsPaneToggleButtonVisible` is true only when the NavigationView is in `LeftMinimal`; `PaneToggleRequested` toggles `IsPaneOpen`.<br>• No search box or commands in the title bar (T-08). |
| Rows | TitleBar (48) → **App InfoBar region** (§2.7) → NavigationView (content frame) → **Status bar** (§2.8, 32 epx). |

### 2.2 Navigation — `NavigationView` `[gallery-navigationview-1, -3, -6]`
| Property | Value |
|---|---|
| `PaneDisplayMode` | `Auto`, with `CompactModeThresholdWidth = 640` and `ExpandedModeThresholdWidth = 1008`. This gives L = Left expanded, M = LeftCompact and S = LeftMinimal (overlay) (T-08.1). |
| Menu items | Dashboard (`ViewDashboard`), Board (`Tiles`), Grid (`GridView`), Calendar (`Calendar`), Checklists (`CheckList`). These five are the only top-level destinations (T-08). |
| Footer | The built-in Settings item (`IsSettingsVisible = true`) `[gallery-navigationview-6]`. Settings › Data is a sub-page reached from Settings and is **not** a nav item (D13). |
| Back button | `IsBackButtonVisible = Collapsed`. Back belongs to the TitleBar, and only for drill-down return (§2.9). |
| Header | `AlwaysShowHeader = false`. Each page renders its own header (§2.3). |
| Badges | The Checklists item may carry an `InfoBadge` with the open count `[gallery-infobadge-1]`. Its accessible name includes the count. |
| Selection | Selection always reflects the visible page. At S the overlay closes after an item is invoked, and the page title stays visible in the page header (T-08.1). |
| Page cache | Retained page ViewModels are kept per T-07, and navigation never duplicates subscriptions (AC-17). Each page view is created once and then stays in the shell's page host for the session; a page switch only changes which view is `Visible` (the others are `Collapsed`), so returning to a page keeps its realized rows and cards, scroll position, selection, and drafts and costs one re-measure (T-11). A collapsed page takes no layout, focus, keyboard accelerators or UI Automation. **Preload (T-11):** after the first frame of the ready initial page, the shell creates the other main pages in the order Board, Grid, Calendar, Checklists, Dashboard (Settings is created on its first visit). Each page is one sequence of low-priority dispatcher steps: its ViewModel, then (after its first data arrived) its view, which is laid out for one frame behind the current page while transparent, disabled (no focus or accelerators), not hit-test visible and hidden from UI Automation, and is then collapsed; it starts hidden, so its StateView does not announce and its `OnShown` runs only when the user navigates to it. Navigation never waits: a page not preloaded yet is created on demand, and the preload skips it. Closing the dataset (restore) cancels the preload before the pages are disposed. The first visit of a preloaded page is therefore an ordinary page switch; log event 5606 records the preload's duration and the working set after it. Leaving a page closes its open menus, flyouts and error popup (light dismiss), and returning restores a Grid cell's error popup or flyout editor with its draft. All views are dropped when the dataset closes. |

### 2.3 Page header pattern
Each page starts with a header `Grid` that has these rows:

| Row | Content | L | M | S |
|---|---|---|---|---|
| 1 Title | `TextBlock` `TitleTextBlockStyle`, `AutomationProperties.HeadingLevel=Level1`, plus a secondary subtitle (`CaptionTextBlockStyle`, `TextFillColorSecondaryBrush`), e.g. "Wednesday, October 21, 2026 · non-archived records only" | Same line | Same line | Subtitle wraps below |
| 2 Commands | Left: a **Filters** button (Filter glyph, "Filters" label, `InfoBadge` with the active-field count) and, last in that group, the **expanding search** (§2.3.1). Right: the page `CommandBar` (§2.4) and one **accent** primary `Button` (`AccentButtonStyle`) placed outside the CommandBar | One row; search expands to 280 epx | One row; search expands to what the free space allows | One row; search overlays the Filters group (§2.3.1); the primary button keeps its icon and shows its label when space allows |
| 3 Context | Active-filter chips (§2.6) and Calendar/Grid view selectors | Wrap | Wrap | Wrap; the chips row scrolls horizontally when there are more than 2 rows |

#### 2.3.1 Expanding search (`Common/Controls/ExpandingSearchBox`, Board, Grid, Calendar, Checklists)
- **Collapsed:** a default-style icon `Button` (Find glyph E721, 14 px) named and tooltipped with the page scope ("Search tasks", tooltip "Search tasks (Ctrl+F)"). It is the last control of its left command group (Checklists: the only one), so it grows only into the free space after it.
- **Expanded:** the `AutoSuggestBox` (`LandmarkType=Search`, up to 280 epx, Checklists 320) bound to the page's persisted search, with an inline subtle **Clear search** button (Clear glyph E894; replaces the text box's own delete button, not a tab stop). Its layout footprint never exceeds its own slot, so no neighbour moves or reflows. When the slot is narrower than 200 epx the box overlays the neighbouring commands of its row on `SolidBackgroundFillColorBaseBrush`. Expanding grows the box out of the Search button (a Composition inset clip opening from the button with a fade, 250 ms decelerate); collapsing (Esc, Clear, or focus leaving an empty box) plays it back (167 ms accelerate) before the button returns. Both snap when `UISettings.AnimationsEnabled` is off.
- **Expands** on click/Enter/Space (focus moves into the box), on Ctrl+F, and by itself whenever the search text is non-empty (a restored B-10 search or a Dashboard drill-down), without taking focus.
- **Collapses** on Esc or Clear (text cleared exactly like an emptied box, focus returns to the Search button), when focus leaves with empty text (Tab out), or when the text is cleared elsewhere while focus is outside. With non-empty text it never collapses: an active search stays visible.
- AutomationIds: `{Page}_SearchButton`, `{Page}_SearchBox`, `{Page}_SearchClearButton`.

Page padding: L 32/24 (left-right/top), M 24/16, S 16/12. Content may extend to the right edge only when it scrolls horizontally (Board, Grid).

### 2.4 CommandBar pattern `[gallery-commandbar-1, gallery-appbarseparator-1]`
- `DefaultLabelPosition="Right"`, `IsDynamicOverflowEnabled="True"`, and `OverflowButtonVisibility="Auto"`. Each command sets `DynamicOverflowOrder` so the least-used commands overflow first.
- Destructive or rare commands (Export, Reset layout) start in `SecondaryCommands`. A command is never just hidden: at S everything except the accent primary button reaches the user through overflow (T-08).
- Commands that depend on a selection use `IsEnabled` bindings. A disabled command's tooltip says why ("Select a task").
- `KeyboardAccelerators` are declared on the command itself, so the tooltip shows the shortcut `[gallery-accessibilitykeyboard-5]` (§7).

### 2.5 StateView pattern (every data-driven region, T-08.1, `layout-review.md`)
`StateView` is a shared `UserControl` with one `State` enum. It sits in the page content row, and the header and commands stay usable in every state.

| State | Visual | Actions | Announcement |
|---|---|---|---|
| Loading | Indeterminate `ProgressRing` (32) `[gallery-progressring-1]` with "Loading tasks…". It appears after a 300 ms delay; there are **no zero values** before data arrives (B-15.7). | None | Polite: "Loading" |
| Empty | Glyph (48, `TextFillColorTertiaryBrush`), a title, one sentence, and the primary create action, e.g. "No tasks yet" + **New task**; on the Dashboard, **New CR** + **New task**. | Create | None |
| Filtered-empty | Filter glyph, "No tasks match your search and filters", and the active-filter summary. This is visually different from Empty (B-10). | **Clear all filters**, Edit filters | Polite: "No matching results" |
| Error | Error glyph, the actionable message from the typed error, and the operation id in a "Details" expander. | **Retry**; **Open data folder** when relevant | Assertive |
| Populated | The content. | — | — |

### 2.6 Filters (Board, Grid, Calendar, Dashboard drill-down)
- The **Filters** button opens a `Flyout` `[gallery-flyout-1]` with `ShouldConstrainToRootBounds = true` and a max height of 80% of the window. The content scrolls, and the footer stays pinned.
- The flyout edits a **draft** (B-10):
  - The header reads "Filter tasks — Board and Grid · 3 fields set".
  - Footer: **Clear** (clears the draft), the hint "Nothing changes until you apply", **Cancel**, and **Apply** (accent, Enter).
  - Light dismiss or Esc behaves like Cancel.
- Field controls:

| Field kind | Control |
|---|---|
| Lookup (Status, Priority, Squad, incl. "General", CR Owner) | `ToggleButton` chips in a `WrapPanel` `[gallery-togglebutton-1, gallery-wrappanel-1]`. A checked chip shows a CheckMark glyph as well as its fill. |
| People (Designers, Developers) and Tags | People picker (§5.0). Tags add an "Any selected / All selected" `RadioButtons` (2 columns) `[gallery-radiobutton-1]`. |
| CR | `AutoSuggestBox` CR picker with the special entries "Any CR" and "No CR". |
| Service, text custom fields | `TextBox` with the header "Service contains". |
| Dates | Two `CalendarDatePicker`s (From/To, inclusive) `[gallery-calendardatepicker-1]` plus a "Include records without a {field}" `CheckBox`. An inverted range shows an inline error and disables Apply. |
| Number custom fields | Two `NumberBox`es (Min/Max) `[gallery-numberbox-2]`. |

- **Active-filter chips:** below the header, one removable chip per field (`Button` with a Cancel glyph; name "Remove filter Squad: Retail Cards"), plus "Search: card" and an immediate **Clear all filters** `HyperlinkButton` (B-10).
- **Invalid saved filters:** a lookup filter that no longer resolves at startup is cleared and reported in a page InfoBar (Warning): "A saved filter referred to a status that no longer exists and was removed." (B-10)

**Proposed notes (Phase 11, pending owner review):**
- The Filters button and flyout are one shared `FilterButton` control and the context row is one `FilterChipsBar` control, used by Board, Grid and Calendar. Lookup chips wrap with a small native `WrapPanel` (ADR 0021: no Toolkit), not the Gallery `WrapPanel`.
- **Apply** is the accent button; Enter activates it when it has focus and **Ctrl+Enter** applies from any field, because plain Enter belongs to the CR picker and the text boxes. Ctrl+Shift+F opens the flyout and Ctrl+Shift+L clears all filters on the visible page; Ctrl+F expands and focuses the page search (§2.3.1).
- The flyout lists CR Owner chips only for people who own a CR (plus any already selected), so the chip group stays short. Designers, Developers and Tags use the TokenPicker with existing names only (no add-new in a filter).
- The search box applies immediately and is part of the scope (persisted with it, shown as a "Search: …" chip); it is outside the draft.
- Custom fields: Text, Multiline and URL use "contains"; Number uses Min/Max; Date uses From/To with "Include records without a {field}"; Lookup uses chips. People/Tags custom fields are not filterable in release 1 (B-10 lists them under "need not be filterable").
- A saved CR filter that refers to a deleted CR is cleared at startup with the same InfoBar ("…referred to a CR that no longer exists…"). A filter whose value is removed while the app runs is cleaned the same way.
- Subtask cards filter by their own status and dates, by the CR values inherited through the parent, and by the parent's priority, people, tags, service and custom values.
- The Grid Filtered-empty action is **Show archived** when only archived records match, otherwise **Clear all filters** (StateView gained a Filtered-empty action).
- Calendar B-08.12: after a CR editor save, a page InfoBar says "CR-1042 was saved, but it is hidden by the current filters." with **Clear filters**; nothing else changes.

### 2.7 InfoBar usage `[gallery-infobar-1, -2, -3]`
| Scope | Placement | Examples | Severity | Closable |
|---|---|---|---|---|
| App | Under the TitleBar, full width, stacked by severity | Resyncing after a publication fault (editing blocked) (T-07); storage unavailable or disk full; daily backup failed (T-09); data folder read-only (exit path, T-03) | Error/Warning | Only when the cause is resolved or the message is informational |
| Page | Under the page header | Invalid saved filter; "Showing Dashboard results" context (§2.9); reordering disabled while the Board is filtered | Informational/Warning | Yes |
| Editor | Top of the dialog content | Validation summary; conflict (§5.10); archived read-only; overdue CR; pending permanent link | Error/Warning/Informational | Validation: no |

InfoBars are never used for success toasts. Success appears in the save status (§2.8). InfoBars are announced through their built-in live behavior, and their action buttons carry AutomationIds.

### 2.8 Status bar and save-status indicator
A 32-epx bar at the bottom of the content area (`LayerFillColorDefaultBrush` with a top `DividerStrokeColorDefaultBrush`). It has two zones:

| Zone | Content |
|---|---|
| Left: page summary | Page-provided text, e.g. Grid "8 of 32 active tasks match · sorted by Created, newest first · archived hidden"; Board "Filtered: 12 of 40 cards"; Calendar "Workweek Sunday–Thursday · Friday and Saturday hidden". Ellipsis at S, with the full text in a tooltip and `AutomationProperties.Name`. |
| Right: save status | One of the states in the table below. At S the text collapses to glyph + short label. |

| Save state | Glyph + text | Live setting |
|---|---|---|
| Idle | CheckMark + "All changes saved" | — |
| Saving | 16-epx `ProgressRing` + "Saving…" | Polite (on change only) |
| Saved | CheckMark + "Saved" (returns to Idle after 3 s) | Polite |
| Failed | Error glyph + "Couldn't save — Details" (button opens the error InfoBar) | Assertive |
| Resyncing | Sync glyph + "Reloading data…" | Assertive |

The indicator shows **actual persistence results**, never optimistic ones (B-07 Grid). Keyboard hints such as "F2 to edit · Enter to commit · Esc to cancel" go into the Grid page summary at L only.

### 2.9 Drill-down and return
- **Dashboard drill-downs** (B-10, B-15.6):
  - Task and Subtask card metrics → Grid.
  - Overdue CRs → Calendar › Records.
  - Review metrics → the Checklists Reviews card.
- The destination receives a **replacement** filter context. It shows a page InfoBar ("Showing Dashboard results: Active tasks · Squad: Retail Cards") with a **Return to Dashboard** button, and the chips include a non-editable "From Dashboard" chip that can be removed. Removing that chip restores the destination's previous filters.
- TitleBar Back (Alt+Left) pops the same stack and restores the Dashboard filters and scroll position (AC-20).

**Proposed notes (Phases 11–12, pending owner review):**
- **Return and Back also restore the destination's previous filters** (the same as removing the "From Dashboard" chip), so a later visit never shows the drill-down predicate as if it were the page's own filters. **Clear all filters** ends the drill-down context without restoring the previous filters. The drill-down context is never persisted; a restart shows the page's own saved filters.
- Board and Grid share one task scope (B-10), so a task drill-down into the Grid also shows its "From Dashboard" chip and InfoBar on the Board (where reordering stays off while it is active).
- The Dashboard metric is the only non-editable part: the drill-down also sets ordinary, editable fields (for example Squad, Priority, CR Owner, or the chart's Status/Squad), shown as normal chips. Applying the flyout keeps the Dashboard metric.
- Review drill-downs replace the Checklists search (Checklists has no Filters flyout) and show the same InfoBar and a removable "From Dashboard" chip.
- A second drill-down into a page that still shows Dashboard results replaces the results but keeps the filters from before the first drill-down; the stack holds one entry per destination page.

## 3. Theme and materials

| Surface | Material / brush | Fallback (transparency off, RDP, power saver, unsupported) |
|---|---|---|
| Main window | `MicaBackdrop` (D6, T-08) | Native solid fallback (`SolidBackgroundFillColorBaseBrush` effect). Nothing depends on transparency (AC-24). |
| NavigationView pane / TitleBar | Transparent over Mica (default templates) | Solid |
| Page content layer | `LayerFillColorDefaultBrush` with `CardStrokeColorDefaultBrush` top-left rounding (`OverlayCornerRadius`), the standard content-on-Mica layer | Solid layer brush |
| Cards (Dashboard, Board, Checklist groups, Settings) | `CardBackgroundFillColorDefaultBrush` + 1-px `CardStrokeColorDefaultBrush` + `ControlCornerRadius` (8 for cards, 4 for chips) | Same; the strokes keep boundaries visible |
| Nested / secondary card (Board column, Calendar backlog) | `CardBackgroundFillColorSecondaryBrush` | Same |
| Flyouts, MenuFlyout, context menus | `FlyoutBase.SystemBackdrop = DesktopAcrylicBackdrop` **only where the platform applies it natively**. Otherwise use the default `AcrylicBackgroundFillColorDefaultBrush` template | Platform solid flyout brush |
| ContentDialog | The native dialog surface (no backdrop override) and the default smoke layer | — |
| Accent | `AccentFillColorDefaultBrush` for the single primary button per surface, selection, the current day and the selected bar outline. `AccentTextFillColorPrimaryBrush` for links | — |
| Dividers | `DividerStrokeColorDefaultBrush` | — |
| Text | `TextFillColorPrimary/Secondary/Tertiary/DisabledBrush` | — |

Rules:
1. **App theme** (B-11, AC-23): "Use system setting / Light / Dark" sets `RequestedTheme` on the window root content (`ElementTheme.Default/Light/Dark`). The `ThemeService` also updates the caption button colors and re-applies the theme to open popups and dialogs (`ContentDialog.RequestedTheme`). System mode follows Windows changes live.
2. **Custom resources** are declared in `Themes/AppBrushes.xaml` under `ThemeDictionaries` with **`Light`, `Dark` and `HighContrast`** keys, never `Default`.
   - Light and Dark use `StaticResource` redirects to platform brushes, e.g. `<StaticResource x:Key="OverdueBackgroundBrush" ResourceKey="SystemFillColorCautionBackgroundBrush"/>`.
   - HighContrast uses only `SystemColor*Brush` with no `Opacity`.
   - The token list is in §6.
3. **High Contrast** is system-controlled and wins over everything:
   - Never set `HighContrastAdjustment="None"`.
   - Chips, bars and badges render as `SystemColorButtonFaceColorBrush` with a 1-px `SystemColorButtonTextColorBrush` border and text.
   - Selection, drop targets and in-progress states use `SystemColorHighlightColorBrush`/`HighlightTextColorBrush`, and links use `SystemColorHotlightColorBrush`.
   - Glyphs and text carry the meaning that tints carry in other themes.
4. **No per-card or per-row material layers** (T-08). Acrylic is never applied to a page, pane, card or dialog.

## 4. Pages

Each page section lists its purpose, regions, controls, item content, commands, states and the S/M/L table (T-08.1). AutomationIds are in §8.

### 4.1 Dashboard (B-15, B-10, AC-18–21)
**Purpose:** a current-state overview with drill-down. It is never a reporting database.

| Region | Controls and content |
|---|---|
| Header | Title "Dashboard". Subtitle: long-format local today + " · non-archived records only". Filters inline, not in a flyout: **Squad** (`ComboBox`: "All squads", configured squads, "General"), **Priority** ("All priorities"), **CR Owner** ("All owners") `[gallery-combobox-2]`, and a **Clear filters** `HyperlinkButton`. Below them, a caption: "Filters apply to tasks, Subtask cards and CRs. Reviews are not filtered." |
| Summary cards | `ItemsRepeater` + `UniformGridLayout` (`MinItemWidth` 220, `ItemsStretch=Fill`) `[gallery-itemsrepeater-1]`. Each card is a `Button` styled as a card (invoke = drill-down). The six cards are in the table after this one. Card anatomy: label (`BodyStrongTextBlockStyle`), value (`TitleLargeTextBlockStyle`), definition caption, optional badge. The badge "Needs attention" (Warning glyph + text, caution foreground) appears only when the overdue count is above 0. |
| Charts | Three `BarChart` user controls, described in the next table. |
| Lists | **Overdue CRs** (B-15.4): CR Number (link), Title, Owner, Squad, Status, Priority chip, Planned end, Days overdue.<br>**Unlinked tasks** (B-15.5): Title, Service, Status, Priority chip, People.<br>Each list is a `ListView` with a header `Grid` (the skill's tabular pattern) `[gallery-listview-1]`, ≤10 rows, and the caption "Showing 10 of 23" plus **View all**. Activating a row opens the CR editor or the task editor. |

| Summary card | Value | Definition caption | Drill-down |
|---|---|---|---|
| Active tasks | Count | "of {total} total · not in a completed status" | Grid |
| Completed tasks | Count | — | Grid |
| Unscheduled tasks | Count | "Subset of active · no CR or CR without planned dates" | Grid |
| Overdue CRs | Count | "Incomplete CRs whose planned end is before today" | Calendar › Records |
| Active Subtask cards | Count | "Not completed · parent not archived" | Grid with a non-editable "From Dashboard: active Subtask cards" chip (§2.9) |
| Reviews | Open count | "{n} done this week · {m} this month" | Checklists Reviews card |

**Bar chart (custom, native, T-08, B-15.1–3).** The Gallery `Chart` (`Microsoft.UI.Xaml.Controls.Charts`, `[gallery-chart-2]`) is preview-only and is **absent from the WinAppSDK 2.5 stable index**, so it is not used (AGENTS §3.4). The chart is built as follows:
- **Header:** title (`SubtitleTextBlockStyle`, HeadingLevel 2), a total caption, and a **Table** `ToggleButton` (name "Show as table").
- **Rows:** an `ItemsRepeater` (`StackLayout`) of `Button` rows. Each row contains a category label (a fixed 35% column, wraps), a bar, and the count (always visible). The bar is a `Rectangle`/`Border` whose width is the star-proportion of the max, filled with `AccentFillColorDefaultBrush`, at least 2 epx when the count is above 0, and absent at 0 while the "0" stays visible.
- **Row accessible name:** "In Progress, 6 tasks, 19 percent of 32. Opens matching tasks in Grid."
- **Table mode:** a `ListView` with the columns Category | Count | Share. The same items, order and commands as the bars.
- **Footnote:** Tasks by squad says "General = unlinked tasks and CRs without a squad". Open reviews by developer says "Unassigned = no developer".
- **Order:** configured status/squad order including zero counts. Reviews are sorted by count descending, then name.

| | L | M | S |
|---|---|---|---|
| Cards | 3–6 per row (by width) | 2 columns | 1 column |
| Charts | Status and Squad side by side; Reviews chart beside the Overdue list | Stacked full-width | Stacked; labels above bars if the label column is under 120 epx |
| Lists | Overdue CRs and Unlinked tasks side by side | Stacked | Stacked. Rows reflow to 2 lines (primary: number + title; secondary: owner · end · days) |
| Filters | Inline row | Inline row, wraps | Behind a "Filters (n)" button opening the same three ComboBoxes in a flyout |

States: Loading, Empty ("Start by creating a CR or a task" + New CR + New task), Filtered-empty (zero cards stay visible and the lists show "No records match these filters" + Clear filters), and Error. **Zero values are shown only after the snapshot has loaded** (B-15.7). Time-relative values refresh at midnight, on resume and on time-zone change without any change in layout (AC-21).

**Proposed notes (Phase 12, pending owner review):**
- Lists and the chart Table mode use `ItemsRepeater` rows of `Button`s instead of `ListView`: at most ten rows inside the page `ScrollViewer`, where a nested `ListView` would capture the wheel and drop virtualization anyway. Rows are two lines at every width (primary: number + title; secondary: owner · squad · status · priority · planned end · days), and the Priority chip is shown as text.
- The Reviews card value is the open count; "{n} done this week" and "{m} done this month" are separate links below the card button (never nested in it), each drilling into the Checklists Reviews card with its own predicate.
- The Empty state is the page's own panel (New CR accent + New task), because the shared StateView has one action.
- A chart category that the Dashboard filter already excludes (for example another squad while Squad is set) stays visible with its zero but is disabled, because no drill-down predicate can show it.
- Open reviews by developer lists only developers with at least one open review (people with zero are not listed); statuses and squads keep their zero-count rows (B-15.1–2).
- At S the three filter ComboBoxes wrap in the header instead of moving into a "Filters (n)" flyout, and chart labels stay in their 35% column.
- The Overdue CRs card has the "Needs attention" badge when the count is above zero; the "Unscheduled" definition is the card caption.

### 4.2 Board (B-07, B-16, AC-05/06/10)
**Purpose:** move task cards and Subtask cards through task statuses.

| Region | Controls |
|---|---|
| Header | Title "Board". Filters, then the expanding Search icon (task scope, §2.3.1). Filters share the Board/Grid scope (B-10). Accent **New task** (opens the editor with the default status). CommandBar: New Subtask card… (task picker), Clear all filters; overflow: Board help. |
| Surface | A horizontal `ScrollViewer` (`HorizontalScrollMode=Enabled`, vertical disabled) hosting a non-virtualized `ItemsRepeater` of **columns**, because there are ≤ ~20 statuses. Each column is a fixed-width card (`CardBackgroundFillColorSecondaryBrush`) with a header and a **virtualized `ListView`** in a bounded height (the column fills the viewport height) `[gallery-listview-3]`. The scroll axes do not conflict: the page scrolls horizontally and each column scrolls vertically. |
| Column header | Status name (`BodyStrongTextBlockStyle`, HeadingLevel 2), a count chip ("6", or "4 of 6" when filtered: visible / total active, B-07), status flag glyph (§6.2), **+** button ("New task in In Progress"), and **⋯** `MenuFlyout` (Collapse column, Workflow settings…). |
| Task card | Anatomy listed after this table. |
| Subtask card | Anatomy listed after this table. |
| Card menu | `MenuFlyout` from right-click, Shift+F10, the Application key or the **⋯** button `[gallery-listview-8, gallery-menuflyout-6]`. Items listed after this table. |
| Drop feedback | The insertion line uses `AccentFillColorDefaultBrush` (2 epx) and the destination column gets a highlight stroke plus the caption "Drop to move to Review". During a drag the card has 60% opacity plus a "Moving" caption. When the Board is filtered, a drop **appends** to the destination and the insertion line is replaced by "Will be added to the end of Review" (B-07 filtered rule). |

**Task card anatomy:**
1. Effective Title (`BodyStrongTextBlockStyle`, max 3 lines, full text in a tooltip).
2. Service chip.
3. CR reference link "CR-1067 · Task 00512", which opens the CR.
4. Chip row: Priority chip, Squad chip ("General" when there is none), Owner (Contact glyph + name) when linked.
5. Checklist progress "Checklist 1/3" with a determinate `ProgressBar` `[gallery-progressbar-2]`. When there are no items the card shows "No checklist items" text with no bar (B-07).
6. Up to 3 incomplete checklist items as `CheckBox`es, plus a **Show all n** `HyperlinkButton`.
7. Subtask cards count (glyph + "2 Subtask cards").
8. "Archived" never appears on the Board, because the Board shows active tasks only.

**Subtask card anatomy:** a "Subtask of {parent title}" `HyperlinkButton` (opens the parent), the parent Service chip, the inherited CR reference, the Subtitle (`BodyStrongTextBlockStyle`), Owner, and dates when set. There is no priority, because Subtask cards have none. A 3-epx left border in `ControlStrongStrokeColorDefaultBrush` and the "Subtask" glyph give a non-color cue.

**Card menu items:**
- Open (Enter)
- Move to ▸ (every status, with Top/Bottom of column; while filtered both address the full column and the card's own column is disabled)
- Move up / Move down
- Copy… (tasks only)
- Add Subtask card (tasks only)
- Archive (tasks only)
- Delete…

| | L | M | S |
|---|---|---|---|
| Columns | Width 300, as many as fit, horizontal scroll | Width 280, about 2 visible, scroll | **One column per viewport** (width = viewport − 32), `HorizontalSnapPointsType=MandatorySingle`. A column `ComboBox` "Column: In Progress (6)" plus ‹ › buttons navigate between columns, so no column shrinks below readable width (T-08.1). |
| Card | Full anatomy | Full anatomy | Checklist preview limited to 2 items |
| Header | Search icon expands inline | Search expands into the free space | Search overlays the Filters button when expanded (§2.3.1) |

States: Loading; Empty (statuses exist but no tasks: "No tasks yet" + New task, and the empty columns still render); Filtered-empty (columns render with "0 of n"); Error. **Filtered mode:**
- A page InfoBar (Informational) says "Reordering within a column is off while search or filters are active. Dropping a card on another status adds it to the end of that column." The InfoBar has a Clear filters action.
- Move up/down are disabled, and `CanReorderItems = false`.

A failed move restores the committed position, sets the save status to Failed, and announces the failure (B-07).

A card that is created, copied or restored, or whose status changes outside a positional Board drop (task or Subtask card editor, Grid status cell, Alt+Shift+Left/Right), lands at the **top** of its destination column (B-07). Only a drop uses the dropped slot (unfiltered) or the end of the column (filtered).

**Proposed notes (Phase 3, pending owner review):**
- Drop feedback is a 2-epx accent stroke on the destination list, the drag caption ("Drop to move to Review" / "Will be added to the end of Review"), and a 2-epx insertion line (`AccentFillColorDefaultBrush`; `SystemColorHighlightColorBrush` in High Contrast) at the slot the card will take. The slot follows the pointer live: before a card when the pointer is above its midpoint, otherwise after it; the space below the last card, or an empty column, is the end. The line is an overlay drawn over the list (no placeholder item), follows edge auto-scroll, and disappears when the drag leaves the list, drops, or ends; the drop uses the same slot. Dropping a card next to its own position is a no-op. While the Board is filtered the line marks the end of the destination list (the append rule above), and same-column reordering stays forbidden. The card itself is dimmed with a "Moving" caption until the move is saved. A card moves on screen only after the move committed, so a failure needs no visual rollback.
- Board search is a task-scope `AutoSuggestBox` (B-10 subset) until the shared Filters flyout (Phase 11). The filtered rules above apply to it.
- ~~"Add Subtask card" from the card menu opens a small dialog with a required Subtitle box. "Open" on a Subtask card opens its parent task until the Subtask card editor (§5.2, Phase 5).~~ Superseded in Phase 5: both open the Subtask card editor (§5.2).

**Proposed notes (Phases 5–6, pending owner review):**
- The CR reference on a card and the Subtask card's "Subtask of …" indicator are `HyperlinkButton`s that are not tab stops, because the card itself is the focusable list item. Their keyboard route is the card menu, which gains **Open CR** (linked cards) and **Open parent task** (Subtask cards).
- **New CR…** sits in an overflow-only `CommandBar` beside New task until the Calendar page exists (Phase 7), where the CR editor belongs (B-03.1). It is removed then.

### 4.3 Grid (B-07 Grid, T-08 gate, D7)
**Purpose:** a spreadsheet-style **task editor**. The control is a **custom virtualized `ListView` grid** with a synchronized header row and custom Grid/Table automation peers. WinUI.TableView failed the gate (ADR 0004).

| Region | Controls |
|---|---|
| Header | Title "Grid". Filters (shared with Board), then the expanding Search icon (§2.3.1). Tasks and Subtask cards always appear together; the Kind column identifies each row, and there is no Kind filter (B-07, owner decision). Accent **New task**. CommandBar: **Show archived** (`AppBarToggleButton`, persisted, B-05), **Columns** (flyout), **Export…** (current view → Data page export with the scope preset), Clear all filters, overflow: Reset column layout. |
| Chips row | Active filters plus "From Dashboard" (§2.9) |
| Table | Virtualized rows with sticky column headers. Header buttons sort (Ascending → Descending → default), and their names are "Sort by Title, currently ascending". Columns can be resized (pointer, or Alt+Left/Right on a focused header) and reordered (drag or header menu "Move left/right"). The column chooser is a `Flyout` with a `CheckBox` per column plus Reset. Widths are clamped to 48–800 on restore. |
| Row | **No bulk-selection checkbox column**, since there are no bulk commands in release 1. Row header ⋯ `MenuFlyout`: Open, Copy…, Link to CR… (unlinked tasks only), Add Subtask card, Archive/Restore, Delete…. Archived rows use secondary text, an "Archived" chip in the Archived column, and are read-only except Link to CR… (B-05). |
| Footer | The status bar summary (§2.8). |

| Column (default order) | Editor | Editable when | Read-only display / navigation |
|---|---|---|---|
| Kind | — | never | Glyph + "Task" / "Subtask card" |
| CR Number | — | never | Link → CR editor |
| Task Number | — | never | Link → CR editor |
| Squad | — | never | Text ("General") |
| Owner | — | never | Text |
| Title | `TextBox` | unlinked task, not archived | Linked: a lock glyph + tooltip "Inherited from CR-1042", and the cell opens the CR. Subtask card: parent title, read-only |
| Subtitle | `TextBox` | Subtask card | Blank for tasks |
| Service | `TextBox` | task (required if linked) | Subtask card: parent service, read-only |
| Status | `ComboBox` | task, Subtask card | Status text + flag glyph |
| Priority | `ComboBox` | task | Priority chip |
| Designers / Developers / Tags | People/tag picker in a `Flyout` | task | Comma-joined names, "+n" |
| Start Date / End Date | `CalendarDatePicker` | task, Subtask card | Short date |
| Notes | Multiline `TextBox` in a `Flyout` with **Save** (Ctrl+Enter) / Cancel | task, Subtask card | First line, ellipsis |
| Archived | — | never | "Archived" chip or blank |
| Custom fields | Typed per kind (§5.0) | task | — |

**Editing:**
- **F2**, double-click or typing a character starts an edit. Enter commits. Esc cancels. **Tab** commits and moves to the next editable cell **only on success**.
- If the cell is invalid, the editor stays open. A red-stroke error state, an error message in a `Popup` anchored under the cell, and an announcement explain the problem.
- While a commit runs, the cell shows a 12-epx `ProgressRing`. The editor stays open until the result arrives (gate criterion).
- **Medium priority columns:** Kind, CR Number, Title/Subtitle, Status, Priority, Squad, Owner. The rest stay reachable through horizontal scroll and the chooser.

| | L | M | S |
|---|---|---|---|
| Columns | Full configured set | Identity + Status/Priority/Squad/Owner visible first. Others need horizontal scroll | The first column (Title/Subtitle) is frozen and the rest scroll horizontally. Row ⋯ → **Open** offers the full editor dialog as the accessible row-details route (T-08.1) |
| Commands | All inline | Export/Show archived go to overflow | Only search, Filters and New task stay inline |

States: Loading, Empty, Filtered-empty (distinct from Empty; when only archived tasks exist the message is "Only archived tasks match — turn on Show archived"), and Error. **Sort never changes Board order** (B-07).

**Proposed notes (Phase 9, pending owner review):**
- Columns are configurable, so header and row cells are created in code from the page's column list; custom field columns are keyed `Custom_{FieldId:N}`, never by label, and a removed field simply drops out of the saved layout. The chooser keeps at least one column visible.
- Frozen column at S: the Title column is displayed first and pinned with a render transform that follows the horizontal scroll, so there is still one scroll region (header and rows cannot desynchronize). The configured order is unchanged at M/L.
- Flyout editors: Notes, Designers, Developers, Tags, and multiline/People/Tags custom fields open in a `Flyout` with **Save** (Ctrl+Enter) and **Cancel**; Esc cancels. Light dismiss counts as leaving the cell, so it attempts the commit and keeps the flyout open with the error if it fails. Other editors are inline; dates have a **Clear** button (an explicit clear for B-04).
- Task cells other than Title, Service, and Status save through `UpdateTask` with the whole task as committed plus the one changed field, at the revision the edit began from (same status, so no transition dates). Subtask card dates and Notes save through `UpdateSubtaskCard`.
- Custom People/Tags values pick existing names only; add-new stays with the built-in Designers, Developers, and Tags (B-02.6).
- Priority shows as text (the §6.1 chip is not used in cells yet). **Export…** is shown disabled until the Data page (Phase 13).

### 4.4 Calendar (B-08, B-03, AC-11)
**Purpose:** schedule CRs (not tasks) and reach every CR.

| Region | Controls |
|---|---|
| Header | Title "Calendar" alone in its row. The command row has a left group and a right group. **Left:** a mode **`SelectorBar`**: Month · Week · Records `[gallery-selectorbar-1, -2]` (replaces the mock's toggle and its "CR records" button, D13), then the Expanding Search icon (§2.3.1; CR scope: Number, Title, TaskNumber, Squad, Owner, EmailSubject, Description, B-10). The search sits in the row's star column, so it expands to the right into the free space. **Right** (a content-sized `Grid`, right-aligned): period navigation (Month/Week only): **Today**, **‹ ›** (`Button`s with names "Previous month"/"Next month"), and a **period button** with the abbreviated month, "Oct 2026 ˅" (Week: "Oct 11 – Oct 15, 2026"), that opens a `Flyout` with `CalendarView` (`DisplayMode=Year` for Month, `Month` for Week) `[gallery-calendarview-1]` (the accessible date selector, B-08.1); The period button keeps one width per mode, the widest text any period of the year produces, so stepping through months never resizes it or moves its neighbours; then **Filters** (CR scope: Status, Priority, Squad, Owner, planned range with overlap semantics and "Include unscheduled", custom fields); then the accent **New CR**. Below L the right group moves to its own row under the left group; at S the period navigation takes one line and Filters and New CR sit under it. The **Backlog (n)** toggle is not in the header: it is the backlog rail on the grid edge (Backlog pane). |
| Caption | "Workweek Sunday–Thursday · Friday and Saturday hidden", shown in the status bar summary. |
| Backlog pane | `SplitView` `[gallery-splitview-1]` (`CompactInline` at L, `CompactOverlay` below), pane 280 epx, placed left. Below L the open pane overlays the surface on an opaque fill (`SolidBackgroundFillColorTertiaryBrush`); dragging a card closes it so any date is a drop target, and it reopens if the drag ends without a drop. Closed, the compact strip is a 36-epx **rail** `Button` (`Calendar_BacklogToggle`, name "Backlog (n)") on the grid edge: a chevron and the label turned a quarter to read top to bottom. Open, the pane is the card, whose heading row reads, left to right: a **Hide backlog** button (`Calendar_BacklogCollapseButton`), the heading "Backlog", and the **count badge** (`Calendar_BacklogCount`, an accent-filled rounded box with the number, named "{n} CRs in the backlog"). The Hide button sits level with the rail's arrow, so one pointer position expands and collapses the pane. The hint reads: "Drag a CR onto a date to schedule it, or select it and choose Schedule." The list is a `ListView` of CR cards (newest first, B-08.3). Each card has the number link, a priority chip, the title, the Owner, and "Squad · n tasks" where "n tasks" opens the linked-task list. A click, tap, Enter or Space on the card opens the CR editor; a drag schedules it instead. Card menu: Open, Schedule on…, Open linked tasks, Delete…. |
| Month surface | A custom `CalendarMonthView`, described after this table. |
| Week surface | The same control with one week row, a taller row and two-line bars (number + title / owner · status). Week mode has no fixed lane cap: it shows every lane that fits the row height, and "+N more" appears only when lanes no longer fit (see §11 Q6). |
| Records mode | Described after this table. |

**Month surface (custom `CalendarMonthView`):**
- A deterministic layout from `CalendarLayoutPolicy`. Rows are the weeks that intersect the month. Each row has **5 day columns (Sun–Thu)** and a **24-epx "Fri–Sat" stub** that shows both dates vertically in `CaptionTextBlockStyle`.
- When a CR lies wholly on Fri/Sat, the stub shows an "Outside visible workdays" glyph + count; activating it opens a flyout listing those CRs (B-08.7).
- Adjacent-month days use `TextFillColorTertiaryBrush` plus a month prefix on the 1st ("Nov 1"). Today gets an accent circle and the text "Today" for screen readers.
- **Bars:**
  - Up to 4 lanes in Month mode (Week mode: as many as fit); overflow shows a "+N more" `HyperlinkButton` that opens a `Flyout` listing every CR on that date (B-08.9).
  - Bar content: CR number + title + status text, styled per §6.3.
  - Continuation chevrons (ChevronLeft/Right glyphs) mark clipped bars, and each clipped bar's name says "continues from previous week".
  - Two `Thumb` edge handles of 8 epx resize the bar; pointer capture is in code-behind (B-08.8).
- Each day cell is a drop target, a focusable `Button` with the name "Tuesday, October 13, 2 CRs".

**Records mode (B-08.4):**
- A `ListView` table with sortable headers: CR Number, Title, Owner, Squad, Status, Priority, Planned start, Planned end, Linked tasks, Overdue ("Overdue · 7 days" with a glyph), and ⋯ (Open, Open linked tasks, Create task, Move to backlog, Delete…).
- It includes every status and every unscheduled CR. Search and filters are the same as the calendar's.
- Records is the target of the Overdue-CR drill-down.

**Interactions:**
- **Schedule** (B-08.5): drop a backlog CR on day D, or use the Schedule on… date flyout. The result is PlannedStart = PlannedEnd = D, and the status becomes the scheduling status. If the CR already has dates, an inline confirmation inside the flyout says "Replace Oct 4–Oct 9 with Oct 13?".
- **Resize** (B-08.8): changes one endpoint only. During a drag the bar shows a ghost and a date tooltip. An inverted range is rejected and the bar snaps back.
- **Move to backlog** (B-08.11): in the menu and the CR editor.
- **Failure** restores the prior dates (B-08.8).

| | L | M | S |
|---|---|---|---|
| Backlog | `SplitView` `CompactInline`, open by default (toggle remembered); the rail when closed | `CompactOverlay`, closed (the rail stays visible) | `CompactOverlay`, full-width pane over the rail |
| Month | Full grid | Full grid; bar text = number + title (ellipsis) | **Agenda presentation**: a `ListView` grouped by visible workday (date header + CR rows with the range text "Oct 13 – Oct 16"). Month and Records remain in the SelectorBar, and the **default mode at S is Week** in agenda form (T-08.1). Resizing uses the keyboard/date editor route. |
| Records | All columns | Number, Title, Owner, Status, Planned end, Overdue; the rest scroll | Two-line rows (number + title / owner · status · end · overdue) |

States: Loading; Empty ("No CRs yet" + New CR); Filtered-empty (the grid still renders, and an InfoBar says "No CRs match your filters"); Error. When an edited CR becomes hidden by the filters, the save message says "Saved CR-1042. It is hidden by the current filters." with a Clear filters action (B-08.12).

**Proposed notes (Phase 7, pending owner review):**
- **Open linked tasks** (backlog card, bar and Records menus, and the backlog card's "n tasks" link) opens the CR editor, whose Linked tasks (n) section is the complete list with its count (B-08.3, §5.3 group 8). There is no separate list dialog.
- The day flyout's **New CR on this date** opens the CR editor for a new CR with a one-day planned range on that day and the configured scheduling status, like a scheduled backlog CR (B-08.5). Cancel without changes does not ask about unsaved changes.
- **Resize by drag** stays within the bar's week row (the ghost is the bar itself, re-spanned live, with the date in the handle's tooltip). Ranges that cross rows are changed with the keyboard (Shift/Ctrl+Shift+arrows, one command after 600 ms or Enter) or **Edit dates…** (Ctrl+E, two date pickers, one command). Keyboard steps are calendar days, so an endpoint can land on a hidden Friday/Saturday; the bar then shows no cue for the hidden part (B-08.6).
- The bar edge handles are a `Thumb` inside a small panel that sets the horizontal resize cursor (`Thumb` is sealed). Handles are pointer-only (`IsTabStop=False`); the keyboard route above is the equivalent.
- **Move to backlog** asks first in a light flyout from a bar (Ctrl+Shift+B or the bar menu). The Records row menu and the CR editor's More menu run it directly, because choosing the command there is the explicit step (B-08.11).
- A drop on a day of a CR that already has dates asks in a dialog ("Replace Oct 4 – Oct 9 with Oct 13?"); **Schedule on…** shows the same question inline in its flyout.
- Records rows also offer **Schedule on…** (the backlog route), so a CR that is unscheduled but not in a backlog status can still be scheduled from Records.
- **Fit to height (§9):** at L/M the Month surface fills the content height with no vertical scrolling; the 5 or 6 week rows share it equally (star rows), and Week mode's single row fills it the same way. The lane cap per row is derived from the row height (day header + lanes × bar height scaled by `TextScaleFactor` + the "+N more" row), at most 4 and at least 0, and is recomputed when the window is resized or the period changes (the surface is rebuilt only when the cap changes). Bars beyond the cap go to "+N more" (B-08.9). Only when not even a lane-less row fits does the surface keep its natural height and scroll.
- There is no bar selection model yet, so the **Selected** bar state (§6.3) is not shown; focus uses the system focus visual.
- The backlog rail (the **Backlog (n)** toggle) is shown at every breakpoint. The pane opens inline at L and closes when the window narrows below L; its open state is not persisted yet.
- Calendar search is in place; the CR **Filters** flyout, active-filter chips and the "hidden by the current filters" save message (B-08.12) arrive with Phase 11.
- The temporary Board overflow **New CR…** (Phase 6) is removed; **New CR** (Ctrl+N) lives on the Calendar.

### 4.5 Checklists (B-09, B-16, B-17, AC-06/32/33)
**Purpose:** work through General to-dos, task checklist items and Review items.

| Region | Controls |
|---|---|
| Header | Title "Checklists". Subtitle "General to-dos, checklist items of active tasks and reviews · 21 open". No section selector: one surface holds every card. The expanding Search icon (§2.3.1) sits left in the commands row; search applies to all cards: item text, parent title and CR reference, and every review field (B-09, B-17.5). Commands: **Add to task…** (task picker → focuses that group's Add-item box, B-09) and accent **New to-do**. |
| Groups surface | An outer `ScrollViewer` with a virtualizing `ItemsRepeater` of group cards using `UniformGridLayout` (`ItemsStretch=Fill`, `MinItemWidth` 380) `[gallery-itemsrepeater-2]`. Inner items use a nested `ItemsRepeater`, which shares the outer effective viewport so both levels virtualize (T-08). Order: **General to-dos** first, **Reviews** second (both always shown), then task groups (newest parent first, then ID). A task group shows only while it has an open item; one whose last open item is checked here (or which "Add to task…" chose) stays until the user leaves the page (B-09). Completed (n) sections list the most recently completed item first. |
| General to-dos card | Glyph + "General to-dos" (HeadingLevel 2), "3 of 7 done" + `ProgressBar`, items, an inline **Add an item** `TextBox` (Enter adds; blank input → inline error, no record), and a **Completed (n)** `Expander`, collapsed by default `[gallery-expander-1]`. |
| Task group card | Effective title (HeadingLevel 2) + Service chip; "CR-1042 · Task 00457 · In Progress"; **Open task** link; progress; incomplete items; Add an item; Completed (n) expander. **Proposed note (Phase 2, pending owner review):** the Add an item box has an icon **Add** button beside it (`Checklists_AddItemButton_{GroupKey}`, name "Add item", tooltip "Add item (Enter)"), so pointer, touch and UI Automation users have an explicit route besides Enter. |
| Item row | Drag handle (`GripperBarVertical` glyph, decorative). `CheckBox` with the item text as content. Double-click/F2 renames inline (`TextBox`, Enter save, Esc cancel). ⋯ `MenuFlyout`: Rename, **Pop out to card** (task items only, B-16.3), Move up, Move down, Delete…. Completed items show strikethrough **and** sit in the Completed section, so color is never the only cue. |
| Reviews card | Styled like General to-dos (B-17.2): glyph + "Reviews" (HeadingLevel 2), "2 of 5 done" + `ProgressBar`, one line per open review: `CheckBox` (Done), Title, and the reference caption "CR-2201 · Task 0091" (text, not a link), plus a ⋯ `MenuFlyout` with **View/Edit** (Enter, or tap the text), Move up/down and **Delete…**. An inline **Add a review** `TextBox` + Add button (Enter adds a title-only review; blank input → inline error, no record), a **New review…** link (the full review dialog), and a **Completed (n)** `Expander` (collapsed) whose rows show "Done Oct 20". |
| Delete confirmation | A light `Flyout` anchored to the row: "Delete 'Draft API spec'? This can't be undone." with **Delete** / Cancel. This applies to checklist items, General to-dos and reviews (B-05). |

| | L | M | S |
|---|---|---|---|
| Groups | `UniformGridLayout`, 2–3 columns (`MinItemWidth` 380) | 1–2 columns | 1 column, full-width inputs (T-08.1) |

States:
- Loading and Error.
- Empty is never shown for Checklists, because the General to-dos card always exists.
- Filtered-empty: "No items match 'abc'" + Clear search.
- With no reviews the Reviews card still shows its heading, "0 of 0 done", the Add a review box and New review….

**Pop out** removes the row and shows the save status "Moved to a Subtask card in Backlog — Open card" (one transaction, B-16.3).

**Proposed notes (Phase 5, pending owner review):**
- Until Phase 8 the item **⋯** menu offers only **Pop out to card**; Rename, Move up/down and Delete arrive with the rest of the Checklists work, as does Ctrl+Shift+P (§7.6).
- "Open card" is a link beside the status-bar text (`Shell_SaveStatusActionLink`). It stays for 15 seconds or until the next command, so a keyboard user can reach it; the announcement reads "Moved to a Subtask card in Backlog". The status name is read from the committed card, never guessed.

**Proposed notes (Phase 8, pending owner review):**
- **Layout:** the groups surface uses a custom virtualizing `ColumnPackingLayout` (Presentation) instead of `UniformGridLayout`: each card goes to the shortest column in order, so the General to-dos card is always first (top left) and cards keep their own heights. Columns: up to 3 at L, 2 at M, 1 at S (`MinColumnWidth` 380 at L/M). Cards not yet measured use the average measured height, so positions below the viewport can shift as cards are realized.
- **Reorder** uses Move up / Move down (menu, Alt+Shift+Up/Down) among open items and open reviews; completed ones are shown by completion, so Move is unavailable there. Drag reordering of checklist items and reviews is not built. Completion recency comes from the audit timestamp for General to-dos and reviews (CompletedOn, then UpdatedAtUtc); task checklist items have no completion timestamp, so the page orders the items it saw completed this session first and the rest in stored order.
- **Rename** shows an inline `TextBox` (F2, double-click, or menu Rename): Enter saves, Esc cancels; leaving an unchanged box closes it, and a changed box stays open until Enter or Esc.
- **Search:** a task group shows when its title, service, CR number or task number matches, or when an item matches; when the group itself matches all its items show, otherwise only the matching items. The General to-dos and Reviews cards stay shown and list only their matching rows.
- **Add to task…** is a flyout with a task search box. Choosing a task shows its group until the user leaves the page, even when it has no open items, clears the search, and focuses the group's Add box.
- A review drill-down (§2.9) opens the Checklists page, applies its scope to the Reviews card and brings that card into view.
- **New to-do** (accent, Ctrl+N) focuses the General to-dos Add box; there is no separate dialog.
- The **Reviews** card's rows use nested `ItemsRepeater`s like the other cards; review volumes are expected to be small.
- The Checklists nav item's `InfoBadge` counts open General to-dos, open checklist items of active tasks, and open reviews; its accessible name is "Checklists, n open".

### 4.6 Settings (B-04, B-11, T-09)
**Purpose:** appearance, workflow, lookups, fields, defaults and data.

**Native settings cards (owner decision, ADR 0021).** "SettingsCard" and "SettingsExpander" below name two Presentation styles, not Toolkit controls:
- **SettingsCard style:** a `Grid` card with icon, header, description and a right-aligned content slot. It uses `CardBackgroundFillColorDefaultBrush` with a `CardStrokeColorDefaultBrush` border, the control corner radius, and Light/Dark/HighContrast resources.
- **SettingsExpander style:** a native `Expander` styled the same way, holding its rows.
- **Actions:** a clickable card is an explicit `Button` (or a `HyperlinkButton` row) inside the card, never a click-enabled card, so UIA Invoke works.
- **Accessibility:** every card sets `AutomationProperties.Name` to its header.
- **Layout:** the 1000-epx cap sits on a `Grid` wrapper inside the `ScrollViewer` (spike 4 found that a capped `StackPanel` directly in a ScrollViewer overflows).

Visual reference: `[toolkit-settingscard-9, toolkit-settingsexpander-1, -2, -4]`.

| Section (HeadingLevel 2) | Cards |
|---|---|
| Appearance | **App theme** `ComboBox`: Use system setting / Light / Dark. It applies immediately (AC-23). |
| Workflow | `SettingsExpander`s, one for each of **Task statuses**, **Change request statuses**, **Priorities** and **Squads**. The row layout and actions are described after this table. |
| People and tags | Expanders for People and Tags. The rows are the same as the workflow rows. Person rows show the CR Owner count. |
| Custom fields | Expanders for Task fields and CR fields. Rows: label, kind, required/default chips, ⋯ (Edit…, Move up/down, Deactivate…). **Add field…** opens the field dialog: Label*, Key* (proposed from the label, editable until first save), Kind* `ComboBox`, Required `ToggleSwitch` (if records exist, shows a backfill preview step), Default, Lookup source. Built-in fields are not listed (B-11). |
| Defaults | Default task status, Default CR status, Default priority, **Default CR Owner** (person picker with "None"), CR scheduling status, and Calendar workweek (read-only "Sunday–Thursday", §11 Q5). |
| Views | Restore default UI preferences (Grid layout, filters, window). The confirmation states that business data is unaffected (B-11). |
| Data | A **SettingsCard** "Data location" with the path, `IsTextSelectionEnabled`, and an **Open folder** button. **Backups**: last backup time, **Back up now**, a **Manage backups…** link. **Import, export and restore** (a card whose full-width `Button` shows a chevron) → the Data page. |
| About | Version, schema version, dataset ID (copyable), Third-party notices (opens the bundled text file). |

**Workflow rows** (task statuses, CR statuses, priorities, squads):
- Each row shows a drag handle, the name, **flag chips as text** (Default, Backlog, Sets start date, Completed · sets end date, Scheduling), and a usage count ("12 tasks · 3 Subtask cards").
- Row ⋯ menu: Rename (inline), Set as default, flag `ToggleMenuFlyoutItem`s (Completed, Sets start date, Backlog), Move up, Move down, Remove…. `[gallery-menuflyout-2]`
- The expander ends with **+ Add status** (inline `TextBox` + Add).
- Rules: names must be unique (inline error), each lookup type keeps at least one item, and a rename is never a status transition (B-04).

**Data page (Settings › Data, D13):**
- A `BreadcrumbBar` "Settings › Data" `[gallery-breadcrumbbar-1]`; the last crumb is non-interactive.
- A `SelectorBar`: **Import · Export · Restore**.
- Each workflow below is a wizard rendered in the page, not a dialog, so file pickers and long previews fit.
- At L a step list (non-interactive `ListView`, the current step marked with text "Current step") sits on the left. At M/S the step list becomes the text "Step 2 of 5 · Map columns" (HeadingLevel 2).
- Footer: **Back**, **Next**/**Apply** (accent), **Cancel**.
- Pickers: `Microsoft.Windows.Storage.Pickers` (works unpackaged). Verify this in spike 6/8.
- Work runs off the UI thread with a determinate `ProgressBar` and a **Cancel** that is honored only before commit (T-10).

| Workflow | Steps |
|---|---|
| Export (B-12) | 1 **Scope**: Entire dataset / Current view results. Arriving from a page's Export… presets the view and states its entity type and filters. 2 **Format**: JSON / Excel (.xlsx) / CSV (single table `ComboBox` or ZIP bundle). Limitations are explained per format. 3 **Destination**: save picker. 4 **Export**: progress. 5 **Result**: counts per entity, path, **Open folder**, limitations. |
| Import (B-12, B-13) | 1 **Source**: file(s), or a folder of Markdown (+ optional General checklist file). Detected format. **Source namespace** `TextBox` (B-13.5). Mode: Create only (default) / Merge (explicit). 2 **Map**: per table, the entity type `ComboBox` and a column mapping table (source column → field `ComboBox`, sample values). Ambiguous aliases are flagged. 3 **Lookups and relationships**: unknown statuses and lookups (map / create proposed); CR-level conflicts (pick one value per field, B-13.7); tasks with multiple CRs (pick at most one; the choice is permanent); CR Owner resolution (default proposed). Link-permanence conflicts are listed as non-applicable. 4 **Preview**: counts for Create / Update / Skip / Conflict / Error, a filterable `ListView` of per-record findings, "data present / absent" summary (B-13.4). Apply is disabled while errors exist. 5 **Apply**: a safety backup for a non-empty target, then an atomic apply with progress. 6 **Result**. |
| Restore (T-09) | 1 **Choose backup**: a `ListView` of `data/backups` (date, kind daily/manual/safety, size) + **Browse…**. 2 **Validate**: a checklist of results (schema version, integrity, foreign keys, checksums, IDs/links), each with glyph + text. 3 **Confirm**: inline warning "Restoring replaces the current dataset. A safety backup will be created first." with the explicit checkbox "I understand the current data will be replaced" → **Restore**. 4 **Restore**: progress; editing is blocked app-wide (App InfoBar). 5 **Result**: success, or a recovery message. A failure leaves the previous data in place. |

| | L | M | S |
|---|---|---|---|
| Settings | Cards max width 1000 (on a `Grid` wrapper), aligned left | Full width | Full width; the card's content slot wraps below the header through the card style's `VisualStateManager` |
| Data wizard | Step list + content | Step caption + content | Mapping table becomes stacked rows (source → field) |

**Proposed notes (Phase 10, pending owner review):**
- The cards are a Presentation `SettingsCard` control (icon, header, description, action slot; the action wraps below at S) and the native `Expander` with a `SettingsExpanderStyle`, per ADR 0021.
- Row actions are the ⋯ menu only (Rename inline, Set as default / Make default CR Owner, flag toggles, Move up/down, Remove…); the drag handle is not built. People and tags have no configured order and list by name.
- Row usage counts come from the committed projections (display only); the removal dialog reads authoritative counts from the database.
- Defaults: the Default CR Owner is a `ComboBox` with "None" plus every person (no add-new here). The "Move to backlog" status offers only statuses flagged Backlog, and that status cannot lose its Backlog flag (Calendar backlog consistency, B-08.3/B-08.11).
- Custom fields: Key and Kind are fixed after the field is added. A field can be made required only while every record of its target has a value (no backfill step yet). A Lookup option that stored values use cannot be removed. People and Tags fields have no default value.
- Not built in this phase: **Back up now** and **Manage backups…** (Phase 15), the Data page (the card is disabled, Phases 13–15), **Views › Restore default UI preferences**, and **About**.
- Settings changes use concurrency stamps instead of row revisions (ADR 0022).

**Proposed notes (Phases 13–15, pending owner review):**
- **Data page placement:** Settings › Data replaces the Settings cards inside the Settings destination (no new NavigationView item); the breadcrumb's "Settings" crumb returns to the cards. The Data card's **Open**, Backups' **Back up now**, and **Manage backups…** are enabled; Grid **Export…** opens Data › Export preset with "Current view results".
- **Fourth SelectorBar item "Backups"** (Manage backups): list with date, kind, size and manifest state as text, **Back up now**, **Open backups folder**, and **Delete…** on manual backups only (confirmation dialog, Cancel default). Daily and safety backups cannot be deleted from the app.
- **Import step 1** asks the source type explicitly ("A Max Planner export" or "Other data") instead of detecting it; a Max Planner export skips steps 2–3 because it carries its own IDs and definitions. Merge is a `ComboBox` choice next to the source.
- **Export steps** are Scope → Format → Export → Result; the destination is chosen with the save picker when **Export…** is pressed (no separate Destination step content beyond a help text).
- **Steps without a separate Apply step:** Import's Preview step's accent button is **Import** (Apply), and the Result step shows the outcome; Restore's Confirm step's accent button is **Restore**. Progress is an indeterminate `ProgressBar` with a status text (not determinate), and **Cancel** cancels the current step's work before any commit.
- **Mapping table** is a column list (header, sample value, field `ComboBox`) per source table with the entity `ComboBox`; it does not switch to a different stacked layout at S (the rows already stack their three parts in one grid row).
- **Lookups and relationships** shows one `ComboBox` per decision: each unknown value ("Create "X"" or "Use existing "Y"", exact-name matches preselected), each differing CR-level value (no preselection; an error until chosen), each task with several CRs ("Don't link" or one number; an error until chosen), and the owner for CRs without one (the default CR Owner preselected).
- **Restore result** is reported in an app InfoBar (`Shell_DataNoticeInfoBar`), because the Data page closes with the dataset session; the restored dataset reopens on its last page.
- **Recovery page:** InterruptedRestore offers **Finish the interrupted restore**; Corrupt, DatabaseMissing and MigrationFailed offer **Restore from a backup…** with an inline confirmation step before the folder picker.

## 5. Dialogs and editors

### 5.0 Common rules
- **ContentDialog** `[gallery-contentdialog-1, -2]`:
  - Uses the main window's `XamlRoot` and the current `RequestedTheme`, and dialogs are **serialized** by the `DialogService` (T-08).
  - Dialogs **never stack**. Every nested decision is an **inline step** inside the same dialog (the content swaps to a step panel with **Back**), or the first dialog closes before the next one opens. The one exception is the §5.8 dirty-close confirmation: it is a modal layer drawn above the open editor within the same window, not a second `ContentDialog`.
  - A press (left mouse button, pen or touch) on the smoke outside a dialog closes it **exactly like Cancel** (Esc, the Close button): an editor with unsaved changes shows §5.8, and a confirmation never takes its Primary action. Presses inside the dialog or its own flyouts and drop-downs do not close it.
  - The title row is a `Grid`: the title, a secondary caption (e.g. "Created Oct 6, 2026 · last saved 2 minutes ago"), and a right-aligned **More** `DropDownButton` (⋯) for secondary record commands `[gallery-dropdownbutton-1]`.
  - Footer: Primary = **Save** (or a verb such as "Create task" or "Link permanently"), Close = **Cancel**, Secondary only where it is listed below. `DefaultButton = Primary` for editors and `Close` for destructive confirmations.
- **Width:**
  - L: a local `ContentDialogMaxWidth` override of 840, with two-column field groups (`Grid` with 2 star columns).
  - M: max 640, one column.
  - S, or a window height under 600: `FullSizeDesired = true`, one column.
  - In every case the content is a `ScrollViewer` and the footer buttons stay visible (T-08.1).
- **Required** fields show the header text followed by " *" and `AutomationProperties.IsRequiredForForm=True`. The record editors (Task, CR, Review, Subtask card) show no separate legend because each required field is already marked.
- **Validation** (B-02.9):
  - The error text sits directly under the field (`CaptionTextBlockStyle`, `SystemFillColorCriticalBrush`, Error glyph), and the field gets the error stroke.
  - On Save, a summary InfoBar (Error) says "Fix 2 fields to save". Focus moves to the first invalid field and its error is announced.
  - Limits (B-02.10) are enforced with `MaxLength` where the limit is exact, plus a counter "248/250" when within 10% of the limit. Text is never truncated silently.
- **Saving:**
  - The primary and secondary buttons are disabled.
  - A content footer row shows a 16-epx `ProgressRing` and "Saving…".
  - Repeated clicks are ignored, and Esc, Cancel and an outside click do nothing until the command finishes.
  - On failure the draft is kept, an InfoBar (Error) shows the typed message, and the buttons are re-enabled.
- **Dirty close** (Esc, Cancel, an outside click, window close, navigating away or a follow-up command that closes the editor) shows the §5.8 confirmation.
- **Pickers and inputs:**

| Input | Control |
|---|---|
| People / tags (multi) | **TokenPicker** (Presentation control, ADR 0021): an `AutoSuggestBox` with case-insensitive suggestions and an "Add 'name'" suggestion, plus a wrapping `ItemsRepeater` of removable chip `Button`s (Delete removes). A new name is created only on Save (B-02.6). |
| Single person (Owner, Review developer) | `AutoSuggestBox` with the same add-new behavior |
| Lookups | `ComboBox` |
| Dates | `CalendarDatePicker` with a **Clear** button beside it ("Clear start date"). An explicit clear is tracked for B-04. |
| Numbers | `NumberBox` |
| URLs | `TextBox` + an **Open** button, enabled only for http/https (B-02.11) |
| Multiline | `TextBox` with `AcceptsReturn`, `TextWrapping=Wrap`, MinHeight 96, MaxHeight 320 `[gallery-textbox-4]` |

### 5.1 Task editor (B-02, B-03, B-05, T-13, AC-28/29)
| # | Group | Fields (in order) | Behavior |
|---|---|---|---|
| 1 | Identity | **Title** | Unlinked: an editable `TextBox`*. Linked: a read-only `TextBlock` (selectable) with the helper "Inherited from CR-1042. Edit it on the CR." + **Open CR** link. |
| | | **Service** | Marked * when linked or a link is pending. Helper when linked: "Tells this task apart from the other 2 tasks on CR-1042." |
| 2 | Change request | Link card | **Unlinked:** the text "Not linked to a CR" + a **Link to CR…** button (inline step §5.6). **Pending:** a CR summary card + an InfoBar (Warning) "This link is permanent. When you save, the title becomes 'Card limits API' and the task number, squad and owner come from CR-1042." + **Choose a different CR** (allowed only while pending). **Linked:** a read-only card with the CR number link, title, CR status, Owner and "3 linked tasks". There is **no Unlink and no Change** (B-03.7). |
| | | Task number · Squad · Owner | Read-only text, side by side at L. Helper: "From CR-1042. Edit them on the CR." Unlinked: Squad "General", no task number, "No owner". |
| 3 | Workflow | **Status***, **Priority*** | `ComboBox`es. The creation route preselects the column status (B-02.1). |
| 4 | People and tags | Designers, Developers, Tags | Token pickers |
| 5 | Dates | Start date, End date | Helpers come from the flags: "Set automatically when moved to In Progress" / "Filled when the task is completed". A range error appears under End date. |
| 6 | Custom fields | Per definition order | Typed |
| 7 | Checklist items | A `ListView` with `CanReorderItems` and an inline add box | Row: `CheckBox`, text (F2 rename), ⋯ (Rename, Pop out to card, Move up/down, Delete). These edits are part of the draft. **Pop out to card** runs immediately and is disabled while the draft is dirty (tooltip "Save the task first"). |
| 8 | Subtask cards | A list: subtitle, status, dates | **Add Subtask card** (inline subtitle box; creates immediately; disabled for an unsaved new task). Opening a card closes this dialog (after §5.8) and opens §5.2. |
| 9 | Notes | Multiline | — |

- **More menu:** Copy… (§5.5), Archive / Restore, Add Subtask card, Delete… (§5.7).
- **Archived task:** an InfoBar (Informational) "Archived tasks are read-only. Restore to edit." with **Restore**. All fields are disabled except **Link to CR…** when the task is unlinked (B-05).
- **Layout by width:** L puts groups 1–2 on the left and groups 3–6 on the right, with groups 7–9 full width below. M and S use one column.

### 5.2 Subtask card editor (B-16)
- **Header:** "Subtask card" plus the caption "Subtask of {parent title}".
- **Read-only parent block** (`CardBackgroundFillColorSecondaryBrush`): the parent Title (an **Open parent** link), Service, CR number, Task number, Squad ("General"), and Owner.
- **Own fields:** **Subtitle*** (250), **Status*** (task statuses), Start date, End date (with B-04 helper text), Notes.
- **More menu:** Delete… (confirmation names the subtitle).
- **Footer:** Save / Cancel.
- **Archived parent:** the dialog is read-only and an InfoBar says "The parent task is archived."
- **Parent effects:** saving never changes the parent task's status or dates. The helper under Status states this (AC-32).

**Proposed notes (Phase 5, pending owner review):**
- The same dialog creates cards: the title is "New Subtask card" and the primary button **Create card**. It opens from the Board/Grid card menu "Add Subtask card" and the task editor's More › Add Subtask card (the task editor closes first, §5.0). The task editor's inline quick-add box (§5.1 group 8) stays as the fast route.
- The parent title in the read-only block is the **Open parent** link; opening it closes this dialog first (after §5.8). More › Delete… also closes this dialog first, then the §5.7 confirmation opens, because dialogs never stack.
- The parent block shows "No owner" and squad "General" for an unlinked parent, "None" for a parent without a service, and the CR number placeholder of the Grid for an unlinked parent.
- Conflict review and Reload work as in §5.10, over Subtitle, Status, Start date, End date and Notes.

### 5.3 CR editor (B-03, B-11, T-13, AC-04/30/31)
| # | Fields | Behavior |
|---|---|---|
| — | InfoBar (Warning), shown when overdue | "Overdue by 7 days — Planned end was Tuesday, October 14. Reschedule or complete this CR." Uses the same predicate as the Dashboard (B-15). |
| 1 | **CR number*** · Task number | Side by side at L. Number helper: "Must be unique." A duplicate error reads "CR-1042 already exists" + **Open CR-1042**. Task number helper: "Shared by all 3 linked tasks." Leading zeros are kept. |
| 2 | **Title*** | Helper: "Every linked task shows this title; their services tell them apart." |
| 3 | **Owner*** · Squad | Owner: a person picker prefilled with the default CR Owner. **If no default exists**, a `CheckBox` "Make {name} the default CR Owner" (checked) appears under the field once a person is chosen. This is the first-CR owner prompt (B-11), done inline. Squad: a `ComboBox` with "General (no squad)". |
| 4 | **Status*** · **Priority*** | — |
| 5 | Planned start · Planned end | Both or neither; an inline error otherwise. Caption: "Unscheduled" when both are empty. **Move to backlog** sits in the More menu. |
| 6 | Email subject; Solution design link (+ Open); Description | — |
| 7 | Custom fields | — |
| 8 | **Linked tasks (3)** | A `ListView`: service chip, effective status, "Checklist 2/5", an "Archived" chip. A row opens the task (closes after §5.8). **+ Create task**: for a new or dirty CR, an inline step "Save CR-1042 and create a task?" → Save and create (T-13). **Link existing task…**: inline step §5.6 (task-side picker). |

- **More menu:** Move to backlog, Delete… (inline cascade step §5.7). **No Archive** (B-05).

**Proposed notes (Phase 6, pending owner review):**
- **Owner** uses the TokenPicker in single-selection mode (one chip; choosing another name replaces it), so suggestions and "Add 'name'" match the people pickers. A new person is created only by Save (B-02.6).
- "Make {name} the default CR Owner" is offered whenever no default exists, also on an existing CR, and saving with it checked sets the default in the same transaction.
- The four field pairs (Number · Task number, Owner · Squad, Status · Priority, Planned start · end) are side by side at L (window ≥ 1008 epx) and stacked below that.
- **+ Create task** on a saved, unchanged CR closes the editor and opens the task editor with that CR preselected as a pending permanent link (§5.1 "Pending"); closing that new task without other changes does not ask about unsaved changes. A new or changed CR first shows the inline step "Save CR-1042 and create a task?" with **Save and create**.
- A linked task row opens the task (Enter or click) after this dialog closes. **Link existing task…** (reverse picker, §5.6) is not built yet; custom fields are Phase 10. **Move to backlog** is in the More menu since Phase 7: the editor closes (after §5.8) and the command runs with the committed CR's revision.
- An empty CR has the text "No tasks are linked to this CR yet." instead of an empty list.

### 5.4 Review editor (B-17)
**Title*** (250); **CR number** (text, 100); **Task number** (text, 100); **Services** (free-text token box, each ≤250); **Developer** (single person picker, add-new on save); **Review comments** (multiline); **Done** `CheckBox` with the caption "Completed on Oct 20, 2026" when checked.

- **CR number:** when it matches an existing CR's normalized number, an **Open CR CR-1042** `HyperlinkButton` appears beside it. It never links (B-17.3).
- **More menu:** Delete….
- The dialog title is "Review" in view/edit mode and "New review" when creating.

**Proposed notes (Phase 8, pending owner review):**
- Services use the TokenPicker with free-text chips only (no suggestions from other records). The picker's "Add 'name'" suggestion appears for names up to 100 characters (the people/tag limit), although a service may hold 250.
- A conflict (the review changed elsewhere) keeps the draft and offers **Reload** only; the per-field conflict review of §5.10 is not built for reviews.
- **More › Delete…** closes the editor first and then asks in a confirmation dialog (the row flyout is used from the list).
- The Done caption reads "Completed on {date}" with the stored completion day, or local today for a newly checked box; the stored day is set by the save (B-17.1).

### 5.5 Copy task (B-06, AC-09)
**Step 1, Copy options:**
- Caption "Copy of: {effective title} · CR-1042".
- **Title*** (prefilled from the effective title).
- **Service** (blank; * if a CR is chosen).
- InfoBar (Informational): "New copy will be unlinked."
- Optional **Link to CR…** (inline §5.6, which includes the permanence notice).
- **Status*** (Backlog) and **Priority*** (the source priority if it still exists, otherwise the default).
- Copy checkboxes, all unchecked: People, Dates, Tags, Notes, Checklist items (with an indented "Preserve completion"), Custom values.
- The note "Subtask cards are not copied."

**Step 2, Review:** a read-only summary of the resulting draft. Primary **Create copy**, plus **Back**. Cancel creates nothing.

### 5.6 Link to CR (picker + permanence step) (B-02.4, B-03.6, AC-29)
Route: the task editor and copy dialog (inline), the Grid row ⋯ "Link to CR…" (its own dialog), and the CR editor "Link existing task…" (reverse picker).

1. **Pick:**
   - An `AutoSuggestBox` searches by number, title or owner.
   - The `ListView` rows show the CR number, title, Owner, and "{n} linked tasks". A CR is never shown as "occupied" (B-03.6).
   - The reverse picker shows only **unlinked** tasks (active and archived, each with its chip).
   - Primary **Next**.
2. **Confirm permanence:**
   - The heading is "Link permanently?".
   - A list states: "The title becomes '{CR title}'", "Task number, squad and owner come from {CR}", and "This link can't be removed or changed. To fix a wrong link, delete or copy the task."
   - If Service is empty, a required **Service** field appears in the step.
   - Primary **Link permanently** (task editor/copy: **Use this CR**, which commits on Save); Secondary **Back**; Close **Cancel**.
3. If the CR revision changed before commit, the step refreshes its preview and shows an InfoBar "CR-1042 changed — review the new values" (T-13).

**Proposed notes (Phase 4, pending owner review):**
- The link, conflict-review and reload steps carry their own inline Back / Next / Use this CR / Apply to draft / Reload buttons; the dialog footer keeps Save and Cancel, and Save is disabled while a step is open.
- While a link is pending, the Title box is replaced by the CR title preview. For an existing task the draft's own title moves to `PreLinkTitle` on Save; a new task created with its link has no own title.
- Editor checklist items reorder with Move up / Move down (menu) rather than ListView drag reordering, because the draft list is exposed read-only to the view.
- Archive and Restore run immediately (reversible, announced); Delete always confirms with Cancel as the default.

### 5.7 Delete confirmations (B-05, AC-30)
| Record | Body | Buttons |
|---|---|---|
| Task | "Delete '{title}' permanently? Its {n} checklist items and {m} Subtask cards are deleted too. Its CR stays." | Primary **Delete task**; Secondary **Archive instead**; Close Cancel; Default = Close |
| Subtask card | "Delete Subtask card '{subtitle}'? The parent task is unchanged." | **Delete**; Cancel |
| **CR cascade** (inline step of the CR editor) | Heading "Delete {CR number} and its tasks?". A list of counts: "{n} linked tasks ({a} active, {b} archived)", "{c} checklist items", "{d} Subtask cards". The note "Review items that mention this CR number are not affected." and the warning "This can't be undone." | Primary **Delete CR and {n} tasks**; Close Cancel; Default = Close. **Scope changed** at commit: the dialog stays open with an InfoBar (Warning) "The linked tasks changed. Review the new counts." The counts refresh and must be confirmed again. |
| Checklist item / General to-do / Review | Light `Flyout` (§4.5) | Delete / Cancel |

**Proposed notes (Phase 6, pending owner review):** the CR cascade is an inline step of the CR editor, so its buttons are in the step: **Cancel** (focused first; it returns to the form) and **Delete CR and {n} tasks** (a standard button, never the default). The counts are taken from the committed projection when the step opens and stay as confirmed; the CR's current revision is sent, because the confirmation is about the deleted scope, not the CR's content.

### 5.8 Dirty-close confirmation (B-02.8)
- When the user tries to close an editor with unsaved changes (any trigger listed in §5.0), the closing is cancelled and a **confirmation dialog** opens above the editor: title "Unsaved changes", body "You have unsaved changes. Do you want to save them?", and the buttons **Save** (accent, default), **Discard**, **Continue editing**. An editor without changes closes without asking.
- Because WinUI shows one `ContentDialog` per window, the confirmation is a modal layer (`DirtyCloseConfirmation`) drawn with the ContentDialog resources (background, border, corner radius, padding, smoke). Its smoke covers the window, so the editor beneath takes no input while it is open.
- **Save** runs the editor's normal Save (validation, revision check, operation ID, progress). On success the editor closes; on a validation error or conflict it stays open with the error, the draft is kept, and focus moves to the first invalid field. **Discard** closes the editor without saving. **Continue editing** returns to the editor with focus on the element that had it.
- Keyboard: focus starts on Save; Enter chooses Save (or the focused button), Esc and a press on the confirmation's smoke choose Continue editing, and Tab cycles within the confirmation only.
- UI Automation: the layer reports a dialog window named "Unsaved changes" (`DirtyClose_Dialog`), with `DirtyClose_SaveButton`, `DirtyClose_DiscardButton` and `DirtyClose_ContinueButton`.
- When the window close was the trigger, `AppWindow.Closing` is deferred until the user chooses: Save (when it succeeds) or Discard continues the window close; Continue editing or a failed save cancels it.

### 5.9 Lookup removal with replacement (B-04, B-11, AC-08/31)
- **Title:** "Remove status 'Review'?".
- **Body:** "In use by:" followed by counts (tasks incl. archived, Subtask cards, CRs, review items, defaults).
- **Replacement:**
  - A **Replace with*** `ComboBox` that excludes the item being removed.
  - For Designers/Developers, tags, squads and Review developers, `RadioButtons` offer "Replace with…" / "Clear from records". CR Owner usage and statuses/priorities offer **replace only** (see §11 Q4).
- **Note:** "Dates are kept. This is not a status move."
- **Buttons:** Primary **Remove and replace**; Close Cancel.
- If the usage changes before commit, the counts refresh and a new confirmation is required.
- **Proposed note (Phase 10):** the counts are read from the database when the dialog opens (`PreviewLookupRemoval`) and sent with the command; a different count at commit keeps the dialog open with the Warning InfoBar "The usage changed. Review the new counts and confirm again." An unused value uses the same dialog with "Not used by any record or default." and **Remove**. Close (Cancel) is the default button.

### 5.10 Conflict (B-02.9, AC-07)
- An editor InfoBar (Error): "This {record} changed elsewhere since you opened it. Your changes are not saved."
- **Review changes** shows an inline step with a table: Field | Your draft | Saved now. Each row has **Use mine**, which carries the value into a refreshed draft at the new revision, and the step ends with **Apply to draft**.
- **Reload** discards the draft after a confirmation.
- The draft is never replaced silently.
- **Proposed note — single Grid cell (Phase 2, pending owner review):** an inline cell edit has one field, so the conflict is shown in the cell's error `Popup` instead of an inline step: the error text, "Saved now: {committed value}" (it follows later commits), "Your change: {draft}", **Use mine** (`Conflict_UseMineButton`: keeps the draft and commits it against the current revision) and **Reload** (`Conflict_ReloadButton`: discards the draft and shows the saved value). Both values are visible side by side, so Reload is the explicit decision and has no extra confirmation. The editor stays open with the draft until one of the two is chosen, and leaving the cell never retries a conflicting draft. Requirements are unchanged (B-02.9, AC-07).

## 6. Visual tokens

### 6.1 Chips
Anatomy: a `Border` with `CornerRadius` 4, padding 6×2, the `CaptionTextBlockStyle` text, and an optional 12-epx glyph. The text label is always present. Light and Dark use `StaticResource` redirects. HC uses `SystemColorButtonFaceColorBrush` with `SystemColorButtonTextColorBrush` text and border.

| Token | Meaning | Background → Light/Dark redirect | Foreground | Glyph | HC |
|---|---|---|---|---|---|
| `PriorityLevel1` | Highest configured priority (default "Urgent") | `SystemFillColorCriticalBackgroundBrush` | `SystemFillColorCriticalBrush` (glyph), text primary | Important | Face/Text + glyph |
| `PriorityLevel2` | 2nd ("High") | `SystemFillColorCautionBackgroundBrush` | Caution glyph | ChevronUp | Same |
| `PriorityLevelMid` | Middle ranks ("Medium") | `SystemFillColorNeutralBackgroundBrush` | Text primary | Remove (dash) | Same |
| `PriorityLevelLowest` | Lowest, when ≥3 priorities ("Low") | `SubtleFillColorSecondaryBrush` | Text secondary | ChevronDown | Same |
| `SquadChip` | Squad / "General" | `ControlFillColorSecondaryBrush` + `ControlStrokeColorDefaultBrush` | Text primary | People | Same |
| `ServiceChip` | Service (Board cards use `AccentChip`) | `SystemFillColorAttentionBackgroundBrush` | Text primary | Repair | Same |
| `ArchivedChip` | Archived | `SystemFillColorNeutralBackgroundBrush` | Text secondary | Archive† | Same |
| `AccentChip` | Board column counts and Board card service | `AccentFillColorDefaultBrush` | `TextOnAccentFillColorPrimaryBrush` (text and glyph) | Repair (service only) | Highlight/HighlightText |
| `FlagChip` | Settings flags | `SubtleFillColorSecondaryBrush` + stroke | Text secondary | per flag | Same |

Priority tokens are assigned by **configured rank**, never by name, because priorities are editable lookups (B-04). A chip's name is "Priority: Urgent".

### 6.2 Status presentation
Statuses are configurable, so status text is always shown. The glyph comes from the flags:

| Flag | Glyph |
|---|---|
| IsBacklog | CircleRing† |
| SetsStartDate | Play† |
| IsCompleted | Completed (`SystemFillColorSuccessBrush`) |
| None | No glyph |

There is no per-status color in release 1 (§11 Q8).

### 6.3 Calendar bars and dashboard emphasis
| State | Background | Stroke | Text/glyph | HC |
|---|---|---|---|---|
| Scheduled | The CR's palette fill `CalendarBarFill{i}` | 3-epx left stripe `CalendarBarStripe{i}` | Number + title + status text | ButtonFace + 3-epx ButtonText stripe |
| **Overdue** | The CR's palette fill | 1-epx `SystemFillColorCautionBrush` outline (replaces the stripe) | Warning glyph + "Overdue" + status | ButtonFace + ButtonText outline + Warning glyph + text |
| **Completed** | The CR's palette fill and stripe at 50 % opacity | 3-epx faded left stripe | Completed glyph + "Completed" status, secondary text | ButtonFace + GrayText stripe and text + glyph |
| Hover / pressed | The palette fill blended 20 % / 32 % toward the stripe, so the bar keeps its own hue | Unchanged | Unchanged | System button hover/pressed brushes |
| Selected | (unchanged) | 2-epx `AccentFillColorDefaultBrush` + the system focus visual when focused | Name suffix ", selected" | Highlight / HighlightText |
| Drop target day | `SubtleFillColorSecondaryBrush` | Dashed accent | "Drop to schedule on Oct 13" caption | Highlight |
| Continuation | — | — | ChevronLeft/ChevronRight glyph at the clipped edge | Same |

**Per-CR palette (owner decision 2026-10-26).** Bars are colored per CR so neighbouring CRs are easy to tell apart. `CalendarBarPalette` picks one of 10 entries from the CR Id (FNV-1a over the Guid bytes, mod 10; never `string.GetHashCode`, which changes per process), so a CR has the same color on every week row and in Month and Week. The entries are `CalendarBarFill0…9` (tinted, low-saturation fill) and `CalendarBarStripe0…9` (stronger accent) in `Themes/AppResources.xaml`, defined for Light and Dark in the same hue order: blue, teal, purple, orange, magenta, green, indigo, brown, cyan, rose. Primary text stays at or above 4.5:1 on every fill, including the hover, pressed and faded completed states. No entry reuses the Caution yellow, and hue never carries meaning: state is still glyph + text (P3), and the palette is only an identity aid. These are the only custom color literals in the app (a deliberate exception to §3 rule 2). In High Contrast every entry maps to ButtonFace/ButtonText and the bars use their styles' system brushes (§3 rule 3). The palette is resolved for the surface's current theme and applied as local brushes plus lightweight `Button*` overrides, so the surface re-renders on a Light/Dark change, on a High Contrast change, and when it is loaded after a theme change made while the page was cached. The agenda list (S) has no bar visual and stays uncolored.

The Dashboard "Needs attention" badge uses Warning glyph + text in `SystemFillColorCautionBrush`, and the Overdue list "Days overdue" column shows a number + "days".

### 6.4 Icon map (Segoe Fluent Icons; † = verify the codepoint in Phase 0 before use)
| Name | Code | Use | Name | Code | Use |
|---|---|---|---|---|---|
| GlobalNavigationButton | E700 | Pane toggle | Add | E710 | New |
| ViewDashboard | F246† | Dashboard | Search | E721 | Search |
| Tiles | ECA5† | Board | Filter | E71C | Filters |
| Table grid | E80A | Grid (Phase 3: F0E2 looked like Tiles) | More | E712 | ⋯ menus |
| Calendar | E787 | Calendar | Edit | E70F | Edit/Rename |
| CheckList | E9D5† | Checklists | Delete | E74D | Delete |
| Setting | E713 | Settings | Copy | E8C8 | Copy |
| ChevronLeft/Right | E76B/E76C | Period nav, continuation | Link | E71B | CR link |
| ChevronUp/Down | E70E/E70D | Priority, expanders | OpenInNewWindow | E8A7 | Open design URL |
| Important | E8C9 | Priority level 1 | Warning | E7BA | Overdue, warnings |
| Error | E783 | Errors | Completed | E930 | Completed |
| CheckMark | E73E | Saved, chip checked | Sync | E895 | Resyncing |
| People | E716 | Squad, people | Contact | E77B | Owner |
| Tag | E8EC | Tags | GripperBarVertical | E784† | Drag handle |
| Download / Upload | E896 / E898 | Export / Import | History | E81C | Restore/backups |
| Repair | E90F† | Service chip | Archive | †  | Archive chip/command |
| Remove | E738 | Priority mid | Cancel | E711 | Remove chip |

## 7. Keyboard map and focus

### 7.1 Global (`KeyboardAccelerator`s on the shell; listed in tooltips)
| Keys | Action |
|---|---|
| Ctrl+1 … Ctrl+5 | Dashboard, Board, Grid, Calendar, Checklists |
| Ctrl+, | Settings |
| Ctrl+F | Expand and focus the page search (§2.3.1); Esc in it clears and collapses it |
| Ctrl+Shift+F | Open the Filters flyout |
| Ctrl+Shift+L | Clear all filters |
| Ctrl+N | The page's primary New (task / CR / to-do / review) |
| Alt+Left | Return to the drill-down origin (TitleBar Back) |
| F6 / Shift+F6 | Cycle focus: navigation pane → page header → content → status bar |
| Shift+F10 / Application key | Context menu for the focused item |
| Enter | Open the focused record |
| Delete | Delete… for the focused record (always confirmed) |
| Ctrl+D | Copy… (focused task) |

### 7.2 Dialogs
Enter = Save (except inside multiline fields); **Ctrl+Enter** = Save from anywhere; Esc = Cancel (→ §5.8 when dirty); Alt+Left = Back on an inline step; Tab order follows the field order in §5.

### 7.3 Board
| Keys | Action |
|---|---|
| Up/Down, Home/End | Move within a column (ListView) |
| Ctrl+Left/Right | Focus the adjacent column (same index, clamped) |
| **Alt+Shift+Left/Right** | Move the card to the previous/next status (placed at the top; B-07) |
| **Alt+Shift+Up/Down** | Reorder within the column (disabled while filtered) |
| Space | Toggle a focused checklist checkbox on a card |
| Shift+F10 → Move to ▸ | Any status, Top/Bottom |

### 7.4 Grid
| Keys | Action |
|---|---|
| Arrows, Home/End, Ctrl+Home/End, PageUp/PageDown | Move between cells/rows |
| **F2** / double-click / typing | Edit the cell |
| **Enter** | Commit (stay on the cell; Enter again moves down) |
| **Esc** | Cancel the edit |
| **Tab / Shift+Tab** | Commit, then move to the next/previous editable cell **only on success** |
| **Ctrl+Enter** | Commit a multiline (Notes) editor; plain Enter inserts a line break |
| Alt+Down | Open a ComboBox/date/people editor |
| Header focused: Enter / Alt+Left/Right / Ctrl+Shift+Left/Right | Sort / resize ±16 epx / move the column |

### 7.5 Calendar
| Keys | Action |
|---|---|
| Arrows | Move between days (Fri/Sat skipped; week rows wrap) |
| PageUp/PageDown | Previous/next month or week; Ctrl+T Today |
| Tab | Day cell → its bars → "+N more" → next day |
| Enter on a day | Open the day flyout (the CRs on that date + "New CR on this date") |
| Enter on a bar | Open the CR |
| **Shift+Left/Right** on a bar | Planned end −/+1 day |
| **Ctrl+Shift+Left/Right** on a bar | Planned start −/+1 day |
| Ctrl+E on a bar | Edit dates… flyout (two date pickers) |
| Backlog card: **Ctrl+Shift+S** or menu Schedule on… | Date flyout (default = the focused day, else today) |
| Ctrl+Shift+B | Move to backlog (confirmed inline) |

Each keyboard date change is committed after 600 ms of inactivity or on Enter, so it is a single command, and the result is announced ("CR-1042 now Oct 13 to Oct 17").

### 7.6 Checklists
Space = toggle checkbox (items and reviews); F2 = rename; **Alt+Shift+Up/Down** = reorder; Ctrl+Shift+P = Pop out to card; Enter in the Add box = add; Delete = delete (flyout confirm); Enter on a review row = View/Edit.

### 7.7 Focus restoration
| Event | Focus goes to |
|---|---|
| Dialog closed | The invoking element. If that element was deleted, the next item in the same list, otherwise the list itself. |
| Dirty-close confirmation closed | Continue editing: the editor element that had focus before the confirmation opened. A failed Save: the first invalid field, else that element. Save or Discard that closes the editor: as "Dialog closed". |
| Board/Checklist move | The moved card or item in its new position. The scroll position is kept (B-07). |
| Grid commit | The same cell. On Tab, the next editable cell. |
| Incremental projection update | Focus, the draft and scroll are kept, and updating an item never resets its container (T-07; verified in spike 5). Further rules from spike 5 (ADR 0010):<ul><li>**Selection** is held in ViewModel state and restored after each change set, because the ListView clears it when another row moves above it. The transient null `SelectionChanged` this causes is ignored.</li><li>**A focused or edited item that re-sorts:** while it has an open editor or an unsaved draft, its position is held and the move is applied when editing ends. Otherwise focus follows the item, and if its new position is off-screen, the list scrolls just enough to bring it into view. This is the only case where scroll changes.</li></ul> |
| Flyout Apply/Cancel | The Filters button |
| Search collapsed (Esc / Clear) | The Search button. Tab out of an empty box collapses it and focus continues in tab order. |
| Breakpoint change / Snap / DPI | The same element when it is still visible. If it was collapsed into overflow or a pane, its route button. |
| Page navigation | The page title (programmatic focus with a heading), then the user moves on with Tab. Return via drill-down restores the prior focused element. |

## 8. Accessibility

### 8.1 AutomationId convention
`Area_NameSuffix` in PascalCase. Suffixes: `Button`, `Box`, `Combo`, `Toggle`, `Check`, `List`, `Item`, `Picker`, `Date`, `Link`, `Menu`, `Chip`, `Dialog`, `Step`, `Bar`, `Card`. **Repeated items** append a record key: `{Area}_{Name}_{Guid:N}` for records and `{Area}_{Name}_{Key}` for lookups (stable id), e.g. `Board_TaskCard_3f2a…`. Display text is never part of an AutomationId.

| Area | Key ids |
|---|---|
| Shell | `Shell_TitleBar`, `Shell_Nav`, `Shell_NavDashboardItem`, `Shell_NavBoardItem`, `Shell_NavGridItem`, `Shell_NavCalendarItem`, `Shell_NavChecklistsItem`, `Shell_NavSettingsItem`, `Shell_BackButton`, `Shell_AppInfoBar`, `Shell_SaveStatus`, `Shell_StatusSummary` |
| Common | `{Page}_SearchButton`, `{Page}_SearchBox`, `{Page}_SearchClearButton`, `{Page}_FiltersButton`, `{Page}_FiltersApplyButton`, `{Page}_FiltersCancelButton`, `{Page}_FiltersClearButton`, `{Page}_ClearAllFiltersLink`, `{Page}_FilterChip_{Field}`, `{Page}_StateView`, `{Page}_RetryButton`, `{Page}_ReturnToDashboardButton` |
| Dashboard | `Dashboard_SquadCombo`, `Dashboard_PriorityCombo`, `Dashboard_OwnerCombo`, `Dashboard_ClearFiltersLink`, `Dashboard_ActiveTasksCard`, `Dashboard_CompletedTasksCard`, `Dashboard_UnscheduledTasksCard`, `Dashboard_OverdueCrsCard`, `Dashboard_ActiveSubtaskCardsCard`, `Dashboard_ReviewsCard`, `Dashboard_StatusChart`, `Dashboard_StatusChartTableToggle`, `Dashboard_StatusBar_{StatusId}`, `Dashboard_SquadBar_{SquadId|General}`, `Dashboard_ReviewDevBar_{PersonId|Unassigned}`, `Dashboard_OverdueList`, `Dashboard_OverdueRow_{CrId}`, `Dashboard_OverdueViewAllLink`, `Dashboard_UnlinkedList`, `Dashboard_UnlinkedRow_{TaskId}`, `Dashboard_UnlinkedViewAllLink` |
| Board | `Board_NewTaskButton`, `Board_Column_{StatusId}`, `Board_ColumnAddButton_{StatusId}`, `Board_ColumnMenu_{StatusId}`, `Board_ColumnList_{StatusId}`, `Board_TaskCard_{TaskId}`, `Board_SubtaskCard_{CardId}`, `Board_CardMenuButton_{Id}`, `Board_CardCrLink_{Id}`, `Board_ChecklistCheck_{ItemId}`, `Board_ShowAllLink_{TaskId}`, `Board_ColumnPickerCombo`, `Board_PrevColumnButton`, `Board_NextColumnButton`, `Board_FilteredInfoBar` |
| Grid | `Grid_Table`, `Grid_ShowArchivedToggle`, `Grid_ColumnsButton`, `Grid_ColumnCheck_{ColumnKey}`, `Grid_ExportButton`, `Grid_NewTaskButton`, `Grid_Header_{ColumnKey}`, `Grid_Row_{Id}`, `Grid_Cell_{Id}_{ColumnKey}`, `Grid_RowMenuButton_{Id}`, `Grid_CellEditor` |
| Calendar | `Calendar_ModeBar`, `Calendar_ModeMonthItem`, `Calendar_ModeWeekItem`, `Calendar_ModeRecordsItem`, `Calendar_TodayButton`, `Calendar_PrevButton`, `Calendar_NextButton`, `Calendar_PeriodButton`, `Calendar_NewCrButton`, `Calendar_BacklogToggle`, `Calendar_BacklogCollapseButton`, `Calendar_BacklogCount`, `Calendar_BacklogList`, `Calendar_BacklogCard_{CrId}`, `Calendar_Day_{yyyyMMdd}`, `Calendar_Bar_{CrId}_{yyyyMMdd}` (segment start), `Calendar_BarStartThumb_{CrId}`, `Calendar_BarEndThumb_{CrId}`, `Calendar_MoreLink_{yyyyMMdd}`, `Calendar_OutsideWorkdays_{yyyyMMdd}`, `Calendar_RecordsList`, `Calendar_RecordRow_{CrId}` |
| Checklists | `Checklists_AddToTaskButton`, `Checklists_NewTodoButton`, `Checklists_GeneralGroup`, `Checklists_ReviewsGroup`, `Checklists_NewReviewLink`, `Checklists_TaskGroup_{TaskId}`, `Checklists_AddItemBox_{GroupKey}`, `Checklists_ItemCheck_{ItemId}`, `Checklists_ItemMenuButton_{ItemId}`, `Checklists_CompletedExpander_{GroupKey}`, `Checklists_ReviewCheck_{ReviewId}`, `Checklists_ReviewMenuButton_{ReviewId}` (`GroupKey` = `General`, `Reviews`, or the TaskId) |
| Settings | `Settings_ThemeCombo`, `Settings_{Lookup}Expander` (TaskStatuses, CrStatuses, Priorities, Squads, People, Tags, TaskFields, CrFields), `Settings_{Lookup}Row_{Id}`, `Settings_{Lookup}AddBox`, `Settings_{Lookup}AddButton`, `Settings_Default{Name}Combo`, `Settings_DefaultCrOwnerPicker`, `Settings_DataLocationCard`, `Settings_OpenDataFolderButton`, `Settings_BackupNowButton`, `Settings_DataPageCard`, `Data_Breadcrumb`, `Data_ModeBar`, `Data_StepList`, `Data_BackButton`, `Data_NextButton`, `Data_CancelButton`, `Data_PreviewList` |
| Dialogs | `TaskEditor_Dialog`, `TaskEditor_TitleBox`, `TaskEditor_ServiceBox`, `TaskEditor_LinkToCrButton`, `TaskEditor_CrCard`, `TaskEditor_StatusCombo`, `TaskEditor_PriorityCombo`, `TaskEditor_DesignersPicker`, `TaskEditor_DevelopersPicker`, `TaskEditor_TagsPicker`, `TaskEditor_StartDate`, `TaskEditor_EndDate`, `TaskEditor_NotesBox`, `TaskEditor_MoreMenu`, `SubtaskEditor_*`, `CrEditor_NumberBox`, `CrEditor_TaskNumberBox`, `CrEditor_TitleBox`, `CrEditor_OwnerPicker`, `CrEditor_MakeDefaultOwnerCheck`, `CrEditor_SquadCombo`, `CrEditor_LinkedTasksList`, `CrEditor_CreateTaskButton`, `CrEditor_LinkExistingButton`, `ReviewEditor_*`, `CopyTask_*`, `LinkCr_SearchBox`, `LinkCr_ResultsList`, `LinkCr_ConfirmStep`, `LinkCr_LinkButton`, `CascadeDelete_Dialog`, `CascadeDelete_Counts`, `DirtyClose_Dialog`, `DirtyClose_SaveButton`, `DirtyClose_DiscardButton`, `DirtyClose_ContinueButton`, `Conflict_ReviewButton`, `Conflict_ReloadButton`, `LookupRemove_ReplacementCombo` |

### 8.2 Names, roles and structure
- **Names:** every interactive control has a name, through visible content or a `Header` (`[gallery-accessibilityscreenreader-1, -2]`) or through `AutomationProperties.Name`/`LabeledBy` `[-5]`. Icon-only buttons name the action and its target: "More actions for CR-1042".
- **Cards and rows** set `AutomationProperties.Name` to a concise summary:
  - Board task card: "Card limits API, Payments service, CR-1067, High priority, Retail Cards, checklist 1 of 3".
  - Subtask card: "Subtask card Fix rounding, subtask of Card limits API, In Progress".
  - `HelpText` carries "Press Shift F10 for actions".
- **Structure:** page titles are `HeadingLevel 1` and sections are level 2 `[-10]`. The navigation pane is `LandmarkType=Navigation`, and the content frame is `Main`. Search boxes are `Search` `[-9]`.
- **Lists:** Board columns and Checklists groups are lists that expose `PositionInSet`/`SizeOfSet` (auto from ListView) `[-7]`. Each column list's name is "Review column, 3 cards".
- **Required and invalid fields:** `IsRequiredForForm`, plus a `FullDescription` linked to the error text.
- **Charts:** each bar row is a button named with its category, count and share (§4.1). The Table toggle exposes the same data as a list. Charts never depend on hover.

### 8.3 Announcements
Use `AutomationPeer.RaiseNotificationEvent` (`AutomationNotificationKind.ActionCompleted`/`ActionAborted`, processing `ImportantMostRecent`) from a shared `Announcer` service, plus `LiveSetting` on the status bar.

| Event | Text (resw) | Kind |
|---|---|---|
| Card moved | "Moved Card limits API to Review, position 3 of 4" | ActionCompleted |
| Move failed | "Couldn't move Card limits API. It stays in In Progress." | ActionAborted |
| Saved | "Saved" (status bar; polite) | — |
| Save failed / validation | "Couldn't save. 2 fields need attention. Title: required." | ActionAborted |
| Checklist toggle | "Draft API spec, done" / "not done" | ActionCompleted |
| Review toggle | "Review Card limits done, completed today" | ActionCompleted |
| Filters applied | "Filters applied, 8 of 32 tasks" | ActionCompleted |
| Calendar change | "CR-1042 scheduled October 13" / "now October 13 to 17" | ActionCompleted |
| Resync / storage error | App InfoBar text | Assertive |

## 9. Motion, text scaling and drag/drop
- **Motion:** use only the platform's default theme transitions (`EntranceThemeTransition` on page load, `RepositionThemeTransition` in lists, the built-in drag visuals). There are no decorative animations.
  - When `UISettings.AnimationsEnabled` is false, transitions are removed and the scroll animation for "move into view" is disabled.
  - Information never depends on motion.
- **Text scaling (100–225%):** text wraps in cards, headers and dialogs. Use `TextTrimming` only where the full text is also available in a tooltip and the accessible name.
  - Fixed heights are avoided. Board column width stays fixed, while card height grows. Calendar row height = header + lanes × (bar height scaled by `TextScaleFactor`). In Month mode the lane cap is 4; in Week mode it is however many lanes fit. When lanes no longer fit, the lane cap drops and "+N more" absorbs the rest (B-08.9).
  - Test at 200% scale and with larger text at every breakpoint (AC-16, AC-27).
- **Mouse wheel** (mouse input only; touch, pen and precision-touchpad gestures stay native):
  - The normal wheel only ever scrolls vertically. A horizontal-only scroller (Grid table, Board columns, Calendar Records, filter chips) never turns it sideways: it goes to the innermost vertical scroller under the pointer (doing nothing at that list's end), else to the region's designated list (Grid header → Grid rows; Records header → Records rows), else to the nearest vertically scrolling ancestor, else nowhere.
  - The tilt (side) wheel, and Shift+wheel, only ever scroll horizontally, to the nearest horizontally scrolling element under the pointer or ancestor, even from inside a vertical list (a Board column → the Board; Grid rows → the Grid's horizontal scroll). Tilt right and Shift+wheel down reveal content to the right.
  - Board exception: the wheel over a column's card list scrolls that column; over the column header or other non-list parts of the column it scrolls the Board horizontally; over the gaps between and after columns it does nothing.
  - Ctrl+wheel, popups and flyouts, dialogs, and controls that consume the wheel themselves (ComboBox lists, NumberBox, date pickers, multi-line text) keep their native behaviour. A routed step is 120 epx per wheel notch (one column per notch on the snapped Board at S), animated unless animations are off.
  - Mechanism: `Common/WheelScrolling` (`RoutesMouseWheel` on the scroller or list, `VerticalWheelTarget`, `VerticalWheelScrollsHorizontally` on a region).
- **Drag and drop:**
  - Use native `ListView` drag/reorder for Board columns, checklist items, reviews and Settings rows `[gallery-listview-3]`.
  - Calendar drop targets use `AllowDrop` and `DragOver` with an `AcceptedOperation` and `DragUIOverride.Caption` text such as "Schedule on Oct 13".
  - Drop feedback is a stroke plus a caption, never color only (§6.3).
  - Every drag has a keyboard and menu equivalent (§7), and touch drag works through the same handlers. Long-press opens the context menu.
  - A drop that is rejected, for example because the range would invert or reordering is disabled while filtered, shows the forbidden cursor and an explanatory caption.

## 10. Deviations from the HTML mock
| Mock element | Native choice | Reason |
|---|---|---|
| Custom web title bar | `TitleBar` control | T-08 |
| Light-only palette, hex colors | Theme resources; System/Light/Dark/HC | T-08, AC-23 |
| Hand-styled pills (purple service, colored priority) | §6.1 chip tokens with glyph + text; rank-based priority | P3, B-04 configurable lookups |
| Month/Week toggle + "CR records" button | `SelectorBar` Month · Week · Records | D13, B-08.4 |
| "Oct 2026 ˅" text menu | Period button + `CalendarView` flyout | Accessible date selector, B-08.1 |
| Card "Subtasks 0/3" | "Checklist 0/3" + a separate Subtask cards count | D16, B-07 |
| Column footer "4 more — scroll the column" | Removed; the virtualized column scrolls and the header count shows totals | Native scrolling, T-08 |
| Grid row selection checkbox column | Removed | There are no bulk commands in release 1 (B-07) |
| Filter flyout "Any selected / All selected" segmented | `RadioButtons` | No extra dependency for one segmented control |
| "Unset" squad footnote/chip | "General" | D15 |
| Checklists "General" group | "General to-dos" | D15 |
| Task editor "Change…" and "Unlink" on the CR card | Removed; **Link to CR…** only on unlinked tasks, with a permanence step | D4, B-03.7, AC-29 |
| Task editor Title editable for linked tasks | Read-only, inherited, with Open CR | D3, B-02.3 |
| Task footer "… Copy, archive, delete" | Title-row **More** `DropDownButton` (ContentDialog has only 3 buttons) | ContentDialog pattern |
| CR "Must be unique, including archived CRs" | "Must be unique." | D11 — CRs are not archived |
| CR footer "Move to backlog, archive, delete" | More: Move to backlog, Delete… (cascade step) | D5, D11 |
| No Owner anywhere | Owner on the CR editor (required), cards, Grid, Records, Dashboard filter | D14 |
| No Reviews or Subtask cards | Checklists Reviews card; Subtask cards on Board/Grid; Dashboard cards and chart | D17, D18 |
| Import/Export as menu items | Settings › Data page with `BreadcrumbBar` and wizards | D13 |
| Overdue amber bar | The CR's palette fill + Caution outline + Warning glyph + "Overdue" text | P3, §6.3 |
| Fixed desktop layout | S/M/L layouts per page | T-08.1 |
| "✓ All changes saved · F2 to edit…" Grid footer | Shell status bar; hints at L only | Consistent save status for every page |

## 11. Design questions
Q1–Q6 and Q8 were resolved on 2026-10-22, and the requirements were updated (B-04, B-07, B-08.9–10, B-10, B-16.3, B-16.6).

| # | Question | Resolution |
|---|---|---|
| Q1 | Kind filter scope between Board and Grid | **Resolved (owner):** there is no Kind filter. Grid shows both kinds with a Kind column. |
| Q2 | Which completed status a checked, popped-out item gets | **Resolved:** the first IsCompleted task status in configured order (B-16.3). |
| Q3 | Whether Subtask cards of archived parents are editable in Grid | **Resolved:** read-only (B-16.6). |
| Q4 | Clearing versus replacing a removed in-use person | **Resolved (owner):** CR Owner and the default CR Owner require a replacement. Designers, Developers and review Developer may be cleared or replaced (B-04). |
| Q5 | Whether the Calendar workweek is configurable | **Resolved (owner):** fixed Sun–Thu in release 1, shown read-only in Settings. |
| Q6 | Lane cap in Week mode | **Resolved:** 4, the same as Month (B-08.9). **Revised 2026-10-28 by the owner:** Week mode shows as many lanes as fit the available height; Month keeps 4. |
| Q7 | Dashboard drill-downs that need predicates outside B-10 (unscheduled, unlinked, active Subtask cards) | **Default:** non-editable "From Dashboard" chips; "No CR" also exists in the CR filter. |
| Q8 | Per-status colors | **Resolved (owner):** none in release 1 (B-08.10). |
| Q9 | Toolkit dependencies (`SettingsControls`, `TokenizingTextBox`, `StaggeredLayout`) | **Resolved (owner, ADR 0021):** native settings cards, TokenPicker and `UniformGridLayout`. No Community Toolkit WinUI packages in release 1. |
