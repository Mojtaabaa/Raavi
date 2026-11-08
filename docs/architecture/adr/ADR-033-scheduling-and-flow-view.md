# ADR-033 — Scheduling: reminders, time-based trigger, flow view (Phase 10c)

**Status:** Accepted (2026-11-08)

## Context

10a gave rules that react to page events; 10b gave approvals with a display-only due date. 10c adds the time dimension on the platform that exists (Hangfire with tenant envelopes, the outbox) and a picture of a rule. It does **not** deliver a drag-and-drop designer — see "Not in 10c".

## Decisions

1. **Approval reminders are per-request tenant jobs** (the same mechanism as scheduled publishing, `ITenantJobScheduler.Schedule`). A request with a due date schedules a *reminder* one day before (only if that moment is still ahead) and an *overdue* notice at the due date. Jobs run without a user and re-check the world: they do nothing unless the request is still pending **and** its due date equals the one in the job (so a changed or decided request makes old jobs harmless). They emit outbox events (`approval.reminder`, `approval.overdue`) handled by the existing notifier: reminder → approvers who have not decided; overdue → those approvers **and** the requester. Recipients' View right is re-checked as for every notification. No escalation to space administrators yet.
2. **Time-based workflow trigger `content.stale`** ("a page has not been updated for N days", N = 1–3650 stored as `Workflow.StaleDays`; refused for other triggers). It is not an outbox event: a **daily system job** (`TenantFanOut<StaleSweepJob, StaleSweepArgs>`, 03:00, queue `maintenance`) lists active tenants and enqueues the per-tenant `StaleSweepJob` through the new `ITenantJobScheduler.EnqueueForTenant` (system code only; it still goes through `TenantJobRunner`, so the tenant status check and tenant scope apply).
3. **Sweep rules** (`StaleSweep`): per enabled stale workflow, oldest current pages first, at most 100 per workflow and sweep, never beyond the workflow's hourly run budget (so the rate limit of ADR-031 holds and nothing is "skipped forever"). A page is handled **once per update**: a run recorded after the page's last update means "done"; editing the page re-arms it. The run log's unique (workflow, event) key — event id derived from (workflow, page, last-update time) — keeps even a concurrent double sweep idempotent. Conditions and actions are the normal ones; the actor of a sweep run is "the system", so `Notify → whoever triggered it` reaches nobody (use watchers).
4. **Failures** of one page do not stop the sweep; the run row says `Failed` with the error and administrators see it in the history. A failed stale run is not retried by the sweep (it counts as handled for that update) — unlike outbox-driven runs, which the outbox retries.
5. **Flow view.** The workflows page draws any rule as trigger → conditions → actions (`WorkflowFlow`), both in the list (toggle) and as a live preview inside the editor, in plain HTML so it follows the reading direction and is an ordered list for screen readers. Editing stays in the form.

## Not in 10c

A drag-and-drop canvas designer (no requirement we could pin down justified a second editor next to the form; the flow view is read-only), branching/parallel steps, per-workflow schedules (cron), reminders for tasks/calendar items, escalation chains, a configurable reminder lead time, a retention job for old runs, stale-page sweeps for non-current content (archived/trashed).
