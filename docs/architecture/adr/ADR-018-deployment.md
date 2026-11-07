# ADR-018: Deployment

**Status:** Accepted · **Date:** 2026-11-06

## Context
Fully on-premises; customers range from single-server installs to HA clusters; no mandatory external dependency.

## Decision
- Container images: `raavi-api`, `raavi-workers`, `raavi-collab`, `raavi-web` (reverse proxy + SPA static files), `raavi-migrator` (applies EF migrations, then exits). Non-root, minimal base images, health checks, OCI labels, SBOM.
- **Docker Compose** profiles: `dev` (infrastructure only for local development), `single-server` (everything incl. SQL Server, Valkey, OpenSearch, S3 store), `external-infra` (customer-provided SQL/Redis/OpenSearch/S3).
- **Kubernetes Helm chart** for HA: API/workers/collab Deployments with HPAs, PodDisruptionBudgets, Ingress with wildcard + custom domain TLS, sticky routing for `/collab` by document (consistent hashing on query param) recommended.
- Configuration: `sandbox/config` mounted read-only at `/etc/raavi/sandbox/config` (`RAAVI_SANDBOX_PATH`), secrets via Docker/Kubernetes secrets as `RAAVI_*` env vars or files.
- Observability: OpenTelemetry (OTLP) → customer collector; optional Prometheus/Grafana/Loki bundle; JSON logs to stdout.
- Health: `/health/live` (process), `/health/ready` (dependencies: SQL, Redis, OpenSearch, S3 — degraded vs unhealthy distinguished).
- Upgrades: migrator job first (expand migrations), rolling update of services, contract migrations in a later release.
- Air-gapped installs supported: images as tarballs, no runtime downloads (fonts/assets self-hosted, no CDNs).

## Alternatives considered
- IIS/Windows-service deployment — not a primary target; may be documented for Windows-only customers later (API and workers are plain ASP.NET Core and can run on IIS).

## Consequences
Docker was not available on the Phase 0 machine; compose files are untested until a Docker host is available.
