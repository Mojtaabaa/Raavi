# ADR-015: Plugin Architecture

**Status:** Accepted · **Date:** 2026-11-06

## Context
Extension points: editor extensions, macros, renderers, widgets, search providers, automation triggers/actions, authentication providers, integrations, export providers, content providers. Untrusted code must not run in the privileged process.

## Decision
- Every plugin ships a **manifest**: id, name, version, compatibility range (Raavi API version), dependencies, requested permissions (scopes), configuration JSON Schema, capabilities (extension points).
- Two trust tiers:
  1. **Trusted (first-party / signed by the operator)** — .NET assemblies loaded in an isolated `AssemblyLoadContext` at startup; frontend bundles loaded as ES modules from the API. Allowed for authentication providers, content providers, server renderers.
  2. **Untrusted (third-party)** — run **out of process**: server-side logic as a separate service (container) talking to Raavi through the public REST API + webhooks with a scoped API token per tenant installation; UI contributions rendered in sandboxed iframes (`sandbox="allow-scripts"`, separate origin, `postMessage` bridge with a narrow, permission-checked API).
- Tenant admins install/enable plugins from an operator-approved catalogue; system admins approve plugins globally.
- Plugins never receive raw database access, secrets of other plugins, or other tenants' data.

## Alternatives considered
- In-process loading of arbitrary plugins — rejected (security).
- WebAssembly sandbox for server plugins — promising; reconsider when WASI component tooling for .NET is mature.

## Consequences
Out-of-process plugins require stable public APIs and webhooks (ADR-021), which are needed anyway for integrations.
