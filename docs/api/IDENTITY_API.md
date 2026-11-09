# Identity API (Phase 2a)

Paths under `/api/v1`. **Host**: tenant / system / any (wrong host → 404 before authentication). **CSRF**: every non-GET request authenticated by cookie (and every anonymous auth POST) needs `X-XSRF-TOKEN` from `GET /auth/antiforgery`; refetch it after sign-in/out. Requests with `Authorization: Bearer` are exempt and are never authenticated by the session cookie.

## Authentication

| Method | Path | Host | Auth | Notes |
|---|---|---|---|---|
| GET | `/auth/antiforgery` | any | anonymous | `{token, headerName}`; sets the antiforgery cookie |
| GET | `/auth/providers` | tenant | anonymous | Sign-in methods enabled for the tenant: `[{id, type, flow, displayName{fa,en}, isDefault}]` |
| POST | `/auth/login/local` | tenant | anonymous, rate-limited | `{email, password}` → `Me` + session cookie. Every failure → `401 auth.invalid_credentials` (unknown user, wrong password, locked, not a member, disabled) |
| POST | `/auth/invitations/inspect` | tenant | anonymous, rate-limited | `{token}` → `{email, expiresAt, tenantAdministrator}` or `404 invitation.invalid` |
| POST | `/auth/invitations/accept` | tenant | anonymous, rate-limited | `{token, mode: "new"|"existing", password, displayName?}` → `Me` + session. `new` on an existing address → `409 invitation.account_exists` |
| POST | `/auth/logout` | any | — | Revokes the server-side session, clears cookie |
| GET | `/auth/me` | any | user | `{id, displayName, email, preferredLocale, hostKind, isTenantAdministrator, isSystemAdministrator, provider}` |
| PUT | `/auth/me/preferences` | any | user | `{locale: "fa"|"en"|null}` |
| POST | `/auth/system/login` | system | anonymous, rate-limited | `{email, password, code}` (TOTP mandatory). Failures → `401 auth.invalid_credentials` |
| POST | `/auth/system/setup/inspect` | system | anonymous | `{token}` |
| POST | `/auth/system/setup/begin` | system | anonymous | `{token}` → `{secret (base32), otpAuthUri}` (QR rendered locally by the SPA) |
| POST | `/auth/system/setup/complete` | system | anonymous | `{token, displayName, password, code}` → MFA session |

## External providers (Phase 2b)

| Method | Path | Host | Notes |
|---|---|---|---|
| POST | `/auth/login/credentials/{providerId}` | tenant | LDAP/AD (and local) credentials: `{email (username), password}`; generic `401` on bad credentials |
| POST | `/auth/login/{providerId}/begin` | tenant | `{returnPath}` → `{redirectUrl}`; sets the browser-binding cookie |
| GET | `/auth/callback/{providerId}` | tenant | Provider redirect (PerHost) → `302` to return path or `/login?error=<code>` |
| GET | `/auth/relay/{providerId}` | system | Provider redirect (Relay) → `302` to `/api/v1/auth/relay/complete?code=…` on the tenant host |
| GET | `/auth/relay/complete` | tenant | One-time hand-off → session → `302` |
| POST | `/auth/me/identities/{providerId}/link` | tenant | Signed-in user → `{redirectUrl}`; completion → `/me?linked=…` or `/me?error=…` |
| GET/DELETE | `/auth/me/identities[/{id}]` | any | Linked identities; unlink refuses to remove the last sign-in method (`409 identity.last_sign_in_method`) |
| GET | `/auth/me/workspaces` | any | Caller's active workspaces with canonical URLs (`isCurrent`) |
| POST | `/tenant/authentication-providers/{providerId}/test` | tenant (admin) | `{isHealthy, statusCode, detail}` |

Error codes returned after a provider round-trip: `auth.not_provisioned`, `auth.account_link_required`, `auth.pending_approval`, `auth.not_member`, `auth.email_not_verified`, `auth.domain_not_allowed`, `auth.group_not_allowed`, `auth.state_invalid`, `auth.provider_error`, `auth.provider_denied`, `auth.identity_in_use`, `auth.external_account_inactive`.

## Tenant administration (tenant host, role TenantAdministrator)

| Method | Path | Notes |
|---|---|---|
| GET | `/tenant/members` | Members of the current tenant only |
| POST | `/tenant/members/{userId}/suspend` / `activate` | Suspension revokes the member's sessions in this tenant immediately |
| DELETE | `/tenant/members/{userId}` | Removes membership, roles and group memberships; revokes sessions |
| PUT | `/tenant/members/{userId}/roles` | `{tenantAdministrator: bool}`; the last active administrator cannot be removed, demoted or suspended (`409 member.last_administrator`) |
| GET/POST | `/tenant/invitations` | POST `{email, tenantAdministrator}` → `{id, email, expiresAt, acceptUrl}`; link shown once; token in URL **fragment** |
| DELETE | `/tenant/invitations/{id}` | Revoke |
| GET/POST | `/tenant/groups` | `{name, description}`; names unique per tenant |
| PUT/DELETE | `/tenant/groups/{id}` | |
| GET | `/tenant/groups/{id}/members` | user ids |
| POST/DELETE | `/tenant/groups/{id}/members/{userId}` | only members of this tenant |
| GET/PUT | `/tenant/authentication-providers` | `[{providerId, enabled, isDefault, displayOrder, provisioning, allowedEmailDomains[], allowedExternalGroups[]}]`; at least one enabled; only globally defined providers |
| GET | `/tenant/security-events?take=` | Tenant security audit (no secrets) |

## System administration (system host, SystemAdministrator: MFA session or operator API key)

| Method | Path | Notes |
|---|---|---|
| GET | `/system/administrators` | |
| POST | `/system/administrators/invitations` | `{email}` → setup link (`/system/setup#token=…`) |
| POST | `/system/tenants/{id}/invitations` | `{email, tenantAdministrator}` → link on the tenant's canonical host |

See also `TENANCY_API.md` for tenant lifecycle endpoints.

## Bootstrapping a new installation

**Simplest:** set `Raavi:Authentication:Bootstrap:SystemAdministrator:{Email,InitialPassword}` (sandbox `authentication.json`) and restart the API. If no system administrator exists, the API creates one whose temporary password only opens the first sign-in: `POST /api/v1/auth/system/login` with the temporary password answers `401 auth.first_login_required` (no session); `POST /api/v1/auth/system/first-login {email, password}` verifies it (lockout rules apply) and returns `{acceptUrl}` to the normal `/system/setup` page, whose `/setup/inspect` then reports `passwordChangeRequired: true` and `/setup/complete` takes `{token, displayName, password: <temporary>, code, newPassword}` (policy applies; must differ). Afterwards the account is an ordinary administrator with TOTP. Remove the settings after the first sign-in. Alternative (API key + invitation):

1. Configure an operator API key hash (TENANCY_API.md) and run `Raavi.Migrator`.
2. `POST /api/v1/system/administrators/invitations` with the API key → open the setup link, scan the QR code, set a password, confirm a TOTP code.
3. Sign in at `/system/login`; create tenants and invite each tenant's first administrator from the UI.
4. Remove or rotate the bootstrap API key if it is no longer needed for automation.
