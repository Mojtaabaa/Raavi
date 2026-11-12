# Authorization and Security

## Current model (keep)

- Tenant membership ≠ authentication ≠ authorization  
- `WikiAuthorizer` / `IPermissionEvaluator` for all resource access  
- Space grants + content restrictions; invisible → 404  
- RLS on tenant-owned tables; never trust request TenantId  
- Identity: local, OIDC, LDAP, Jira login; no email auto-merge  
- Sessions, XSRF, MFA for system admins  

## EKOS extensions (additive)

| Need | Approach |
|------|----------|
| Asset-type permissions | Map Decision/Incident/Catalog to ContentNode providers **or** dedicated evaluators called from WikiAuthorizer |
| Graph traversal | View both endpoints |
| AI tools / agents | Evaluate as initiating user; tool allow-list |
| Connector admin | TenantAdministrator (existing pattern) |
| Classification | Deny AI external send / export when Restricted unless policy allows |
| Analytics | Tenant admin dashboards; member widgets only on viewable ids |

## Do not

- Second permission product  
- Client-side authz  
- Broaden service keys beyond collaboration internal HMAC pattern  

## Compliance backlog (later phases)

Encryption-at-rest guidance for SQL/files; retention/legal hold; AI data-sharing policy UI; ASVS L2 remaining items; pen test (roadmap Phase 12).

## Isolation test mandate

Every new table: `ITenantOwned` + `AddTenantIsolation` + isolation tests. Graph/search/AI paths covered by leakage tests (pattern: RagService permission tests).