# ADR-032 — Page approvals (Phase 10b)

**Status:** Accepted (2026-11-08)

## Context

ADR-019 gave spaces a *publish* approval: a space administrator approves a snapshot and the page is published. Phase 10 asks for approvals in general: named people confirm that a page is fine. 10b adds that as its own object, independent of publishing, and wires it to notifications and a personal inbox. (Multi-step chains, escalation and use as a workflow action come later.)

## Decisions

1. **An approval request belongs to one page.** `content.ApprovalRequests` (note ≤ 500, mode, status, the page version it was asked about, requester, optional due date, revision) plus `content.ApprovalAssignments` (one row per approver: decision, comment, time; key = request + user). Tenant-owned, RLS, composite FKs (request → space, assignment → request). No FK to the page node, so a purged page does not block anything; reads join through the page and an orphan simply never appears.
2. **Outcome rules.** Mode `All`: every approver must approve; mode `Any`: one approval is enough. **One rejection always ends the request** (rejected), even in mode `Any`. A rejection requires a comment. Decided requests are final; a new request can follow. Only one pending request per page (409 `approval.already_pending`).
3. **Who may do what.** Request: a user who can *edit* the page (read-only users get 403), on a non-archived space. Approvers: 1–10 distinct existing users other than the requester, each of whom must be able to read the page *now* (otherwise `approval.approvers_invalid`). Decide: only an assigned approver, who must still be able to read the page (otherwise 404). Cancel: the requester or a space administrator. Everyone who cannot read the page gets 404 for the request, its history and its inbox entry.
4. **Concurrency.** The request carries a revision that is a concurrency token and is bumped by every decision, so two approvers deciding at the same moment cannot both compute "not final yet" (one gets 412 and retries). No ETag is exposed: clients just reload.
5. **The page may change after the request.** The request records the version number asked about; the API reports `pageChanged` when the page has a newer version. We deliberately do not auto-invalidate: the approvers see the flag and decide.
6. **Notifications** (outbox handler `ApprovalNotifier`, idempotent per event and recipient, View right re-checked per recipient): `approval.requested` → each approver; final outcome → the requester (`approval.approved` / `approval.rejected`); cancel → approvers who have not decided yet. The actor is never notified. In-app only.
7. **Inbox.** `GET /approvals?scope=inbox` (waiting for my decision, due date first) and `scope=requested`; pages the caller can no longer read are dropped.
8. **Due dates** must lie in the future (≤ 1 year) and are shown as *overdue* once passed. **No reminder or escalation job exists yet** — an overdue request only looks overdue.

## Deferred

Multi-step / sequential chains, group approvers, delegation, reminders and escalation (need the scheduling slice 10c), approval as a workflow action or trigger (the events `approval.requested` / `approval.decided` are already in the outbox), e-mail notifications, requiring an approval before publishing, retention of old requests.
