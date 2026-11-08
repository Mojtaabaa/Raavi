# Dependency Inventory and Licensing Risks

Raavi is distributed to customers for on-premises installation, so every bundled component's licence matters. **This is an engineering risk assessment, not legal advice — legal review is required before the first commercial release.**

## 1. Mandated stack

| Component | Licence (as understood at 2026-11) | Risk | Mitigation / decision |
|---|---|---|---|
| ASP.NET Core 10, EF Core, SqlClient | MIT | Low | — |
| SQL Server 2022+ | Commercial (customer-licensed); Developer/Express for dev | Low (customer supplies licence) | Document edition requirements (RLS available in all editions) |
| Vue 3, Vue Router, Pinia, vue-i18n, Vite, Vitest | MIT | Low | — |
| PrimeVue **4.5.5**, @primeuix/themes 2.0.3 (+ @primevue/core, @primevue/icons, @primeuix/styled/styles/utils) | MIT | **High if upgraded** | **PrimeVue 5 is NOT MIT**: from v5 PrimeTek ships PrimeVue under the commercial *PrimeUI License* (compiled package, license key required, red "Invalid PrimeUI License" notice without one; free Community License only for orgs with < 10 employees, < $1M revenue, < 5 devs; removing the license mechanism is prohibited). Phase 0 wrongly recorded v5 as MIT; this was found during Phase 1 browser testing and the project was pinned to **4.5.5 (MIT)**. **Decision (2026-11-06, product owner): stay on PrimeVue 4.5.x (MIT); no PrimeUI commercial license.** Only 4.x patch/minor updates are allowed; upgrading to ≥ 5 requires a new decision and a purchased license. Do not use paid PrimeBlocks/templates. |
| Tiptap 3 (core + open-source extensions incl. Collaboration, CollaborationCaret) | MIT | **Medium** | Only open-source extensions; Pro extensions (comments, AI, DOCX import/export, version history UI) are **not** used — Raavi implements its own |
| Yjs, y-prosemirror, y-indexeddb | MIT | Low | — |
| Hocuspocus server/provider/Redis extension | MIT | Low | — |
| SignalR (server in ASP.NET Core; `@microsoft/signalr` 10.0.11 and `Microsoft.AspNetCore.SignalR.Client` 10.0.14 for the SPA/tests) + Redis backplane (not wired) | MIT | Low | Transitives of the JS client (ws, node-fetch, eventsource, fetch-cookie, abort-controller, tough-cookie) are MIT/BSD |
| MailKit / MimeKit 4.18.2 | MIT | Low | SMTP and .eml pickup for notification e-mail (Phase 7a) |
| Redis | Redis 7.4+: RSALv2/SSPLv1; Redis 8+: adds AGPLv3 option | **High** for redistribution | Raavi only talks the Redis protocol; ship/recommend **Valkey** (BSD-3) in our compose/Helm, support customer-provided Redis. Spec decision "Redis" honoured at protocol level. |
| OpenSearch | Apache-2.0 | Low | — |
| MinIO | AGPLv3 (community); community edition distribution/feature changes in 2025 | **High** | Code depends only on the S3 API (AWSSDK.S3, Apache-2.0). Default deployment uses customer-provided S3-compatible storage; compose ships MinIO **for development only**; evaluate SeaweedFS (Apache-2.0) / Ceph RGW (LGPL) as bundled production default. Decision needed: Q4 in `REPOSITORY_ASSESSMENT.md`. |
| Hangfire (core + SqlServer storage) | LGPL-3.0 (or commercial) | Medium | Used as an unmodified library via NuGet (LGPL-compatible). Hangfire Pro not required. |
| Mermaid | MIT | Low | — |
| Monaco Editor | MIT | Low | — |
| Konva.js / Fabric.js | MIT / MIT | Low | Phase 9 evaluation |
| Docker, Kubernetes, Helm | Apache-2.0 | Low | — |

## 2. Libraries deliberately avoided (licence changes)

| Library | Reason | Use instead |
|---|---|---|
| MediatR ≥ 13, AutoMapper ≥ 15 | Commercial licensing since 2025 | Plain handler interfaces; explicit mapping code |
| MassTransit ≥ 9 | Commercial | Outbox + Hangfire |
| FluentAssertions ≥ 8 | Commercial licence for commercial use | xUnit `Assert` (optionally Shouldly, BSD) |
| Duende IdentityServer | Commercial | Raavi is not an OAuth server for third parties in v1; if needed, OpenIddict (Apache-2.0) |
| Moq ≥ 4.20.0 (SponsorLink episode) | Trust concern | NSubstitute (BSD) or hand-written fakes |

## 3. Planned additional libraries

