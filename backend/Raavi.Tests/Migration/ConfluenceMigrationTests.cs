using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Tenancy;
using Raavi.Application.Abstractions.Content;
using Raavi.Migration.Confluence;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Migration;

/// <summary>Phase 8 acceptance: a Confluence source is migrated with spaces, pages, all versions, authors, timestamps, labels and links; re-running creates no duplicates.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class ConfluenceMigrationTests(SqlServerFixture fx)
{
    private const string Token = "tok-very-secret-123";
    private const string Url = "https://wiki.example.test/confluence";
    private readonly IdentityTestKit _identity = new(fx);

    private static async Task<JsonElement> CreateJobAsync(Browser admin, bool dryRun)
    {
        using var r = await admin.PostAsync("api/v1/migrations/confluence", new { url = Url, token = Token, dryRun, spaces = (string[]?)null, keyMap = (object?)null });
        return await ReadAsync(r);
    }

    /// <summary>Runs the queued job the way the worker would: inside the tenant's scope (the API host shares the Data Protection keys).</summary>
    private async Task RunAsync(IdentityTestKit.TestTenant tenant, Guid jobId)
    {
        var scope = fx.Api.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(new ResolvedTenant(tenant.Id, tenant.Code, tenant.Slug, TenantResolutionSource.BackgroundJob, null));
        await using (scope.ConfigureAwait(false))
        {
            var job = ActivatorUtilities.GetServiceOrCreateInstance<ConfluenceMigrationJob>(scope.ServiceProvider);
            await job.ExecuteAsync(new MigrationJobArgs(jobId), CancellationToken.None);
        }
    }

    private static async Task<JsonElement> StartAsync(Browser admin, Guid id, string? token = null)
    {
        using var r = await admin.PostAsync($"api/v1/migrations/confluence/{id}/start", token is null ? new { } : new { token });
        return await ReadAsync(r);
    }

    private static Task<JsonElement> JobAsync(Browser admin, Guid id) => admin.GetJsonAsync($"api/v1/migrations/confluence/{id}");

    private static async Task<JsonElement> RunToEndAsync(ConfluenceMigrationTests t, IdentityTestKit.TestTenant tenant, Browser admin, bool dryRun)
    {
        var id = Id(await CreateJobAsync(admin, dryRun));
        await StartAsync(admin, id);
        await t.RunAsync(tenant, id);
        return await JobAsync(admin, id);
    }

    private static IEnumerable<JsonElement> Walk(JsonElement n)
    {
        yield return n;
        if (n.TryGetProperty("content", out var c))
        {
            foreach (var child in c.EnumerateArray())
            {
                foreach (var d in Walk(child))
                {
                    yield return d;
                }
            }
        }
    }

    [Fact]
    public async Task A_dry_run_reads_and_reports_but_writes_nothing_to_the_workspace()
    {
        fx.Confluence.Seed();
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();

        var created = await CreateJobAsync(admin, dryRun: true);
        Assert.Equal("DryRun", created.GetProperty("mode").GetString());
        Assert.Equal("Created", created.GetProperty("status").GetString());
        Assert.DoesNotContain(Token, created.ToString(), StringComparison.Ordinal);

        var id = Id(created);
        await StartAsync(admin, id);
        await RunAsync(tenant, id);
        var job = await JobAsync(admin, id);

        Assert.Equal("Completed", job.GetProperty("status").GetString());
        Assert.Equal((2, 4, 4, 0), (job.GetProperty("spacesDone").GetInt32(), job.GetProperty("pagesTotal").GetInt32(), job.GetProperty("pagesDone").GetInt32(), job.GetProperty("pagesFailed").GetInt32()));
        Assert.Equal(7, job.GetProperty("versionsDone").GetInt32());
        Assert.Equal("10.2.13 (build 1)", job.GetProperty("sourceVersion").GetString());

        var report = await admin.GetJsonAsync($"api/v1/migrations/confluence/{id}/report");
        Assert.Equal(1, report.GetProperty("unsupportedMacros").GetProperty("contentbylabel").GetInt32());
        Assert.Equal<string?>(["Ali (ali)", "Sara (sara)"], report.GetProperty("unmappedAuthors").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(3, report.GetProperty("pageLinks").GetInt32());
        Assert.Equal(2, report.GetProperty("spaces").GetArrayLength());

        // Nothing was written: no spaces, and the (hidden) job token is gone.
        Assert.Empty((await admin.GetJsonAsync("api/v1/spaces")).EnumerateArray());
        Assert.DoesNotContain(Token, job.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execution_imports_hierarchy_every_version_authors_times_labels_and_resolves_links_without_widening_access()
    {
        fx.Confluence.Seed();
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();
        var (member, _, _) = await _identity.AddMemberAsync(tenant, admin);

        var job = await RunToEndAsync(this, tenant, admin, dryRun: false);
        Assert.Equal("Completed", job.GetProperty("status").GetString());
        var jobId = Id(job);

        var spaces = (await admin.GetJsonAsync("api/v1/spaces")).EnumerateArray().Select(s => s.GetProperty("key").GetString()!).Order().ToArray();
        Assert.Equal(["ENG", "OPS"], spaces);

        // Hierarchy (children were delivered before their parents): Home > Guide > Child.
        var eng = await admin.GetJsonAsync("api/v1/spaces/ENG");
        Assert.Equal("Engineering", eng.GetProperty("name").GetString());
        var homeId = eng.GetProperty("homePageId").GetGuid();
        var top = (await admin.GetJsonAsync("api/v1/spaces/ENG/tree")).EnumerateArray().ToArray();
        Assert.Equal(["Home"], top.Select(n => n.GetProperty("title").GetString()));
        Assert.Equal(homeId, Id(top[0]));
        var guide = (await admin.GetJsonAsync($"api/v1/spaces/ENG/tree?parentId={homeId}")).EnumerateArray().Single();
        var child = (await admin.GetJsonAsync($"api/v1/spaces/ENG/tree?parentId={Id(guide)}")).EnumerateArray().Single();
        Assert.Equal(("Guide", "Child"), (guide.GetProperty("title").GetString(), child.GetProperty("title").GetString()));

        // History: all versions, oldest = 1, original timestamps and comments, type Import, authors kept as source identities.
        var homeVersions = (await admin.GetJsonAsync($"api/v1/content/{homeId}/versions")).EnumerateArray().ToArray(); // newest first
        Assert.Equal([3, 2, 1], homeVersions.Select(v => v.GetProperty("version").GetInt32()));
        Assert.All(homeVersions, v => Assert.Equal("Import", v.GetProperty("type").GetString()));
        Assert.Equal<string?>(["final", null, "created"], homeVersions.Select(v => v.GetProperty("changeSummary").GetString()));
        Assert.Equal(new DateTimeOffset(2023, 3, 1, 9, 0, 0, TimeSpan.Zero), homeVersions[2].GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2023, 3, 21, 9, 0, 0, TimeSpan.Zero), homeVersions[0].GetProperty("createdAt").GetDateTimeOffset());
        var home = await admin.GetJsonAsync($"api/v1/content/{homeId}");
        Assert.Equal(3, home.GetProperty("version").GetInt32());
        Assert.Equal(new DateTimeOffset(2023, 3, 1, 9, 0, 0, TimeSpan.Zero), home.GetProperty("createdAt").GetDateTimeOffset());

        // Links: "Guide" resolved to the Raavi page; the dead "Missing" link was removed but its text kept.
        var guidePage = await admin.GetJsonAsync($"api/v1/content/{Id(guide)}");
        var hrefs = Walk(guidePage.GetProperty("body").GetProperty("doc")).Where(n => n.TryGetProperty("marks", out _))
            .SelectMany(n => n.GetProperty("marks").EnumerateArray()).Select(m => m.GetProperty("attrs").GetProperty("href").GetString()!).ToArray();
        Assert.Equal([$"/spaces/ENG/pages/{homeId:D}"], hrefs);
        Assert.Contains("Missing", guidePage.GetProperty("body").ToString(), StringComparison.Ordinal);
        Assert.Contains(Walk(home.GetProperty("body").GetProperty("doc")).SelectMany(n => n.TryGetProperty("marks", out var m) ? m.EnumerateArray() : []),
            m => m.GetProperty("attrs").GetProperty("href").GetString() == $"/spaces/ENG/pages/{Id(guide):D}");
        Assert.Equal(["howto"], guidePage.GetProperty("labels").EnumerateArray().Select(l => l.GetString())); // "bad label!" is invalid in Raavi

        // Unsupported macro: kept as an unsupported node (escaped source), reported as a warning.
        var childPage = await admin.GetJsonAsync($"api/v1/content/{Id(child)}");
        Assert.Contains(Walk(childPage.GetProperty("body").GetProperty("doc")), n => n.TryGetProperty("type", out var t) && t.GetString() == "unsupported");

        // Never widen access: only the administrator who imported can see the spaces.
        using (var denied = await member.GetAsync("api/v1/spaces/ENG"))
        {
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }

        Assert.Empty((await member.GetJsonAsync("api/v1/spaces")).EnumerateArray());

        // The report: every page and version accounted for; link statistics; no validation issues; warnings recorded.
        var report = await admin.GetJsonAsync($"api/v1/migrations/confluence/{jobId}/report");
        Assert.Equal((2, 1), (report.GetProperty("linksResolved").GetInt32(), report.GetProperty("linksUnresolved").GetInt32()));
        Assert.Empty(report.GetProperty("validation").EnumerateArray());
        var events = await admin.GetJsonAsync($"api/v1/migrations/confluence/{jobId}/events");
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("code").GetString() == "link.unresolved");
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("code").GetString() == "macro.unsupported");
        Assert.DoesNotContain(Token, events.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Re_running_an_import_creates_no_duplicates_and_only_downloads_missing_versions()
    {
        fx.Confluence.Seed();
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();
        var first = await RunToEndAsync(this, tenant, admin, dryRun: false);
        Assert.Equal(7, first.GetProperty("versionsDone").GetInt32());
        var homeId = (await admin.GetJsonAsync("api/v1/spaces/ENG")).GetProperty("homePageId").GetGuid();

        // The source gains one new version of "Home" and nothing else changes.
        fx.Confluence.VersionDownloads.Clear();
        var home = fx.Confluence.Pages["eng"].First(p => p.Page.SourceContentId == "100");
        var last = home.Versions[^1];
        home.Versions[^1] = last with { IsCurrent = false };
        home.Versions.Add(last with { SourceVersionNumber = 4, Title = "Home renamed", StorageFormatBody = "<p>v4</p>", VersionComment = "again", IsCurrent = true, CreatedAtUtc = last.CreatedAtUtc.AddDays(1) });
        var idx = fx.Confluence.Pages["eng"].IndexOf(home);
        fx.Confluence.Pages["eng"][idx] = home with { Page = home.Page with { Title = "Home renamed", CurrentVersionNumber = 4 } };

        var second = await RunToEndAsync(this, tenant, admin, dryRun: false);
        Assert.Equal("Completed", second.GetProperty("status").GetString());
        Assert.Equal(["100:4"], fx.Confluence.VersionDownloads); // nothing already imported was downloaded again
        Assert.Equal(1, second.GetProperty("versionsDone").GetInt32());

        var versions = (await admin.GetJsonAsync($"api/v1/content/{homeId}/versions")).EnumerateArray().ToArray();
        Assert.Equal([4, 3, 2, 1], versions.Select(v => v.GetProperty("version").GetInt32()));
        Assert.Equal("Home renamed", (await admin.GetJsonAsync($"api/v1/content/{homeId}")).GetProperty("title").GetString());
        Assert.Equal(["ENG", "OPS"], (await admin.GetJsonAsync("api/v1/spaces")).EnumerateArray().Select(s => s.GetProperty("key").GetString()).Order()); // no duplicate spaces
        Assert.Equal(["Home renamed"], (await admin.GetJsonAsync("api/v1/spaces/ENG/tree")).EnumerateArray().Select(n => n.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task A_pause_request_stops_the_job_between_pages_and_starting_again_resumes_it()
    {
        fx.Confluence.Seed();
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();
        var id = Id(await CreateJobAsync(admin, dryRun: false));
        await StartAsync(admin, id);
        using (var pause = await admin.PostAsync($"api/v1/migrations/confluence/{id}/pause"))
        {
            Assert.Equal("Pause", (await ReadAsync(pause)).GetProperty("control").GetString());
        }

        await RunAsync(tenant, id);
        var paused = await JobAsync(admin, id);
        Assert.Equal("Paused", paused.GetProperty("status").GetString());
        Assert.True(paused.GetProperty("canResume").GetBoolean());
        Assert.Equal(0, paused.GetProperty("pagesDone").GetInt32());

        // Resume: the token is kept for a paused job, so no new token is needed.
        await StartAsync(admin, id);
        await RunAsync(tenant, id);
        var done = await JobAsync(admin, id);
        Assert.Equal(("Completed", 4), (done.GetProperty("status").GetString(), done.GetProperty("pagesDone").GetInt32()));

        // A finished job cannot be paused, and a cancelled one cannot be cancelled again.
        using var late = await admin.PostAsync($"api/v1/migrations/confluence/{id}/pause");
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task One_failing_page_does_not_stop_the_rest_and_a_key_conflict_never_touches_existing_spaces()
    {
        fx.Confluence.Seed();
        fx.Confluence.BrokenPages.Add("101"); // "Guide" cannot be downloaded
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();
        using (var native = await admin.PostAsync("api/v1/spaces", new { key = "OPS", name = "Existing native space", type = "Team", visibility = "Open" }))
        {
            await ReadAsync(native);
        }

        var job = await RunToEndAsync(this, tenant, admin, dryRun: false);
        Assert.Equal("CompletedWithErrors", job.GetProperty("status").GetString());
                // Guide (download failed) + Child (its parent is missing) + Runbook (key conflict); Home was imported.
        Assert.Equal((3, 1), (job.GetProperty("pagesFailed").GetInt32(), job.GetProperty("pagesDone").GetInt32()));
        var events = (await admin.GetJsonAsync($"api/v1/migrations/confluence/{Id(job)}/events?level=Error")).EnumerateArray().ToArray();
        Assert.Contains(events, e => e.GetProperty("code").GetString() == "space.key_conflict");
        Assert.Contains(events, e => e.GetProperty("code").GetString() == "source.error");

        // The pre-existing native space is untouched; the other pages imported.
        Assert.Equal("Existing native space", (await admin.GetJsonAsync("api/v1/spaces/OPS")).GetProperty("name").GetString());
        Assert.DoesNotContain((await admin.GetJsonAsync("api/v1/spaces/OPS/tree")).EnumerateArray(), n => n.GetProperty("title").GetString() == "Runbook");
        var eng = await admin.GetJsonAsync("api/v1/spaces/ENG");
        var homeId = eng.GetProperty("homePageId").GetGuid();
        Assert.DoesNotContain((await admin.GetJsonAsync($"api/v1/spaces/ENG/tree?parentId={homeId}")).EnumerateArray(), n => n.GetProperty("title").GetString() == "Guide");
        Assert.Equal(3, (await admin.GetJsonAsync($"api/v1/content/{homeId}/versions")).GetArrayLength());
    }

    [Fact]
    public async Task Only_tenant_administrators_may_migrate_sources_must_be_allow_listed_and_tokens_are_checked()
    {
        fx.Confluence.Seed();
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();
        var (member, _, _) = await _identity.AddMemberAsync(tenant, admin);

        using (var forbidden = await member.GetAsync("api/v1/migrations/confluence"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var create = await member.PostAsync("api/v1/migrations/confluence", new { url = Url, token = Token }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        using (var other = await admin.PostAsync("api/v1/migrations/confluence", new { url = "https://evil.example/", token = Token }))
        {
            await Browser.AssertStatusAsync(other, HttpStatusCode.BadRequest, "source.host_not_allowed");
        }

        using (var local = await admin.PostAsync("api/v1/migrations/confluence", new { url = "http://169.254.169.254/", token = Token }))
        {
            await Browser.AssertStatusAsync(local, HttpStatusCode.BadRequest);
        }

        using (var noToken = await admin.PostAsync("api/v1/migrations/confluence", new { url = Url }))
        {
            await Browser.AssertStatusAsync(noToken, HttpStatusCode.BadRequest, "migration.token_required");
        }

        fx.Confluence.ExpectedToken = "a-different-token";
        using (var rejected = await admin.PostAsync("api/v1/migrations/confluence", new { url = Url, token = Token }))
        {
            await Browser.AssertStatusAsync(rejected, HttpStatusCode.BadRequest, "source.unauthorized");
        }

        // Jobs are tenant-private: another workspace's administrator cannot see or touch them.
        fx.Confluence.ExpectedToken = null;
        var id = Id(await CreateJobAsync(admin, dryRun: true));
        var (_, otherAdmin, _) = await _identity.TenantWithAdminAsync();
        Assert.Empty((await otherAdmin.GetJsonAsync("api/v1/migrations/confluence")).EnumerateArray());
        using var peek = await otherAdmin.GetAsync($"api/v1/migrations/confluence/{id}");
        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
    }

    private static string System(string url = Url) => SourceKeys.System(SourceKeys.Of(new Uri(url.TrimEnd('/') + "/")));

    private static async Task<JsonElement> RunWithAsync(ConfluenceMigrationTests t, IdentityTestKit.TestTenant tenant, Browser admin, bool reconvert)
    {
        using var created = await admin.PostAsync("api/v1/migrations/confluence", new { url = Url, token = Token, dryRun = false, reconvert });
        var id = Id(await ReadAsync(created));
        await StartAsync(admin, id);
        await t.RunAsync(tenant, id);
        return await JobAsync(admin, id);
    }

    [Fact]
    public async Task Attachments_are_imported_images_point_at_them_and_oversized_or_foreign_ones_are_reported()
    {
        fx.Confluence.Seed();
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();
        var job = await RunToEndAsync(this, tenant, admin, dryRun: false);
        Assert.Equal("Completed", job.GetProperty("status").GetString());

        var eng = await admin.GetJsonAsync("api/v1/spaces/ENG");
        var home = eng.GetProperty("homePageId").GetGuid();
        var guide = Id((await admin.GetJsonAsync($"api/v1/spaces/ENG/tree?parentId={home}")).EnumerateArray().Single());

        // Files: stored with the page; the 50 MB one is left out (product limit), nothing else.
        var files = (await admin.GetJsonAsync($"api/v1/content/{guide}/attachments")).EnumerateArray().ToArray();
        var diagram = Assert.Single(files);
        Assert.Equal(("diagram.png", "image/png", true), (diagram.GetProperty("fileName").GetString(), diagram.GetProperty("mediaType").GetString(), diagram.GetProperty("isImage").GetBoolean()));
        Assert.Equal(ImportIds.Attachment(tenant.Id, System(), "101", "diagram.png"), Id(diagram));
        Assert.Equal("logo.png", (await admin.GetJsonAsync($"api/v1/content/{home}/attachments")).EnumerateArray().Single().GetProperty("fileName").GetString());
        using (var download = await admin.GetAsync($"api/v1/attachments/{Id(diagram)}"))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(FakeConfluence.Png().Length, (await download.Content.ReadAsByteArrayAsync()).Length);
        }

        // The image in the page points at its attachment; an image showing another page's file is kept without a file.
        var images = Walk((await admin.GetJsonAsync($"api/v1/content/{guide}")).GetProperty("body").GetProperty("doc")).Where(n => n.TryGetProperty("type", out var t) && t.GetString() == "image").ToArray();
        Assert.Equal(2, images.Length);
        Assert.Equal(Id(diagram).ToString("D"), images[0].GetProperty("attrs").GetProperty("attachmentId").GetString());
        Assert.Equal("Architecture", images[0].GetProperty("attrs").GetProperty("alt").GetString());
        Assert.Equal(JsonValueKind.Null, images[1].GetProperty("attrs").GetProperty("attachmentId").ValueKind);

        var report = await admin.GetJsonAsync($"api/v1/migrations/confluence/{Id(job)}/report");
        var a = report.GetProperty("attachments");
        Assert.Equal((3, 2, 1, 0), (a.GetProperty("found").GetInt32(), a.GetProperty("imported").GetInt32(), a.GetProperty("tooLarge").GetInt32(), a.GetProperty("failed").GetInt32()));
        Assert.Equal(1, report.GetProperty("warningsByCode").GetProperty("image.other_page").GetInt32());
        var events = await admin.GetJsonAsync($"api/v1/migrations/confluence/{Id(job)}/events");
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("code").GetString() == "attachment.too_large" && e.GetProperty("sourceRef").GetString()!.Contains("huge.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_download_is_recorded_without_losing_the_page_and_a_re_run_fetches_only_what_is_missing()
    {
        fx.Confluence.Seed();
        fx.Confluence.BrokenAttachments.Add("diagram.png");
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();

        var first = await RunToEndAsync(this, tenant, admin, dryRun: false);
        Assert.Equal("CompletedWithErrors", first.GetProperty("status").GetString());
        Assert.Equal(0, first.GetProperty("pagesFailed").GetInt32()); // the page itself was imported
        var eng = await admin.GetJsonAsync("api/v1/spaces/ENG");
        var home = eng.GetProperty("homePageId").GetGuid();
        var guide = Id((await admin.GetJsonAsync($"api/v1/spaces/ENG/tree?parentId={home}")).EnumerateArray().Single());
        Assert.Empty((await admin.GetJsonAsync($"api/v1/content/{guide}/attachments")).EnumerateArray());
        Assert.Contains(fx.Confluence.AttachmentDownloads, f => f == "logo.png");

        fx.Confluence.BrokenAttachments.Clear();
        fx.Confluence.AttachmentDownloads.Clear();
        var second = await RunToEndAsync(this, tenant, admin, dryRun: false);
        Assert.Equal("Completed", second.GetProperty("status").GetString());
        Assert.Equal(["diagram.png"], fx.Confluence.AttachmentDownloads); // logo.png was already stored
        Assert.Single((await admin.GetJsonAsync($"api/v1/content/{guide}/attachments")).EnumerateArray());

        fx.Confluence.AttachmentDownloads.Clear();
        await RunToEndAsync(this, tenant, admin, dryRun: false);
        Assert.Empty(fx.Confluence.AttachmentDownloads); // nothing left to fetch
    }

    [Fact]
    public async Task Files_the_source_changes_or_misreports_are_refreshed_by_reconvert_and_flagged_when_sizes_disagree()
    {
        fx.Confluence.Seed();
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();
        await RunToEndAsync(this, tenant, admin, dryRun: false);
        var home = (await admin.GetJsonAsync("api/v1/spaces/ENG")).GetProperty("homePageId").GetGuid();
        var guide = Id((await admin.GetJsonAsync($"api/v1/spaces/ENG/tree?parentId={home}")).EnumerateArray().Single());
        var before = (await admin.GetJsonAsync($"api/v1/content/{guide}/attachments")).EnumerateArray().Single();
        Assert.Equal(FakeConfluence.Png().Length, before.GetProperty("size").GetInt32());

        // The source now delivers different bytes for diagram.png and (separately) misreports another file's size.
        var files = fx.Confluence.Attachments["101"];
        var diagram = files.First(f => f.Meta.FileName == "diagram.png");
        var newer = FakeConfluence.Png(200);
        files[files.IndexOf(diagram)] = (diagram.Meta with { Size = newer.Length }, newer);
        fx.Confluence.AddAttachment("100", "photo.png", FakeConfluence.Png(30), declaredSize: 7);

        var plain = await RunToEndAsync(this, tenant, admin, dryRun: false); // no re-conversion: stored files are left alone
        Assert.Equal(FakeConfluence.Png().Length, (await admin.GetJsonAsync($"api/v1/content/{guide}/attachments")).EnumerateArray().Single().GetProperty("size").GetInt32());
        Assert.Equal(0, (await admin.GetJsonAsync($"api/v1/migrations/confluence/{Id(plain)}/report")).GetProperty("attachments").GetProperty("replaced").GetInt32());

        var job = await RunWithAsync(this, tenant, admin, reconvert: true);
        Assert.Equal("Completed", job.GetProperty("status").GetString());
        Assert.Equal(newer.Length, (await admin.GetJsonAsync($"api/v1/content/{guide}/attachments")).EnumerateArray().Single().GetProperty("size").GetInt32());
        var report = (await admin.GetJsonAsync($"api/v1/migrations/confluence/{Id(job)}/report")).GetProperty("attachments");
        Assert.Equal((1, 1), (report.GetProperty("replaced").GetInt32(), report.GetProperty("sizeMismatch").GetInt32()));
        var events = await admin.GetJsonAsync($"api/v1/migrations/confluence/{Id(job)}/events");
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("code").GetString() == "attachment.size_mismatch" && e.GetProperty("sourceRef").GetString()!.Contains("photo.png", StringComparison.Ordinal));
        Assert.Equal(3, (await admin.GetJsonAsync($"api/v1/content/{home}/attachments")).GetArrayLength() + (await admin.GetJsonAsync($"api/v1/content/{guide}/attachments")).GetArrayLength());
    }

    [Fact]
    public async Task Reconvert_refreshes_already_imported_versions_in_place_and_is_otherwise_a_no_op()
    {
        fx.Confluence.Seed();
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();
        await RunToEndAsync(this, tenant, admin, dryRun: false);
        var home = (await admin.GetJsonAsync("api/v1/spaces/ENG")).GetProperty("homePageId").GetGuid();

        // The source body of version 1 is "better converted" now (simulated by a changed storage body).
        var page = fx.Confluence.Pages["eng"].First(p => p.Page.SourceContentId == "100");
        page.Versions[0] = page.Versions[0] with { StorageFormatBody = "<h1>Welcome again</h1>" };

        await RunToEndAsync(this, tenant, admin, dryRun: false); // normal re-run: already imported versions are not looked at
        var plain = await admin.GetJsonAsync($"api/v1/content/{home}/versions/1");
        Assert.Contains("Welcome", plain.GetProperty("body").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("again", plain.GetProperty("body").ToString(), StringComparison.Ordinal);

        var job = await RunWithAsync(this, tenant, admin, reconvert: true);
        Assert.Equal("Completed", job.GetProperty("status").GetString());
        Assert.Contains("Welcome again", (await admin.GetJsonAsync($"api/v1/content/{home}/versions/1")).GetProperty("body").ToString(), StringComparison.Ordinal);
        var versions = (await admin.GetJsonAsync($"api/v1/content/{home}/versions")).EnumerateArray().ToArray();
        Assert.Equal([3, 2, 1], versions.Select(v => v.GetProperty("version").GetInt32())); // no new versions, history intact
        Assert.Equal(new DateTimeOffset(2023, 3, 1, 9, 0, 0, TimeSpan.Zero), versions[2].GetProperty("createdAt").GetDateTimeOffset());
    }

    private static async Task<JsonElement> RunMappedAsync(ConfluenceMigrationTests t, IdentityTestKit.TestTenant tenant, Browser admin, bool dryRun, bool mapByEmail, bool reconvert = false)
    {
        using var created = await admin.PostAsync("api/v1/migrations/confluence", new { url = Url, token = Token, dryRun, reconvert, mapUsersByEmail = mapByEmail });
        var id = Id(await ReadAsync(created));
        await StartAsync(admin, id);
        await t.RunAsync(tenant, id);
        return await JobAsync(admin, id);
    }

    private static async Task<Guid> MeAsync(Browser b) => (await b.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();

    [Fact]
    public async Task Source_users_are_only_suggested_until_confirmed_and_groups_map_by_name_without_ever_widening_restrictions()
    {
        fx.Confluence.Seed();
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();
        var (sara, saraEmail, saraId) = await _identity.AddMemberAsync(tenant, admin);
        var (_, aliEmail, aliId) = await _identity.AddMemberAsync(tenant, admin);
        var adminId = await MeAsync(admin);
        using (var g = await admin.PostAsync("api/v1/tenant/groups", new { name = "Developers", description = (string?)null }))
        {
            var group = await ReadAsync(g);
            fx.Confluence.Users["sara"] = new SourceUser("sara", "sara", "Sara Source", saraEmail);
            fx.Confluence.Users["ali"] = new SourceUser("ali", "ali", "Ali Source", aliEmail);
            fx.Confluence.Users["ghost"] = new SourceUser("ghost", "ghost", "Ghost", "ghost@nowhere.example");
            fx.Confluence.Restrictions["101"] = new SourceRestrictions(
                [new SourceSubject(SourceSubjectKind.User, "sara", "Sara Source"), new SourceSubject(SourceSubjectKind.User, "ghost", "Ghost"), new SourceSubject(SourceSubjectKind.Group, "developers", "developers")],
                [new SourceSubject(SourceSubjectKind.Group, "no-such-group", "no-such-group")]);

            // 1) Dry run: suggestions only — nothing applied, nothing written.
            var dry = await RunMappedAsync(this, tenant, admin, dryRun: true, mapByEmail: false);
            Assert.Equal("Completed", dry.GetProperty("status").GetString());
            var report = await admin.GetJsonAsync($"api/v1/migrations/confluence/{Id(dry)}/report");
            var suggestions = report.GetProperty("authorMappings").EnumerateArray().ToDictionary(a => a.GetProperty("sourceKey").GetString()!);
            Assert.Equal(("Member", false), (suggestions["sara"].GetProperty("suggestedUser").GetString(), suggestions["sara"].GetProperty("applied").GetBoolean()));
            Assert.Equal(JsonValueKind.Null, suggestions["ghost"].GetProperty("suggestedUser").ValueKind);
            Assert.Equal(1, report.GetProperty("access").GetProperty("restrictedPages").GetInt32());
            Assert.Empty((await admin.GetJsonAsync("api/v1/spaces")).EnumerateArray());

            // 2) Import without confirming user mapping: authors stay source identities; the group maps by name; unmapped subjects are left out.
            var run = await RunMappedAsync(this, tenant, admin, dryRun: false, mapByEmail: false);
            Assert.Equal("Completed", run.GetProperty("status").GetString());
            var home = (await admin.GetJsonAsync("api/v1/spaces/ENG")).GetProperty("homePageId").GetGuid();
            var guide = Id((await admin.GetJsonAsync($"api/v1/spaces/ENG/tree?parentId={home}")).EnumerateArray().Single());
            Assert.All((await admin.GetJsonAsync($"api/v1/content/{home}/versions")).EnumerateArray(), v => Assert.Equal(JsonValueKind.Null, v.GetProperty("createdBy").ValueKind));
            var restrictions = await admin.GetJsonAsync($"api/v1/content/{guide}/restrictions");
            var view = restrictions.GetProperty("view").EnumerateArray().Select(s => s.GetProperty("subjectId").GetGuid()).ToHashSet();
            Assert.Equal(new HashSet<Guid> { adminId, Id(group) }, view);          // owner + mapped group; sara/ghost not applied
            Assert.Equal(new HashSet<Guid> { adminId }, restrictions.GetProperty("edit").EnumerateArray().Select(s => s.GetProperty("subjectId").GetGuid()).ToHashSet()); // unknown group → owner only
            var access = (await admin.GetJsonAsync($"api/v1/migrations/confluence/{Id(run)}/report")).GetProperty("access");
            Assert.Equal(1, access.GetProperty("restrictionsApplied").GetInt32());
            Assert.Contains(access.GetProperty("unmappedSubjects").EnumerateArray().Select(u => u.GetString()), u => u == "user: Ghost");
            Assert.Contains(access.GetProperty("unmappedSubjects").EnumerateArray().Select(u => u.GetString()), u => u == "user: Sara Source");
            Assert.Equal("not_imported", access.GetProperty("spacePermissions").GetString());

            // 3) The administrator confirms the e-mail matches: existing versions get their authors (re-convert), restrictions already set stay as they are.
            var confirmed = await RunMappedAsync(this, tenant, admin, dryRun: false, mapByEmail: true, reconvert: true);
            Assert.Equal("Completed", confirmed.GetProperty("status").GetString());
            var versions = (await admin.GetJsonAsync($"api/v1/content/{home}/versions")).EnumerateArray().ToDictionary(v => v.GetProperty("version").GetInt32());
            Assert.Equal(saraId, versions[1].GetProperty("createdBy").GetGuid()); // v1 by sara
            Assert.Equal(aliId, versions[2].GetProperty("createdBy").GetGuid());  // v2 by ali
            Assert.Equal(JsonValueKind.Null, versions[3].GetProperty("createdBy").ValueKind); // v3 had no author in the source
            var after = await admin.GetJsonAsync($"api/v1/content/{guide}/restrictions");
            Assert.Equal(view, after.GetProperty("view").EnumerateArray().Select(s => s.GetProperty("subjectId").GetGuid()).ToHashSet()); // not overwritten
            _ = sara;
        }
    }

    [Fact]
    public async Task Confirmed_mapping_restricts_pages_to_the_mapped_people_and_nobody_else_can_open_them()
    {
        fx.Confluence.Seed();
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();
        var (sara, saraEmail, saraId) = await _identity.AddMemberAsync(tenant, admin);
        var (outsider, _, _) = await _identity.AddMemberAsync(tenant, admin);
        var adminId = await MeAsync(admin);
        fx.Confluence.Users["sara"] = new SourceUser("sara", "sara", "Sara Source", saraEmail);
        fx.Confluence.Restrictions["101"] = new SourceRestrictions([new SourceSubject(SourceSubjectKind.User, "sara", "Sara Source")], []);

        var job = await RunMappedAsync(this, tenant, admin, dryRun: false, mapByEmail: true);
        Assert.Equal("Completed", job.GetProperty("status").GetString());
        var home = (await admin.GetJsonAsync("api/v1/spaces/ENG")).GetProperty("homePageId").GetGuid();
        var guide = Id((await admin.GetJsonAsync($"api/v1/spaces/ENG/tree?parentId={home}")).EnumerateArray().Single());
        Assert.Equal(new HashSet<Guid> { adminId, saraId }, (await admin.GetJsonAsync($"api/v1/content/{guide}/restrictions")).GetProperty("view").EnumerateArray().Select(s => s.GetProperty("subjectId").GetGuid()).ToHashSet());

        // The administrator later opens the space to every member (a deliberate act); the page restriction still holds.
        var grants = new[] { "View", "Create", "Edit", "Comment" }.Select(a => new { subjectType = "AllMembers", subjectId = (Guid?)null, action = a, effect = "Allow" }).Cast<object>()
            .Concat(new[] {"View", "ManagePermissions", "Edit", "Create", "Publish", "Move", "Archive", "Delete", "Comment", "ManageTemplates" }.Select(a => (object)new { subjectType = "User", subjectId = (Guid?)adminId, action = a, effect = "Allow" })).ToList();
        using (var put = await admin.PutAsync("api/v1/spaces/ENG/permissions", new { grants }))
        {
            await ReadAsync(put);
        }

        using (var ok = await sara.GetAsync($"api/v1/content/{guide}"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using (var hidden = await outsider.GetAsync($"api/v1/content/{guide}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        using var open = await outsider.GetAsync($"api/v1/content/{home}");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode); // unrestricted pages are visible
    }

    [Fact]
    public async Task Comments_are_imported_as_threads_with_inline_anchors_resolved_state_and_source_authors_without_notifying_anyone()
    {
        fx.Confluence.Seed();
        var at = new DateTimeOffset(2023, 5, 1, 8, 0, 0, TimeSpan.Zero);
        fx.Confluence.Comments["101"] =
        [
            new SourceComment("c1", null, false, null, "<p>Looks <strong>good</strong> to me</p>", "sara", "Sara Source", at, false),
            new SourceComment("c3", "c2", false, null, "<p>nested reply</p>", "ghost", "Ghost", at.AddHours(3), false), // reply to a reply → flattened into the thread
            new SourceComment("c2", "c1", false, null, "<p>first reply</p>", "ali", "Ali Source", at.AddHours(1), false),
            new SourceComment("c4", null, true, "first draft", "<p>Is this still true?</p>", null, "Anonymous", at.AddDays(1), true),
            new SourceComment("c5", null, true, null, "<p>no quoted text</p>", "sara", "Sara Source", at.AddDays(2), false), // inline without a selection → page comment + warning
            new SourceComment("c6", null, false, null, "<p> </p>", "sara", "Sara Source", at.AddDays(3), false), // empty → skipped
        ];
        var (tenant, admin, _) = await _identity.TenantWithAdminAsync();

        var dry = await RunToEndAsync(this, tenant, admin, dryRun: true);
        Assert.Equal(6, (await admin.GetJsonAsync($"api/v1/migrations/confluence/{Id(dry)}/report")).GetProperty("comments").GetProperty("found").GetInt32());
        Assert.Empty((await admin.GetJsonAsync("api/v1/spaces")).EnumerateArray());

        var job = await RunToEndAsync(this, tenant, admin, dryRun: false);
        Assert.Equal("Completed", job.GetProperty("status").GetString());
        var counts = (await admin.GetJsonAsync($"api/v1/migrations/confluence/{Id(job)}/report")).GetProperty("comments");
        Assert.Equal((6, 5, 1), (counts.GetProperty("found").GetInt32(), counts.GetProperty("imported").GetInt32(), counts.GetProperty("skipped").GetInt32()));

        var home = (await admin.GetJsonAsync("api/v1/spaces/ENG")).GetProperty("homePageId").GetGuid();
        var guide = Id((await admin.GetJsonAsync($"api/v1/spaces/ENG/tree?parentId={home}")).EnumerateArray().Single());
        var listing = await admin.GetJsonAsync($"api/v1/content/{guide}/comments?includeResolved=true");
        var threads = listing.GetProperty("threads").EnumerateArray().ToArray();
        Assert.Equal(3, threads.Length); // c1 (+2 replies), c4, c5

        var t1 = threads.Single(t => t.GetProperty("comments")[0].GetProperty("authorName").GetString() == "Sara Source" && t.GetProperty("comments").GetArrayLength() == 3);
        Assert.Equal(["Sara Source", "Ali Source", "Ghost"], t1.GetProperty("comments").EnumerateArray().Select(c => c.GetProperty("authorName").GetString()!)); // flat, oldest first
        Assert.All(t1.GetProperty("comments").EnumerateArray(), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("authorId").ValueKind)); // nobody was invented
        Assert.Equal(at, t1.GetProperty("comments")[0].GetProperty("createdAt").GetDateTimeOffset());
        Assert.False(t1.GetProperty("comments")[0].GetProperty("rights").GetProperty("canEdit").GetBoolean()); // nobody owns an unmapped comment

        var inline = threads.Single(t => t.GetProperty("kind").GetString() == "Inline");
        Assert.Equal(("first draft", "Resolved"), (inline.GetProperty("anchor").GetProperty("exact").GetString(), inline.GetProperty("status").GetString()));
        Assert.Equal("Anonymous", inline.GetProperty("comments")[0].GetProperty("authorName").GetString());
        Assert.Equal(1, threads.Count(t => t.GetProperty("kind").GetString() == "Page" && t.GetProperty("comments")[0].GetProperty("authorName").GetString() == "Sara Source" && t.GetProperty("comments").GetArrayLength() == 1));

        // Importing comments notifies nobody: no outbox event, no notification.
        Assert.Empty((await admin.GetJsonAsync("api/v1/notifications")).GetProperty("items").EnumerateArray());

        // Re-running creates nothing new.
        var again = await RunToEndAsync(this, tenant, admin, dryRun: false);
        var second = (await admin.GetJsonAsync($"api/v1/migrations/confluence/{Id(again)}/report")).GetProperty("comments");
        Assert.Equal((0, 6), (second.GetProperty("imported").GetInt32(), second.GetProperty("skipped").GetInt32())); // 5 already imported + the empty one again
        Assert.Equal(3, (await admin.GetJsonAsync($"api/v1/content/{guide}/comments?includeResolved=true")).GetProperty("threads").GetArrayLength());
        var events = await admin.GetJsonAsync($"api/v1/migrations/confluence/{Id(job)}/events");
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("code").GetString() == "comment.skipped");
    }

    [Theory]
    [InlineData("eng", "ENG")]
    [InlineData("my-space_1", "MYSPACE1")]
    [InlineData("1abc", "S1ABC")]
    [InlineData("~user", "USER")]
    [InlineData("é", "SX")]
    public void Confluence_space_keys_are_mapped_to_valid_Raavi_keys(string source, string expected)
    {
        Assert.Equal(expected, SourceKeys.SpaceKey(source, null));
        Assert.Equal("CUSTOM", SourceKeys.SpaceKey(source, new Dictionary<string, string> { [source] = "custom" }));
    }
}
