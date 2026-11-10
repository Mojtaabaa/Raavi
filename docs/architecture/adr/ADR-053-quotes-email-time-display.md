# ADR-053 — Comment quotes, e-mail settings in the application, workspace time zone and calendar

**Status:** Accepted (2026-11-10)

## Decisions
1. **Quotes of selected text** (inline comments existed, ADR-024): a thread header shows the quoted text; a quote longer than 220 characters or 3 lines is cut with "…" and a "Show more / Show less" toggle; the quote is a button that scrolls the page to the *start* of the quoted passage (smooth, below the sticky header). The composer preview is cut the same way.
2. **E-mail settings** (system host → "E-mail settings", `GET/PUT /system/email`, `POST /system/email/test`): saved as `sandbox/data/email-settings.json`, password protected with Data Protection (the API and the Workers share the key ring) and never returned (`hasPassword`); the sender reads the file at send time (cached by timestamp), so a change applies to both processes without restart. Without the file the configuration files / environment apply as before; an unreadable file falls back to them. Modes: Off, SMTP (STARTTLS / SSL / none), Pickup folder. The test sends one message with the saved settings and reports the transport error type and message (never credentials).
3. **Workspace time zone and calendar**: workspace administrators edit them under Administration → Display (`GET/PUT /tenant/settings`, quota untouched; the tenant directory cache is invalidated). One module (`core/runtime/display.ts`) holds them; `formatDate` uses them for every instant, so all displayed times follow the zone; stored values stay UTC.
4. **Date fields**: `CalendarDateInput` replaces the native date / datetime-local fields (tasks, calendar events, approvals due date, scheduled publishing, activity and error log filters): Gregorian → the browser's date field, Persian → year / month-name / day pickers (conversion by bisection over Intl, no second calendar implementation); the value is always ISO. Date-time fields are the wall clock of the workspace zone and are converted to UTC instants (`zonedInputToIso`).

## Not covered / limits
- One zone and one calendar per workspace; no per-user override and none on the system host (the browser's zone is used there).
- The Persian picker has no popup month grid and no `min` check (the native Gregorian field honours `min`); the year list is ±15 years around the value.
- E-mail settings live in a file under the sandbox data folder, not in the database: the folder must be writable by the application pool and shared by the worker service; the test message needs the settings saved first.
- Inline comment anchors still need the quoted text to exist in the current page version (otherwise the thread is marked orphaned).
