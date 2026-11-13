# Final verification — Calendar UX & Status navigation (release 0.26.2 scope)

**Date (UTC):** 2026-11-13 (updated after PO feedback)  
**Branch:** `ekos-stage-b-calendar-labels` (commit `a099c2c` on remote; additional local changes **not committed**)  
**Verifier:** automated tests + static review  
**Baseline package:** `artifacts/raavi-0.26.2` does **not** include post–PO-feedback changes below.

---

## Scope

| In scope | Out of scope |
|----------|----------------|
| Calendar a11y (keyboard, touch), KPI module, Status in **More** | Backend API / migrations |
| Test hardening for flaky integration tests | New ZIP / commit (per PO) |

---

## 1. Failed tests: `databases` & `first-login` (investigation)

### Symptoms (earlier full-suite run)

| Test | Failure |
|------|---------|
| `databases` › creates one… | `expect(…/databases/d2).toBe(true)` **false** — navigation fetch after POST not observed in time |
| `first-login` › bootstrap complete | `router.name` **system-setup** instead of **system-tenants** — async `router.replace` not settled |

### Relation to Calendar / Status work

- `git diff a099c2c` shows **no changes** to `DatabasesView.vue`, `SystemSetupView.vue`, or database/auth routes from the calendar scope.
- Failures are **not regressions** from calendar KPI or Status navigation.

### Root cause

1. **Timing / ordering (flake)** under full Vitest run (~80–100s, 40 files, jsdom). Fixed `settle()` windows (`~160ms` + 8×20ms) and short post-save loops (`4×30ms`) sometimes complete before `router.push` / `router.replace` and the next `GET /databases/{id}`.
2. **Load**: 40 parallel workers recreate jsdom; occasional slow ticks (documented in Vitest output: environment ~44% of time).
3. **Not** SQL/LocalDB (frontend-only tests with `fakeFetch`).

### Fix applied (test-only, no product change)

- `databases.test.ts`: poll up to ~1s for POST + GET `/api/v1/databases/d2` after create.
- `first-login.test.ts`: poll up to ~1s for route `system-tenants` after successful `setup/complete`.

### Current status

```text
npm test -- --run  →  40 files, 264 passed (0 failed)  [re-run 2026-11-13]
npm test -- databases.test.ts first-login.test.ts  →  6/6 passed (isolated)
```

**Conclusion:** Pre-existing **integration test flake**; hardened waits added. Not a calendar regression.

---

## 2. Calendar accessibility (PO item 2)

### Implemented

| Requirement | Implementation |
|-------------|----------------|
| Keyboard focus on day | `tabindex="0"` on month cells (`inMonth`), week columns, timeline track, agenda rows when `canCreate` |
| Enter → create | `onDayCellKeydown` → `onDayCreate` (same as double-click) |
| Touch / mobile alternative | `day-add-event` button (`calendar.addOn` aria-label); `@pointer: coarse` keeps button visible; header **New event** unchanged |
| Double-click preserved | `onDayDblClick` → `onDayCreate` |
| Event click isolation | `@dblclick.stop` on occurrence buttons unchanged |
| RTL / LTR | Logical CSS; `dir="auto"` on titles; fa/en `addOn` strings tested |

### Views

| View | Focus / Enter | Add button |
|------|---------------|------------|
| Month | gridcell | per-day `+` |
| Week / Work week | week-col | per-column `+` |
| Day / Timeline | timeline-track | corner `+` |
| Agenda | agenda-row | per-row `+` |
| Year | native `<button>` (Enter = jump to month) | dblclick create unchanged — **no nested add control** (HTML constraint) |

### Tests added

- Enter on focused day + click `day-add-event` (`calendar.test.ts`)
- `aria-label` English vs Persian (`addOn` / `افزودن`) (`calendar.test.ts`)

---

## 3. Prior verification items (unchanged summary)

- **KPI:** `calendarRangeStats.ts` + unit tests; derived only from filtered API list.
- **Jalali toggle:** single extra occurrences fetch (tested).
- **Status:** `AppShell` → More menu → `/status`.

---

## 4. Tests & build (final run)

| Command | Result |
|---------|--------|
| `npm test -- --run calendar.test.ts calendarRangeStats.test.ts` | 21 passed |
| `npm test -- --run` | **264 passed**, 0 failed |
| `npm run build` (frontend) | OK |
| `dotnet build Raavi.slnx` | 0 warnings, 0 errors |

---

## 5. Remaining limitations

1. KPI **Today** uses real calendar today; 0 when today ∉ visible range.
2. **Year view:** keyboard Enter on month tile navigates to month (single action); create = double-click or drill down to month/week/day and use `+` / Enter.
3. No roving `tabIndex` arrow-key grid navigation (only Tab + Enter).
4. Agenda duplicate days use one add control per occurrence row (same date semantics).
5. Changing month/view still runs full `load()` (boards + prefs + occurrences) — pre-existing.
6. Remote commit `a099c2c` + local-only verification/a11y delta; **no new ZIP** until PO approves commit.

---

## 6. Final verdict

| Area | Status |
|------|--------|
| Calendar UX + a11y (keyboard, touch, dblclick) | **Approved** |
| Flaky DB/first-login tests | **Mitigated** (test waits) |
| Full frontend + builds | **Green** |
| Commit / push / package | **Awaiting PO** |

**Overall:** Ready for PO commit approval including this verification delta.
