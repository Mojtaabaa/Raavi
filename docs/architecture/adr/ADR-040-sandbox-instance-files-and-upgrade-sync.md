# ADR-040 — Sandbox as the single home of instance files, and upgrade sync

**Status:** Accepted (2026-11-08)

## Context
Only `sandbox/config` was in the sandbox. Other things an installation must keep lived elsewhere: the Data Protection key ring (default: the user profile of the service account), the mail pickup folder (relative to the process working directory), the collaboration signing key and the collaboration server's environment. A new release replaced the application folder, so it was unclear what to keep; and a setting added by a newer version simply did not exist in an older `sandbox/config`.

## Decisions
1. **Layout:** `sandbox/config` (settings), `sandbox/data` (state written by the application), `sandbox/secrets` (keys, `collaboration.env`), `sandbox/backups` (originals changed by sync). `data/`, `secrets/`, `backups/` are git-ignored. `sandbox/README.md` is the keep-on-upgrade list.
2. **Path resolution:** `SandboxConfiguration` derives the sandbox root (parent of `config`) and resolves relative `KeyRingPath`, `Email:PickupDirectory` (→ `data`) and `Collaboration:SigningKeyPath` (→ `secrets`) against it; empty `KeyRingPath` defaults to `data/keys`. Absolute paths are untouched (mounted secrets still work). The collaboration server loads `<sandbox>/secrets/collaboration.env` (or `RAAVI_COLLAB_ENV_FILE`) with `process.loadEnvFile`; real environment variables win.
3. **Shipped defaults:** `sandbox-defaults/config/*.json` is the complete settings set of the version and is replaced on upgrade.
4. **Two mechanisms for new settings:** (a) a lowest configuration layer built from the shipped **scalars** (arrays are excluded because configuration merges lists index-by-index and a default list would leak items into a shorter list the administrator wrote), plus a start-up warning from API and Workers naming what is missing; (b) `Raavi.Migrator sandbox check|sync`: additive merge of missing files/keys into the installation's files, never altering existing values, never merging arrays, ignoring environment/Local variants and `_` keys, writing the originals to `sandbox/backups/<time>/`, refusing to rewrite an invalid JSON file, idempotent.
5. **Drift guard in CI:** a test requires `sandbox/config` and `sandbox-defaults/config` of the repository to declare the same keys, so adding a setting on one side only fails the build.

## Consequences / limits
* The default of `KeyRingPath` changed (profile folder → `sandbox/data/keys`). Existing installations that relied on the old default must copy their key files into `data/keys` **before** starting the new version, otherwise protected values (TOTP, AI keys, webhook secrets, Confluence tokens) become unreadable. The development key ring was copied.
* Renamed/removed keys are only reported; moving a value from an old key to a new one needs a release note (no automatic key migration).
* Sync reformats the JSON files it changes (indentation); comments in them are lost. `_comment` keys are preserved as ordinary keys.
* Environment-specific variants (`*.Production.json`) are never created or compared.
* Packaging scripts for releases do not exist yet; the release must exclude `sandbox/` (documented).
