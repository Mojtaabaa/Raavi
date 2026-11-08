# ADR-027 — Calendar (Phase 9b)

**Status:** Accepted (2026-11-08)

## Context

Second step of Phase 9 (after tasks, ADR-026). The requirement is team/space calendars that work in both the Jalali and the Gregorian calendar, with recurring events, per-space permissions, navigation and export.

## Decisions

1. **An event belongs to a space** (`content.CalendarEvents`, tenant-owned, RLS, composite FK to the space), like a task: not a node of the page tree. Permissions come from the space — View to read, Edit on an active space to create/change/delete; an event of a space the caller cannot view does not exist for them (404). `/calendar` is a cross-space feed limited to spaces the caller may view.
2. **Wall-clock storage.** An event stores start/end *dates* and *times* as wall-clock values plus the IANA zone, never a single UTC instant. A weekly 09:00 meeting therefore stays at 09:00 across daylight-saving changes; occurrences carry computed UTC instants for clients. A time that does not exist (spring-forward gap) moves to the first valid instant; an ambiguous one takes the later offset. All-day events have no times; `EndDate` is inclusive (the iCalendar export converts to its exclusive form). An event spans at most 366 days.
3. **Recurrence is expanded on the server** by a pure, deterministic engine (`RecurrenceExpander`): daily/weekly/monthly/yearly with an interval, weekdays (weekly), and either an end date or a count (never both, as in RFC 5545). Months without the day (31st, 30 Esfand in a common year, 29 February) are **skipped, not clamped**. A candidate cap bounds work for pathological rules; the list API serves windows of at most 400 days and 2 000 occurrences.
4. **Two calendars for rules.** Monthly and yearly rules carry the calendar they are evaluated in (`Gregorian` or `Persian`): "the 1st of every Persian month" falls on different Gregorian days. Persian arithmetic uses the .NET `PersianCalendar` on the server; the browser derives Jalali month grids from `Intl` (`-u-ca-persian`), so there is one source of truth per side and no hand-rolled Jalali algorithm. Weekly rules in the Persian calendar start weeks on Saturday.
5. **A weekly rule must include the weekday of its start** (iCalendar treats DTSTART as the first instance; accepting a rule that excludes it would make the app and the exported file disagree). The UI forces that day on.
6. **Single occurrences can be cancelled** (`ExcludedDates`, at most 200, only dates the rule produces; stale cancellations are dropped when the series changes). Editing one occurrence on its own (an "exception with changes") is **not** in 9b.
7. **Optimistic concurrency** with `"r{n}"` ETags on update, like pages and tasks.
8. **Export is iCalendar (RFC 5545)**: escaping, 75-octet folding without cutting UTF-8, `TZID`, `RRULE` (with `WKST=SA` for Persian weeks), `EXDATE`. iCalendar cannot express "monthly/yearly in the Persian calendar", so those series are written as an explicit `RDATE` list for the next two years (at most 200 dates) instead of a wrong `RRULE`; this is a documented limitation of the format, not of the model.

## Not in 9b

Editing a single occurrence separately from its series, attendees/invitations/reminders, iCalendar import and subscription feeds, free/busy, per-user personal calendars, time-zone display conversion in the browser (events show the wall-clock time of their own zone), Persian-calendar `UNTIL`/`COUNT` semantics beyond what the engine computes, and search indexing of events.
