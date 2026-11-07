# ADR-007: Jira Authentication Integration

**Status:** Accepted · implemented in Phase 2b · real-instance verification pending (Q1) · **Date:** 2026-11-06

## Context
Users must log in with accounts from a self-hosted Jira (e.g. `https://jira.company.local`). Jira Server reached end of support in February 2024; most remaining installations are Jira **Data Center**. Jira is not an OpenID Provider: it issues no `id_token`. Capabilities differ by version.

## Decision
Provider type `JiraDataCenter` with a configurable `Mode`, chosen per instance after inspection:

| Mode | Requirement | Flow | Identity |
|---|---|---|---|
| `SharedIdp` (preferred when available) | Jira already uses SAML/OIDC SSO through an enterprise IdP | Raavi uses the same IdP via its OIDC provider; Jira provider used only for lookups/integration | IdP subject, linked to Jira user via configured attribute |
| `OAuth2` | Jira DC ≥ 8.22 (incoming OAuth 2.0 application link) | Authorization code (+PKCE when supported), confidential client, scope `READ`, server-side code exchange | `GET /rest/api/2/myself` → `key` (stable), `name`, `emailAddress`, `displayName`; optionally groups via `/rest/api/2/user?key=…&expand=groups` |
| `OAuth1` | Older versions with application links | RSA-SHA1 three-legged OAuth 1.0a | same |

Rules:
- Subject = Jira user **key** (survives username rename); provider id distinguishes Jira instances.
- Jira e-mail untrusted by default (`TrustEmail=false`).
- Access token used for the identity call and discarded unless the Jira integration (Phase 11) is enabled for the tenant, in which case it is stored encrypted with refresh handling.
- All values configurable (base URL, mode, client id/secret, scopes, endpoints); `TestConnectionAsync` checks `/rest/api/2/serverInfo` (version, deployment type) and OAuth endpoint reachability, and reports which modes the instance supports.
- **Forbidden:** password forwarding (`/rest/auth/1/session`, Basic auth with user credentials), cookie scraping, undocumented endpoints.

## Verification plan (Phase 2 spike)
1. Read `/rest/api/2/serverInfo` of the customer instance (version, `deploymentType`).
2. Confirm an incoming OAuth 2.0 link can be created by the Jira admin and which redirect URIs it accepts (wildcards are typically not allowed → relay callback on the system host).
3. Confirm PKCE support and token lifetimes.
4. If the instance is older than 8.22 or OAuth 2.0 is not allowed, fall back to OAuth 1.0a or SharedIdp; document the outcome in this ADR.

## Phase 2b outcome (2026-11-06)

Implemented: provider type `JiraDataCenter`, `Mode=OAuth2` only — authorization code + PKCE (S256) against `{BaseUrl}/rest/oauth2/latest/authorize` and `/token` (client_secret_post), identity from `GET /rest/api/2/myself` (subject = `key`, inactive accounts refused), groups from `GET /rest/api/2/user?key=…&expand=groups`, e-mail untrusted by default, access token discarded after the identity calls. Default `CallbackMode=Relay`: one fixed redirect URI on the system host (`/api/v1/auth/relay/{providerId}`) followed by a one-time 60 s hand-off to the tenant host, which re-checks tenant and the browser-binding cookie. `TestConnectionAsync` reads `/rest/api/2/serverInfo` and reports `jira_version_unsupported` below 8.22. `Mode=OAuth1` fails configuration validation with guidance to use the SharedIdp option (an `OpenIdConnect` provider against the IdP Jira already uses).

Verified against an in-process fake Jira that enforces client credentials, redirect URI and PKCE (`ExternalLoginTests`). **Not yet verified against the customer's Jira** — Q1 (version, deployment type, whether an incoming OAuth 2.0 link can be created, accepted redirect URIs, PKCE support) remains open; the verification plan above still applies.

## Consequences
Because a fixed redirect URI per application link is likely, Jira logins use the system-host relay (AUTHENTICATION_ARCHITECTURE §4) when tenants use different hosts.
