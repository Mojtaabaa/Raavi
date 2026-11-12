# Wiki API (Phases 3–6)

Paths under `/api/v1`, **tenant host only**, signed-in members (`AuthenticatedUser`). Every unsafe request needs `X-XSRF-TOKEN` (cookie sessions). Anything the caller cannot see — other tenants, private spaces, restricted or trashed content — is `404`; `403 permission.denied` only when the resource is visible but the action is not allowed.

**Concurrency:** spaces, content and templates return `ETag: "r{revision}"`. Updates marked *If-Match* require that header: missing → `428 precondition.required`, stale → `412 precondition.failed`.

**Body format:** `{"schema":"raavi.doc","schemaVersion":2,"doc":{…Tiptap JSON…}}` validated against the extension registry (`schema/editor/extensions`); anything outside it → `400 content.body_invalid`. Max 5 MB, depth 48. Version 1 bodies are accepted and upgraded. The stored and returned body is a canonical re-serialization with attribute defaults applied.

## Spaces

| Method | Path | Notes |
|---|---|---|
| GET | `/spaces?includeArchived=&scope=administration` | Viewable spaces; `scope=administration` lists all spaces for tenant administrators (recovery) |
| GET | `/spaces-capabilities` | `{canCreateSpaces}` |
| POST | `/spaces` | `{key, name, description, type: Team|Project|Documentation|Personal|KnowledgeBase, visibility: Open|Private}` → `201`; creates the home page and default grants. `409 space.key_taken`, `400 space.key_invalid`, `403` without the tenant Create grant |
| GET | `/spaces/{key}` | Space + `rights {canView, canCreate, canAdminister, canManageTemplates}`; visible to viewers and administrators |
| PUT/PATCH | `/spaces/{key}` *If-Match* | `{name, description, type, homePageId}` (administrators) |
| POST | `/spaces/{key}/archive`, `/restore` *If-Match* | Archived spaces are read-only (`409 space.archived`) |
| GET/PUT | `/spaces/{key}/permissions` | `{grants:[{subjectType: User|Group|AllMembers, subjectId, action, effect: Allow|Deny}]}` full replacement; actions View, Create, Edit, Move, Archive, Delete, Comment, ManageTemplates, ManagePermissions; subjects must belong to the tenant (`400 permission.subject_not_found`); at least one user/group administrator (`409 space.last_administrator`) |
| GET | `/spaces/{key}/tree?parentId=` | Visible current children `[{id, parentId, contentType, title, slug, position, hasChildren}]` |
| GET | `/spaces/{key}/recent`, `/spaces/{key}/activity` | |
| POST | `/spaces/{key}/content` | `{title, parentId?, contentType: page|folder, body?, templateId?}` → `201` |
| GET/POST | `/spaces/{key}/templates` | Space + global templates / create a space template (ManageTemplates) |

## Content

| Method | Path | Notes |
|---|---|---|
| GET | `/content/{id}` | Page with body, version, labels, breadcrumbs, `isRestricted`, `rights` |
| PUT | `/content/{id}` *If-Match* | `{title, body, changeSummary}`; a new version per change, identical saves are no-ops |
| POST | `/content/{id}/move` *If-Match* | `{spaceKey?, parentId?, position?}`; subtree follows; `400 content.move_into_descendant`; the home page cannot leave its space |
| POST | `/content/{id}/archive`, `/unarchive` *If-Match* | Applies to the subtree |
| DELETE | `/content/{id}` *If-Match* | Soft delete (trash) of the subtree; restore arrives in Phase 5 |
| GET | `/content/{id}/versions` | Version list (diff/restore: Phase 5) |
| GET/PUT | `/content/{id}/restrictions` | `{view:[{subjectType, subjectId}], edit:[…]}`; View is inherited by descendants, Edit applies to the node; editors must stay included (`400 restriction.excludes_self`); space administrators may manage restrictions of pages they cannot read (recovery) |
| POST | `/content/{id}/labels` | `{name}` → labels; `DELETE /content/{id}/labels/{name}` |

