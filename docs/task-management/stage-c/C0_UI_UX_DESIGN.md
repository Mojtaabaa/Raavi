# C0 — UI/UX Design (Stage C)

**Date:** 2026-11-12  
**Constraint:** Extend `modules/tasks` + Calendar; PrimeVue 4.x; i18n fa+en; CSS logical properties; no new design system; no complex canvas designer.

---

## 1. Information architecture

| Route | Purpose |
|-------|---------|
| `/tasks` | My Tasks (list; later role sections) |
| `/spaces/:key/tasks` | Space hub with **view switcher**: List \| Board \| Timeline \| Workload |
| `/spaces/:key/tasks?view=board` | Kanban |
| `/spaces/:key/tasks?view=timeline` | Gantt-lite |
| `/spaces/:key/tasks?view=workload` | Workload |
| `/spaces/:key/settings/task-workflow` (or under space settings) | Workflow admin |
| `/calendar` | Existing calendar + **Tasks** overlay toggle |

Detail: keep **drawer/dialog** from B for C3–C4; dedicated `/tasks/:id` optional in C7 if drawer limits hit.

---

## 2. Workflow designer (C2) — deliberately simple

**Do not** build a BPMN canvas. Reuse the ADR-033 spirit:

1. **Form** for definition metadata + status table (add/remove/reorder, category, color, guards).
2. **Transition matrix** or edge list editor (from → to, permission, roles, conditions checkboxes).
3. **Read-only graph preview** patterned on `WorkflowFlow.vue` (ordered statuses as columns + SVG/HTML edges) — validation messages inline.
4. **Versions** panel: active published N, draft, publish CTA, view historical read-only.
5. Design errors before publish: list with anchors to fields.

Visual “boxes and arrows” editing via drag to connect is **optional nicety**; ship matrix first.

Who: space **Administer** (same bar as page workflows).

---

## 3. Kanban (C3)

- Columns = workflow statuses (SortOrder); column header shows count.
- Card: title, priority chip, due (overdue style), progress ring/bar, checklist %, child open/total, assignee avatars + role tooltip.
- DnD: only to columns in `allowedTargetStatusIds` **hint**; on reject toast + snap back; never local status commit.
- Filters bar: assignee, priority, due range, text search.
- Large boards: per-column “Load more” (cursor); virtualize card list if PrimeVue/ScrollPanel sufficient — avoid heavy grid libs unless proven need.
- RTL: columns flow with inline-start; drag library must respect logical direction.

---

## 4. Calendar overlay (C4)

- Toggle “Tasks” on Calendar toolbar; filter merged `/calendar/occurrences` by `sourceKind=Task` (backend: `ICalendarOverlaySource`, same feed as Jira overlays).
- Rendering: reuse all-day / multi-day event chrome with distinct `sourceKind` color/token (`--raavi-task-overlay`).
- Click → open task drawer (not event editor).
- Drag move (if Edit): update due/start via task API; show same permission failures as task edit.
- Preserve meetings/events behavior when toggle off.
- Jalali/Gregorian grids unchanged (`calendarMath` / Intl).

---

## 5. Timeline / Gantt-lite (C5)

- Left pane: tree (expand parent/children).
- Right: time axis (day/week/month control).
- Bars: Start→Due; hatch or badge for missing dates; progress overlay.
- Dependency lines: simple SVG connectors (FS only).
- Drag resize/move dates with Edit; cycle/dep warnings as toast.
- Undated: collapsible section below or left-only rows without bars.
- No full MS-Project feature set; no critical-path solver in C.

---

## 6. Workload (C6)

- Rows = people (filter team/space members); columns = time buckets (day/week).
- Cell: allocated vs capacity; overload uses danger token (theme tokens, not hard-coded colors).
- Side panel: tasks contributing to allocation for selected user/bucket.
- Allocation editor on task drawer: minutes or % per assignee; Observer greyed; live sum vs Original.
- Distinguish legends: **Allocated (estimate share)** · **Actual (time entries)** · **Calendar span** (not shown as load by default).

---

## 7. Shared UX rules

- Loading / empty / error states on every view.
- 409/412: reload task + message (`errorHelp` pattern where codes exist).
- Prevent double-submit on transition and publish.
- All strings in fa+en catalogs; escape `@` in vue-i18n as `{'@'}`.
- Accessibility: keyboard column focus; DnD has keyboard alternative (status select on card menu).

---

## 8. Component sketch

```
modules/tasks/
  TasksView.vue          # hub + view switcher
  TaskBoardView.vue
  TaskTimelineView.vue
  TaskWorkloadView.vue
  TaskWorkflowSettings.vue
  TaskStatusGraph.vue    # read-only preview
  TaskAllocationEditor.vue
  (existing panels…)
modules/calendar/
  CalendarView.vue       # overlay toggle + merge
```
