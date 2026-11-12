# Implementation Backlog (Stage A)

Complexity: S / M / L / XL. Priority: P0–P2.

---

### EKOS-1.1 Page ownership  
**Objective:** Explicit knowledge ownership.  
**Story:** As a steward, I see and set the owner of a page.  
**Requirements:** `OwnerUserId` on Content (or meta table); API get/set; Edit or Manage required; default backfill CreatedBy; show on page header.  
**Accept:** Owner visible; unauthorized cannot change; isolation tests.  
**BE/FE/DB/API:** Wiki PageService + migration; PageView; WIKI_API.  
**Perms / tenant:** WikiAuthorizer; RLS.  
**Tests:** API + isolation.  
**Deps:** none. **Risk:** med (backfill). **Complexity:** M. **Priority:** P0.

### EKOS-1.2 ReviewAt + review queue  
**Objective:** Scheduled review.  
**Story:** As owner, I get pages due for review.  
**Requirements:** ReviewAt; My Work / home list; workflow/stale integration optional.  
**Accept:** Due list permission-safe.  
**Complexity:** M. **Priority:** P0. **Deps:** 1.1.

### EKOS-1.3 KnowledgeRelationships CRUD  
**Objective:** Manual graph edges.  
**Story:** As editor, I relate page A to page B as References.  
**Requirements:** Table + controlled types; panel UI; View both ends to list.  
**Accept:** Cross-tenant impossible; no ghost nodes.  
**Complexity:** L. **Priority:** P0.

### EKOS-1.4 Project existing links into edges  
**Objective:** Avoid dual mental models.  
**Story:** Jira links and labels appear as relationships.  
**Requirements:** Read model or sync job; provenance Integration/System.  
**Complexity:** M. **Priority:** P1. **Deps:** 1.3.

### EKOS-1.5 Saved searches  
**Objective:** Reusable discovery.  
**Story:** As user, I save a tag+space query.  
**Requirements:** User-owned rows; run reapplies ACL.  
**Complexity:** S. **Priority:** P1.

### EKOS-1.6 Search UI filter bar  
**Objective:** Unified discovery.  
**Story:** Combine text + tags + space (extend SmartSearch).  
**Complexity:** M. **Priority:** P0. **Deps:** Labels search exists.

### EKOS-2.1 Editor AI actions  
**Objective:** Writing help with control.  
**Story:** Summarize selection; apply only on confirm.  
**Requirements:** AiService; no auto-publish.  
**Complexity:** L. **Priority:** P1. **Deps:** AI enabled. **Risk:** provider.

### EKOS-2.2 Stale/duplicate scanner  
**Objective:** Maintenance suggestions.  
**Story:** Admin sees near-duplicate candidates.  
**Requirements:** Job; suggestions only.  
**Complexity:** L. **Priority:** P1.

### EKOS-3.1 Decision record type  
**Objective:** ADR/decision knowledge.  
**Story:** Create decision with context/alternatives/outcome.  
**Requirements:** Entity or content type; template; relate to pages/Jira.  
**Complexity:** L. **Priority:** P1. **Deps:** 1.3 preferred.

### EKOS-3.2 Incident knowledge  
**Objective:** Postmortem knowledge layer (not IM tool).  
**Complexity:** L. **Priority:** P2.

### EKOS-3.3 System catalog  
**Objective:** Service/API inventory.  
**Complexity:** L. **Priority:** P2.

### EKOS-3.4 Graph visualization  
**Objective:** Navigate neighborhood.  
**Complexity:** L. **Priority:** P2. **Deps:** 1.3.

### EKOS-3.5 Knowledge analytics  
**Objective:** Coverage/freshness/ownership metrics.  
**Complexity:** M. **Priority:** P1. **Deps:** 1.1–1.2.

### EKOS-4.1 Meeting → knowledge  
**Objective:** Minutes/decisions from calendar.  
**Complexity:** M. **Priority:** P2. **Deps:** Calendar + 3.1.

### EKOS-4.2 Jira real-host verification  
**Objective:** Production confidence.  
**Blocked:** external. **Priority:** P0 ops.

### EKOS-5.x Connectors / agents / OpenSearch / S3  
Per ROADMAP Phase 5; each split when unblocked.

---

## PO approvals required before Stage B

1. Page meta: columns on `Content` vs side table  
2. Decision/Incident as ContentTypes vs separate tables  
3. Classification enum values for first tenants  
4. Whether P1 includes Saved Searches or only filter bar  
5. Legal hold/retention in scope of year-1 or later