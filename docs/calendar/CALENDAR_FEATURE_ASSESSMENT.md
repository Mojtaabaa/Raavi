# Calendar Feature Assessment

**Date:** 2026-11-11 (updated with product decisions)  
**Baseline:** Phase 9b (ADR-027) + display calendar (ADR-053)  
**Spec:** Raavi Calendar Complete Product Specification (enterprise calendar V1)  
**Locked decisions:** [CALENDAR_ARCHITECTURE.md](./CALENDAR_ARCHITECTURE.md)

## Executive summary

Raavi today has a **space-scoped event store** with wall-clock + IANA time zone, server-side recurrence expansion (Gregorian/Persian monthly-yearly), occurrence cancellation (EXDATE-style), ETag concurrency, space View/Edit authz, cross-space feed, ICS **export**, and a **month-only** UI with Jalali/Gregorian toggle. Almost everything else in the V1 spec is **Missing**. Templar has **no real integration** (stub provider only). Jira connectors are **read-only issue lookup** (no due-date sync into calendar). External OAuth calendars (Google/Outlook) and CalDAV are **not present**.

**Product decisions (2026-11-11):** Templar connector shell in V1, live calls Blocked until API contract; Jira read-only overlay; sync conflicts keep both + user resolve; archive-first delete; OAuth via admin secrets; personal Free/Busy **and** Full sharing; recurrence edit scopes + TTS notifications are **V1 mandatory** (not deferred).

| Status | Count (approx.) |
|---|---|
| Complete | ~8 |
| Partial | ~14 |
| Missing | majority of V1 |
| Defective | 0 confirmed E2E defects in 9b core (gaps are missing scope) |
| Blocked | Templar live API (contract pending); Google/Outlook until OAuth configured |

---

## Assessment table