| Library | Licence | Purpose |
|---|---|---|
| FluentValidation | Apache-2.0 | DTO validation |
| Asp.Versioning.Http | MIT | API versioning |
| OpenTelemetry .NET | Apache-2.0 | Traces/metrics |
| DocumentFormat.OpenXml **3.5.1** | MIT | PPTX export of slide decks (Phase 9d). No PDF library: PDF comes from the browser print dialog (ADR-029) |
| HtmlSanitizer (Ganss.Xss) **11.0.16** (+ AngleSharp, AngleSharp.Css) | MIT | Defence-in-depth HTML sanitizing (Phase 4) |
| Tiptap **3.33.0** packages: vue-3, core, pm, starter-kit, extension-link, -table, -list, -text-align, -text-style, -highlight, -mention, suggestion, extensions | MIT | Phase 4; open-source only |
| Konva **10.9.1** | MIT | Whiteboard canvas (Phase 9e, ADR-030; chosen over Fabric 7.4.0 after measuring) |
| KaTeX **0.19.1** (also forced for Mermaid via npm `overrides`) | MIT | Phase 4 |
| Hocuspocus **4.8.0** (server, provider, extension-redis), Yjs **13.6.33**, y-indexeddb **9.0.12**, y-protocols 1.0.7, @tiptap/y-tiptap 3.2.0, @tiptap/extension-collaboration(-caret) 3.33.0, prosemirror-model 1.25.12, jose **6.2.15**, ws 8.22.1 (dev) | MIT | Phase 6 |
| Mermaid **11.17.2** | MIT | Phase 4. **Not 12.x**: 12.x depends on `elkjs` (EPL-2.0 OR GPL-3.0-or-later). Re-check before upgrading. |
| AWSSDK.S3 | Apache-2.0 | S3-compatible storage |
| OpenSearch.Client / OpenSearch.Net | Apache-2.0 | Search |
| StackExchange.Redis | MIT | Redis/Valkey |
| Novell.Directory.Ldap.NETStandard / System.DirectoryServices.Protocols | MIT | LDAP/AD |
| Microsoft.Extensions.AI | MIT | AI provider abstraction — **not yet used**: 11a calls OpenAI-compatible endpoints through its own `IAiChat` over `HttpClient` (no new package); see ADR-034 |
| KaTeX | MIT | Math rendering |
| vazirmatn font | OFL-1.1 | Persian UI font (self-hosted) |
| Inter font (@fontsource-variable/inter) | OFL-1.1 | Latin UI font (self-hosted) |
| xUnit, Microsoft.AspNetCore.Mvc.Testing, Testcontainers | Apache-2.0 / MIT / MIT | Tests |

## 4. Pinned versions

Backend versions are managed centrally in `backend/Directory.Packages.props` (central package management with **transitive pinning**).

| Package | Version | Licence | Added |
|---|---|---|---|
| Microsoft.AspNetCore.OpenApi / Mvc.Testing | 10.0.14 | MIT | 0 |
| Microsoft.EntityFrameworkCore.SqlServer / .Design | 10.0.14 | MIT | 1 |
| Microsoft.Extensions.Caching.Hybrid | 10.11.0 | MIT | 1 |
| Microsoft.Extensions.Caching.StackExchangeRedis | 10.0.14 | MIT | 1 |
| Hangfire.Core / .SqlServer / .NetCore | 1.8.25 | LGPL-3.0 | 1 |
| DnsClient | 1.8.0 | Apache-2.0 | 1 |
| OpenTelemetry.* (Hosting, AspNetCore, Http, OTLP exporter) | 1.20.x | Apache-2.0 | 1 |
| dotnet-ef (local tool) | 10.0.14 | MIT | 1 |
| Microsoft.Extensions.Identity.Core (PasswordHasher) | 10.0.14 | MIT | 2a |
| Microsoft.IdentityModel.Protocols.OpenIdConnect (+ JsonWebTokens, Tokens) | 8.23.0 | MIT | 2b |
| System.DirectoryServices.Protocols | 10.0.14 | MIT | 2b |
| xunit / runner / Test.Sdk / coverlet | 2.9.3 / 3.1.4 / 17.14.1 / 6.0.4 | Apache-2.0 / MIT | 0 |

**Security pins** (transitive dependencies lifted because the versions pulled in by default have published advisories; `dotnet list package --vulnerable` reports none after pinning):

| Package | Pulled by | Vulnerable version | Pinned |
|---|---|---|---|
| Newtonsoft.Json | Hangfire.Core | 11.0.1 (GHSA-5crp-9r3c-p9vr, high) | 13.0.4 |
| System.Security.Cryptography.Pkcs | SqlClient chain | 9.0.11 (GHSA-2fmv-rv2c-59x7, moderate) | 10.0.14 |

Frontend (exact versions in `package.json`): vue 3.5.45, vue-router 4.5.1, pinia 3.0.4, vue-i18n 11.4.18, **primevue 4.5.5**, **@primeuix/themes 2.0.3**, vazirmatn 33.0.3 (OFL), @fontsource-variable/inter 5.3.0 (OFL), vite 8.3.6, @vitejs/plugin-vue 6.0.10, typescript 5.9.3 (TS 7 not yet supported by vue-tsc), vue-tsc 3.3.14, vitest 5.1.0, jsdom 27.0.0.

Phase 2a additions: qrcode 1.5.4 (MIT), primeicons **7.0.0** (MIT). **primeicons 8 is under the commercial PrimeUI License**, like PrimeVue 5, so it is pinned to 7 per the 2026-11-06 decision.

Phase 2a production-dependency licence scan (`npm ls --omit=dev`): 81 packages, all MIT/ISC/Apache-2.0/BSD/OFL. `npm audit --omit=dev`: 0 vulnerabilities. No PrimeUI-licensed code in node_modules (scanned for the licence notice string).

## 5. Rule for upgrades

Before any major-version upgrade, re-check the licence of the **new** version (not the old one): several libraries in this ecosystem (MediatR, AutoMapper, MassTransit, FluentAssertions, PrimeVue) changed from permissive to commercial licences on a major version boundary.
