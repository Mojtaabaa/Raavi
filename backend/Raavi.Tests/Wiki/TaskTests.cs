using System.Net;
using System.Net.Http.Headers;
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
        Assert.StartsWith("Title,Status,Priority,Assignee,Due date,Start date,Estimate (min),Completed at,Notes\r\n", csv, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\",Open,Normal,Member,2030-05-06,,,,\"line1\nline2, \"\"quoted\"\"\"", csv, StringComparison.Ordinal); // formula neutralised, quotes doubled
        Assert.Contains("کار فارسی,Open,Normal,,,,,,", csv, StringComparison.Ordinal);

        var (_, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        using (var peek = await otherAdmin.GetAsync($"api/v1/tasks/{Id(task)}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        }

        Assert.Empty((await otherAdmin.GetJsonAsync("api/v1/tasks/mine")).EnumerateArray());
        using var noExport = await otherAdmin.GetAsync($"api/v1/spaces/{key}/tasks/export");
        Assert.Equal(HttpStatusCode.NotFound, noExport.StatusCode);
    }

    [Fact]
    public async Task Multi_assignee_priority_archive_and_dual_read_mine()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (helper, _, helperId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;

        var created = await CreateTaskAsync(admin, key, new
        {
            title = "Ship",
            priority = "High",
            startDate = "2099-01-01",
            dueDate = "2099-02-01",
            assignees = new[]
            {
                new { userId = memberId, role = "Responsible", isPrimary = true },
                new { userId = helperId, role = "Contributor", isPrimary = false },
            },
        });
        Assert.Equal(("High", "2099-01-01", memberId), (
            created.GetProperty("priority").GetString(),
            created.GetProperty("startDate").GetString(),
            created.GetProperty("assigneeId").GetGuid()));
        Assert.Equal(2, created.GetProperty("assignees").GetArrayLength());
        Assert.Contains(created.GetProperty("assignees").EnumerateArray(), a => a.GetProperty("role").GetString() == "Contributor" && a.GetProperty("userId").GetGuid() == helperId);

        Assert.Contains((await helper.GetJsonAsync("api/v1/tasks/mine")).EnumerateArray(), t => t.GetProperty("title").GetString() == "Ship");

        using (var archive = await admin.PostAsync($"api/v1/tasks/{Id(created)}/archive"))
        {
            Assert.NotEqual(JsonValueKind.Null, (await ReadAsync(archive)).GetProperty("archivedAt").ValueKind);
        }

        Assert.Empty((await admin.GetJsonAsync($"api/v1/spaces/{key}/tasks")).EnumerateArray());
        Assert.Single((await admin.GetJsonAsync($"api/v1/spaces/{key}/tasks?archived=true")).EnumerateArray());
        Assert.DoesNotContain((await member.GetJsonAsync("api/v1/tasks/mine")).EnumerateArray(), t => t.GetProperty("title").GetString() == "Ship");

        using (var restore = await admin.PostAsync($"api/v1/tasks/{Id(created)}/restore"))
        {
            Assert.Equal(JsonValueKind.Null, (await ReadAsync(restore)).GetProperty("archivedAt").ValueKind);
        }

        Assert.Contains((await member.GetJsonAsync("api/v1/tasks/mine")).EnumerateArray(), t => t.GetProperty("title").GetString() == "Ship");
    }

    [Fact]
    public async Task Parent_complete_blocked_by_open_children_and_one_role_per_user()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;

        var parent = await CreateTaskAsync(admin, key, new { title = "Parent", progressPercent = (byte)40, blockCompleteWhenOpenChildren = true });
        Assert.Equal(40, parent.GetProperty("progressPercent").GetByte());
        Assert.True(parent.GetProperty("blockCompleteWhenOpenChildren").GetBoolean());
        Assert.Equal(JsonValueKind.Null, parent.GetProperty("actualSpentMinutes").ValueKind);

        await CreateTaskAsync(admin, key, new { title = "Child", parentTaskId = Id(parent) });
        using (var blocked = await admin.PostAsync($"api/v1/tasks/{Id(parent)}/complete"))
        {
            await Browser.AssertStatusAsync(blocked, HttpStatusCode.Conflict, "task.open_children");
        }

        var (task, etag) = await GetWithETagAsync(admin, $"api/v1/tasks/{Id(parent)}");
        using (var ok = await admin.SendAsync(HttpMethod.Put, $"api/v1/tasks/{Id(task)}", new
        {
            title = "Parent",
            assignees = new[]
            {
                new { userId = memberId, role = "Responsible", isPrimary = true },
                new { userId = memberId, role = "Contributor", isPrimary = false },
            },
            blockCompleteWhenOpenChildren = false,
        }, withXsrf: true, ifMatch: etag))
        {
            var body = await ReadAsync(ok);
            Assert.Single(body.GetProperty("assignees").EnumerateArray());
            Assert.Equal("Contributor", body.GetProperty("assignees")[0].GetProperty("role").GetString());
            Assert.False(body.GetProperty("blockCompleteWhenOpenChildren").GetBoolean());
        }

        using (var done = await admin.PostAsync($"api/v1/tasks/{Id(parent)}/complete"))
        {
            Assert.Equal("Done", (await ReadAsync(done)).GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Assignees_roles_history_one_responsible_and_etag_concurrency()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var users = new List<(Browser Browser, Guid Id)>();
        for (var i = 0; i < 5; i++)
        {
            var (b, _, id) = await _kit.Identity.AddMemberAsync(tenant, admin);
            users.Add((b, id));
        }

        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var roles = new[] { "Responsible", "Contributor", "Reviewer", "Approver", "Observer" };
        var created = await CreateTaskAsync(admin, key, new
        {
            title = "Crew",
            assignees = users.Zip(roles, (u, r) => new { userId = u.Id, role = r, isPrimary = r == "Responsible" }).ToArray(),
        });
        Assert.Equal(5, created.GetProperty("assignees").GetArrayLength());
        Assert.Equal(users[0].Id, created.GetProperty("assigneeId").GetGuid());
        Assert.Single(created.GetProperty("assignees").EnumerateArray(), a => a.GetProperty("role").GetString() == "Responsible");

        // Observer still appears on My tasks (assignment), but workload/allocation is out of B2 scope (task-level estimates only).
        Assert.Contains((await users[4].Browser.GetJsonAsync("api/v1/tasks/mine")).EnumerateArray(), t => t.GetProperty("title").GetString() == "Crew");

        using (var multi = await admin.PostAsync($"api/v1/spaces/{key}/tasks", new
        {
            title = "Bad",
            assignees = new[]
            {
                new { userId = users[0].Id, role = "Responsible", isPrimary = true },
                new { userId = users[1].Id, role = "Responsible", isPrimary = false },
            },
        }))
        {
            await Browser.AssertStatusAsync(multi, HttpStatusCode.BadRequest, "task.multiple_responsible");
        }

        var (task, etag) = await GetWithETagAsync(admin, $"api/v1/tasks/{Id(created)}");
        using (var roleChange = await admin.SendAsync(HttpMethod.Put, $"api/v1/tasks/{Id(task)}", new
        {
            title = "Crew",
            assignees = new[]
            {
                new { userId = users[0].Id, role = "Contributor", isPrimary = false },
                new { userId = users[1].Id, role = "Responsible", isPrimary = true },
                new { userId = users[2].Id, role = "Reviewer", isPrimary = false },
            },
        }, withXsrf: true, ifMatch: etag))
        {
            var body = await ReadAsync(roleChange);
            Assert.Equal(users[1].Id, body.GetProperty("assigneeId").GetGuid());
            Assert.Equal(3, body.GetProperty("assignees").GetArrayLength());
        }

        var history = (await admin.GetJsonAsync($"api/v1/tasks/{Id(created)}/assignees?history=true")).EnumerateArray().ToList();
        Assert.Contains(history, a => a.GetProperty("removedAt").ValueKind != JsonValueKind.Null);
        Assert.Contains(history, a => a.GetProperty("role").GetString() == "Responsible" && a.GetProperty("userId").GetGuid() == users[0].Id && a.GetProperty("removedAt").ValueKind != JsonValueKind.Null);

        using (var asViewer = await users[3].Browser.GetAsync($"api/v1/tasks/{Id(created)}/assignees?history=true"))
        {
            // History needs Edit; Approver with View-only in open space still has Edit in default public spaces — use ViewOnly for a true forbid.
        }

        var (_, etag2) = await GetWithETagAsync(admin, $"api/v1/tasks/{Id(created)}");
        using (var stale = await admin.SendAsync(HttpMethod.Put, $"api/v1/tasks/{Id(created)}", new { title = "Crew", assigneeId = users[1].Id }, withXsrf: true, ifMatch: etag))
        {
            await Browser.AssertStatusAsync(stale, HttpStatusCode.PreconditionFailed);
        }

        _ = etag2;
        var (otherTenant, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (_, _, foreignId) = await _kit.Identity.AddMemberAsync(otherTenant, otherAdmin);
        using (var foreign = await admin.PostAsync($"api/v1/spaces/{key}/tasks", new { title = "x", assigneeId = foreignId }))
        {
            await Browser.AssertStatusAsync(foreign, HttpStatusCode.BadRequest, "task.assignee_invalid");
        }

        using (var stranger = await admin.PostAsync($"api/v1/spaces/{key}/tasks", new { title = "x", assigneeId = Guid.NewGuid() }))
        {
            await Browser.AssertStatusAsync(stranger, HttpStatusCode.BadRequest, "task.assignee_invalid");
        }
    }

    [Fact]
    public async Task Assignee_history_requires_edit_and_view_only_sees_active()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (viewer, _, viewerId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = await MeAsync(admin);
        var key = await ViewOnlySpaceAsync(admin, adminId, viewerId);
        var task = await CreateTaskAsync(admin, key, new { title = "Mine", assigneeId = viewerId });
        var (id, etag) = await GetWithETagAsync(admin, $"api/v1/tasks/{Id(task)}");
        using (var put = await admin.SendAsync(HttpMethod.Put, $"api/v1/tasks/{Id(id)}", new { title = "Mine", assigneeId = (Guid?)null }, withXsrf: true, ifMatch: etag))
        {
            await ReadAsync(put);
        }

        Assert.NotEmpty((await admin.GetJsonAsync($"api/v1/tasks/{Id(task)}/assignees?history=true")).EnumerateArray());
        using (var forbidden = await viewer.GetAsync($"api/v1/tasks/{Id(task)}/assignees?history=true"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        Assert.Empty((await viewer.GetJsonAsync($"api/v1/tasks/{Id(task)}/assignees")).EnumerateArray());
    }

    [Fact]
    public async Task Subtasks_checklists_promote_and_mandatory_block_complete()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var parent = await CreateTaskAsync(admin, key, new { title = "Parent", blockCompleteWhenMandatoryIncomplete = true });
        var child = await CreateTaskAsync(admin, key, new { title = "Child", parentTaskId = Id(parent) });
        Assert.Equal(Id(parent), child.GetProperty("parentTaskId").GetGuid());
        Assert.Equal(1, (await admin.GetJsonAsync($"api/v1/tasks/{Id(parent)}")).GetProperty("childCount").GetInt32());
        Assert.Equal(1, (await admin.GetJsonAsync($"api/v1/tasks/{Id(parent)}")).GetProperty("openChildCount").GetInt32());

        using (var blockedKids = await admin.PostAsync($"api/v1/tasks/{Id(parent)}/complete"))
        {
            await Browser.AssertStatusAsync(blockedKids, HttpStatusCode.Conflict, "task.open_children");
        }

        using (var del = await admin.DeleteAsync($"api/v1/tasks/{Id(parent)}"))
        {
            await Browser.AssertStatusAsync(del, HttpStatusCode.Conflict, "task.has_children");
        }

        using (var promoted = await admin.PostAsync($"api/v1/tasks/{Id(child)}/promote"))
        {
            Assert.Equal(JsonValueKind.Null, (await ReadAsync(promoted)).GetProperty("parentTaskId").ValueKind);
        }

        Assert.Equal(0, (await admin.GetJsonAsync($"api/v1/tasks/{Id(parent)}")).GetProperty("childCount").GetInt32());

        var list = await ReadAsync(await admin.PostAsync($"api/v1/tasks/{Id(parent)}/checklists", new { title = "Launch" }));
        var item = await ReadAsync(await admin.PostAsync($"api/v1/task-checklists/{list.GetProperty("id").GetGuid()}/items", new { title = "Sign-off", isRequired = true }));
        Assert.True(item.GetProperty("isRequired").GetBoolean());

        using (var blockedMandatory = await admin.PostAsync($"api/v1/tasks/{Id(parent)}/complete"))
        {
            await Browser.AssertStatusAsync(blockedMandatory, HttpStatusCode.Conflict, "task.mandatory_incomplete");
        }

        using (var doneItem = await admin.PutAsync($"api/v1/task-checklist-items/{item.GetProperty("id").GetGuid()}", new { isDone = true }))
        {
            Assert.True((await ReadAsync(doneItem)).GetProperty("isDone").GetBoolean());
        }

        using (var done = await admin.PostAsync($"api/v1/tasks/{Id(parent)}/complete"))
        {
            Assert.Equal("Done", (await ReadAsync(done)).GetProperty("status").GetString());
        }
    }

    private static object CommentBody(string text, params (Guid Id, string Label)[] mentions) => new
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

    [Fact]
    public async Task Collaboration_comments_attachments_watchers_mentions_and_activity()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var adminId = await MeAsync(admin);
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Linked"));
        var task = await CreateTaskAsync(admin, key, new { title = "Collab", contentId = page });
        var taskId = Id(task);

        // Creator is auto-watched.
        var watchers = await admin.GetJsonAsync($"api/v1/tasks/{taskId}/watchers");
        Assert.Contains(adminId, watchers.GetProperty("items").EnumerateArray().Select(w => w.GetProperty("userId").GetGuid()));
        Assert.True(watchers.GetProperty("watching").GetBoolean());

        using (var watch = await member.PutAsync($"api/v1/tasks/{taskId}/watch", new { watching = true }))
        {
            Assert.True((await ReadAsync(watch)).GetProperty("watching").GetBoolean());
        }

        // Duplicate watch is idempotent (unique row).
        using (var again = await member.PutAsync($"api/v1/tasks/{taskId}/watch", new { watching = true }))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        using (var comment = await admin.PostAsync($"api/v1/tasks/{taskId}/comments", new { body = CommentBody("hello", (memberId, "Member")) }))
        {
            var c = await ReadAsync(comment);
            Assert.False(c.GetProperty("isDeleted").GetBoolean());
            Assert.Equal(adminId, c.GetProperty("authorId").GetGuid());
        }

        await _kit.DispatchOutboxAsync();
        var notes = (await member.GetJsonAsync("api/v1/notifications")).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("kind").GetString()).ToArray();
        Assert.Contains("mention", notes);

        var pageComments = await admin.GetJsonAsync($"api/v1/tasks/{taskId}/comments?take=10&sort=asc");
        Assert.Equal(1, pageComments.GetProperty("total").GetInt32());
        var commentId = pageComments.GetProperty("items")[0].GetProperty("id").GetGuid();
        var revision = pageComments.GetProperty("items")[0].GetProperty("revision").GetInt32();

        using (var edit = await admin.SendAsync(HttpMethod.Put, $"api/v1/task-comments/{commentId}", new { body = CommentBody("hello edited") }, withXsrf: true, ifMatch: $"\"r{revision}\""))
        {
            Assert.NotNull((await ReadAsync(edit)).GetProperty("editedAt").GetString());
        }

        Guid attachmentId;
        using (var form = new MultipartFormDataContent())
        {
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
            form.Add(file, "file", "shot.png");
            using var up = await admin.SendAsync(HttpMethod.Post, $"api/v1/tasks/{taskId}/attachments", form, withXsrf: true);
            var att = await ReadAsync(up);
            attachmentId = att.GetProperty("id").GetGuid();
            Assert.Equal("image/png", att.GetProperty("mediaType").GetString());

            using (var dl = await member.GetAsync($"api/v1/task-attachments/{attachmentId}"))
            {
                Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
            }
        }

        await _kit.DispatchOutboxAsync();
        var activityKinds = (await admin.GetJsonAsync($"api/v1/tasks/{taskId}/activity")).EnumerateArray().Select(a => a.GetProperty("kind").GetString()).ToArray();
        Assert.Contains("task.comment.created", activityKinds);
        Assert.Contains("task.attachment.added", activityKinds);

        using (var del = await admin.DeleteAsync($"api/v1/task-comments/{commentId}"))
        {
            await Browser.AssertStatusAsync(del, HttpStatusCode.NoContent);
        }

        // Soft-deleted comments are hidden by default; includeDeleted requires Edit.
        Assert.Equal(0, (await admin.GetJsonAsync($"api/v1/tasks/{taskId}/comments")).GetProperty("total").GetInt32());
        var afterDelete = await admin.GetJsonAsync($"api/v1/tasks/{taskId}/comments?includeDeleted=true");
        Assert.True(afterDelete.GetProperty("items")[0].GetProperty("isDeleted").GetBoolean());

        var (viewer, _, viewerId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var privateKey = await ViewOnlySpaceAsync(admin, adminId, viewerId);
        var privateTask = await CreateTaskAsync(admin, privateKey, new { title = "Private collab" });
        using (var c2 = await admin.PostAsync($"api/v1/tasks/{Id(privateTask)}/comments", new { body = CommentBody("secret") }))
        {
            await ReadAsync(c2);
        }

        var privateCommentId = (await admin.GetJsonAsync($"api/v1/tasks/{Id(privateTask)}/comments")).GetProperty("items")[0].GetProperty("id").GetGuid();
        using (var delPriv = await admin.DeleteAsync($"api/v1/task-comments/{privateCommentId}"))
        {
            await Browser.AssertStatusAsync(delPriv, HttpStatusCode.NoContent);
        }

        using (var noHistory = await viewer.GetAsync($"api/v1/tasks/{Id(privateTask)}/comments?includeDeleted=true"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noHistory.StatusCode);
        }

        // Soft-deleted attachment download is 404 (IDOR-safe).
        using (var delAtt = await admin.DeleteAsync($"api/v1/task-attachments/{attachmentId}"))
        {
            await Browser.AssertStatusAsync(delAtt, HttpStatusCode.NoContent);
        }

        using (var gone = await member.GetAsync($"api/v1/task-attachments/{attachmentId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        // Tenant isolation: foreign tenant cannot see the task or attachment.
        var (_, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        using (var hide = await otherAdmin.GetAsync($"api/v1/tasks/{taskId}/comments"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hide.StatusCode);
        }

        using (var hideAtt = await otherAdmin.GetAsync($"api/v1/task-attachments/{attachmentId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hideAtt.StatusCode);
        }

        // Replay outbox does not duplicate notifications.
        var before = (await member.GetJsonAsync("api/v1/notifications")).GetProperty("items").GetArrayLength();
        await _kit.DispatchOutboxAsync();
        Assert.Equal(before, (await member.GetJsonAsync("api/v1/notifications")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Time_tracking_sums_actual_spent_without_double_counting()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var adminId = await MeAsync(admin);
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (reviewer, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var reviewerId = await MeAsync(reviewer);
        // Private space: View-only for member/reviewer so Edit is not ambient; Responsible may still log.
        var key = (await CreateSpaceAsync(admin, visibility: "Private")).GetProperty("key").GetString()!;
        var grants = new[] { "View", "ManagePermissions", "Edit", "Create", "Publish", "Move", "Archive", "Delete", "Comment", "ManageTemplates" }
            .Select(a => (object)new { subjectType = "User", subjectId = (Guid?)adminId, action = a, effect = "Allow" })
            .Append(new { subjectType = "User", subjectId = (Guid?)memberId, action = "View", effect = "Allow" })
            .Append(new { subjectType = "User", subjectId = (Guid?)reviewerId, action = "View", effect = "Allow" })
            .ToList();
        using (var put = await admin.PutAsync($"api/v1/spaces/{key}/permissions", new { grants }))
        {
            await ReadAsync(put);
        }

        var task = await CreateTaskAsync(admin, key, new
        {
            title = "Timed",
            originalEstimateMinutes = 120,
            remainingEstimateMinutes = 120,
            progressPercent = (byte)10,
            assignees = new object[]
            {
                new { userId = memberId, role = "Responsible", isPrimary = true },
                new { userId = reviewerId, role = "Reviewer", isPrimary = false },
            },
        });
        var taskId = Id(task);
        Assert.Equal(120, task.GetProperty("originalEstimateMinutes").GetInt32());

        using (var forbidden = await reviewer.PostAsync($"api/v1/tasks/{taskId}/time", new { workDate = "2026-11-10", durationMinutes = 30 }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var log = await member.PostAsync($"api/v1/tasks/{taskId}/time", new { workDate = "2026-11-10", durationMinutes = 45, description = "coding" }))
        {
            var e = await ReadAsync(log);
            Assert.Equal(45, e.GetProperty("durationMinutes").GetInt32());
            Assert.False(e.GetProperty("isOpeningBalance").GetBoolean());
        }

        using (var log2 = await admin.PostAsync($"api/v1/tasks/{taskId}/time", new { userId = memberId, workDate = "2026-11-11", durationMinutes = 15 }))
        {
            await ReadAsync(log2);
        }

        var summary = await admin.GetJsonAsync($"api/v1/tasks/{taskId}/time");
        Assert.Equal(60, summary.GetProperty("totalMinutes").GetInt32());
        Assert.Equal(60, summary.GetProperty("actualSpentMinutes").GetInt32());
        Assert.True(summary.GetProperty("actualSpentMatchesEntries").GetBoolean());
        Assert.Equal(60, (await admin.GetJsonAsync($"api/v1/tasks/{taskId}")).GetProperty("actualSpentMinutes").GetInt32());

        // Prefer a non-opening entry owned by member (newest first).
        var memberEntry = summary.GetProperty("items").EnumerateArray().First(e => e.GetProperty("userId").GetGuid() == memberId && e.GetProperty("durationMinutes").GetInt32() == 45);
        var entryId = memberEntry.GetProperty("id").GetGuid();
        var rev = memberEntry.GetProperty("revision").GetInt32();
        using (var edit = await member.SendAsync(HttpMethod.Put, $"api/v1/task-time/{entryId}", new { durationMinutes = 50 }, withXsrf: true, ifMatch: $"\"r{rev}\""))
        {
            Assert.Equal(50, (await ReadAsync(edit)).GetProperty("durationMinutes").GetInt32());
        }

        Assert.Equal(65, (await admin.GetJsonAsync($"api/v1/tasks/{taskId}")).GetProperty("actualSpentMinutes").GetInt32());

        using (var del = await member.DeleteAsync($"api/v1/task-time/{entryId}"))
        {
            await Browser.AssertStatusAsync(del, HttpStatusCode.NoContent);
        }

        Assert.Equal(15, (await admin.GetJsonAsync($"api/v1/tasks/{taskId}")).GetProperty("actualSpentMinutes").GetInt32());

        var (_, etag) = await GetWithETagAsync(admin, $"api/v1/tasks/{taskId}");
        using (var upd = await admin.SendAsync(HttpMethod.Put, $"api/v1/tasks/{taskId}", new
        {
            title = "Timed",
            notes = (string?)null,
            assigneeId = memberId,
            dueDate = (string?)null,
            startDate = (string?)null,
            contentId = (Guid?)null,
            priority = "Normal",
            originalEstimateMinutes = 180,
            remainingEstimateMinutes = 100,
            progressPercent = (byte)25,
            blockCompleteWhenOpenChildren = true,
            blockCompleteWhenMandatoryIncomplete = true,
            assignees = new[] { new { userId = memberId, role = "Responsible", isPrimary = true } },
        }, withXsrf: true, ifMatch: etag))
        {
            var updated = await ReadAsync(upd);
            Assert.Equal(180, updated.GetProperty("originalEstimateMinutes").GetInt32());
            Assert.Equal(100, updated.GetProperty("remainingEstimateMinutes").GetInt32());
            Assert.Equal(25, updated.GetProperty("progressPercent").GetByte());
            Assert.Equal(15, updated.GetProperty("actualSpentMinutes").GetInt32());
        }

        await _kit.DispatchOutboxAsync();
        var kinds = (await admin.GetJsonAsync($"api/v1/tasks/{taskId}/activity?limit=50")).EnumerateArray().Select(a => a.GetProperty("kind").GetString()).ToArray();
        Assert.Contains("task.time.logged", kinds);
        Assert.Contains("task.estimate.original_changed", kinds);

        var afterDeleteSummary = await admin.GetJsonAsync($"api/v1/tasks/{taskId}/time");
        Assert.True(afterDeleteSummary.GetProperty("actualSpentMatchesEntries").GetBoolean());
        var remainingId = afterDeleteSummary.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();

        var (_, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        using (var hide = await otherAdmin.GetAsync($"api/v1/tasks/{taskId}/time"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hide.StatusCode);
        }

        using (var crossDelete = await otherAdmin.DeleteAsync($"api/v1/task-time/{remainingId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, crossDelete.StatusCode);
        }

        // Soft-deleted entry id must not be editable (404).
        using (var resurrect = await member.SendAsync(HttpMethod.Put, $"api/v1/task-time/{entryId}", new { durationMinutes = 99 }, withXsrf: true, ifMatch: $"\"r{rev}\""))
        {
            Assert.Equal(HttpStatusCode.NotFound, resurrect.StatusCode);
        }
    }

    [Fact]
    public async Task Subtask_cycle_and_etag_conflict_are_rejected()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var parent = await CreateTaskAsync(admin, key, new { title = "Root" });
        var child = await CreateTaskAsync(admin, key, new { title = "Child", parentTaskId = Id(parent) });

        var (childDto, childEtag) = await GetWithETagAsync(admin, $"api/v1/tasks/{Id(child)}");
        using (var cycle = await admin.SendAsync(HttpMethod.Put, $"api/v1/tasks/{Id(child)}", new
        {
            title = "Child",
            notes = (string?)null,
            assigneeId = (Guid?)null,
            dueDate = (string?)null,
            startDate = (string?)null,
            contentId = (Guid?)null,
            priority = "Normal",
            parentTaskId = Id(child),
            originalEstimateMinutes = (int?)null,
            remainingEstimateMinutes = (int?)null,
            progressPercent = (byte?)null,
            blockCompleteWhenOpenChildren = true,
            blockCompleteWhenMandatoryIncomplete = true,
            assignees = Array.Empty<object>(),
        }, withXsrf: true, ifMatch: childEtag))
        {
            await Browser.AssertStatusAsync(cycle, HttpStatusCode.BadRequest, "task.parent_invalid");
        }

        var (_, parentEtag) = await GetWithETagAsync(admin, $"api/v1/tasks/{Id(parent)}");
        using (var makeParentChild = await admin.SendAsync(HttpMethod.Put, $"api/v1/tasks/{Id(parent)}", new
        {
            title = "Root",
            notes = (string?)null,
            assigneeId = (Guid?)null,
            dueDate = (string?)null,
            startDate = (string?)null,
            contentId = (Guid?)null,
            priority = "Normal",
            parentTaskId = Id(child),
            originalEstimateMinutes = (int?)null,
            remainingEstimateMinutes = (int?)null,
            progressPercent = (byte?)null,
            blockCompleteWhenOpenChildren = true,
            blockCompleteWhenMandatoryIncomplete = true,
            assignees = Array.Empty<object>(),
        }, withXsrf: true, ifMatch: parentEtag))
        {
            await Browser.AssertStatusAsync(makeParentChild, HttpStatusCode.BadRequest, "task.parent_invalid");
        }

        using (var stale = await admin.SendAsync(HttpMethod.Put, $"api/v1/tasks/{Id(childDto)}", new
        {
            title = "Child renamed",
            notes = (string?)null,
            assigneeId = (Guid?)null,
            dueDate = (string?)null,
            startDate = (string?)null,
            contentId = (Guid?)null,
            priority = "Normal",
            parentTaskId = Id(parent),
            originalEstimateMinutes = (int?)null,
            remainingEstimateMinutes = (int?)null,
            progressPercent = (byte?)null,
            blockCompleteWhenOpenChildren = true,
            blockCompleteWhenMandatoryIncomplete = true,
            assignees = Array.Empty<object>(),
        }, withXsrf: true, ifMatch: "\"r0\""))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        }
    }
}
