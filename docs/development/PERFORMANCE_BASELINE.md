# Performance baseline (Phase 12c)

> **What this is:** a repeatable measurement of the API under concurrent signed-in users, and a check that hot endpoints do not issue one SQL query per item. **What it is not:** a capacity statement. The numbers come from one development machine with the load generator and the server in the *same process* sharing four old CPU cores (Intel i5-3470, 24 GB), SQL Server LocalDB 2016, no reverse proxy, no Redis, no search engine, no network. A production installation will differ — in both directions.

## How to run

```bash
cd backend
RAAVI_LOAD=1 RAAVI_LOAD_PAGES=1000 RAAVI_LOAD_USERS=40 RAAVI_LOAD_SECONDS=20 RAAVI_LOAD_REPORT=load.json \
  dotnet test Raavi.Tests --filter "Category=Load" --logger "console;verbosity=detailed"
```

Without `RAAVI_LOAD=1` the tests do nothing (they are in the default run only as no-ops). Knobs: `RAAVI_LOAD_PAGES` (pages in one space, three-level tree), `RAAVI_LOAD_USERS` (members, one virtual user each), `RAAVI_LOAD_SECONDS`, `RAAVI_LOAD_WARMUP` (default 8 s, excluded). The workload is closed-loop (no think time) and weighted like browsing with some commenting: page 25 %, tree children 20 %, space tree 10 %, quick-find 15 %, comments 8 %, unread-count 8 %, spaces 6 %, new comment 8 %. `QueryCountTests` counts SQL statements per request from SQL Server's plan-cache statistics.

## Results (2026-11-08)

| Run | Pages | Users | Requests | Throughput | 5xx |
|---|---|---|---|---|---|
| A | 1 000 | 40 | 3 062 in 20 s | 152 req/s | 0 |
| B | 4 000 | 80 | 4 977 in 25 s | 198 req/s | 0 |

Latency in milliseconds, after warm-up (run A | run B). Closed-loop users never pause, so latency here is mostly queueing behind the other users; read it as "what a request waits when the server is saturated", not as the cost of a request on an idle server.

| Endpoint | p50 | p95 | p99 | max |
|---|---|---|---|---|
| GET page | 352 \| 506 | 536 \| 755 | 750 \| 1 025 | 832 \| 1 091 |
| GET tree children | 287 \| 438 | 457 \| 640 | 623 \| 950 | 770 \| 998 |
| GET space tree (roots) | 264 \| 497 | 413 \| 699 | 615 \| 942 | 626 \| 1 034 |
| GET quick-find | 197 \| 317 | 330 \| 453 | 387 \| 821 | 558 \| 878 |
| GET comments | 237 \| 327 | 363 \| 487 | 423 \| 800 | 503 \| 859 |
| POST comment | 281 \| 429 | 442 \| 643 | 639 \| 934 | 721 \| 982 |
| GET spaces | 81 \| 125 | 145 \| 205 | 267 \| 423 | 498 \| 447 |
| GET unread-count | 30 \| 46 | 84 \| 99 | 174 \| 142 | 261 \| 352 |

SQL statements per request (idle server, three calls averaged): page with 6 ancestors and 12 comments 13, children of a page with 31 children 12, space roots 7, comments 10, quick-find 7, spaces 3, unread-count 1. **Nothing grows with the number of items** (a 31-child listing costs the same as a 5-child one), so there is no N+1 pattern in these paths.

## What was learned

* The first run without warm-up showed single stalls of about 5.7 s on every endpoint at once. That was start-up cost (JIT, EF model building, connection pool) hitting 40 cold callers simultaneously, not steady-state behaviour — hence the warm-up. A production start-up under load will show the same effect: roll instances behind a readiness check and warm them (a request to a few endpoints) before sending traffic.
* Four times the data (1 000 → 4 000 pages) and twice the users raised throughput (152 → 198 req/s) without errors and with p99 under 1.1 s: the paths measured are index-driven (`TenantId`-leading indexes, closure table) and do not degrade visibly at this size.
* The heaviest reads are the page and tree reads (13 and 12 statements); they are the first candidates if more throughput is needed (response caching of the tree per tenant and permission fingerprint, merging statements). No optimisation was made in this step because nothing was found to be wrong.

## Targets of the test guide that are NOT met by this evidence

`TESTING_GUIDE.md` names 500 concurrent users and 100 000 pages / 1 000 000 attachment records. This baseline reached 80 users and 4 000 pages, on one machine, without attachments, search engine (OpenSearch is deferred), WebSocket traffic or the background workers. Reaching and proving the real targets needs a dedicated load generator (k6 scripts under `deployment/scripts/perf`, not written), a representative server and a seeded data set. The collaboration server has its own earlier load test (100 editors on one document against a mock API).
