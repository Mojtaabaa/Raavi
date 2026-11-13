# Target Product Vision — Enterprise Task Management

## Positioning

Raavi already ships **space work items** (`content.Tasks`, ADR-026). Enterprise Task Management evolves that foundation into a first-class work system that:

1. Keeps **Native Raavi Tasks** as SoT for operational/knowledge work.
2. Keeps **Jira Issues** as SoT for software delivery (display + link; never silent overwrite).
3. Ties work to **wiki knowledge** (pages, decisions/incidents-as-pages, meetings, calendar).
4. Reuses Calendar, Workflow patterns, Notifications, AI, Labels, Permissions, Search, and Connectors — without duplicating engines.

## Outcomes

| Persona | Outcome |
|---------|---------|
| Individual | My Tasks by role (responsible / contributor / reviewer / approver), due/overdue, watching |
| Space team | Shared backlog with List + Kanban + Calendar overlay + Timeline + Workload |
| Knowledge worker | Create/link tasks from pages, comments, meeting notes; evidence after completion |
| Admin | Configurable task workflow per space/tenant; automation without custom code |
| Integrator | Jira-linked issues visible beside native tasks; sync status honest |

## Non-goals (confirmed)

- Replacing Jira advanced SDLC.
- Microservices / separate task database.
- Binding TipTap checklist nodes 1:1 to WorkTasks.
- Templar without a real contract.
- Fake production connectors.

## Principles

1. **One query/authorization spine** for all views.
2. **Confirm-to-apply** for AI writes (same as Editor AI / EKOS-7).
3. **Opaque 404** for inaccessible tasks and related knowledge.
4. **Additive migrations**; no destructive schema changes without PO approval.
5. **Persian/English, RTL/LTR, Jalali/Gregorian** via existing display stack.
