# ADR-006: Identity and Authentication Provider Architecture

**Status:** Accepted · **Date:** 2026-11-06

## Context
Customers use several identity sources at once (local, LDAP/AD, OIDC, multiple Jira instances). Authentication must be separate from membership and authorization.

## Decision
- Provider-based design: `IAuthenticationProvider` per provider type, `IAuthenticationProviderRegistry` built from **global definitions** in `sandbox/config/identity-providers.json`; **tenant enablement and policy** in SQL Server (`TenantAuthenticationProviders`).
- Contracts (in code since Phase 0): `IAuthenticationProvider`, `IAuthenticationProviderRegistry`, `ITenantProviderPolicyService`, `IExternalIdentityResolver`, `IUserProvisioningService`, `ITenantMembershipService`.
- Identity key `(ProviderId, ExternalSubjectId)`; no e-mail auto-linking; explicit, audited account linking.
- Provisioning modes: existing-only, automatic (domain/group restricted), admin approval.
- Browser sessions: server-side session + `__Host-` host-only cookie (BFF style, no tokens in browser storage). Anti-forgery header for state-changing requests.
- ASP.NET Core authentication handlers are used for protocol mechanics (OIDC handler, cookie handler); Raavi's provider layer wraps them to add tenant binding, identity mapping and provisioning.
- System administrators authenticate only on the system host, with MFA.

## Alternatives considered
- JWT access tokens in the SPA — rejected: XSS-exfiltratable, revocation harder; same-origin cookie sessions are simpler and safer.
- Embedding an OAuth/OIDC server (Duende/OpenIddict) as hub — not needed for v1 (Raavi is a relying party). OpenIddict remains the option if Raavi must issue tokens to third parties.
- One `AuthService` with a switch per provider type — rejected (spec + extensibility).

## Consequences
Each provider is independently testable; plugin providers possible (ADR-015). Callback handling across many tenant hosts needs per-host registration or the relay flow.
