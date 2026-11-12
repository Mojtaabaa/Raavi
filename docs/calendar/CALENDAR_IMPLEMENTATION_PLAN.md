# Calendar Implementation Plan

**Date:** 2026-11-11 (updated with locked decisions)  
**Assessment:** [CALENDAR_FEATURE_ASSESSMENT.md](./CALENDAR_FEATURE_ASSESSMENT.md)  
**Architecture:** [CALENDAR_ARCHITECTURE.md](./CALENDAR_ARCHITECTURE.md)  
**Principle:** Extend Phase 9b; do not rewrite recurrence/wall-clock/ICS export. V1 = enterprise calendar, not a thin upgrade of month view.

## Locked decisions (summary)

1. Templar: provider shell + config + contract doc; live Blocked until API; no fake prod APIs; core independent.
2. Jira: read-only dates/milestones; open in Jira to edit; write-back-ready seams.
3. Sync conflicts: keep both; user picks Raavi / External / merge; per-connection policy; no silent destructive apply.
4. Delete: Archive (+ Restore); hard delete typed confirm; cascade only owned Raavi events; never delete remote Google/Outlook without confirm; never delete Jira/Templar authority rows.
5. OAuth: system admin + `RAAVI_*` / secret store; encrypted tenant-isolated tokens; disabled when unconfigured; fakes in tests; Google/Entra setup docs.
6. Personal sharing: Free/Busy **and** Full (View/Create/Edit/Manage); private events; backend enforce.
7. Recurrence edit scopes + exception moves + Google/Outlook RRULE sync = V1.
8. Reminders + browser alert + TTS (fa/en) + Quiet Hours + privacy = V1.

## Stage A — Foundation (in progress — backend schema + CRUD landed)

**Goal:** Introduce `Calendars` without losing existing space events.

1. Entity `Calendar` (Personal | Space | Organization): name, description, color, icon, `ArchivedAt`, ownership (`OwnerUserId` / `SpaceId`). ✅
2. Entity `CalendarAccessGrant` (subject user/group + grant) — table + model; share APIs later in Stage A/C. ✅ schema
3. Add `CalendarId` to `CalendarEvent`; backfill **one default Space calendar per space that has events (and empty spaces on first write)**; keep `SpaceId` denormalized. ✅ migration `CalendarBoards`
4. Baseline event columns: `Type`, `Status`, `Priority`, `Color`, `OnlineUrl`, `OrganizerId`, `UpdatedBy`, `IsPrivate`. ✅
5. APIs: list/create/update/archive/restore/purge calendars; occurrence `calendarIds` filter. ✅
6. Migration: idempotent SQL backfill with RLS off/on + event-count check; Down restores `SpaceId`. ✅
7. Preserve create-event-on-space via `EnsureSpaceDefaultCalendarAsync`. ✅
8. Docs: locked decisions + [TEMPLAR_API_CONTRACT.md](./TEMPLAR_API_CONTRACT.md). ✅

**Stage A remainder (tranche 2) ✅:** grant CRUD + FreeBusy; rich event DTO; create on calendar; frontend picker. Migration `CalendarV1Core`.

## Stage B — UI views & interaction (P0) — tranche 2 partial ✅

Month / Day / Week / Work week / Agenda / Year / Timeline + multi-calendar toggles + offline last-view cache (edits blocked) + day drag-drop + context menu. **Open:** resize, richer DnD.

## Stage C — Meetings, reminders, TTS, availability (P0→P1) — tranche 2 partial ✅

Reminders + Workers; prefs + browser Notification + TTS; Free/Busy share; attendees + RSVP + page/whiteboard links (tranche 4 ✅). **Open:** scheduling assistant, richer meeting notes UX, content-less invite notifications.

## Stage D — Recurrence completion (P0) — ✅

This / ThisAndFollowing / EntireSeries + exclude; monthly Nth-weekday (`monthWeek`, tranche 4). **Open:** more preset polish.

## Stage E — External sync (P1) — partial

ICS **import** (sanitized) ✅. **Open:** ICS feeds/subscribe; CalDAV; Google/Outlook OAuth; conflict store + UI; never auto-delete remote without confirm.

## Stage F — Jira + Templar (P1) — tranche 2 ✅ shell + overlay

Jira due-date read-only overlay. Templar provider shell (disabled until contract); live Blocked.

## Stage G — Wiki / Whiteboard / Search / Audit (P1)

## Stage H — AI (P2 / V1 required)

Preview + confirm before writes.

## Stage I — Hardening & docs

FINAL_REPORT, INTEGRATIONS, AI, SECURITY, user-guide fa, WIKI_API, IMPLEMENTATION_STATUS.

## Regression gates (each stage)

- `dotnet test --filter Calendar`
- `npx vitest run tests/calendar*.ts tests/locales.test.ts`
- Update assessment statuses after each stage