## Comments, watching, notifications (Phase 7a, ADR-024)

| Method | Path | Notes |
|---|---|---|
| GET | `/content/{id}/comments?includeResolved=` | `{threads, canComment, watching, reactions}`; View on the page. Thread: `{id, kind: Page\|Inline, anchor{exact,prefix,suffix}, status, resolvedByName, canResolve, canReply, comments[]}` |
| POST | `/content/{id}/comments` | `{body (Raavi doc), kind, anchor}` → thread; `Comment` right on a current page; inline needs an anchor |
| POST | `/comments/{id}/replies` | `{body}` → comment (replies attach to the thread root) |
| PUT | `/comments/{id}` | `{body}`, **If-Match** `"r{n}"`; author only |
| DELETE | `/comments/{id}` | soft delete; author or space administrator |
| POST | `/comments/{id}/resolve` · `/reopen` | thread author, page editors, administrators |
| POST | `/comments/{id}/reactions` | `{emoji, on}` (allow-list) → reaction counts |
| GET/PUT/DELETE | `/content/{id}/watch` | `{watching}` |
| GET | `/notifications?unreadOnly=&before=` | `{items, unreadCount}` (own inbox; items about content the caller can no longer view are omitted) |
| GET | `/notifications/unread-count` | `{count}` (capped at 99) |
| POST | `/notifications/{id}/read` · `/notifications/read-all` | 204 |
| GET/PUT | `/notifications/preferences` | `{emailEnabled, emailAvailable}` |

Page DTO `rights` gained `canComment`, `canModerateComments`. Push: WebSocket `/hubs/events` (SignalR, tenant host, same-origin only); the server sends the content-free message `notifications`, clients then call the REST endpoints above.

## Tasks (Phase 9a, ADR-026)

| Method | Path | Notes |
|---|---|---|
| GET | `/spaces/{key}/tasks?status=&assignee=` | View on the space; open first, by due date, undated last (max 500) |
| POST | `/spaces/{key}/tasks` | `{title, notes?, assigneeId?, dueDate? (yyyy-MM-dd), contentId?}`; Edit on an active space. `task.assignee_invalid`, `task.assignee_no_access`, `task.content_invalid`, `task.title_invalid` |
| GET | `/spaces/{key}/tasks/export` | CSV (UTF-8 BOM, formula-safe) |
| GET | `/tasks/mine?status=` | tasks assigned to the caller in spaces they can still view |
| GET/PUT | `/tasks/{id}` | PUT needs **If-Match** `"r{n}"` (Edit) |
| POST | `/tasks/{id}/complete` · `/reopen` | Edit, or the assignee |
| DELETE | `/tasks/{id}` | Edit |

Task DTO: `{id, spaceKey, spaceName, contentId, title, notes, status: Open|Done, assigneeId, assigneeName, dueDate, isOverdue, completedAt, createdAt, createdByName, revision, rights{canEdit, canComplete, canDelete}}`.

## Workspace export (Phase 12a, ADR-037)

| Method | Path | Notes |
|---|---|---|
| GET | `/tenant/export` | **Tenant administrator.** Streams a ZIP (`application/zip`, `no-store`, file name `raavi-export-{slug}-{yyyyMMdd-HHmm}.zip`) of everything the caller may view; `409 export.in_progress` while another export of the workspace runs |

Archive layout: `manifest.json` (format version, counts, omissions, scope text), `users.json`, `spaces/{key}/space.json`, `pages/{id}.json` (labels, body, comments, attachment list), `attachments/{pageId}/{id}-{name}`, `tasks.json`, `events.json`, `databases/{id}.json`, `slides/{id}.json`, `whiteboards/{id}.json|.svg`. Drafts, archive, trash, history, permissions and secrets are not included.

