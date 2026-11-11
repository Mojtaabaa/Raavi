# Calendar Architecture Decisions (V1)

**Date:** 2026-11-11  
**Status:** Accepted (product decisions locked)

## Product decisions (locked)

1. **Templar:** Connector/provider architecture + config + error handling + documented API contract in V1. Real calls **Blocked** until contract is provided. No fake production endpoints. Core calendar works without Templar. Real Templar remains in V1 scope.
2. **Jira:** Read-only overlay (due/start/end/milestones). Edit → open Jira. Architecture allows future write-back with Jira permissions.
3. **Sync conflicts:** Never silently drop Raavi or external changes. Keep both versions, record Conflict, user chooses Raavi / External / merge. Per-connection default policy; with no decision, no destructive auto-apply.
4. **Delete:** Default = Archive (+ Restore). Hard delete needs explicit confirm; cascades only that calendar’s owned events; never deletes Jira/Templar/external-source rows; disconnecting Google/Outlook never deletes remote events without explicit confirm.
5. **OAuth on-prem:** System admin panel; Client ID/Secret via `RAAVI_*` / secret store; encrypted tenant-isolated user tokens; disabled UI when unconfigured; fakes in tests; setup docs for Google Cloud + Entra.
6. **Personal sharing:** Free/Busy **and** Full share with independent grants (View details, Create, Edit, Manage); private events still respect privacy; **backend-enforced**.

## Domain shape (Stage A+)

- `Calendars` — Personal | Space | Organization; archive; color/icon; ownership.
- `CalendarEvents` — `CalendarId` (required); space events keep denormalized `SpaceId`; wall-clock model preserved (ADR-027).
- `CalendarAccessGrants` — subject + grant for FreeBusy / View / Create / EditOwn / EditAll / Manage (sharing wired in later Stage A/C; table exists from Stage A).
- External/Jira/Templar occurrences are **source overlays** or mapped rows with `SourceKind`; deleting a Raavi calendar never cascades to authoritative external systems.
- Templar live API: [TEMPLAR_API_CONTRACT.md](./TEMPLAR_API_CONTRACT.md) (Blocked until published).

## Recurrence & notifications (V1 — not deferred)

- Full series edit scopes; exceptions; DST-safe wall-clock; Google/Outlook RRULE mapping.
- Reminders: before/at start; browser alert + TTS (fa/en OS voices); per calendar/event; Quiet Hours; no overlapping spam; privacy for confidential events; graceful when tab closed / permission denied.
