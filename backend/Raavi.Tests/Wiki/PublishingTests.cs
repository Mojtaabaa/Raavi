using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Tenancy;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Application;
using Raavi.Wiki.Persistence;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 5 acceptance: drafts never leak to readers, published versions stay distinct and recoverable.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class PublishingTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static object DocV2(params string[] paragraphs) => new
    {
        schema = "raavi.doc",
        schemaVersion = 2,
        doc = new { type = "doc", content = paragraphs.Select(p => new { type = "paragraph", content = new[] { new { type = "text", text = p } } }).ToArray() },
    };

    private static string FirstText(JsonElement body) => body.GetProperty("doc").GetProperty("content")[0].GetProperty("content")[0].GetProperty("text").GetString()!;

    private static async Task<(JsonElement Draft, string ETag)> GetDraftAsync(Browser b, Guid id) => await GetWithETagAsync(b, $"api/v1/content/{id}/draft");

    private static async Task<HttpResponseMessage> SaveDraftAsync(Browser b, Guid id, string etag, string title, params string[] text) =>
        await b.SendAsync(HttpMethod.Put, $"api/v1/content/{id}/draft", new { title, body = DocV2(text) }, withXsrf: true, ifMatch: etag);

    private static async Task<JsonElement> PublishAsync(Browser b, Guid id, string? summary = null, DateTimeOffset? at = null)
    {
        var (_, etag) = await GetDraftAsync(b, id);
        using var r = await b.SendAsync(HttpMethod.Post, $"api/v1/content/{id}/publish", new { changeSummary = summary, publishAt = at }, withXsrf: true, ifMatch: etag);
        return await ReadAsync(r);
    }

    private static async Task EditDraftAsync(Browser b, Guid id, string title, params string[] text)
    {
        var (_, etag) = await GetDraftAsync(b, id);
        using var r = await SaveDraftAsync(b, id, etag, title, text);
        await ReadAsync(r);
    }

    [Fact]
    public async Task Shared_drafts_are_invisible_to_readers_until_published_with_all_contributors()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (reader, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var id = Id(await CreatePageAsync(admin, key, "Handbook", body: DocV2("published v1")));

        var (fresh, freshTag) = await GetDraftAsync(admin, id);
        Assert.False(fresh.GetProperty("exists").GetBoolean());
        Assert.Equal("\"r0\"", freshTag);
        Assert.Equal("published v1", FirstText(fresh.GetProperty("body")));

        using (var save = await SaveDraftAsync(admin, id, freshTag, "Handbook (new)", "draft text"))
        {
            await ReadAsync(save);
        }

        using (var stale = await SaveDraftAsync(member, id, freshTag, "Handbook", "lost"))
        {
            await Browser.AssertStatusAsync(stale, HttpStatusCode.PreconditionFailed);
        }

        await EditDraftAsync(member, id, "Handbook (new)", "draft text", "member line");

        // Readers see the published version; editors see that a draft exists.
        var readerView = await reader.GetJsonAsync($"api/v1/content/{id}");
        Assert.Equal("Handbook", readerView.GetProperty("title").GetString());
        Assert.Equal("published v1", FirstText(readerView.GetProperty("body")));
        Assert.True((await admin.GetJsonAsync($"api/v1/content/{id}")).GetProperty("hasDraft").GetBoolean());
        using (var readerDraft = await reader.GetAsync($"api/v1/content/{id}/draft"))
        {
            // Readers in an open space can edit too; with an Edit restriction they cannot read the draft.
            Assert.Equal(HttpStatusCode.OK, readerDraft.StatusCode);
        }

        var result = await PublishAsync(admin, id, "second edition");
        Assert.Equal("Published", result.GetProperty("outcome").GetString());
        Assert.Equal(2, result.GetProperty("page").GetProperty("version").GetInt32());
        Assert.Equal("Handbook (new)", (await reader.GetJsonAsync($"api/v1/content/{id}")).GetProperty("title").GetString());
        Assert.False((await admin.GetJsonAsync($"api/v1/content/{id}/draft")).GetProperty("exists").GetBoolean());

        var history = await reader.GetJsonAsync($"api/v1/content/{id}/versions");
        var v2 = history.EnumerateArray().First();
        Assert.Equal("second edition", v2.GetProperty("changeSummary").GetString());
        Assert.Equal(2, v2.GetProperty("contributors").GetArrayLength()); // admin + member
    }

    [Fact]
    public async Task Unpublished_pages_are_private_to_their_creator_and_discarding_removes_them()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;

        using var create = await member.PostAsync($"api/v1/spaces/{key}/content", new { title = "Secret plan", body = DocV2("idea"), publish = false });
        var draftPage = await ReadAsync(create);
        var id = Id(draftPage);
        Assert.Equal("Draft", draftPage.GetProperty("status").GetString());
        Assert.Equal(0, draftPage.GetProperty("version").GetInt32());

        using (var other = await admin.GetAsync($"api/v1/content/{id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode); // not even the space administrator
        }

        Assert.DoesNotContain("Secret plan", await TreeTitlesAsync(member, key));
        Assert.Contains((await member.GetJsonAsync("api/v1/me/drafts")).EnumerateArray(), d => Id(d) == id);

        var published = await PublishAsync(member, id);
        Assert.Equal("Current", published.GetProperty("page").GetProperty("status").GetString());
        Assert.Equal(1, published.GetProperty("page").GetProperty("version").GetInt32());
        Assert.Equal("idea", FirstText((await admin.GetJsonAsync($"api/v1/content/{id}")).GetProperty("body")));

        using var create2 = await member.PostAsync($"api/v1/spaces/{key}/content", new { title = "Throwaway", body = DocV2("x"), publish = false });
        var throwaway = Id(await ReadAsync(create2));
        using (var discard = await member.DeleteAsync($"api/v1/content/{throwaway}/draft"))
        {
            await Browser.AssertStatusAsync(discard, HttpStatusCode.NoContent);
        }

        using var gone = await member.GetAsync($"api/v1/content/{throwaway}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Versions_can_be_compared_and_restored_without_rewriting_history()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var id = Id(await CreatePageAsync(admin, key, "Policy", body: DocV2("Keep backups weekly", "Unchanged line")));
        await EditDraftAsync(admin, id, "Policy", "Keep backups daily", "Unchanged line", "New rule");
        await PublishAsync(admin, id);

        var diff = await admin.GetJsonAsync($"api/v1/content/{id}/diff?from=1&to=2");
        var blocks = diff.GetProperty("blocks").EnumerateArray().ToList();
        var changed = blocks.Single(b => b.GetProperty("op").GetString() == "Change");
        Assert.Contains(changed.GetProperty("segments").EnumerateArray(), s => s.GetProperty("op").GetString() == "Delete" && s.GetProperty("text").GetString() == "weekly");
        Assert.Contains(changed.GetProperty("segments").EnumerateArray(), s => s.GetProperty("op").GetString() == "Insert" && s.GetProperty("text").GetString() == "daily");
        Assert.Contains(blocks, b => b.GetProperty("op").GetString() == "Equal" && b.GetProperty("after").GetString() == "Unchanged line");
        Assert.Contains(blocks, b => b.GetProperty("op").GetString() == "Insert" && b.GetProperty("after").GetString() == "New rule");

        await EditDraftAsync(admin, id, "Policy", "draft only");
        Assert.Contains((await admin.GetJsonAsync($"api/v1/content/{id}/diff?from=2&to=draft")).GetProperty("blocks").EnumerateArray(), b => b.GetProperty("op").GetString() == "Change");

        var (_, etag) = await GetWithETagAsync(admin, $"api/v1/content/{id}");
        using (var restore = await admin.SendAsync(HttpMethod.Post, $"api/v1/content/{id}/versions/1/restore", null, withXsrf: true, ifMatch: etag))
        {
            Assert.Equal(3, (await ReadAsync(restore)).GetProperty("page").GetProperty("version").GetInt32());
        }

        var v3 = await admin.GetJsonAsync($"api/v1/content/{id}/versions/3");
        Assert.Equal("Restore", v3.GetProperty("type").GetString());
        Assert.Equal(1, v3.GetProperty("restoredFrom").GetInt32());
        Assert.Equal("Keep backups weekly", FirstText(v3.GetProperty("body")));
        Assert.Equal("Keep backups daily", FirstText((await admin.GetJsonAsync($"api/v1/content/{id}/versions/2")).GetProperty("body"))); // v2 still there
        Assert.True((await admin.GetJsonAsync($"api/v1/content/{id}/draft")).GetProperty("isStale").GetBoolean()); // the draft predates v3
    }

    [Fact]
    public async Task Spaces_requiring_approval_publish_only_the_approved_snapshot()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var id = Id(await CreatePageAsync(admin, key, "Release notes", body: DocV2("v1")));
        var (_, spaceTag) = await GetWithETagAsync(admin, $"api/v1/spaces/{key}");
        using (var require = await admin.SendAsync(HttpMethod.Put, $"api/v1/spaces/{key}/publishing", new { requireApproval = true }, withXsrf: true, ifMatch: spaceTag))
        {
            Assert.True((await ReadAsync(require)).GetProperty("requirePublishApproval").GetBoolean());
        }

        var (_, pageTag) = await GetWithETagAsync(member, $"api/v1/content/{id}");
        using (var direct = await UpdatePageAsync(member, id, pageTag, "Release notes", DocV2("sneaky")))
        {
            await Browser.AssertStatusAsync(direct, HttpStatusCode.Conflict, "content.approval_required");
        }

        await EditDraftAsync(member, id, "Release notes", "proposed v2");
        var requested = await PublishAsync(member, id, "please review");
        Assert.Equal("PendingApproval", requested.GetProperty("outcome").GetString());
        Assert.Equal(1, requested.GetProperty("page").GetProperty("version").GetInt32());
        Assert.True(requested.GetProperty("page").GetProperty("pendingApproval").GetBoolean());

        await EditDraftAsync(member, id, "Release notes", "edited after the request");
        using (var notAllowed = await member.GetAsync($"api/v1/spaces/{key}/publish-requests"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, notAllowed.StatusCode);
        }

        var pending = (await admin.GetJsonAsync($"api/v1/spaces/{key}/publish-requests")).EnumerateArray().Single();
        using (var memberApprove = await member.PostAsync($"api/v1/publish-requests/{Id(pending)}/approve", new { comment = (string?)null }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, memberApprove.StatusCode);
        }

        using (var approve = await admin.PostAsync($"api/v1/publish-requests/{Id(pending)}/approve", new { comment = "ok" }))
        {
            await ReadAsync(approve);
        }

        var page = await member.GetJsonAsync($"api/v1/content/{id}");
        Assert.Equal(2, page.GetProperty("version").GetInt32());
        Assert.Equal("proposed v2", FirstText(page.GetProperty("body"))); // the snapshot, not the later draft edit
        Assert.True(page.GetProperty("hasDraft").GetBoolean());

        var again = await PublishAsync(member, id);
        Assert.Equal("PendingApproval", again.GetProperty("outcome").GetString());
        var second = (await admin.GetJsonAsync($"api/v1/spaces/{key}/publish-requests")).EnumerateArray().Single();
        using (var reject = await admin.PostAsync($"api/v1/publish-requests/{Id(second)}/reject", new { comment = "not yet" }))
        {
            await ReadAsync(reject);
        }

        Assert.Equal(2, (await member.GetJsonAsync($"api/v1/content/{id}")).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Scheduled_publishing_runs_as_the_scheduler_and_respects_cancellation_and_revoked_rights()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var id = Id(await CreatePageAsync(admin, key, "Launch", body: DocV2("before")));
        await EditDraftAsync(member, id, "Launch", "after launch");
        var at = DateTimeOffset.UtcNow.AddHours(2);
        var scheduled = await PublishAsync(member, id, "go live", at);
        Assert.Equal("Scheduled", scheduled.GetProperty("outcome").GetString());
        Assert.NotEqual(JsonValueKind.Null, scheduled.GetProperty("page").GetProperty("scheduledPublishAt").ValueKind);

        // A stale job (different time) does nothing; the matching one publishes as the member.
        await RunScheduledAsync(tenant, new ScheduledPublishArgs(id, at.AddMinutes(1), memberId));
        Assert.Equal(1, (await admin.GetJsonAsync($"api/v1/content/{id}")).GetProperty("version").GetInt32());
        await RunScheduledAsync(tenant, new ScheduledPublishArgs(id, (await ScheduledAtAsync(tenant, id))!.Value, memberId));
        var page = await admin.GetJsonAsync($"api/v1/content/{id}");
        Assert.Equal(2, page.GetProperty("version").GetInt32());
        Assert.Equal("after launch", FirstText(page.GetProperty("body")));
        Assert.Equal("go live", (await admin.GetJsonAsync($"api/v1/content/{id}/versions")).EnumerateArray().First().GetProperty("changeSummary").GetString());

        // Cancelled schedules are not executed.
        await EditDraftAsync(member, id, "Launch", "third");
        await PublishAsync(member, id, null, DateTimeOffset.UtcNow.AddHours(1));
        var when = (await ScheduledAtAsync(tenant, id))!.Value;
        using (var cancel = await member.DeleteAsync($"api/v1/content/{id}/schedule"))
        {
            await ReadAsync(cancel);
        }

        await RunScheduledAsync(tenant, new ScheduledPublishArgs(id, when, memberId));
        Assert.Equal(2, (await admin.GetJsonAsync($"api/v1/content/{id}")).GetProperty("version").GetInt32());

        // Rights are evaluated when the job runs: a member who lost access cannot publish.
        await PublishAsync(member, id, null, DateTimeOffset.UtcNow.AddHours(1));
        when = (await ScheduledAtAsync(tenant, id))!.Value;
        using (var remove = await admin.DeleteAsync($"api/v1/tenant/members/{memberId}"))
        {
            Assert.True(remove.IsSuccessStatusCode);
        }

        await RunScheduledAsync(tenant, new ScheduledPublishArgs(id, when, memberId));
        Assert.Equal(2, (await admin.GetJsonAsync($"api/v1/content/{id}")).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Trash_lists_restores_and_purges_whole_subtrees_for_space_administrators()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var parent = Id(await CreatePageAsync(admin, key, "Old area"));
        var child = Id(await CreatePageAsync(admin, key, "Old child", parent));

        using (var trash = await ConditionalAsync(admin, HttpMethod.Delete, $"api/v1/content/{parent}"))
        {
            await Browser.AssertStatusAsync(trash, HttpStatusCode.NoContent);
        }

        var items = (await admin.GetJsonAsync($"api/v1/spaces/{key}/trash")).EnumerateArray().ToList();
        Assert.Equal(parent, Id(items.Single()));
        Assert.Equal(1, items[0].GetProperty("descendants").GetInt32());
        using (var memberTrash = await member.GetAsync($"api/v1/spaces/{key}/trash"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, memberTrash.StatusCode);
        }

        using (var restore = await admin.PostAsync($"api/v1/content/{parent}/restore-from-trash"))
        {
            Assert.Equal("Current", (await ReadAsync(restore)).GetProperty("status").GetString());
        }

        Assert.Equal("Old child", (await member.GetJsonAsync($"api/v1/content/{child}")).GetProperty("title").GetString());

        using (var trashAgain = await ConditionalAsync(admin, HttpMethod.Delete, $"api/v1/content/{parent}"))
        {
            await Browser.AssertStatusAsync(trashAgain, HttpStatusCode.NoContent);
        }

        using (var purge = await admin.DeleteAsync($"api/v1/content/{parent}/purge"))
        {
            await Browser.AssertStatusAsync(purge, HttpStatusCode.NoContent);
        }

        Assert.Empty((await admin.GetJsonAsync($"api/v1/spaces/{key}/trash")).EnumerateArray());
        await using var scope = Scope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        Assert.False(await db.Nodes.AnyAsync(n => n.Id == parent || n.Id == child));
        Assert.False(await db.PageVersions.AnyAsync(v => v.ContentId == parent || v.ContentId == child));
    }

    [Fact]
    public async Task Publishing_is_a_separate_right()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var id = Id(await CreatePageAsync(admin, key, "Guarded"));
        using (var put = await admin.PutAsync($"api/v1/spaces/{key}/permissions", new
        {
            grants = new object[]
            {
                new { subjectType = "User", subjectId = (Guid?)adminId, action = "ManagePermissions", effect = "Allow" },
                new { subjectType = "User", subjectId = (Guid?)adminId, action = "View", effect = "Allow" },
                new { subjectType = "AllMembers", subjectId = (Guid?)null, action = "View", effect = "Allow" },
                new { subjectType = "AllMembers", subjectId = (Guid?)null, action = "Edit", effect = "Allow" },
            },
        }))
        {
            await ReadAsync(put);
        }

        await EditDraftAsync(member, id, "Guarded", "member proposal"); // can edit the draft …
        var (_, etag) = await GetDraftAsync(member, id);
        using var publish = await member.SendAsync(HttpMethod.Post, $"api/v1/content/{id}/publish", new { changeSummary = (string?)null }, withXsrf: true, ifMatch: etag);
        await Browser.AssertStatusAsync(publish, HttpStatusCode.Forbidden); // … but not publish it
        Assert.False((await member.GetJsonAsync($"api/v1/content/{id}")).GetProperty("rights").GetProperty("canPublish").GetBoolean());
    }

    private AsyncServiceScope Scope(IdentityTestKit.TestTenant tenant) =>
        fx.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(new ResolvedTenant(tenant.Id, tenant.Code, tenant.Slug, TenantResolutionSource.BackgroundJob, null));

    private async Task RunScheduledAsync(IdentityTestKit.TestTenant tenant, ScheduledPublishArgs args)
    {
        await using var scope = Scope(tenant);
        await scope.ServiceProvider.GetRequiredService<ScheduledPublishJob>().ExecuteAsync(args, CancellationToken.None);
    }

    private async Task<DateTimeOffset?> ScheduledAtAsync(IdentityTestKit.TestTenant tenant, Guid id)
    {
        await using var scope = Scope(tenant);
        return await scope.ServiceProvider.GetRequiredService<WikiDbContext>().Drafts.Where(d => d.ContentId == id).Select(d => d.ScheduledPublishAt).FirstOrDefaultAsync();
    }
}