## Webhooks (Phase 11d, ADR-036)

Tenant administrators only (403 for members).

| Method | Path | Notes |
|---|---|---|
| GET | `/integrations/webhooks/catalog` | Subscribable events and whether the installation allows any host |
| GET/POST | `/integrations/webhooks` | POST `{name, url, events[], spaceId?}` → `{webhook, secret}` — the signing secret is returned **only here** (`Cache-Control: no-store`) |
| GET/PUT | `/integrations/webhooks/{id}` | PUT needs **If-Match**; `enabled: true` after an automatic switch-off resets the failure counter |
| POST | `/integrations/webhooks/{id}/rotate-secret` | New secret, returned once |
| POST | `/integrations/webhooks/{id}/test` | Sends a signed `ping` now: `{success, httpStatus, error, durationMs}` (no response body) |
| GET | `/integrations/webhooks/{id}/deliveries?take=` | Delivery log: event, status, HTTP status, attempts, error code |
| DELETE | `/integrations/webhooks/{id}` | Also deletes the log |

### Issue tracker connectors (Phase 13a, ADR-043)

| Method | Path | Notes |
|---|---|---|
| GET | `/integrations/connectors/catalog` | Admin. `{kinds, hostPolicyConfigured}` |
| GET/POST | `/integrations/connectors` | Admin. POST `{name, kind: GitHub|GitLab|Jira, baseUrl, token?}`; the token is write-only (responses carry `hasToken`) |
| GET/PUT/DELETE | `/integrations/connectors/{id}` | Admin. PUT needs **If-Match**; omit `token` to keep it, `""` removes it |
| GET | `/integrations/issues/connectors` | Any member: enabled connectors, `{id, name, kind}` only |
| GET | `/integrations/issues/lookup?connector=&reference=` | Any member: `{reference, title, status, url, type}`; references `owner/repo#1`, `group/project#1`, `KEY-1`; errors `issue.*` / `connector.*` |

### Jira page linking (extends ADR-043 for Jira write/link)

Raavi owns Page–Issue relationships; Jira owns issue fields. Requires an enabled Jira connector with a token (Create Issues + Link Issues). See `docs/integrations/`.

| Method | Path | Notes |
|---|---|---|
| GET | `/integrations/jira/connectors` | Enabled Jira connectors `{id, name}` |
| GET | `/integrations/jira/connectors/{id}/projects` | |
| GET | `/integrations/jira/connectors/{id}/issuetypes?project=` | |
| GET | `/integrations/jira/connectors/{id}/createmeta?project=&issuetype=` | Field metadata for create |
| GET | `/integrations/jira/connectors/{id}/search?q=` | Key, text, or `jql:…` |
| GET | `/pages/{contentId}/jira-links` | View on page |
| POST | `/pages/{contentId}/jira-links/create` | Edit; create issue + Remote Link + relationship |
| POST | `/pages/{contentId}/jira-links` | Edit; link existing `{connectorId, issueKeyOrId, quote?}` |
| POST | `/pages/{contentId}/jira-links/{linkId}/refresh` | Refresh cached issue fields |
| DELETE | `/pages/{contentId}/jira-links/{linkId}` | Soft unlink (does not delete the Jira issue) |
| GET | `/integrations/jira/issues/{connectorId}/{issueId}/pages` | Related pages the caller may View |

Request to the receiver: `POST` JSON `{id, type, occurredAt, tenant, data}` with ids only; headers `X-Raavi-Event`, `X-Raavi-Delivery` (event id), `X-Raavi-Signature: t=<unix>,v1=<hex(HMAC-SHA256(secret, "<t>.<body>"))>`. 2xx = delivered; 5xx/408/429/timeouts are retried by the outbox; other 4xx are final; 20 consecutive failures switch the webhook off. Global configuration (`sandbox/config/integrations.json`): `AllowedWebhookHosts` (empty = webhooks off), `TimeoutSeconds`. Errors: `webhook.name_invalid|url_invalid|host_not_allowed|host_forbidden|events_invalid|not_found|too_many`.

