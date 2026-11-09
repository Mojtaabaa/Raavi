# ADR-044 — Bootstrap system administrator with forced first sign-in (Phase 13f)

**Status:** Accepted (2026-11-09)

## Context
The first system administrator needed an operator API key hash in the settings, an API call to create an invitation and a setup link. The operator asked for a simple "default admin with a temporary password that is changed at the first sign-in". Shipping a fixed default credential is not acceptable (CWE-798: every installation would share it and anyone could find it), and system administrators must keep mandatory TOTP.

## Decisions
1. **No credential is shipped.** `Raavi:Authentication:Bootstrap:SystemAdministrator` (`Email`, `InitialPassword`, `DisplayName`) is empty in the shipped defaults; the installation puts its own values in `sandbox/config/authentication.json`.
2. **Created only when needed:** `BootstrapAdministratorSeeder` (API host only, hosted service) creates the account at start **only if no system administrator exists**, with `Users.MustChangePassword = true` and the SystemAdministrator role. Failures are logged, never fatal; a warning is logged while the account waits for its first sign-in. While the account has not completed its first sign-in, a restart re-applies the configured temporary password (recovery from a typo/lost password); after that the seeder never touches the account again.
3. **The temporary password never opens a session.** `POST /auth/system/login` with it answers `401 auth.first_login_required` (checked before the TOTP code, not counted as a failed attempt). `POST /auth/system/first-login` verifies it under the normal lockout rules and rate limit, and returns a one-hour setup link of the existing enrolment page. The setup completion for such an account additionally requires `newPassword` (password policy applies, must differ from the temporary one) and the TOTP code of the new authenticator; it sets the new hash, which clears `MustChangePassword`, enrols TOTP and issues the first session.
4. The temporary password may be shorter than the policy minimum (it must be replaced at once); the replacement may not.
5. The SPA's system sign-in page follows `auth.first_login_required` automatically to the setup page, which then asks for the temporary password, the new password and its repetition, and the authenticator code.

## Risks and limits
* **Whoever knows the temporary password first completes the setup** and owns the installation (they enrol their own authenticator). The window lasts from the first start until the first sign-in. Choose a hard-to-guess temporary password, sign in immediately, and remove the settings afterwards (the guide says so; the log warns while pending). There is no automatic expiry in this version.
* The user name is an e-mail-shaped address (the sign-in identifier is the e-mail); a short name like "admin" is not supported.
* Operator API keys and invitations still work as the alternative bootstrap.
* Verified by API tests against a real database and SPA tests with fakes; **not** run on a real installation.
