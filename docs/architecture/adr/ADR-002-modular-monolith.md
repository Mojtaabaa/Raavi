# ADR-002: Modular Monolith

**Status:** Accepted · **Date:** 2026-11-06

## Context
Clear boundaries are needed to keep a large product maintainable, without the cost of microservices.

## Decision
- Layers: Domain ← Application ← Infrastructure ← Feature modules (Tenancy, Identity, Migration, Search, AI, Integrations) ← Hosts (Api, Workers). Details in `MODULE_BOUNDARIES.md`.
- Feature modules do not reference each other; they communicate through Application-level interfaces or outbox events.
- Each module owns a SQL schema and its own EF Core DbContext/migrations; all contexts inherit the tenant-enforcing base context.
- Rules are **enforced by architecture tests** (`DependencyRulesTests`) that run in CI.
- No mediator library (MediatR is now commercial); use small handler interfaces (`ICommandHandler<TCommand, TResult>`), registered by assembly scanning.
- Separate processes only where justified: API, Workers, collaboration server, search indexing workers (a Worker queue, not a separate codebase).

## Alternatives considered
- One project per bounded context from day one (20+ projects) — rejected for now: high ceremony; core wiki contexts start as namespaces and may be split out later without changing contracts.
- Vertical-slice only with no shared layers — rejected: cross-cutting tenancy/authorization contracts need one home.

## Consequences
Discipline needed for namespace boundaries inside the core projects (code review + future namespace-level architecture tests).