## AI assistant (Phase 11a, ADR-034)

| Method | Path | Notes |
|---|---|---|
| GET | `/ai/status` | Any member: `{available}` (AI is switched on, a key is stored and the installation allows an AI host) |
| POST | `/ai/pages/{id}/summary` | Member who can read the page. `{text, model, promptTokens, completionTokens, truncated}` |
| POST | `/ai/pages/{id}/ask` | `{question}` (1–1000 characters); answers from that page only |
| GET/PUT | `/ai/settings` | **Tenant administrator.** `{enabled, baseUrl, model, apiKey?, monthlyTokenCap, maxOutputTokens}`; the key is write-only (`hasApiKey` is returned), omit it to keep it, send `""` to remove it. The first save needs no `If-Match`; later saves do |
| GET | `/ai/usage` | Tenant administrator: tokens/requests/failures this month, by feature, top users |

Global configuration (`sandbox/config/ai.json`, `Raavi:Ai`): `AllowedHosts` (empty = AI off), `RequestTimeoutSeconds`, `MaxInputChars`, `MaxRequestsPerUserPerHour`. Errors: 404 `ai.page_not_found` (also for pages the caller cannot read), 409 `ai.not_configured`, 429 `ai.quota_exceeded` / `ai.rate_limited`, 503 `ai.provider_auth|rate_limited|not_found|unavailable|rejected|bad_response`, 400 `ai.url_invalid|host_not_allowed|host_forbidden|model_invalid|cap_invalid|output_invalid|key_required|key_invalid|question_invalid`, 400 `ai.page_empty`.

## Approvals (Phase 10b, ADR-032)

| Method | Path | Notes |
|---|---|---|
| POST | `/content/{id}/approvals` | Edit right on the page. `{note?, mode: All|Any, approverIds: [1–10 users], dueAt?}`; one pending request per page (409 `approval.already_pending`) |
| GET | `/content/{id}/approvals` | View; latest 50 requests of the page |
| GET | `/approvals?scope=inbox|requested` | Waiting for my decision (due date first) / requested by me |
| GET | `/approvals/{id}` | View of the page; `status`, `approvers[]` (decision, comment), `pageChanged`, `overdue`, `canDecide`, `canCancel` |
| POST | `/approvals/{id}/decision` | Assigned approvers only. `{decision: Approved|Rejected, comment}`; a rejection needs a comment; 409 once final |
| POST | `/approvals/{id}/cancel` | Requester or space administrator, while pending |

Errors: `approval.not_found`, `approval.not_approver`, `approval.self_approver`, `approval.approvers_invalid`, `approval.already_pending`, `approval.not_pending`, `approval.already_decided`, `approval.note_invalid`, `approval.mode_invalid`, `approval.due_invalid`, `approval.decision_invalid`, `approval.comment_required`, `approval.comment_invalid`, `approval.scope_invalid`. Notification kinds: `approval.requested|approved|rejected|cancelled|reminder|overdue`. With a `dueAt`, a reminder is sent one day before the due date and an overdue notice at the due date (approvers still waiting; the overdue notice also reaches the requester).

## Workflows (Phase 10a, ADR-031)

Space administrators only (403 for other viewers of the space, 404 when the space is not visible).

| Method | Path | Notes |
|---|---|---|
| GET | `/workflows/catalog` | Supported triggers |
| GET | `/spaces/{key}/workflows` | Rules of the space |
| POST | `/spaces/{key}/workflows` | `{name, enabled?, trigger, staleDays? (only for content.stale, 1–3650), conditions: [{field: Title|ContentType|Label, operator: Contains|Equals|StartsWith|Has, value}], actions: [{type: AddLabel, label} | {type: Notify, recipients: Watchers|Actor, message}]}` — ≤ 10 conditions, 1–5 actions |
| GET/PUT | `/workflows/{id}` | PUT replaces the definition, needs **If-Match** `"r{n}"` |
| DELETE | `/workflows/{id}` | Also deletes the run history |
| GET | `/workflows/{id}/runs?take=` | Latest runs (≤ 100): `status` Succeeded/Skipped/Failed, `message`, `attempts` |

