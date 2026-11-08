# sandbox — everything an installation owns

This folder is the **only** place that holds files which belong to *your installation* rather than to the Raavi version. When you install a new version you replace the application files and **keep this folder** — nothing else needs to be carried over.

| Folder | What | In git | Who writes it | Keep on upgrade |
|---|---|---|---|---|
| `config/` | Global (all-tenant) settings: `appsettings.json`, `<module>.json`, `<module>.<Environment>.json`, `*.Local.json` | yes (`*.Local.json`, `*.Production.json` are ignored) | you | **yes** |
| `data/` | State the application writes: `keys/` (Data Protection key ring — **without it every TOTP secret, AI key and webhook secret is unreadable**), `dev-mail/` (mail pickup, development) | no | Raavi | **yes** |
| `secrets/` | Keys and secret settings: `collaboration.env`, collaboration signing key (`.pem`), dev account notes | no | you | **yes** |
| `backups/` | Copies of `config/` files made by `sandbox sync` before it changed them | no | `sandbox sync` | optional |

Tenant settings are **not** here — they live in SQL Server (back them up with the database).

## Rules

* Relative paths in settings are resolved against this folder (`KeyRingPath` → `data/`, `PickupDirectory` → `data/`, `SigningKeyPath` → `secrets/`), never against the program folder. An empty `KeyRingPath` means `data/keys`.
* Put secrets in `secrets/` or in `RAAVI_…` environment variables, not in `config/` (see `DEVELOPMENT_GUIDE.md`).
* Do not edit the application's own folders for configuration; they are replaced on every upgrade.

## Upgrading when the new version has new settings or files

The new version ships its complete default settings in `sandbox-defaults/config/` (that folder **is** replaced on upgrade — it belongs to the version). Two things protect you:

1. **Safety net:** at start-up the shipped scalar defaults are the lowest configuration layer, so a setting that your files do not mention yet still has its default, and the API/Workers log a warning listing what is missing.
2. **Sync:** run, from the new version's folder, before restarting

   ```bash
   dotnet Raavi.Migrator.dll sandbox check   # exit code 1 and a list when something is missing
   dotnet Raavi.Migrator.dll sandbox sync    # adds missing files/keys; originals go to backups/<time>/
   ```

   `sync` only **adds**: it never changes or deletes a value you set, never merges lists, ignores `*.<Environment>.json` and `*.Local.json`, and reports (does not remove) keys that no longer exist in the new version.

Developers: when you add a setting, add it to `sandbox/config/<file>.json` **and** `sandbox-defaults/config/<file>.json` (a test fails if the two differ), and a new module file also to `SandboxConfiguration.ModuleFiles`.
