# Risks and Dependencies

## Critical risks

| Risk | Mitigation |
|------|------------|
| Scope explosion / rewrite urge | Stage A gate; incremental Stage B; reuse modules |
| ACL leakage via search/AI/graph | Mandatory FilterViewable; leakage tests |
| Dual SoT with Jira/Templar | Ownership matrix; no silent overwrite |
| Migration breaks on-prem DBs | Additive migrations; backfill; restore drills |
| External AI data exfil | Tenant AI policy; classification later |
| License creep (TipTap Pro, PrimeVue 5) | Won’t — DEPENDENCY_AND_LICENSING |

## External dependencies (Blocked)

| Dependency | Blocks |
|------------|--------|
| Real Jira/AD/OIDC hosts | Production confidence |
| Templar API enablement | Live Templar calendar/analytics |
| MinIO/S3 | 7b media |
| OpenSearch cluster | 7c full-text |
| Google/Outlook OAuth apps | External calendar sync |

## Internal dependencies

- Relationships before rich graph viz and “related search”  
- Ownership before stewardship analytics  
- Content providers pattern before Decision/Incident if choosing that path  

## Decisions needing PO approval

Listed in IMPLEMENTATION_BACKLOG.md (meta storage, record types, classification, P1 scope, retention/hold timing).