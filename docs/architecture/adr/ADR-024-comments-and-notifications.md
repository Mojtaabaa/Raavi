# ADR-024 — Comments, Watching and Notifications

**Status:** Accepted (Phase 7a, 2026-11-07)

## Context

Phase 7 needs discussion on pages (page and inline), reactions, mentions, watching, in-app notifications with push, and e-mail. Earlier documents sketched inline comments as Yjs relative positions inside the document.

## Decisions

1. **Inline anchors are text-quote selectors, not document marks.** A thread stores `{exact, prefix, suffix}` over the page's plain text (`Comment.AnchorJson`); the read view resolves it with ProseMirror *decorations* (`commentAnchors.ts`). Reasons: commenting must not change page content (no Edit right, no new version, works on any published version), and it works unchanged with live editing. Cost: anchors follow text by matching, not by position — if the quoted text disappears the thread is shown as "text changed" (orphaned) but is kept. This **supersedes** the "Yjs relative positions" sketch in COLLABORATION_ARCHITECTURE.md.
2. **Threads are flat.** The first comment is the root (`ThreadId` = own id); replies attach to the root. Status (Open/Resolved), kind and anchor live on the root. Delete is soft (body cleared) so replies keep their thread. Bodies are Raavi documents (schema-validated, ≤ 32 KB), so mentions and links reuse the editor.
3. **Permissions.** Reading a comment requires View on the page (otherwise 404 for page and comment alike). Writing, replying, reacting need the space's `Comment` right on a *current* page. Authors edit (If-Match, ETag `"r{n}"`) and delete their own; space administrators delete any (`CanModerateComments`). Resolve/reopen: thread author, page editors, administrators. `ContentRights` gained `CanComment`, `CanModerateComments`.
4. **Reactions** come from a fixed allow-list (👍 ❤️ 🎉 😄 👀 ✅), one row per (comment, user, emoji); toggling is idempotent.
5. **Watching.** `Watches` (page, user, Watching|Muted). Authors and commenters auto-watch unless they explicitly muted. Watchers are notified of comments and publications; a Muted row also suppresses participant/watcher notifications, but never direct mentions.
6. **Delivery pipeline.** State changes write an outbox event in the same transaction (`comment.created`, `comment.resolved`, plus the existing `content.published/created`). `NotificationFanOut` (outbox handler, tenant scope, idempotent via the unique `(EventId, RecipientId)`) computes recipients = mentions ∪ thread participants ∪ watchers − actor, then **re-evaluates each recipient's View right with fresh membership** (`WikiAuthorizer` evaluates an explicit principal) before creating a `Notification`. The inbox additionally hides notifications about content the user can no longer view. E-mail is a separate outbox step (`notification.email`, `NotificationEmailer`), so a failing relay never blocks in-app delivery; progress is saved per message.
7. **E-mail.** `IEmailSender` (Application) with a MailKit implementation: `Smtp`, `Pickup` (writes .eml; development) or `Disabled`, configured in `sandbox/config/notifications.json`; the SMTP password only from environment/secret store. Sent only to **verified** addresses (`IPrincipalDirectory.GetContactsAsync`) of active members who did not switch e-mail off (`NotificationPreferences`, per tenant), in the user's locale (fa/en); every user value is HTML-encoded; `Auto-Submitted: auto-generated`.
8. **Push.** SignalR hub `/hubs/events`, **WebSockets only**, tenant host only, authenticated members only, Origin must equal the request host (cookies are the credential). The server sends a content-free `notifications` signal; the client then fetches via REST, which applies the permission checks. `NotificationPushService` (API host) compares per-user unread count/newest time every 2 s for tenants with open connections and signals changes — it works regardless of which host created the notification and needs no backplane (each API node serves its own connections). Connections are re-validated every 60 s (membership, tenant status, sign-in session) and aborted otherwise. The client falls back to a 60 s poll while the channel is down.

## Consequences

- Fan-out and e-mail run in the Workers host (outbox poller); push latency ≈ outbox poll (2 s) + push interval (2 s).
- Many API nodes need no Redis backplane for this feature; a future per-event push can add one.
- Orphaned inline threads need a manual "show resolved/orphaned" review; automatic re-anchoring across versions is deferred.
- Not in 7a: S3 attachments/quotas/scanning/previews/branding assets (7b) and OpenSearch (7c).
