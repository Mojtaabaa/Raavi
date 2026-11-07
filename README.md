# Raavi (راوی)

Enterprise wiki and knowledge-management platform — multi-tenant, real-time collaborative, fully on-premises, Persian and English.

**Status:** Phase 0 (architecture foundation) complete. See [`docs/IMPLEMENTATION_STATUS.md`](docs/IMPLEMENTATION_STATUS.md) and the [roadmap](docs/product/IMPLEMENTATION_ROADMAP.md).

## Stack

ASP.NET Core 10 · Vue 3 + TypeScript + PrimeVue · SQL Server 2022+ · Tiptap 3 · Yjs + Hocuspocus · SignalR · Redis protocol (Valkey/Redis) · OpenSearch · S3-compatible storage · Hangfire · Docker Compose / Kubernetes.

## Repository

| Path | Contents |
|---|---|
| `backend/` | .NET solution (`Raavi.slnx`) — Domain, Application, Infrastructure, feature modules, Api, Workers, Tests |
| `frontend/` | Vue 3 SPA |
| `collaboration/` | Hocuspocus collaboration server (Phase 6) |
| `sandbox/config/` | Global (all-tenant) configuration — no secrets |
| `deployment/` | Compose files, Kubernetes, scripts |
| `docs/` | Architecture, ADRs, database, API, security, migration, deployment, development |

## Quick start (development)

```bash
cd backend && dotnet build Raavi.slnx && dotnet test Raavi.slnx
```

```bash
cd frontend && npm install && npm run build && npm test
```

Full instructions: [`docs/development/DEVELOPMENT_GUIDE.md`](docs/development/DEVELOPMENT_GUIDE.md).

## Key documents

- [System architecture](docs/architecture/SYSTEM_ARCHITECTURE.md)
- [Multi-tenant architecture](docs/architecture/MULTI_TENANT_ARCHITECTURE.md) · [Tenant isolation](docs/architecture/TENANT_ISOLATION.md)
- [Authentication](docs/architecture/AUTHENTICATION_ARCHITECTURE.md) · [Security](docs/architecture/SECURITY_ARCHITECTURE.md)
- [Editor](docs/architecture/EDITOR_ARCHITECTURE.md) · [Collaboration](docs/architecture/COLLABORATION_ARCHITECTURE.md)
- [Confluence migration](docs/migration/CONFLUENCE_MIGRATION.md)
- [ADRs](docs/architecture/adr/README.md)
