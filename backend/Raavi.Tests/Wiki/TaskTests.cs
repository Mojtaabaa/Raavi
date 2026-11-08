using System.Net;
using System.Text.Json;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 9a acceptance: tasks have persistence, tenant ownership, space-based permissions, navigation ("my tasks") and CSV export.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class TaskTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static async Task<Guid> MeAsync(Browser b) => (await b.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();

    private static async Task<JsonElement> CreateTaskAsync(Browser b, string key, object body)
    {
        using var r = await b.PostAsync($"api/v1/spaces/{key}/tasks", body);
        return await ReadAsync(r);
    }

    /// <summary>A private space where <paramref name="viewer"/> may only view (no Edit).</summary>
    private static async Task<string> ViewOnlySpaceAsync(Browser admin, Guid adminId, Guid viewer)
    {
        var key = (await CreateSpaceAsync(admin, visibility: "Private")).GetProperty("key").GetString()!;
        var grants = new[] { "View", "ManagePermissions", "Edit", "Create", "Publish", "Move", "Archive", "Delete", "Comment", "ManageTemplates" }
            .Select(a => (object)new { subjectType = "User", subjectId = (Guid?)adminId, action = a, effect = "Allow" })
            .Append(new { subjectType = "User", subjectId = (Guid?)viewer, action = "View", effect = "Allow" }).ToList();
        using var put = await admin.PutAsync($"api/v1/spaces/{key}/permissions", new { grants });
        await ReadAsync(put);
        return key;
    }

    [Fact]
    public async Task Tasks_are_created_listed_filtered_ordered_and_validated()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Spec"));
        var otherSpace = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var foreignPage = Id(await CreatePageAsync(admin, otherSpace, "Elsewhere"));

        var late = await CreateTaskAsync(admin, key, new { title = "Late one", dueDate = "2020-01-01", assigneeId = memberId, contentId = page, notes = "has, a \"comma\"" });
        var soon = await CreateTaskAsync(admin, key, new { title = "Later", dueDate = "2099-12-31" });
        var open = await CreateTaskAsync(admin, key, new { title = "No date" });
        Assert.Equal(("Open", true, "Member"), (late.GetProperty("status").GetString(), late.GetProperty("isOverdue").GetBoolean(), late.GetProperty("assigneeName").GetString()));
        Assert.False(soon.GetProperty("isOverdue").GetBoolean());
        Assert.Equal(page, late.GetProperty("contentId").GetGuid());

        // Members of an open space may create too; open tasks first, by due date, undated last.
        await CreateTaskAsync(member, key, new { title = "From a member" });
        var listed = (await member.GetJsonAsync($"api/v1/spaces/{key}/tasks")).EnumerateArray().Select(t => t.GetProperty("title").GetString()!).ToArray();
        Assert.Equal(new[] { "Late one", "Later", "No date", "From a member" }, listed); // dated first by due date, then undated by creation
        Assert.Equal("Late one", listed[0]);
        Assert.Equal("Later", listed[1]);
        Assert.Equal(1, (await admin.GetJsonAsync($"api/v1/spaces/{key}/tasks?assignee={memberId}")).GetArrayLength());

        // Validation.
        using (var empty = await admin.PostAsync($"api/v1/spaces/{key}/tasks", new { title = "  " }))
        {
            await Browser.AssertStatusAsync(empty, HttpStatusCode.BadRequest, "task.title_invalid");
        }

        using (var stranger = await admin.PostAsync($"api/v1/spaces/{key}/tasks", new { title = "x", assigneeId = Guid.NewGuid() }))
        {
            await Browser.AssertStatusAsync(stranger, HttpStatusCode.BadRequest, "task.assignee_invalid");
        }

        using (var foreign = await admin.PostAsync($"api/v1/spaces/{key}/tasks", new { title = "x", contentId = foreignPage }))
        {
            await Browser.AssertStatusAsync(foreign, HttpStatusCode.BadRequest, "task.content_invalid");
        }

        // Done tasks sort after open ones and can be filtered.
        using (var done = await admin.PostAsync($"api/v1/tasks/{Id(soon)}/complete"))
        {
            Assert.Equal("Done", (await ReadAsync(done)).GetProperty("status").GetString());
        }

        Assert.Equal("Later", (await admin.GetJsonAsync($"api/v1/spaces/{key}/tasks?status=Done")).EnumerateArray().Single().GetProperty("title").GetString());
        Assert.Equal("Later", (await admin.GetJsonAsync($"api/v1/spaces/{key}/tasks")).EnumerateArray().Last().GetProperty("title").GetString());
        using (var reopen = await admin.PostAsync($"api/v1/tasks/{Id(soon)}/reopen"))
        {
            Assert.Equal("Open", (await ReadAsync(reopen)).GetProperty("status").GetString());
        }

        _ = open;
    }

    [Fact]
    public async Task Assignees_must_be_able_to_view_the_space_and_may_only_complete_their_own_tasks_when_they_cannot_edit()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (viewer, _, viewerId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (outsider, _, outsiderId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = await MeAsync(admin);
        var key = await ViewOnlySpaceAsync(admin, adminId, viewerId);

        using (var noAccess = await admin.PostAsync($"api/v1/spaces/{key}/tasks", new { title = "x", assigneeId = outsiderId }))
        {
            await Browser.AssertStatusAsync(noAccess, HttpStatusCode.BadRequest, "task.assignee_no_access");
        }

        var mine = await CreateTaskAsync(admin, key, new { title = "Mine", assigneeId = viewerId });
        var notMine = await CreateTaskAsync(admin, key, new { title = "Not mine" });
        Assert.True(mine.GetProperty("rights").GetProperty("canEdit").GetBoolean());

        // The viewer can read, cannot create or edit, but may complete and reopen the task assigned to them.
        var asViewer = (await viewer.GetJsonAsync($"api/v1/spaces/{key}/tasks")).EnumerateArray().ToDictionary(t => t.GetProperty("title").GetString()!);
        Assert.Equal((false, true, false), (asViewer["Mine"].GetProperty("rights").GetProperty("canEdit").GetBoolean(), asViewer["Mine"].GetProperty("rights").GetProperty("canComplete").GetBoolean(), asViewer["Not mine"].GetProperty("rights").GetProperty("canComplete").GetBoolean()));
        using (var create = await viewer.PostAsync($"api/v1/spaces/{key}/tasks", new { title = "nope" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        using (var done = await viewer.PostAsync($"api/v1/tasks/{Id(mine)}/complete"))
        {
            Assert.Equal("Done", (await ReadAsync(done)).GetProperty("status").GetString());
        }

        using (var other = await viewer.PostAsync($"api/v1/tasks/{Id(notMine)}/complete"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        }

        using (var del = await viewer.DeleteAsync($"api/v1/tasks/{Id(mine)}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
        }

        // Someone without access to the space does not even learn that tasks exist.
        using (var hidden = await outsider.GetAsync($"api/v1/spaces/{key}/tasks"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        using (var hiddenTask = await outsider.GetAsync($"api/v1/tasks/{Id(mine)}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hiddenTask.StatusCode);
        }
    }

    [Fact]
    public async Task My_tasks_span_spaces_follow_access_and_updates_use_etags()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = await MeAsync(admin);
        var a = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var b = await ViewOnlySpaceAsync(admin, adminId, memberId);
        var t1 = await CreateTaskAsync(admin, a, new { title = "In A", assigneeId = memberId, dueDate = "2099-01-02" });
        await CreateTaskAsync(admin, b, new { title = "In B", assigneeId = memberId, dueDate = "2099-01-01" });
        await CreateTaskAsync(admin, a, new { title = "Someone else's" });

        Assert.Equal<string>(["In B", "In A"], (await member.GetJsonAsync("api/v1/tasks/mine")).EnumerateArray().Select(t => t.GetProperty("title").GetString()!));
        Assert.Equal(["In A", "In B"], (await member.GetJsonAsync("api/v1/tasks/mine?status=Open")).EnumerateArray().Select(t => t.GetProperty("title").GetString()!).Order());
        Assert.Empty((await member.GetJsonAsync("api/v1/tasks/mine?status=Done")).EnumerateArray());

        // Optimistic concurrency.
        var (task, etag) = await GetWithETagAsync(admin, $"api/v1/tasks/{Id(t1)}");
        using (var missing = await admin.SendAsync(HttpMethod.Put, $"api/v1/tasks/{Id(task)}", new { title = "Renamed" }, withXsrf: true))
        {
            await Browser.AssertStatusAsync(missing, HttpStatusCode.PreconditionRequired);
        }

        using (var ok = await admin.SendAsync(HttpMethod.Put, $"api/v1/tasks/{Id(task)}", new { title = "Renamed", assigneeId = memberId, dueDate = "2099-01-02", notes = "n" }, withXsrf: true, ifMatch: etag))
        {
            Assert.Equal("Renamed", (await ReadAsync(ok)).GetProperty("title").GetString());
        }

        using (var stale = await admin.SendAsync(HttpMethod.Put, $"api/v1/tasks/{Id(task)}", new { title = "Again" }, withXsrf: true, ifMatch: etag))
        {
            await Browser.AssertStatusAsync(stale, HttpStatusCode.PreconditionFailed);
        }

        // Losing access to a space removes its tasks from "my tasks".
        var grants = new[] { "View", "ManagePermissions", "Edit", "Create", "Publish", "Move", "Archive", "Delete", "Comment", "ManageTemplates" }
            .Select(x => new { subjectType = "User", subjectId = (Guid?)adminId, action = x, effect = "Allow" }).ToList();
        using (var put = await admin.PutAsync($"api/v1/spaces/{b}/permissions", new { grants }))
        {
            await ReadAsync(put);
        }

        Assert.Equal(["Renamed"], (await member.GetJsonAsync("api/v1/tasks/mine")).EnumerateArray().Select(t => t.GetProperty("title").GetString()!));

        using (var del = await admin.DeleteAsync($"api/v1/tasks/{Id(task)}"))
        {
            await Browser.AssertStatusAsync(del, HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Csv_export_is_complete_escaped_and_safe_to_open_in_a_spreadsheet_and_tasks_are_tenant_private()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var task = await CreateTaskAsync(admin, key, new { title = "=HYPERLINK(\"http://evil\")", notes = "line1\nline2, \"quoted\"", assigneeId = memberId, dueDate = "2030-05-06" });
        await CreateTaskAsync(admin, key, new { title = "کار فارسی" });

        using var response = await member.GetAsync($"api/v1/spaces/{key}/tasks/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains($"tasks-{key}.csv", response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]); // BOM: spreadsheets open the file as UTF-8 (Persian)
        var csv = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("Title,Status,Assignee,Due date,Completed at,Notes\r\n", csv, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\",Open,Member,2030-05-06,,\"line1\nline2, \"\"quoted\"\"\"", csv, StringComparison.Ordinal); // formula neutralised, quotes doubled
        Assert.Contains("کار فارسی,Open,,,,", csv, StringComparison.Ordinal);

        var (_, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        using (var peek = await otherAdmin.GetAsync($"api/v1/tasks/{Id(task)}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        }

        Assert.Empty((await otherAdmin.GetJsonAsync("api/v1/tasks/mine")).EnumerateArray());
        using var noExport = await otherAdmin.GetAsync($"api/v1/spaces/{key}/tasks/export");
        Assert.Equal(HttpStatusCode.NotFound, noExport.StatusCode);
    }
}
