# ADR-047 — Experience polish: themes, administrator tools, workspace logo, two-step setting, guidance (Phase 14)

**Status:** Accepted (2026-11-09)

## Context
Real use on the first installation produced a list of requests: text covering icons inside inputs, the guide not reachable from the app, no address column for workspaces, no way for an administrator to help a user who forgot a password, workspaces looking alike, a second authentication step that could not be switched off, vertical space menus, unclear errors ("the page content is not valid"), a toolbar that disappeared while editing the end of a long page, one fixed look, a flat page tree, and thin whiteboards and databases.

## Decisions

### 1. Two-step verification is an installation setting
`Raavi:Authentication:SystemMfaRequired` (default **true**, in both `sandbox/config/authentication.json` and the shipped defaults). It only concerns **system administrators** (tenant users never had a second step).
* `false`: `POST /auth/system/login` ignores the code; `setup/begin` returns an empty secret (nothing to enrol); `setup/complete` does not need a code and does not store a secret. The SPA asks `GET /auth/system/options` (anonymous, system host) and hides the code field and the QR code.
* The setting is read per request (options are bound at start-up, so a restart applies a change). An account created while it was off has **no authenticator**; turning the setting on again makes it fail with `mfa_not_enrolled` until a new invitation re-enrols it (documented in the guide). Documented trade-off: password-only administrator sign-in is the operator's decision (e.g. behind a VPN / on an internal network).

### 2. Administrator tools for members (password, picture)
`PUT /tenant/members/{id}/password`, `PUT|DELETE /tenant/members/{id}/avatar` (tenant administrators). Because an account is global (one `Users` row can belong to several workspaces), **a workspace administrator may touch only an account that is a member of this workspace and of no other workspace and holds no system role** (`TenantIdentityAdminService.ManageableMemberAsync`, using the global `MembershipIndex`). Anything else answers `403 member.account_shared`; a non-member answers `404`. Setting a password applies the normal policy, rotates the security stamp, ends the member's sessions in this workspace and writes a `member.password_reset` security event. Rejected alternatives: letting any administrator reset any member (account takeover across workspaces); sending reset links (no e-mail guarantee in this release).

### 3. Workspace logo
Table `system.TenantLogos` (tenant-owned, RLS, one row per tenant; bytes apart from `TenantBranding`, which only gets `LogoVersion`). Set/removed by **system administrators** (`PUT|DELETE /system/tenants/{id}/logo`, multipart, PNG/JPEG/WebP by content, ≤ 256 KB); public on the workspace host as `GET /host/logo?v=` (anonymous like the branding, `public, max-age=3600`, ETag, `nosniff`). The SPA crops a square in the browser (`PictureCropDialog`, 256 px, PNG with transparency) and shows the logo in the header, sign-in and invitation pages. The system workspace list gained `url` (the canonical address, a link in a new "Address" column).

### 4. Colour themes saved per user
`identity.Users.Theme` (nullable, ≤ 16 chars, allow-list in `User.Themes`) via `PUT /auth/me/theme`, returned in `/auth/me`. The SPA (`core/theme/themes.ts`, `stores/theme.ts`) derives everything from a few numbers per theme (hue, saturation, accent): the `--raavi-*` tokens, the PrimeVue primary and surface scales (only `#rrggbb` values are ever set) and `--raavi-icon`, which colours icons (not on filled buttons, tags and messages). Themes: like my device, light, dark, chocolate, ocean, forest, rose, midnight, high contrast. Dark mode is the `raavi-dark` class (PrimeVue `darkModeSelector`), so the choice, not only the OS, decides. The choice is also kept in `localStorage` for the first paint and before sign-in; after sign-in the profile wins. A tenant's brand colour still overrides the accent.

### 5. Layout and responsiveness
Space menu is a horizontal bar above tree and page. The page tree draws connector lines (CSS only). Sticky offsets use the measured height of the (wrapping) header and title bar, which fixes the bar covering the editor toolbar. Phones: header wraps, labels become icons, grid pages use `minmax(0, 1fr)`, tables scroll inside their own box.

### 6. Guidance for errors
`errorHelp.<code>.<n>` messages (numbered steps) shown by `ErrorHelp.vue` (message + steps + link to the guide) where a person can act: invalid page content, unsupported schema, password rules, shared account, logo type/size. Errors without steps stay a message.

### 7. Whiteboards and databases
Whiteboard: shapes Diamond, Triangle and Line (`BoardShape` 7–9, validated and exported to SVG), grid and snap-to-grid (browser preference), numeric X/Y/size/rotation, key shortcuts and arrow-key nudging. Databases: server-side column filters (`f=colId:value`, AND), totals of Number columns over all matches (`summary`), column visibility, board view by a Select column, duplicate row, CSV import (≤ 500 rows, mapped by header), column reordering.

## Not covered / limits
Two-step setting is for system administrators only; no TOTP for tenant users. No "forgot password" self-service. Logo is set by system administrators only (not by tenant administrators), and tenant export does not include it. Themes do not restyle the editor's code blocks or the whiteboard canvas (the board stays white to match exports). The board view of databases shows at most 500 rows; board and filters are not saved as views. Whiteboard shapes cannot be connected/glued to each other. CSV import posts rows one by one (no batch API).
