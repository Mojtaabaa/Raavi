# Raavi Collaboration Server

Hocuspocus 4 + Yjs server for real-time editing of page drafts (Phase 6). Design: `docs/architecture/COLLABORATION_ARCHITECTURE.md`, ADR-011.

- **Authentication**: single-use ES256 tickets issued by the API (`POST /api/v1/collaboration/tickets`, ≤ 60 s, `aud=raavi-collaboration`, bound to user, session, tenant and document). The public key is fetched from the API's signed internal JWKS endpoint.
- **Persistence**: no database access. Load/store through `/internal/collaboration/*` on the API with HMAC-signed requests (`src/signature.ts` = `ServiceSignature` in .NET). Every store also sends the document converted to Document JSON; the API validates it against the editor registry and writes the shared draft.
- **Schema**: built from the shared manifests (`schema/editor/extensions`), conversion with `@tiptap/y-tiptap` (same library as the editor).
- **Revocation**: periodic re-check of every connection (`RAAVI_RECHECK_SECONDS`, default 60) plus immediate re-checks requested by the API (`POST /internal/recheck`); revoked connections are closed (`raavi:revoked`), downgraded ones become read-only.
- **API → server**: `POST /internal/flush` (before publishing), `/internal/reset` (draft discarded/restored), `/internal/recheck` — all HMAC-signed.

## Run

```
RAAVI_API_URL=http://localhost:5180 RAAVI_COLLAB_SERVICE_KEY=<same as RAAVI_Raavi__Collaboration__ServiceKey, ≥ 32 chars> node src/main.ts
```

Node.js ≥ 22.18 (TypeScript is executed with Node's type stripping). Other settings: `PORT` (1234), `RAAVI_ALLOWED_ORIGINS`, `RAAVI_SNAPSHOT_EVERY` (500), `RAAVI_SCHEMA_DIR`.

Multi-node deployments additionally need the Hocuspocus Redis extension (`@hocuspocus/extension-redis` is installed but **not yet wired or tested** — single node only in Phase 6).

## Test

```
npm test            # unit (schema round trip of all fixtures, tickets, signatures) + integration (real WebSocket clients against a mock API)
npm run load -- 100 20
```