| Feature ID | Feature | Status | Existing Implementation | Missing Work | Dependencies | Priority | Verification |
|---|---|---|---|---|---|---|---|
| CAL-CORE-001 | Wall-clock event model + IANA TZ | Complete | `CalendarEvent` stores date/time + zone; UTC computed for clients | Event types, status, priority, color, URL, attachments, organizer, calendarId | Wiki DbContext, RLS | P0 | Domain + API tests; DST cases |
| CAL-CORE-002 | All-day / multi-day events | Complete | `IsAllDay`, inclusive `EndDate`, max span 366d | Dual-date display polish | ADR-027 | P0 | CalendarTests all-day |
| CAL-CORE-003 | Space event CRUD + ETag | Complete | Endpoints create/get/put/delete; `"r{n}"` | Move between calendars; richer fields | Space Edit | P0 | CalendarTests |
| CAL-CORE-004 | Occurrence window API | Complete | `/calendar/occurrences`, space + cross-space, ≤400d / 2000 | Filter by calendar/source/type | Authz | P0 | API window tests |
| CAL-CORE-005 | Tenant isolation | Complete | `ITenantOwned` + RLS on `CalendarEvents` | New calendar tables must use same pattern | TENANT_ISOLATION | P0 | TenantIsolationModelTests |
| CAL-SPACE-001 | Space calendar (implicit one-per-space) | Partial | Default Space `Calendar` + `CalendarId` backfill; create-event ensures default | Multiple calendars UI; color/icon polish | Spaces | P0 | Multi-calendar CRUD |
| CAL-PERSONAL-001 | Personal calendar | Partial | Create/list personal API | Preferences; default privacy UX; share UI | Identity user | P0 | Isolation: other user 404 |
| CAL-ORG-001 | Organization calendars | Partial | Create/list; manage = tenant admin | Holidays pack; member View UX | Tenant admin | P0 | Org CRUD + View for members |
| CAL-MGMT-001 | Calendar management (CRUD, archive, favorite, color) | Partial | List/create/update/archive/restore/purge APIs | Favorites; admin UI | — | P0 | CRUD + archive restore |
| CAL-MGMT-002 | Calendar permissions (View / FreeBusy / EditOwn / Manage) | Partial | `CalendarAccessGrants` table | Grant CRUD + enforcement matrix | WikiAuthorizer pattern | P0 | Permission matrix tests |
| CAL-UI-001 | Month view Jalali/Gregorian | Partial | `CalendarView` month grid; tenant/locale calendar; `CalendarDateInput` | Dual labels; first-day user pref | ADR-053 | P0 | calendar.test.ts + browser |
| CAL-UI-002 | Day / Week / Work week / Year / Agenda / Timeline | Missing | Month only | Views + navigation + time indicator | calendarMath | P0 | Frontend view tests |
| CAL-UI-003 | Drag/resize/move/duplicate/context menu | Missing | Click day → dialog create/edit | DnD interactions | UI | P0 | Component tests |
| CAL-UI-004 | Multi-calendar overlay + toggles | Missing | Cross-space feed only | Overlay colors, remember prefs | Preferences | P0 | Overlay render |
| CAL-UI-005 | Search & filters | Missing | — | Search API + UI filters | Search/index optional | P0 | Permission-filtered search |
| CAL-RECURRENCE-001 | Daily/weekly/monthly/yearly + interval + until/count | Complete | `RecurrenceExpander`; Persian monthly/yearly | Relative weekday (2nd Tuesday); no-end + bounded expand already | ADR-027 | P0 | CalendarRecurrenceTests |
| CAL-RECURRENCE-002 | Exclude occurrence | Complete | `ExcludedDates` + API | — | — | P0 | Exclusion tests |
| CAL-RECURRENCE-003 | Edit this / this+following / series; modified exceptions | Missing | Entire series edit only; cancel occurrence only | Exception instances; split series | Recurrence engine | P0 | Series edit scenarios |
| CAL-MEETING-001 | Meeting type + attendees + invitations | Missing | Title/location/description only | Attendees, RSVP, notifications | Notifications, directory | P0/P1 | Invite accept/decline |
| CAL-MEETING-002 | Agenda + meeting notes (Wiki page) | Missing | — | Agenda items; create page from template; bidirectional link | Wiki pages, templates | P1 | Notes round-trip |
| CAL-MEETING-003 | Action items from notes | Missing | Tasks module exists (9a) | Link tasks to meeting; AI extract with confirm | Tasks, AI | P1 | Create task from meeting |
| CAL-AVAIL-001 | Free/Busy + scheduling assistant | Missing | — | Availability API; suggest slots; privacy | Working hours | P1 | FreeBusy no titles |
| CAL-REMIND-001 | Event reminders + notifications | Missing | Notification infra (7a); workflow reminders (10c) | Event reminder rows + Hangfire job | Notifications, Workers | P0 | Idempotent reminder delivery |
| CAL-SYNC-001 | ICS export | Complete | Space export `.ics` with RRULE/RDATE/EXDATE | Per-event export | ADR-027 | P1 | Export parse |
| CAL-SYNC-002 | ICS import + feed subscribe | Missing | — | Import sanitize; feed job | Workers | P1 | Import fixtures |
| CAL-SYNC-003 | CalDAV provider | Missing | — | Provider + sync state + conflicts | ExternalAccounts | P1 | Mock CalDAV tests |
| CAL-SYNC-004 | Google Calendar OAuth sync | Missing | — | OAuth, tokens encrypted, incremental sync | Admin OAuth config | P1 | Fake HttpHandler tests |
| CAL-SYNC-005 | Outlook / Graph sync | Missing | — | Same as Google via Graph | Admin OAuth config | P1 | Fake Graph tests |
| CAL-SYNC-006 | Sync direction + conflict policy | Missing | — | Configurable direction; conflict UI | SyncStates | P1 | Conflict no silent drop |
| CAL-JIRA-001 | Jira dates on calendar | Missing / Blocked-partial | Issue connectors: lookup title/status/url only (ADR-043) | Due/start/end/version dates; JQL/filter; open issue | IssueConnectorService | P1 | Fake Jira dates |
| CAL-TEMPLAR-001 | Templar calendar events (live) | Blocked | Prompt Hub stub “unavailable” only | Live sync after API contract; **connector shell + config + docs in V1** | External product | P1 | No fake prod API; core works offline Templar |
| CAL-TEMPLAR-002 | Templar provider shell | Partial | Contract doc `TEMPLAR_API_CONTRACT.md` | `ITemplarCalendarProvider`, settings, auth, errors | Config | P1 | Disabled when unconfigured |
| CAL-JIRA-001 | Jira dates on calendar | Missing | Lookup title/status/url only | Read-only due/start/end/milestones overlay; open in Jira; write-back-ready seams | IssueConnectorService | P1 | Fake Jira dates; no silent local authority |
| CAL-SYNC-006 | Sync conflict keep-both + user resolve | Missing | — | Conflict rows; UI choose Raavi/External/merge; per-connection policy; no destructive default | SyncStates | P1 | Conflict never auto-drops |
| CAL-MGMT-003 | Archive / restore / hard-delete | Partial | Archive/restore/purge (confirm name); events on calendar only | External-source guards when overlays exist; UI confirm | — | P0 | Archive hidden in default list |
| CAL-SHARE-001 | Personal Free/Busy + Full share | Partial | Grant storage model | Grant CRUD; FreeBusy strips titles; private events; backend enforce | CalendarAccessGrants | P0 | FreeBusy strips titles |
| CAL-RECUR-STANDUP | Daily standup / selected weekdays / biweekly | Partial | Daily/weekly + weekDays + interval | UI presets; biweekly=Weekly interval 2 already possible | Recurrence | P0 | Preset creates correct RRULE |
| CAL-RECUR-SCOPE | Edit this / following / series + moved exceptions | Missing | Series edit; exclude only | Exception instances; split series; cancel one/all | Recurrence engine | P0 | Three-scope dialog tests |
| CAL-RECUR-DST | DST + TZ preservation | Partial | Wall-clock + IANA (ADR-027) | Browser display TZ; sync RRULE mapping Google/Outlook | Sync | P0/P1 | DST occurrence tests |
| CAL-RECUR-EXT | Recurrence sync Google/Outlook | Missing | ICS export RDATE for Persian | Provider RRULE/exception mapping | Sync providers | P1 | Fake provider recurrence |
| CAL-NOTIFY-001 | Pre-start + at-start reminders | Missing | Notification infra | Reminder rows + Workers job + in-app/email | Notifications | P0 | Idempotent delivery |
| CAL-NOTIFY-002 | Browser alert + TTS (fa/en) | Missing | — | Web Notification + `speechSynthesis`; voice/rate prefs; per calendar/event | Browser APIs | P0 | Unit + manual checklist |
| CAL-NOTIFY-003 | Quiet Hours / DND / no overlap / privacy | Missing | — | Prefs; queue coalescing; skip confidential TTS title | Prefs | P0 | Quiet hours suppress |
| CAL-NOTIFY-004 | Closed tab / denied permission | Missing | — | Server still queues in-app/email; UI explains denied Notification/TTS | Workers | P0 | Graceful degrade |
| CAL-WIKI-001 | Link events ↔ pages / embed | Missing | — | Event links; editor node optional | Editor schema | P1 | Link + navigate |
| CAL-WB-001 | Link events ↔ whiteboards | Missing | Whiteboards exist | Event links | Whiteboards | P1 | Open board from event |
| CAL-AI-001 | NL create / briefing / meeting AI / wiki dates / conflicts | Missing | AiService + Prompt Hub | Calendar-scoped prompts + confirm UX | AI module | P2 | Preview before write |
| CAL-OFFLINE-001 | Offline viewing only | Missing | Connection banner exists (phase 15) | Cache last view; block edits offline | Frontend cache | P0 | Offline edit disabled |
| CAL-PREF-001 | Working hours / holidays / user TZ | Partial | Tenant TZ + calendar (ADR-053); no per-user TZ | User prefs; holiday calendars | Tenancy settings | P0 | Prefs persist |
| CAL-AUDIT-001 | Calendar audit trail | Partial | Activity log exists for some domains | Calendar-specific ops | Activity log | P0 | Audit on invite/sync |
| CAL-SECURITY-001 | Authz on every route | Partial | Endpoints RequireAuthorization + space rights | New routes + FreeBusy rules | EndpointAuthorizationTests | P0 | Guard test |
| CAL-OAUTH-001 | Google/Outlook admin OAuth config | Missing | — | System settings; RAAVI_* secrets; disabled when unset; setup guide | Secrets | P1 | Unconfigured = disabled UI |

