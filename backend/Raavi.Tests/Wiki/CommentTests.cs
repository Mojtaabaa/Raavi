using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 7a acceptance: comments, inline threads, reactions, mentions, watching and notification delivery.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class CommentTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static object Body(string text, params (Guid Id, string Label)[] mentions) => new
    {
        schema = "raavi.doc",
        schemaVersion = 2,
        doc = new
        {
            type = "doc",
            content = new object[]
            {
                new
                {
                    type = "paragraph",
                    content = mentions.Select(m => (object)new { type = "mention", attrs = new { id = m.Id.ToString(), label = m.Label, mentionSuggestionChar = "@" } })
                        .Append(new { type = "text", text = " " + text }).ToArray(),
                },
            },
        },
    };

    private static async Task<JsonElement> CommentAsync(Browser b, Guid pageId, string text, object? anchor = null, params (Guid, string)[] mentions)
    {
        using var r = await b.PostAsync($"api/v1/content/{pageId}/comments", new { body = Body(text, mentions), kind = anchor is null ? "Page" : "Inline", anchor });
        return await ReadAsync(r);
    }

    private static async Task<JsonElement> ReplyAsync(Browser b, Guid commentId, string text, params (Guid, string)[] mentions)
    {
        using var r = await b.PostAsync($"api/v1/comments/{commentId}/replies", new { body = Body(text, mentions) });
        return await ReadAsync(r);
    }

    private static async Task<JsonElement[]> NotificationsAsync(Browser b) =>
        (await b.GetJsonAsync("api/v1/notifications")).GetProperty("items").EnumerateArray().ToArray();

    [Fact]
    public async Task Threads_support_replies_reactions_inline_anchors_editing_and_resolution()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Runbook", body: Doc("Restart the queue before deploying")));

        var listing = await member.GetJsonAsync($"api/v1/content/{page}/comments");
        Assert.True(listing.GetProperty("canComment").GetBoolean());
        Assert.Contains("👍", listing.GetProperty("reactions").EnumerateArray().Select(e => e.GetString()));

        var thread = await CommentAsync(member, page, "Which queue?");
        var root = thread.GetProperty("comments")[0];
        var rootId = Id(root);
        Assert.Equal("Page", thread.GetProperty("kind").GetString());
        Assert.True(root.GetProperty("rights").GetProperty("canEdit").GetBoolean());

        var reply = await ReplyAsync(admin, rootId, "The billing one.");
        Assert.Equal(rootId, reply.GetProperty("threadId").GetGuid());
        var replyToReply = await ReplyAsync(member, Id(reply), "Thanks"); // replies stay flat: attached to the root
        Assert.Equal(rootId, replyToReply.GetProperty("parentId").GetGuid());

        // Inline thread with a text-quote anchor; the anchor is validated.
        var inline = await CommentAsync(admin, page, "Is this still true?", new { exact = "Restart the queue", prefix = "", suffix = " before" });
        Assert.Equal("Inline", inline.GetProperty("kind").GetString());
        Assert.Equal("Restart the queue", inline.GetProperty("anchor").GetProperty("exact").GetString());
        using (var bad = await admin.PostAsync($"api/v1/content/{page}/comments", new { body = Body("x"), kind = "Inline", anchor = new { exact = "" } }))
        {
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest);
        }

        // Reactions: idempotent toggles from an allow-list.
        using (var on = await admin.PostAsync($"api/v1/comments/{rootId}/reactions", new { emoji = "👍", on = true }))
        {
            await ReadAsync(on);
        }

        using (var again = await member.PostAsync($"api/v1/comments/{rootId}/reactions", new { emoji = "👍", on = true }))
        {
            var r = (await ReadAsync(again))[0];
            Assert.Equal(2, r.GetProperty("count").GetInt32());
            Assert.True(r.GetProperty("mine").GetBoolean());
        }

        using (var off = await member.PostAsync($"api/v1/comments/{rootId}/reactions", new { emoji = "👍", on = false }))
        {
            Assert.False((await ReadAsync(off))[0].GetProperty("mine").GetBoolean());
        }

        using (var bogus = await member.PostAsync($"api/v1/comments/{rootId}/reactions", new { emoji = "💩", on = true }))
        {
            await Browser.AssertStatusAsync(bogus, HttpStatusCode.BadRequest);
        }

        // Editing: author only, with If-Match.
        using (var foreign = await admin.SendAsync(HttpMethod.Put, $"api/v1/comments/{rootId}", new { body = Body("hijack") }, withXsrf: true, ifMatch: "\"r1\""))
        {
            await Browser.AssertStatusAsync(foreign, HttpStatusCode.Forbidden);
        }

        using (var missing = await member.SendAsync(HttpMethod.Put, $"api/v1/comments/{rootId}", new { body = Body("x") }, withXsrf: true))
        {
            await Browser.AssertStatusAsync(missing, HttpStatusCode.PreconditionRequired);
        }

        using (var edit = await member.SendAsync(HttpMethod.Put, $"api/v1/comments/{rootId}", new { body = Body("Which queue exactly?") }, withXsrf: true, ifMatch: "\"r1\""))
        {
            var edited = await ReadAsync(edit);
            Assert.Equal("\"r2\"", edit.Headers.ETag!.Tag);
            Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);
        }

        using (var stale = await member.SendAsync(HttpMethod.Put, $"api/v1/comments/{rootId}", new { body = Body("y") }, withXsrf: true, ifMatch: "\"r1\""))
        {
            await Browser.AssertStatusAsync(stale, HttpStatusCode.PreconditionFailed);
        }

        // Soft delete keeps the thread; the body is gone.
        using (var del = await member.DeleteAsync($"api/v1/comments/{Id(replyToReply)}"))
        {
            await Browser.AssertStatusAsync(del, HttpStatusCode.NoContent);
        }

        // Resolve hides the thread from the default listing; reopen brings it back.
        using (var resolve = await member.PostAsync($"api/v1/comments/{rootId}/resolve"))
        {
            Assert.Equal("Resolved", (await ReadAsync(resolve)).GetProperty("status").GetString());
        }

        var open = await member.GetJsonAsync($"api/v1/content/{page}/comments");
        Assert.Single(open.GetProperty("threads").EnumerateArray()); // only the inline thread
        var all = await member.GetJsonAsync($"api/v1/content/{page}/comments?includeResolved=true");
        var resolved = all.GetProperty("threads").EnumerateArray().Single(t => Id(t) == rootId);
        Assert.False(string.IsNullOrEmpty(resolved.GetProperty("resolvedByName").GetString()));
        var deleted = resolved.GetProperty("comments").EnumerateArray().Single(c => Id(c) == Id(replyToReply));
        Assert.True(deleted.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, deleted.GetProperty("body").ValueKind);

        using (var reopen = await admin.PostAsync($"api/v1/comments/{rootId}/reopen"))
        {
            Assert.Equal("Open", (await ReadAsync(reopen)).GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Commenting_requires_view_and_the_comment_right()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (reader, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (outsider, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var readerId = (await reader.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var key = (await CreateSpaceAsync(admin, visibility: "Private")).GetProperty("key").GetString()!;
        using (var put = await admin.PutAsync($"api/v1/spaces/{key}/permissions", new
        {
            grants = new object[]
            {
                new { subjectType = "User", subjectId = (Guid?)adminId, action = "View", effect = "Allow" },
                new { subjectType = "User", subjectId = (Guid?)adminId, action = "Comment", effect = "Allow" },
                new { subjectType = "User", subjectId = (Guid?)adminId, action = "Edit", effect = "Allow" },
                new { subjectType = "User", subjectId = (Guid?)adminId, action = "Create", effect = "Allow" },
                new { subjectType = "User", subjectId = (Guid?)adminId, action = "Publish", effect = "Allow" },
                new { subjectType = "User", subjectId = (Guid?)adminId, action = "ManagePermissions", effect = "Allow" },
                new { subjectType = "User", subjectId = (Guid?)readerId, action = "View", effect = "Allow" },
            },
        }))
        {
            await ReadAsync(put);
        }

        var page = Id(await CreatePageAsync(admin, key, "Read only"));
        var thread = await CommentAsync(admin, page, "Admin note");
        var rootId = Id(thread.GetProperty("comments")[0]);

        // Reader: may read the discussion but not write to it.
        var listing = await reader.GetJsonAsync($"api/v1/content/{page}/comments");
        Assert.False(listing.GetProperty("canComment").GetBoolean());
        Assert.False((await reader.GetJsonAsync($"api/v1/content/{page}")).GetProperty("rights").GetProperty("canComment").GetBoolean());
        Assert.True((await admin.GetJsonAsync($"api/v1/content/{page}")).GetProperty("rights").GetProperty("canComment").GetBoolean());
        Assert.Single(listing.GetProperty("threads").EnumerateArray());
        using (var post = await reader.PostAsync($"api/v1/content/{page}/comments", new { body = Body("hi") }))
        {
            await Browser.AssertStatusAsync(post, HttpStatusCode.Forbidden);
        }

        using (var react = await reader.PostAsync($"api/v1/comments/{rootId}/reactions", new { emoji = "👍", on = true }))
        {
            await Browser.AssertStatusAsync(react, HttpStatusCode.Forbidden);
        }

        // Outsider: page and comments do not exist.
        using (var list = await outsider.GetAsync($"api/v1/content/{page}/comments"))
        {
            Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        }

        using (var reply = await outsider.PostAsync($"api/v1/comments/{rootId}/replies", new { body = Body("hi") }))
        {
            Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
        }

        using (var resolve = await outsider.PostAsync($"api/v1/comments/{rootId}/resolve"))
        {
            Assert.Equal(HttpStatusCode.NotFound, resolve.StatusCode);
        }
    }

    [Fact]
    public async Task Mentions_participants_and_watchers_are_notified_after_a_fresh_access_check()
    {
        var (tenant, admin, adminEmail) = await _kit.Identity.TenantWithAdminAsync();
        var (member, memberEmail, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (reader, readerEmail, readerId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Release plan"));
        await _kit.DispatchOutboxAsync(); // page creation: the author starts watching
        Assert.True((await admin.GetJsonAsync($"api/v1/content/{page}/watch")).GetProperty("watching").GetBoolean());

        // The member's e-mail is switched off; in-app delivery continues.
        using (var pref = await member.PutAsync("api/v1/notifications/preferences", new { emailEnabled = false }))
        {
            Assert.False((await ReadAsync(pref)).GetProperty("emailEnabled").GetBoolean());
        }

        var thread = await CommentAsync(member, page, "Can you review?", null, (readerId, "Reader"));
        var rootId = Id(thread.GetProperty("comments")[0]);
        await _kit.DispatchOutboxAsync();

        var readerInbox = await NotificationsAsync(reader);
        var mention = Assert.Single(readerInbox);
        Assert.Equal("mention", mention.GetProperty("kind").GetString());
        Assert.Equal("Release plan", mention.GetProperty("title").GetString());
        Assert.Equal(key, mention.GetProperty("spaceKey").GetString());
        Assert.Contains("Can you review?", mention.GetProperty("excerpt").GetString(), StringComparison.Ordinal);
        Assert.Equal("comment.created", Assert.Single(await NotificationsAsync(admin)).GetProperty("kind").GetString()); // watcher
        Assert.Empty(await NotificationsAsync(member)); // the actor is never notified

        var mail = Assert.Single(fx.Mail.To(readerEmail));
        Assert.Contains(page.ToString("D"), mail.TextBody, StringComparison.Ordinal);
        Assert.Contains($"#comment-{rootId:D}", mail.TextBody, StringComparison.Ordinal);
        Assert.Single(fx.Mail.To(adminEmail));

        // A reply reaches the thread participant (member) — in-app only, as configured.
        await ReplyAsync(admin, rootId, "Looks good");
        await _kit.DispatchOutboxAsync();
        Assert.Equal("comment.reply", Assert.Single(await NotificationsAsync(member)).GetProperty("kind").GetString());
        Assert.Empty(fx.Mail.To(memberEmail));
        Assert.Equal(1, (await member.GetJsonAsync("api/v1/notifications/unread-count")).GetProperty("count").GetInt32());

        // Losing access: once the page is restricted, a new mention of the reader produces nothing.
        using (var restrict = await admin.PutAsync($"api/v1/content/{page}/restrictions", new
        {
            view = new[] { new { subjectType = "User", subjectId = adminId }, new { subjectType = "User", subjectId = memberId } },
            edit = Array.Empty<object>(),
        }))
        {
            await ReadAsync(restrict);
        }

        await CommentAsync(member, page, "Secret follow-up", null, (readerId, "Reader"));
        await _kit.DispatchOutboxAsync();
        Assert.Empty(await NotificationsAsync(reader)); // earlier notifications about the page are hidden too
        Assert.Single(fx.Mail.To(readerEmail));

        // Unwatching stops watcher notifications; mark-all-read clears the badge.
        var adminCount = (await NotificationsAsync(admin)).Length;
        Assert.Equal(2, adminCount); // first comment + the follow-up (admin can still view)
        using (var unwatch = await admin.DeleteAsync($"api/v1/content/{page}/watch"))
        {
            Assert.False((await ReadAsync(unwatch)).GetProperty("watching").GetBoolean());
        }

        await CommentAsync(member, page, "Anyone?");
        await _kit.DispatchOutboxAsync();
        Assert.Equal(adminCount, (await NotificationsAsync(admin)).Length);
        Assert.Equal(adminCount, (await admin.GetJsonAsync("api/v1/notifications/unread-count")).GetProperty("count").GetInt32());
        using (var readAll = await admin.PostAsync("api/v1/notifications/read-all"))
        {
            await Browser.AssertStatusAsync(readAll, HttpStatusCode.NoContent);
        }

        Assert.Equal(0, (await admin.GetJsonAsync("api/v1/notifications/unread-count")).GetProperty("count").GetInt32());

        // Notifications belong to their recipient.
        var memberNote = Id((await NotificationsAsync(member))[0]);
        using (var foreign = await admin.PostAsync($"api/v1/notifications/{memberNote}/read"))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        // Replays are idempotent.
        var before = fx.Mail.Sent.Count;
        await _kit.DispatchOutboxAsync();
        Assert.Equal(before, fx.Mail.Sent.Count);
    }

    [Fact]
    public async Task Publishing_notifies_watchers_and_the_event_hub_signals_inbox_changes()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Changelog"));
        using (var watch = await member.PutAsync($"api/v1/content/{page}/watch", new { }))
        {
            Assert.True((await ReadAsync(watch)).GetProperty("watching").GetBoolean());
        }

        var signals = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        await using var connection = Connect(member, tenant.Host);
        connection.On(Raavi.Wiki.Api.EventsHub.NotificationsMethod, () => signals.Writer.TryWrite(true));
        await connection.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Assert.True(await signals.Reader.ReadAsync(timeout.Token)); // initial signal on connect

        // What a client does: whenever it is signalled it re-reads its unread count. Whatever the timing, it must end up knowing.
        async Task WaitForUnreadAsync(int expected)
        {
            while ((await member.GetJsonAsync("api/v1/notifications/unread-count")).GetProperty("count").GetInt32() != expected)
            {
                await signals.Reader.ReadAsync(timeout.Token);
            }
        }

        var (_, etag) = await GetWithETagAsync(admin, $"api/v1/content/{page}/draft");
        using (var save = await admin.SendAsync(HttpMethod.Put, $"api/v1/content/{page}/draft", new { title = "Changelog", body = Doc("v2") }, withXsrf: true, ifMatch: etag))
        {
            await ReadAsync(save);
        }

        (_, etag) = await GetWithETagAsync(admin, $"api/v1/content/{page}/draft");
        using (var publish = await admin.SendAsync(HttpMethod.Post, $"api/v1/content/{page}/publish", new { changeSummary = "second" }, withXsrf: true, ifMatch: etag))
        {
            await ReadAsync(publish);
        }

        await _kit.DispatchOutboxAsync();
        Assert.Equal("page.published", Assert.Single(await NotificationsAsync(member)).GetProperty("kind").GetString());
        await WaitForUnreadAsync(1); // learned through the API node's push service (no polling in this test)

        // Cross-site connections are refused (cookies are the credential).
        await using var foreign = Connect(member, tenant.Host, origin: "https://evil.example");
        var closed = new TaskCompletionSource();
        var leaked = false;
        foreign.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        foreign.On(Raavi.Wiki.Api.EventsHub.NotificationsMethod, () => leaked = true);
        try
        {
            await foreign.StartAsync();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            // refused during the handshake: equally fine
        }

        Assert.False(leaked);
        Assert.Equal(HubConnectionState.Disconnected, foreign.State);
    }

    /// <summary>Regression: a page event whose page no longer exists must be processed (and dropped), not fail forever on a foreign key.</summary>
    [Fact]
    public async Task Events_about_pages_that_no_longer_exist_are_dropped_instead_of_failing_forever()
    {
        var tenant = await fx.CreateTenantAsync(SqlServerFixture.Unique("GA"), SqlServerFixture.Unique("ga").ToLowerInvariant());
        Guid messageId;
        await using (var scope = fx.TenantScope(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<Raavi.Wiki.Persistence.WikiDbContext>();
            var message = Raavi.Infrastructure.Messaging.OutboxMessage.Create(tenant.Id, "content.created", new Raavi.Wiki.Application.WikiEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Gone", 1), DateTimeOffset.UtcNow);
            db.Outbox.Add(message);
            await db.SaveChangesAsync();
            messageId = message.Id;
        }

        await _kit.DispatchOutboxAsync();
        await using var check = fx.TenantScope(tenant);
        var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(check.ServiceProvider.GetRequiredService<Raavi.Wiki.Persistence.WikiDbContext>().Outbox, o => o.Id == messageId);
        Assert.Equal(Raavi.Infrastructure.Messaging.OutboxStatus.Processed, row.Status);
    }

    private HubConnection Connect(Browser browser, string host, string? origin = null)
    {
        var server = fx.Api.Server;
        return new HubConnectionBuilder()
            .WithUrl($"https://{host}{Raavi.Wiki.Api.EventsHub.Path}", o =>
            {
                var cookie = string.Join("; ", browser.Cookies.Select(c => $"{c.Key}={c.Value}"));
                var originHeader = origin ?? $"https://{host}";
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                o.WebSocketFactory = async (ctx, ct) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = r =>
                    {
                        r.Headers["Cookie"] = cookie;
                        r.Headers["Origin"] = originHeader;
                        r.Host = new Microsoft.AspNetCore.Http.HostString(host);
                    };
                    var uri = new UriBuilder(ctx.Uri) { Scheme = "wss" }.Uri;
                    return await client.ConnectAsync(uri, ct);
                };
            })
            .Build();
    }
}
