# ADR-046 — Brand assets and profile pictures (Phase 13h)

**Status:** Accepted (2026-11-09)

## Context
The operator supplied the Raavi brand package (logo, white logo, icon, favicons). Users asked for a profile picture shown wherever a user appears.

## Decisions
### Branding
* The package is kept unchanged in `docs/branding/`; the SPA serves the favicon set, `site.webmanifest`, `raavi-icon.svg`, `raavi-logo.svg` and `raavi-logo-white.svg` from `frontend/public/`. `BrandLogo.vue` shows the icon in the header and the logo (white variant in dark mode) on the sign-in, system sign-in, setup and invitation pages. The Persian user guide carries its own copy (`docs/user-guide/fa/assets`) and is packaged with it under `web/help/fa/`.

### Profile pictures
1. **Storage:** `identity.UserAvatars` (`UserId` PK/FK, `ContentType`, `Data varbinary(max)`, `Version`, `UpdatedAt`) — a separate table so account queries never load image bytes. The picture belongs to the account (global, like `Users`), not to a workspace.
2. **Upload** (`PUT /auth/me/avatar`, multipart, antiforgery header, any signed-in user): the **content** decides — PNG, JPEG or WebP are recognised by their bytes and their pixel size is read from the header; SVG, GIF and everything else are refused regardless of the declared type or file name. Limits (`Raavi:Avatars`): 512 KB and 16–1024 px (a tiny file can describe a gigantic picture a browser must decode). The stored type is the detected one. The SPA first crops to a centre square, scales to 256 px and encodes WebP (else JPEG) in the browser, so uploads are a few kilobytes.
3. **Who sees it:** only inside a workspace (`GET /users/{id}/avatar`, `GET /users/avatars?ids=` for ≤ 100 ids): the requester must be signed in to that workspace and the target an **active member of the same workspace** (or the requester). Everything else answers 404 (no hint whether the user exists); the batch simply omits such ids. A suspended member's picture is not shown. The system area shows initials only.
4. **Caching:** the version is part of the image address (`…/avatar?v=3`), served `private, max-age=31536000, immutable` with an ETag; a new upload has a new address, so nothing stale and no revalidation traffic. The SPA looks pictures up in one batched request per render pass (`stores/avatars.ts`), so users without a picture cause neither failed image loads nor one request per avatar.
5. **Display:** `UserAvatar.vue` (picture or coloured initials, drawn by CSS so the letters are decoration, not text) next to the name in the header, members, group members, comments, notifications, approvals, task assignees and the page's "last edited by", plus the profile page with change/remove.

## Not covered / limits
Places that show only a name without the user's id in the API (activity feed, history, creators of databases/slides/boards/events, mentions, the Confluence migration report) still show names only. No image decoding or re-encoding on the server (header validation only; the browser decodes). The picture is visible to members of every workspace the user belongs to; there is no per-workspace picture. Tenant export does not include pictures.
