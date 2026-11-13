# UI/UX Design — Task Management (Stage A)

## 1. Constraints

- Vue 3 + PrimeVue 4.x + existing i18n (fa/en), logical CSS, no CDN, no unsanitized `v-html`.
- Reuse `PrincipalPicker`, date display (Jalali/Gregorian), AppShell nav, space sidebar link.
- One shared data layer (`tasksApi` + query composable); views must not fork authz rules.

## 2. Information architecture

| Route | Purpose |
|-------|---------|
| `/tasks` | My Tasks (role sections / filters) — evolve current mine list |
| `/spaces/:key/tasks` | Team Tasks — list default |
| `/spaces/:key/tasks?view=board` | Kanban |
| `/spaces/:key/tasks?view=timeline` | Gantt-lite |
| `/spaces/:key/tasks?view=workload` | Workload |
| `/tasks/:id` or drawer | Detail (prefer drawer Stage B, route Stage C) |
| Calendar | Overlay toggle “Tasks” on existing CalendarView |

## 3. Detail layout (tabs/sections)

Overview · Description · Assignees · Subtasks · Checklist · Relationships · Comments · Attachments · Time · Activity · AI — progressive disclosure; hide empty advanced tabs until Stage delivers them.

## 4. View rules

- **List:** columns configurable; bulk only for allowed transitions; URL-persisted filters.
- **Kanban:** drag calls `POST …/transition`; failure reverts card; WIP optional later.
- **Calendar:** show due (and start if set); overdue style; click → detail; no second calendar engine.
- **Timeline:** hierarchy + deps when available; drag-reschedule = authorized field/transition update.
- **Workload:** never attribute 100% estimate to every assignee; show allocation rule.

## 5. Accessibility & locale

- Keyboard complete/reopen; focus management on cite-like jumps in AI panels.
- RTL: logical properties only (existing rule).
- Due dates via display settings / locale stores.

## 6. Current UI debt

`TasksView.vue` is list+dialog only — treat as Stage B baseline, not throwaway if possible (extend rather than rewrite wholesale).
