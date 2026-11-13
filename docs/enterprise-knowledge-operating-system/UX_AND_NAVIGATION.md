# UX and Navigation

## Principle

Coherent product IA — not a flat list of every module. Adapt to existing shell (`AppShell` More + Administration dropdowns).

## Conceptual destinations → current routes

| Concept | Today | Target |
|---------|-------|--------|
| Home | `/` | Enrich widgets: reviews, approvals, meetings, AI entry |
| Knowledge | Spaces / pages | “Knowledge” hub: spaces + decisions/incidents/catalog later |
| Search | `/search` smart search | Unified filters (tags already starting) |
| My Work | Tasks mine, approvals | Aggregate tasks + reviews + RSVPs |
| Calendar | More → Calendar | Keep; deepen meeting→knowledge |
| Decisions / Systems | — | New admin-adjacent nav when types exist |
| Integrations | Admin connectors | Keep under Administration |
| Analytics | Activity + label analytics | Knowledge dashboard (admin) |
| Administration | Existing | + Labels already; + governance settings |

## Patterns to reuse

- Command palette (Ctrl/K) — extend actions  
- LinkedJiraPanel — template for graph-related panel  
- NavDropdown — avoid header overflow  
- ErrorHelp + i18n fa/en + RTL logical CSS  
- Jalali/Gregorian via ADR-053 display settings  

## Home dashboard widgets (phased)

Recent pages, my drafts, pending approvals, upcoming meetings, my tasks, followed spaces, AI ask, recommendations (view-filtered). User layout prefs later.

## Entity navigation

Page ↔ Decision ↔ Meeting ↔ Jira ↔ System via relationship panel + breadcrumbs — permissions preserved.

## Accessibility

Keyboard, contrast, screen reader labels on new graph/search controls; no color-only status.