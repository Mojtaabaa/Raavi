# Module Boundaries

## 1. Projects

| Project | Kind | Contains |
|---|---|---|
| `Raavi.Domain` | Core | Shared kernel (`Entity`, `ITenantOwned`, `DomainException`) and the core wiki domain by bounded context namespace: `Spaces`, `Content`, `Versioning`, `Comments`, `Permissions`, `Media`, `Collaboration`, `Templates`, `Tasks`, `Calendar`, `Databases`, `Whiteboards`, `Slides`, `Workflows`, `Automation`, `Notifications`, `Audit` |
| `Raavi.Application` | Core | Use cases per bounded context, DTOs, validators, cross-cutting abstractions (`Abstractions/*`), `Result`/`Error` |
| `Raavi.Infrastructure` | Core | Shared technical infrastructure: EF Core base DbContext + tenant interceptors, SQL Server, Redis, S3, SMTP, Hangfire, configuration, HTTP foundation, logging |
| `Raavi.Tenancy` | Feature module | Tenants, domains, domain verification, tenant settings, tenant resolution middleware, quotas, lifecycle |
| `Raavi.Wiki` | Feature module | Spaces, content tree (closure table), page versions, labels, templates, activity, PermissionEvaluator v1 (grants + restrictions), outbox producer/handlers (ADR-023) |
| `Raavi.Identity` | Feature module | Users, external identities, provider registry and providers (local, LDAP/AD, OIDC, OAuth2, Jira), provisioning, memberships, groups, roles, sessions, API tokens |
| `Raavi.Migration` | Feature module | Confluence import (source adapters, converter, mappers, executor, validator) |
| `Raavi.Search` | Feature module | OpenSearch index management, analyzers, indexing consumers, query service with ACL filtering, Persian normalization |
| `Raavi.AI` | Feature module | AI providers (Microsoft.Extensions.AI), embeddings, RAG, prompt templates, usage tracking |
| `Raavi.Integrations` | Feature module | Jira/GitHub/GitLab/Teams/Outlook/Google connectors, webhooks, SSRF-guarded HTTP |
| `Raavi.Api` | Host | Composition root, endpoint mapping, OpenAPI, SignalR hubs |
| `Raavi.Workers` | Host | Composition root for Hangfire server and outbox dispatchers |
| `Raavi.Tests` | Tests | Unit, architecture, API integration tests (database integration tests added in Phase 1 with Testcontainers or a dedicated SQL instance) |
| `collaboration/` (Node.js) | Separate process | Hocuspocus server, ticket verification, persistence adapter calling the internal API |
| `frontend/` | SPA | Vue application, module folders mirror backend bounded contexts |

## 2. Dependency rules (enforced by `Raavi.Tests/Architecture/DependencyRulesTests.cs`)

```
Domain  ←  Application  ←  Infrastructure  ←  Feature modules  ←  Hosts (Api, Workers)
```

1. `Raavi.Domain` references no other Raavi project and no ASP.NET Core / EF Core / SqlClient / Redis assembly.
2. `Raavi.Application` references only `Raavi.Domain`, and no ASP.NET Core / EF Core.
3. `Raavi.Infrastructure` never references a feature module or host.
4. Feature modules never reference each other or a host. Cross-module collaboration goes through interfaces declared in `Raavi.Application/Abstractions` (implemented by the owning module, registered by the host) or through outbox events.
   - Example: the Confluence importer needs to map users — it calls `IUserDirectory` (Application abstraction) implemented by Identity.
5. Hosts contain no business logic.

## 3. Persistence ownership

Each module owns its tables in its own SQL schema and its own EF Core `DbContext` + migration history:

| Schema | Owner |
|---|---|
| `system` | Tenancy (tenants, domains, global audit, feature flags) |
| `identity` | Identity |
| `content` (and other core schemas listed in `DATABASE_DESIGN.md`) | Core (Infrastructure `RaaviDbContext`) |
| `migration` | Migration |
| `search` | Search (outbox cursor, index state) |
| `ai` | AI |
| `integration` | Integrations |
| `hangfire` | Hangfire |

A module never writes another module's tables. Reads across modules use the owning module's query interfaces; reporting/analytics may use read-only views created by migrations.

All DbContexts derive from `RaaviDbContextBase` (Phase 1), which installs the tenant query filters, the `TenantId` stamping/immutability interceptor and the `SESSION_CONTEXT` connection interceptor. A module cannot opt out.

## 4. Frontend module structure

`frontend/src/modules/<context>/` with `views/`, `components/`, `stores/`, `api/`, `routes.ts`. Cross-module imports only via `@/core`, `@/shared` or a module's public `index.ts`. Editor extensions live in `modules/editor/extensions/<id>/` and are registered through the Extension Registry (ADR-010).
