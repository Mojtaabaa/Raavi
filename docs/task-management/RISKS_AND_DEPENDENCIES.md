# Risks and Dependencies (Stage A)

## Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Scope explosion (full Jira clone) | Delay | Strict native vs external ownership; views share query spine |
| Dual workflow engines | Confusion | Status machine ≠ page WorkflowEngine; document boundary |
| Calendar event duplication | User trust | Overlay-first |
| Breaking 9a clients | Regressions | Additive DTOs; dual-read AssigneeId |
| Hard delete vs audit | Compliance | Archive before marketing “enterprise audit” |
| List Take(500) | Silent truncation | Cursor pagination before scale claims |
| Jira create vs ADR-043 wording | Ops confusion | Align docs with `PageIssueLinkService` reality |
| AI auto-assign | Wrong people / abuse | Confirm-only; directory-bounded |
| KR Content-only model | Awkward Task edges | ADR before forcing Task into KnowledgeRelationships |
| Wiki module bloat | Build/boundaries | Abstractions first; extract module later if needed |
| TipTap taskList confusion | Support burden | Keep product copy explicit |
| Templar | Blocked | Extension point only |
| OpenSearch/S3 deferred | Search/attachments | SQL + local store until infra |

## Dependencies

| Dependency | Status |
|------------|--------|
| Space permissions / WikiAuthorizer | Ready |
| Hangfire workers | Ready (ops must run Workers) |
| Notification + SignalR | Ready |
| Calendar UI/API | Ready for overlay |
| Integrations HTTP + connectors | Ready (fakes in CI; real Jira Q1 pending) |
| AI tenant settings | Ready when hosts allow-listed |
| Prompt Hub | Ready |
| Display/Jalali | Ready |
| PO approval Stage B | **Required** |
| Destructive migration approval | Required when proposed |

## Open PO questions (non-blocking for docs; needed before B schema freeze)

1. Archive-only vs keep hard delete for admins?
2. Rich description (Raavi doc JSON) in B or later?
3. Task keys (`SPACE-123`) in B or defer?
4. Should Kanban land before multi-assignee notifications?
5. Overlay-only calendar vs optional pin-to-event?