Triggers: `content.created|updated|moved|archived|trashed|published|publish_requested`, and the time-based `content.stale` (a daily sweep runs the rule for current pages not updated for `staleDays` days, once per page update; run history shows `eventType: content.stale`). Extra error: `workflow.stale_days_invalid`. Errors: `workflow.name_invalid`, `workflow.trigger_invalid`, `workflow.too_many_conditions`, `workflow.actions_invalid`, `workflow.condition_invalid`, `workflow.action_invalid`, `label.invalid`, `workflow.not_found`.

## Whiteboards (Phase 9e+, ADR-030 / ADR-035)

| Method | Path | Notes |
|---|---|---|
| GET | `/spaces/{key}/whiteboards` | View; `?archived=true` includes archived. List is light (`elements` empty); includes `description`, `favorited`, `archivedAt`, `background`, `gridStyle` |
| POST | `/spaces/{key}/whiteboards` | Edit on an active space. `{title, description?, background?, gridStyle?, elements:[…]}` |
| POST | `/spaces/{key}/whiteboards/from-template/{templateId}` | Edit; create from a server template |
| GET | `/whiteboard-templates` | View; optional `?spaceKey=` |
| GET/PUT | `/whiteboards/{id}` | PUT replaces title/elements (and optional description/background/gridStyle), needs **If-Match** `"r{n}"` |
| PUT | `/whiteboards/{id}/title` | Edit; `{title}` — renames without touching elements or live sessions |
| PUT | `/whiteboards/{id}/meta` | Edit; `{description?, background?, gridStyle?}` |
| POST | `/whiteboards/{id}/duplicate` | Edit; copies title/elements (images dropped — attachments are not copied) |
| POST | `/whiteboards/{id}/archive` · `/restore` | Edit; soft archive |
| PUT/DELETE | `/whiteboards/{id}/favorite` | View; per-user favourite |
| DELETE | `/whiteboards/{id}` | Edit |
| GET | `/whiteboards/{id}/export` | View; SVG. PNG is client-side from the canvas |
| GET | `/whiteboards/{id}/export.json` | View; JSON schemaVersion 1 snapshot (no attachment bytes) |
| GET/POST | `/whiteboards/{id}/attachments` | View / Edit; board-scoped images (sniffed media type, size cap = `Raavi:Attachments:MaxBytes`) |
| GET | `/whiteboard-attachments/{id}` | View on owning board; `nosniff`, private cache |
| GET | `/whiteboards/{id}/revisions` · `…/{revisionId}` | View; last 50 REST-save snapshots |
| POST | `/whiteboards/{id}/revisions/{revisionId}/restore` | Edit; restores as a new revision (never destructive) |
| POST | `/whiteboards/{id}/template` | Edit; save board as a space template (images omitted) |
| GET/POST | `/whiteboards/{id}/comments` | View / Comment|Edit; threads with optional objectId or x/y |
| POST | `/whiteboards/{id}/comments/{id}/resolve` · `/reopen` | Edit |

Live editing (ADR-035): `POST /collaboration/tickets {contentId: <boardId>, kind: "board"}` returns a ticket for the Yjs document `{tenantId}:board:{boardId}:live` (Edit right required; 403 for readers, 404 otherwise). Awareness may carry peer cursors/selection. A whole-board PUT clears the live state and resets connected editors.

