# Feature Matrix

Baseline: Confluence Cloud. Status values as in `docs/IMPLEMENTATION_STATUS.md`. All rows are tenant-scoped unless marked System.

| Area | Feature | Confluence Cloud | Raavi phase | Status |
|---|---|---|---|---|
| Tenancy | Tenants, codes, slugs (System) | n/a (site per customer) | 1 | Tested |
| Tenancy | Subdomain resolution | yes | 1 | Tested |
| Tenancy | Custom domains with verification | yes (premium) | 1 | Tested |
| Tenancy | Suspension / deactivation | admin | 1 | Tested |
| Tenancy | Tenant export / deletion | admin | 12 | Planned |
| Tenancy | Branding: colors, welcome message | partial | 1 | Tested |
| Tenancy | Branding: logo, favicon, login page | partial | 7 | Planned |
| Identity | Local accounts (+ TOTP MFA for system admins) | Atlassian account | 2a | Tested |
| Identity | LDAP / Active Directory | via Atlassian Access | 2b | Tested (fake directory; real AD pending) |
| Identity | OIDC (SAML via an OIDC-capable IdP) | Atlassian Access | 2b | Tested (fake IdP) |
| Identity | On-prem Jira login (DC 8.22+ OAuth 2.0, relay callback) | n/a | 2b | Tested (fake Jira; real instance pending Q1) |
| Identity | Groups, tenant admin role, invitations, provider policies | yes | 2a | Tested |
| Identity | Provisioning modes for external users, group sync, account linking, workspace switching | yes | 2b | Tested |
| Spaces | Team / project / documentation / personal / KB spaces, permissions, archive | yes | 3 | Tested |
| Content | Pages, page tree, breadcrumbs, move, reorder, archive, trash (soft) | yes | 3 | Tested |
| Content | Page restrictions (view inherited, edit) | yes | 3 | Tested |
| Content | Quick find by title, activity feed | yes | 3 | Tested |
| Content | Live docs | yes | 6 | Planned |
| Content | Blog posts | yes | 5 | Deferred (not in Phase 3 delivery) |
| Content | Folders | yes | 3 | Tested |
| Content | Shortcuts | yes | 9 | Deferred |
| Content | Templates (space + global) | yes | 3 | Tested |
| Content | Labels | yes | 3 | Tested |
| Editor | Rich text, tables, images/files (attachments-lite), code, panels, expand, status, TOC, mentions | yes | 4 | Tested |
| Editor | Mermaid, math | via apps | 4 | Tested |
| Editor | Charts | via apps | 9 | Planned |
| Editor | Includes / excerpts | yes | 5 | Deferred (needs published versions) |
| Editor | Jira issue macro, smart links | yes | 4 / 11 | Planned |
| Editor | Slash commands (fa/en) | yes | 4 | Tested |
| Versioning | Drafts (autosave), publish, history, diff, restore, trash | yes | 5 | Tested |
| Versioning | Review / expiry dates, trash retention | yes | 12 | Planned |
| Versioning | Scheduled publish, approval foundation | apps/premium | 5 | Tested |
| Versioning | Approval workflows | apps/premium | 10 | Planned |
| Collaboration | Real-time co-editing, presence, cursors | yes | 6 | Tested (single collaboration node) |
| Collaboration | Offline edits | limited | 6 | Implemented (y-indexeddb; not browser-verified) |
| Comments | Page + inline comments, reactions, resolve | yes | 7a | Tested |
| Mentions, watching, in-app notifications (push) and e-mail | | yes | 7a | Tested |
| Notifications | In-app, e-mail, mentions, watch | yes | 7 | Planned |
| Media | Attachments, versions, previews, quotas | yes | 7 | Planned |
| Search | Full text, filters, suggestions, Persian normalization | yes (no Persian tuning) | 7 | In Progress (normalizer done) |
| Migration | Confluence import with full history | n/a | 8 | Planned (contracts done) |
| Whiteboards | Infinite canvas, collaboration, export | yes | 9 | Planned |
| Databases | Metadata-driven databases, views | yes | 9 | Planned |
| Tasks | Tasks with assignees, due dates | yes | 9a | Tested |
| Calendar | Team/space calendars, Jalali | Team Calendars app | 9 | Planned |
| Slides | Slide decks, presenter, PDF/PPTX | n/a | 9 | Planned |
| Workflows | Workflow designer, approvals | apps | 10 | Planned |
| Automation | Rules: triggers → conditions → actions | yes | 10 | Planned |
| AI | Q&A, summarization, writing, tagging, related | Atlassian Intelligence | 11 | Planned |
| Integrations | Jira, GitHub, GitLab, Teams, Outlook, Google, webhooks | yes | 11 | Planned |
| Analytics | Content, usage, search, AI analytics | premium | 12 | Planned |
| I18n | Persian + English UI, RTL/LTR, tenant default locale/calendar | partial (no fa) | 0+ | In Progress (shell + tenant defaults tested) |
| Operations | Docker Compose, Helm, backup/restore, monitoring | n/a (SaaS) | 0 / 12 | In Progress (dev compose authored) |
