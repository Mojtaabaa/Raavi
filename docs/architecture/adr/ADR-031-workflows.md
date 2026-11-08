# ADR-031 — Workflows (Phase 10a)

**Status:** Accepted (2026-11-08)

## Context

Phase 10 ("Workflows & automation") asks for a designer and engine, approvals, triggers/conditions/actions, scheduling, logs, retries, idempotency and rate limits. It is delivered in slices. **10a** is the engine and the rule model, built on infrastructure that already exists: the transactional outbox (ADR-017: at-least-once, tenant-scoped, retry with back-off up to 10 attempts), notifications and labels. Approvals (10b), scheduling/time triggers and a visual designer (10c) follow.

## Decisions

1. **A workflow is a rule of one space:** *when* a page event happens, *if* all conditions hold, *then* run 1–5 actions. Stored in `content.Workflows` (name, enabled, trigger, conditions and actions as validated JSON, revision/ETag like every other editable object); tenant-owned, RLS, composite FK to the space.
2. **Triggers are page events only:** `content.created`, `.updated`, `.moved`, `.archived`, `.trashed`, `.published`, `.publish_requested`. Note that edits of versioned pages (the default page type) emit `content.published`; `content.updated` is for non-versioned content types.
3. **Conditions (AND, ≤ 10):** title `contains` / `equals` / `startsWith` (case-insensitive), content type `equals`, label `has`. They are evaluated against the **current** page when the run happens, not against a snapshot.
4. **Actions (≤ 5), deliberately small and safe:**
   - `AddLabel` — adds a label (idempotent, respects the 100-label cap).
   - `Notify` — an in-app notification to the page's *watchers* or to *whoever triggered the event*, with a fixed message (≤ 200 characters). Every recipient's View right on the page is re-evaluated at that moment (same rule as comment notifications), so a rule cannot leak the existence of a page. Notification ids are derived from (workflow, event, message): a retry never notifies twice. No e-mail in 10a.
   - Not included on purpose: anything that edits content, changes permissions, calls external systems (webhooks/HTTP belong to Phase 11 with an egress policy), or emits page events. **Because no action emits a page event, rules cannot trigger each other or themselves.**
5. **Who may define rules:** only administrators of the space (`CanAdminister`). Everyone else gets 403 on a visible space and 404 on an invisible one. Actions run as the system on behalf of the rule's author for label attribution only; they grant nothing the author could not do (AddLabel/Notify need no privilege beyond space administration).
6. **Execution model.** `WorkflowEngine` is an outbox handler: for each event it loads the enabled workflows of the event's space with that trigger (≤ 50) and runs them. Each (workflow, event) pair produces exactly one `WorkflowRun` row (unique index) — this is the idempotency key *and* the history shown to administrators:
   - `Succeeded` / `Skipped` (conditions not met, page gone, rate limit) are final;
   - an unexpected error stores a `Failed` run (attempt counter, message) and rethrows, so the outbox retries it with its back-off; the next attempt re-uses the row; after 10 attempts the outbox row becomes `Failed` (visible to operators). Other workflows of the same event still run;
   - **rate limit:** at most 100 executed runs per workflow per hour; further events are recorded as skipped (`rate_limited`) instead of being queued.
7. **Run history** is kept per workflow (latest 100 are listable); deleting a workflow deletes its runs. A retention job for old runs is not part of 10a.

## Deferred (later slices)

10b approvals (multi-step, assignees, SLA reminders), 10c visual designer and time-based triggers/scheduling, more actions (set properties, move, comment, e-mail, webhooks), explicit-user recipients (needs a user picker), cross-space and tenant-wide rules, dry-run/test button, run retention, failure alerting to administrators.
