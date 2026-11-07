# ADR-012: Permission and ACL

**Status:** Accepted · **Date:** 2026-11-06

## Context
Confluence-like permissions (space permissions + page restrictions) plus explicit deny, groups, roles, shared links, and identical enforcement across REST, search, attachments, exports, AI, collaboration and notifications.

## Decision

### Model
- **Scopes:** System → Tenant → Space → Content.
- **Subjects:** User, Group, Role, Anonymous (only if tenant policy allows public access), System principal.
- **Actions:** `PermissionAction` enum (in code since Phase 0).
- **Grants:** `PermissionGrants(Scope, ScopeId, SubjectType, SubjectId, Action, Effect=Allow|Deny)` at tenant and space scope (and content scope for fine-grained actions such as Comment/Edit overrides).
- **Content restrictions:** `ContentRestrictions(Kind=View|Edit, Subject)` on content nodes. View restrictions are **inherited** by all descendants (a user must satisfy every restricted ancestor); Edit restrictions apply to the node only (Confluence semantics).

### Evaluation order (`IPermissionEvaluator`)
1. Tenant resolved and `Active`; principal is an active member (or anonymous with public policy). Else **deny** (`tenant_unavailable` / `not_member`).
2. System principals for system operations → allow only for the declared operation scope.
3. Resource exists in the current tenant (else `not_found` — surfaced as 404).
4. Collect principal set: user id, group ids (incl. nested, flattened at write time), role ids, `Anonymous`/`AuthenticatedMember` pseudo-groups.
5. **View gate:** for content, every ancestor-or-self with View restrictions must include a principal of the set. Fail → `not_found` (no existence disclosure).
6. **Edit gate** (for Edit/Delete/Move/Publish/Archive/AttachFiles): if the node has Edit restrictions, a principal must be listed.
7. **Deny wins:** any Deny grant for the action at content, space or tenant scope matching the principal set → deny.
8. **Allow:** any Allow grant for the action at content, space or tenant scope → allow.
9. Tenant administrators: may manage permissions and recover access (`ManagePermissions`, restriction removal), but do **not** bypass View restrictions for reading content unless an audited "recover access" action is performed.
10. Default: deny.

### Performance and consistency
- Effective principal sets and space grants cached per tenant with version stamps (`t:{tid}:acl:v{n}`); any grant/restriction/membership/group change bumps the version → immediate invalidation cluster-wide.
- **`EffectiveViewAcl`** materialized per content (principal ids allowed to view, or `*member` when unrestricted) maintained by outbox consumers; indexed into search documents as ACL tokens (ADR-013).
- Revocation: membership/permission changes publish collaboration revocation events, revoke shared links and invalidate SignalR group memberships.

### Shared links
Token (hash stored), bound to one content item and permission (View or Comment), expiry, optional password; tenant policy can disable them. Access via a shared link never exposes children, search or other content.

## Alternatives considered
- Pure RBAC — rejected: insufficient for page-level restrictions.
- Full Zanzibar-style ReBAC service (OpenFGA/SpiceDB) — rejected for v1: extra infrastructure on-prem; the model above is designed so it could be backed by one later.
- Allow-precedence — rejected: deny-wins is safer and expected by enterprise admins.

## Consequences
Search and AI correctness depend on timely `EffectiveViewAcl` updates; search results are additionally post-checked for the returned page of hits (bounded cost) so stale ACL tokens can only hide, never leak, content.
