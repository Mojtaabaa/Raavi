import { existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { buildSchema, loadManifests } from './schema.ts'
import { createCollaborationServer } from './server.ts'

/**
 * Entry point. Configuration via environment variables (no secrets in files):
 *   RAAVI_API_URL            internal base URL of the Raavi API (e.g. http://api:8080)
 *   RAAVI_COLLAB_SERVICE_KEY shared HMAC key (same value as Raavi:Collaboration:ServiceKey)
 *   PORT                     WebSocket/HTTP port (default 1234)
 *   RAAVI_ALLOWED_ORIGINS    comma-separated browser origins allowed to connect
 *   RAAVI_RECHECK_SECONDS    periodic permission re-check (default 60)
 *   RAAVI_SNAPSHOT_EVERY     snapshot after N stored updates (default 500)
 *   RAAVI_SCHEMA_DIR         editor manifests (default ../schema/editor/extensions)
 *
 * The same variables may be kept in `<sandbox>/secrets/collaboration.env` (KEY=value lines) so they survive an upgrade with the rest of the
 * installation's files (ADR-040). The file is found next to RAAVI_SANDBOX_PATH (its parent folder), or via RAAVI_COLLAB_ENV_FILE, or in the
 * repository's sandbox/secrets in development. Variables already set in the process environment win over the file.
 */
const sandboxConfig = process.env.RAAVI_SANDBOX_PATH
const envFile =
  process.env.RAAVI_COLLAB_ENV_FILE ??
  (sandboxConfig
    ? join(dirname(sandboxConfig), 'secrets', 'collaboration.env')
    : fileURLToPath(new URL('../../sandbox/secrets/collaboration.env', import.meta.url)))
if (existsSync(envFile)) {
  process.loadEnvFile(envFile)
}
const env = process.env
const serviceKey = env.RAAVI_COLLAB_SERVICE_KEY ?? ''
const apiUrl = env.RAAVI_API_URL ?? ''
if (!serviceKey || serviceKey.length < 32 || !apiUrl) {
  console.error('RAAVI_API_URL and RAAVI_COLLAB_SERVICE_KEY (≥ 32 characters) are required.')
  process.exit(1)
}

const schemaDir = env.RAAVI_SCHEMA_DIR ?? fileURLToPath(new URL('../../schema/editor/extensions', import.meta.url))
const collab = createCollaborationServer(
  {
    apiUrl,
    serviceKey,
    port: Number(env.PORT ?? 1234),
    recheckSeconds: Number(env.RAAVI_RECHECK_SECONDS ?? 60),
    snapshotEvery: Number(env.RAAVI_SNAPSHOT_EVERY ?? 500),
    debounce: 2000,
    maxDebounce: 10000,
    allowedOrigins: (env.RAAVI_ALLOWED_ORIGINS ?? '').split(',').map((o) => o.trim()).filter(Boolean),
  },
  buildSchema(loadManifests(schemaDir)),
)
await collab.listen()

for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.on(signal, () => {
    // Persist pending edits before exiting.
    void collab.destroy().finally(() => process.exit(0))
  })
}