---

## Locked product decisions (do not reopen without user)

| # | Decision |
|---|---|
| 1 | Templar: shell + docs now; live Blocked until contract; stays in V1 scope |
| 2 | Jira: read-only overlay; write-back architecture reserved |
| 3 | Sync: keep both on conflict; user resolves; no silent destructive apply |
| 4 | Delete: archive default; hard delete confirmed; no external cascade |
| 5 | OAuth: admin + secrets; encrypted tokens; fakes in tests |
| 6 | Personal share: Free/Busy **and** Full with independent grants |
| 7 | Recurrence scopes + TTS notifications are **V1 mandatory** |

## What to preserve (do not rewrite)

- `CalendarEvent` wall-clock model and `RecurrenceExpander` (Persian/Gregorian).
- Occurrence window limits and ETag concurrency.
- Space View → read / Edit → write semantics for space-owned content.
- ICS export folding/escaping behavior.
- Month grid + `CalendarDateInput` Jalali conversion approach.

## Highest-risk gaps

1. **No `Calendars` entity** — Stage A migration from `SpaceId`-only events.
2. **Templar live** — Blocked on contract; shell still required.
3. **Jira date fields** — extend connector reads without a second Jira stack.
4. **OAuth** — customer-configured secrets; features off until set.
5. **TTS/notifications** — browser permission + closed-tab behavior must degrade safely.