Element types: `Rect|RoundRect|Ellipse|Diamond|Triangle|Star|Text|Sticky|Arrow|Line|Pen|Image|Connector|Group|Table`. `Image` references `attachmentId` on the same board (never embeds bytes). Colours `#rrggbb`, ≤ 2 000 elements. Errors: `board.title_invalid`, `board.too_many`, `board.element_invalid`, `board.not_found`, `board.attachment_*`.

## Slides (Phase 9d, ADR-029)

| Method | Path | Notes |
|---|---|---|
| GET | `/spaces/{key}/slides` | View; decks with `slideCount` (the `slides` array is empty in lists) |
| POST | `/spaces/{key}/slides` | Edit on an active space. `{title, direction: Ltr|Rtl, slides: [{layout: Title|Bullets|Text, title, body, notes}]}` |
| GET/PUT | `/slides/{id}` | PUT replaces title, direction and the whole slide list, needs **If-Match** `"r{n}"`; a slide with an `id` keeps it (order = list order), without an id is new |
| DELETE | `/slides/{id}` | Edit |
| GET | `/slides/{id}/export` | View; PPTX (`application/vnd.openxmlformats-officedocument.presentationml.presentation`). PDF is produced by the browser from the `/slides/{id}/print` page |

Errors: `slides.title_invalid`, `slides.direction_invalid`, `slides.too_many` (100), `slides.slide_invalid`, `slides.not_found`.

## Databases (Phase 9c, ADR-028)

| Method | Path | Notes |
|---|---|---|
| GET | `/spaces/{key}/databases` | View; with column definitions and row counts |
| POST | `/spaces/{key}/databases` | Edit on an active space. `{name, description?, columns: [{name, type: Text|Number|Date|Checkbox|Select|Url, options?: [{label}]}]}` |
| GET/PUT | `/databases/{id}` | PUT needs **If-Match** `"r{n}"`; columns with an `id` keep it (type locked: `db.column_type_locked`), without an id are new |
| DELETE | `/databases/{id}` | Edit; deletes all rows |
| GET | `/databases/{id}/rows?q=&sort=<columnId>&dir=asc|desc&skip=&take=` | `{total, rows}`; take ≤ 500; empty cells sort last; `db.sort_invalid` |
| POST | `/databases/{id}/rows` | `{values: {columnId: value}}`; at most 5 000 rows (`db.row_limit`) |
| GET/PUT/DELETE | `/database-rows/{id}` | PUT needs **If-Match**; sends the whole row |
| GET | `/databases/{id}/export` | CSV (UTF-8 BOM, formula-safe) |

Errors: `db.name_invalid`, `db.column_invalid`, `db.too_many_columns`, `db.column_unknown`, `db.column_type_locked`, `db.value_invalid`, `db.not_found`, `db.row_not_found`.

## Calendar (Phase 9b, ADR-027)

