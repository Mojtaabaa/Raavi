# Test Strategy — Task Management (Stage A)

## 1. Existing coverage (baseline — must stay green)

| Suite | Path | Covers |
|-------|------|--------|
| API | `Raavi.Tests/Wiki/TaskTests.cs` | CRUD, assignee rules, mine, ETag, CSV, isolation |
| FE | `frontend/tests/tasks.test.ts` | List/create UX |
| Export | `ExportTests` | tasks in ZIP |
| Isolation model | TenantIsolationModelTests | `content.Tasks` RLS registration |

## 2. Per-stage additions

### Backend

- Unit: domain invariants (roles, cycles, transitions).
- API: authorization matrix; transition_denied; archive.
- Jobs: reminder/recurrence idempotency.
- Outbox: `task.*` handlers.
- Jira: fakes only (existing FakeIssueTracker pattern) — no live host required for CI.
- AI: confirm required; no invent assignee; View filter.
- `EndpointAuthorizationTests` for every new route.

### Frontend

- Vitest: filters, board drag failure revert, overlay flags, i18n fa/en parity, RTL smoke.
- Locale catalogs must match.

### Security / regression

- Cross-tenant task id.
- Private space outsider.
- Facet counts.
- Wiki/Calendar/Workflow/AI/Jira page-link regression filters in CI (`TaskTests|Calendar|Workflow|AiTests|PageIssue` as appropriate).

## 3. Definition of tested

Do not mark a Stage complete without automated tests for its security-sensitive paths. Browser E2E optional but note in reports (as 9a historically did).
