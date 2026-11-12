# Security and Authorization — Task Management (Stage A)

## 1. Existing controls (keep)

| Control | Implementation |
|---------|----------------|
| Tenant isolation | `ITenantOwned` + RLS on `content.Tasks` |
| Existence hiding | No View on space → `task.not_found` 404 |
| Write gate | Space Edit (active) |
| Complete gate | Edit **or** assignee |
| Assignee eligibility | Member + assignee’s View on space |
| Content link | Same-space, non-trashed page |
| Concurrency | If-Match revision |
| Cookies/XSRF | Platform rules for unsafe methods |
| Endpoint authz | `RequireAuthorization` + host kind |

## 2. Required extensions

| Threat | Mitigation |
|--------|------------|
| IDOR on task id | Always Load + space View (already) |
| Assign to outsider | Keep eligibility checks; extend per role |
| Transition bypass via Kanban UI | Server `transition` only |
| Leak via facets/counts | Compute under same ACL |
| Related page/Jira meta leak | Filter both ends; 404 |
| AI context leak | `FilterViewable` / `FilterForExternalModel` |
| Cross-tenant sync | Connector secrets per tenant; RLS on links |
| Enumeration via mine | Only assignee’s viewable spaces (already) |
| Hard delete audit gap | Prefer archive + outbox before claiming compliance |

## 3. Permission matrix (target)

| Action | Space Edit | Responsible | Contributor | Reviewer | Approver | Observer | Assignee (legacy) |
|--------|------------|-------------|-------------|----------|----------|----------|-------------------|
| View | ✓ if View | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Edit fields | ✓ | limited | limited | — | — | — | — |
| Assign | ✓ | ✓ (policy) | — | — | — | — | — |
| Transition | ✓ / policy | policy | policy | policy | policy | — | complete if allowed |
| Comment | View+ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Delete/Archive | ✓ | — | — | — | — | — | — |

Exact transition matrix is workflow-configurable.

## 4. Testing obligations

Every Stage: cross-tenant, outsider 404, assignee_no_access, transition_denied, AI retrieval exclusion, search/facet leakage, external link refresh without Edit.