| Method | Path | Notes |
|---|---|---|
| GET | `/calendars?spaceKey=&includeArchived=` | List calendars the caller may see (personal own + shared grants + org + viewable spaces). Archived hidden unless `includeArchived=true` |
| GET | `/spaces/{key}/calendars?includeArchived=` | Space calendars (View) |
| POST | `/calendars` | `{scope: Personal\|Space\|Organization, spaceKey?, name?, description?, color?, icon?}`. Org = tenant admin; Space = Edit on active space |
| PUT | `/calendars/{id}` | Manage: update name/description/color/icon |
| POST | `/calendars/{id}/archive` | Manage; default delete path (hides from normal lists, keeps events) |
| POST | `/calendars/{id}/restore` | Manage |
| POST | `/calendars/{id}/purge` | Manage; only when archived; body `{confirmName}` must equal calendar name; deletes that calendar’s Raavi-owned events only |
| GET/PUT | `/calendars/{id}/grants` | Manage; list or upsert `{subjectType: User\|Group, subjectId, grant: FreeBusy\|View\|Create\|EditOwn\|EditAll\|Manage}` |
| DELETE | `/calendars/{id}/grants/{grantId}` | Manage |
| GET/PUT | `/calendar/preferences` | Caller’s browser/TTS/quiet-hours prefs |
| GET | `/spaces/{key}/calendar/occurrences?from=&to=&calendarIds=&q=&type=&sourceKind=` | View; window ≤ 400 days; merges Raavi events + Jira due-date overlay + Templar (empty until configured). FreeBusy strips titles; `sourceKind` / `freeBusyOnly` / `externalUrl` on rows; optional `q` / `type` / `sourceKind` filters |
| GET | `/calendar/occurrences?from=&to=&calendarIds=&q=&type=&sourceKind=` | Same across personal/org/space calendars the caller may access |
| POST | `/calendar/events/{id}/move` | Edit on source + Create on target; body `{targetCalendarId}` → moved event with ETag |
| POST | `/calendar/events/{id}/rsvp` | Attendee only; body `{response: Accepted\|Declined\|Tentative}` |
| POST | `/calendars/{id}/import` | Create+; body `{ics}` (≤ 1 MB / 200 VEVENTs); returns `{imported, skipped, warnings[]}`; sanitized SUMMARY/DESCRIPTION/LOCATION/RRULE |
| POST | `/spaces/{key}/calendar/events` | Edit on an active space (default space calendar). Rich fields: `type`, `status`, `priority`, `color`, `onlineUrl`, `isPrivate`, `reminders[]` |
| POST | `/calendars/{id}/events` | Create on a writable calendar (Create+) |
| GET/PUT | `/calendar/events/{id}` | PUT needs **If-Match** `"r{n}"`; optional `editScope` (`EntireSeries` \| `ThisOccurrence` \| `ThisAndFollowing`) + `occurrenceDate` |
| POST | `/calendar/events/{id}/exclusions` | `{date}` cancels one occurrence |
| DELETE | `/calendar/events/{id}` | Edit |
| GET | `/spaces/{key}/calendar/export` | `text/calendar` (.ics); Persian monthly/yearly series become `RDATE` lists |

`recurrence`: `{frequency: None|Daily|Weekly|Monthly|Yearly, interval 1–99, weekDays [0=Sunday…6], calendar: Gregorian|Persian, until? (yyyy-MM-dd) | count? (1–1000), monthWeek? (0=day-of-month, 1–4=Nth weekday, −1/5=last)}` — never both `until` and `count`; a weekly rule must include the start's weekday. Event write also accepts `attendees[{userId,optional?}]`, `linkedPageId`, `linkedWhiteboardId`. Errors: `calendar.title_invalid`, `.time_invalid`, `.range_invalid`, `.range_too_long`, `.time_zone_invalid`, `.recurrence_invalid`, `.event_not_found`, `.rsvp_invalid`, `.link_invalid`.

Occurrence DTO: `{eventId, spaceKey, spaceName, title, location, allDay, startDate, startTime, endDate (inclusive), endTime, timeZone, startUtc, endUtc, isRecurring, occurrenceDate, canEdit}`.

## Real-time collaboration (Phase 6)

| Method | Path | Notes |
|---|---|---|
| GET | `/collaboration/config` | `{enabled}` |
| POST | `/collaboration/tickets` | `{contentId}` → `{ticket, documentName, url, permission, expiresAt}`; Edit on the page; bound to the caller's session; `Cache-Control: no-store`; `503 collaboration.unavailable` when not configured |
| PUT | `/content/{id}/draft/title` | `{title}` (live editors; the body is written by the collaboration server) |

`POST /content/{id}/publish` accepts `If-Match: *` (publish the latest shared draft). Internal service endpoints: COLLABORATION_ARCHITECTURE.md §7b.

## Drafts, publishing, history, trash (Phase 5)

