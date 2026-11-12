# EKOS Roadmap

Aligned with request Phases 0–5, remapped onto **what already exists**. Stage B starts only after PO approval of Stage A.

## Phase 0 — Foundation (this Stage A) ✅ docs

Assessment, matrix, architecture, backlog, risks. **No code.**

## Phase 1 — Enterprise Knowledge Foundation (first Stage B)

**Reuse:** Labels, approvals, workflows, notifications, versioning, RAG ACL.

| Epic | Outcome |
|------|---------|
| EKOS-1 Ownership & review metadata | Owner, ReviewAt on pages; UI; stale/workflow hooks |
| EKOS-2 Relationship edges v1 | Table + API + page panel (manual) |
| EKOS-3 Unified search filters | Tags + space + type on Search UI; saved searches |
| EKOS-4 Notification hygiene | Prefer existing center; digests optional |
| EKOS-5 Audit completeness | Ensure new ops emit outbox |

**Defer in P1:** OpenSearch, S3, Decision types, graph viz polish.

## Phase 2 — Knowledge Intelligence

| Epic | Outcome |
|------|---------|
| EKOS-6 Real AI verification | Optional; needs provider |
| EKOS-7 Editor AI actions | Confirm-to-apply |
| EKOS-8 Maintenance scanners | Stale/dupe suggestions → tasks |
| EKOS-9 AI label suggestions | Via `ILabelSuggestionProvider` |
| EKOS-10 Ask citations UX | Harden sources |

## Phase 3 — Organizational Knowledge

| Epic | Outcome |
|------|---------|
| EKOS-11 Decision records + ADR template | |
| EKOS-12 Incident knowledge center | |
| EKOS-13 System catalog | |
| EKOS-14 Graph visualization | |
| EKOS-15 Knowledge analytics dashboard | |

## Phase 4 — Collaboration & operations

| Epic | Outcome |
|------|---------|
| EKOS-16 Meeting → minutes/decisions/actions | Extend Calendar |
| EKOS-17 Jira/Templar deep integration | Unblock Templar contract |
| EKOS-18 Knowledge approval workflows | Extend 10a/10b |

## Phase 5 — Ecosystem

Connectors (Git/CI/OpenAPI/Cal OAuth), agents, compliance (retention/hold), HA/OpenSearch/S3 when infra available.

## Parallel tracks (do not block P1)

- Finish Confluence leftovers  
- 7b/7c when MinIO/OpenSearch available  
- Real IdP/Jira verification (Q1)