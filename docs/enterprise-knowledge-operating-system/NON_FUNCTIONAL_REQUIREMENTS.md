# Non-Functional Requirements

## Security

Multi-tenant isolation; server-side authz; secret hygiene; AI min-context; audit sensitive ops; ASVS-oriented endpoint authorization tests continue.

## Performance

Respect existing baseline (`docs/development/PERFORMANCE_BASELINE.md` — modest scale). Graph depth capped. Search pagination mandatory. Index jobs idempotent/bounded.

## Availability / ops

Health live/ready/status; worker heartbeat; backup/restore scripts; no silent destructive jobs. Optional OpenSearch/S3 must degrade gracefully when unset.

## Scalability

Prefer SQL + Hangfire; OpenSearch/S3/Redis when justified and available. Avoid microservice split.

## Localization

fa + en parity; RTL logical CSS; Jalali/Gregorian display; Persian search folding continuity.

## Accessibility

Keyboard, labels, contrast; graph/search alternatives (list views).

## Compatibility

On-prem Windows/Linux hosts per ADR-018; sandbox config; upgrade ConfigSync (ADR-040/041).

## Observability

Correlation ids; structured logs; connector/index/AI health surfaces for admins.