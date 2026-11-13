# Capability Matrix

Statuses: **Complete** | **Incomplete** | **Deferred** | **Missing** | **Blocked** | **Won’t**

| Domain | Capability | Status | Notes / evidence |
|--------|------------|--------|------------------|
| A Knowledge | Spaces / pages / tree | Complete | Phase 3–5 |
| A | Editor + schema | Complete | ADR-010; TipTap Pro Won’t |
| A | Templates | Complete | |
| A | Versioning / publish / restore | Complete | ADR-019; review/expiry dates Incomplete |
| A | Approvals / publishing gates | Incomplete | 10b + publish-request; chains/escalation Deferred |
| A | Labels taxonomy | Complete | `LabelTaxonomy` 2026-11-11 |
| A | Attachments | Incomplete | SQL varbinary; S3 Deferred 7b |
| A | Page ownership / review schedule / classification | Missing | Owner ≈ CreatedBy/UpdatedBy only |
| A | Reusable content blocks / includes | Deferred | Editor deferrals |
| B Graph | Controlled relationships + UI | Missing | PageIssueLinks only specialized |
| C Search | Quick-find | Complete | |
| C | Hybrid RAG | Incomplete | Complete vs fakes; real embedder Blocked |
| C | OpenSearch | Deferred | 7c |
| C | Saved searches / history | Missing | |
| C | Attachment/comment index | Missing | |
| D AI | Page ask/summary | Incomplete | Fake-tested |
| D | Prompt Hub | Incomplete | ADR-052 gaps |
| D | Editor writing actions | Missing | |
| D | Maintenance / extraction / agents | Missing | |
| E Governance | Workflows / stale / reminders | Complete | ADR-031–033 |
| E | Classification / retention / legal hold | Missing | |
| F Collab | Live edit / comments / notifs | Complete | |
| F | Calendar + RSVP + links | Incomplete | External sync Missing; Templar Blocked |
| G Records | Decision / ADR / Incident / Catalog | Missing | |
| H Integration | Issue connectors + Jira page links | Incomplete | Fakes; Cloud v3 Missing |
| H | Webhooks | Complete | |
| H | Git/CI/Outlook/Google | Missing | |
| I Workflows | Knowledge lifecycle workflows | Incomplete | Rules+approvals; not general BPM (Won’t full BPM) |
| J Analytics | Ops activity / label / prompt | Incomplete | No EKOS dashboard |
| Platform | Tenant isolation / authz | Complete | |
| Platform | Identity OIDC/LDAP/Jira | Incomplete | Fakes; real Blocked |
| Platform | Export / backup / release | Incomplete | Space import partial |
| Platform | Confluence migration | Incomplete | Comments/XML perms open |
| Platform | HA Redis collab / k6 large | Deferred | |

## Classification for roadmap

- **Reuse & deepen:** Labels, Calendar, RAG, Prompt Hub, WikiAuthorizer, outbox, workflows, Jira links  
- **Introduce carefully:** KnowledgeRelationship edges, Decision/Incident/Catalog types, ownership/review metadata, saved search, governance classification  
- **Wait on externals:** OpenSearch, S3, Templar live, real Jira/AD, calendar OAuth