| Method | Path | Notes |
|---|---|---|
| GET | `/content/{id}/draft` | Shared draft (or the published body with `exists:false`, ETag `"r0"`); Edit |
| PUT | `/content/{id}/draft` *If-Match* | Autosave `{title, body}`; `412` when another editor saved first |
| DELETE | `/content/{id}/draft` | Discard; for a never-published page this deletes the page |
| POST | `/content/{id}/publish` *If-Match (draft)* | `{changeSummary, publishAt?}` → `{outcome: Published|Scheduled|PendingApproval, page}`; needs `Publish` |
| DELETE | `/content/{id}/schedule` | Cancel a scheduled publication |
| GET | `/content/{id}/versions/{n}` | Version with body, type (`Published|Restore|Import`), contributors |
| GET | `/content/{id}/diff?from=n&to=m|draft` | Structural diff `{blocks:[{op, type, before, after, segments}]}` |
| POST | `/content/{id}/versions/{n}/restore` *If-Match (page)* | New `Restore` version, or `RestoredToDraft` in approval spaces |
| GET | `/spaces/{key}/publish-requests?pending=` | Space administrators |
| POST | `/publish-requests/{id}/approve`, `/reject` | `{comment}`; approval publishes the request snapshot; the requester may withdraw via reject |
| PUT | `/spaces/{key}/publishing` *If-Match (space)* | `{requireApproval}` |
| GET | `/spaces/{key}/trash` | Trashed roots (space administrators) |
| POST | `/content/{id}/restore-from-trash` | Restores the subtree deleted together |
| DELETE | `/content/{id}/purge` | Permanent deletion of trashed content (versions, drafts, files, labels) |
| GET | `/me/drafts` | Caller's unpublished pages and drafts they contributed to |

`POST /spaces/{key}/content` accepts `publish:false` (private draft page). Direct `PUT /content/{id}` still publishes immediately; it needs `Publish` and returns `409 content.approval_required` in approval spaces.

## Editor & attachments (Phase 4)

| Method | Path | Notes |
|---|---|---|
| GET | `/editor/schema` | `{schema, schemaVersion, maxBytes, extensions:[{id, name, version, kind, insertable}]}` — the SPA refuses to edit when its registry differs |
| GET | `/content/{id}/html` | `{html}` sanitized server rendering of the current version |
| GET | `/content/{id}/attachments` | Attachments of a page (View) |
| POST | `/content/{id}/attachments` | multipart `file` (Edit); media type sniffed from content; `400 attachment.too_large` / `attachment.empty` |
| GET | `/attachments/{id}` | Download (View on the owning page, else 404); images inline, everything else `attachment`; `nosniff`, CSP `sandbox` |

## Discovery, templates, administration

| Method | Path | Notes |
|---|---|---|
| GET | `/quick-find?q=&limit=` | Spaces and current content by title (full text: Phase 7) |
| GET | `/labels?prefix=` | Labels used on at least one page the caller can view (legacy string list) |
| GET | `/labels/{slug}` | Label detail (displayName, children, related, usageCount, aliases) |
| GET | `/labels/search?tags=&tagMode=any\|all&includeDescendants=&spaceKey=&skip=&take=` | Multi-label page search → `{items,total}` (view-filtered) |
| GET | `/labels/{name}/content` | Single-label pages (legacy); with query params delegates to search |
| GET | `/content/{id}/label-suggestions` | Deterministic suggestions for editors |
| GET/POST/PUT/DELETE | `/admin/labels…` | Tenant admin: list/create/update/archive/restore/delete/move/merge/analytics |
| GET | `/activity` | Activity across viewable spaces (projected from the outbox) |
| GET/POST | `/templates` | Global templates (create: tenant administrators) |
| GET/PUT/DELETE | `/templates/{id}` | PUT *If-Match* |
| GET/PUT | `/tenant/permissions` | Tenant administrators: who may create spaces (`action: Create` grants only). Default: administrators only |
| GET | `/directory?q=` | Users and groups of the current tenant for pickers (Identity module) |
