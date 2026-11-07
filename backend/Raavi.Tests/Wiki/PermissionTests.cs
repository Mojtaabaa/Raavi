using System.Net;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>PermissionEvaluator v1 (ADR-012): space grants with deny-wins, inherited View restrictions, Edit restrictions, recovery.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class PermissionTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    [Fact]
    public async Task View_restrictions_are_inherited_and_hide_content_from_every_listing()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (insider, _, insiderId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var hr = Id(await CreatePageAsync(admin, key, "HR confidential"));
        var child = Id(await CreatePageAsync(admin, key, "Salary bands", hr));
        using (var label = await admin.PostAsync($"api/v1/content/{child}/labels", new { name = "payroll" }))
        {
            await ReadAsync(label);
        }

        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        using (var restrict = await admin.PutAsync($"api/v1/content/{hr}/restrictions", new
        {
            view = new[] { new { subjectType = "User", subjectId = adminId }, new { subjectType = "User", subjectId = insiderId } },
            edit = Array.Empty<object>(),
        }))
        {
            await ReadAsync(restrict);
        }

        // Insider and admin still see both pages.
        Assert.Equal("Salary bands", (await insider.GetJsonAsync($"api/v1/content/{child}")).GetProperty("title").GetString());
        Assert.True((await insider.GetJsonAsync($"api/v1/content/{child}")).GetProperty("isRestricted").GetBoolean());

        // The other member: the restricted page and its descendants are not found anywhere.
        foreach (var id in new[] { hr, child })
        {
            using var r = await member.GetAsync($"api/v1/content/{id}");
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        Assert.DoesNotContain("HR confidential", await TreeTitlesAsync(member, key));
        Assert.Empty((await member.GetJsonAsync("api/v1/quick-find?q=Salary")).GetProperty("content").EnumerateArray());
        Assert.Empty((await member.GetJsonAsync("api/v1/labels/payroll/content")).EnumerateArray());
        Assert.DoesNotContain("payroll", (await member.GetJsonAsync("api/v1/labels?prefix=pay")).EnumerateArray().Select(l => l.GetString()));
        using (var tree = await member.GetAsync($"api/v1/spaces/{key}/tree?parentId={hr}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, tree.StatusCode);
        }

        await _kit.DispatchOutboxAsync();
        var activity = await member.GetJsonAsync($"api/v1/spaces/{key}/activity?limit=100");
        Assert.DoesNotContain(activity.EnumerateArray(), a => a.TryGetProperty("contentId", out var c) && c.ValueKind != System.Text.Json.JsonValueKind.Null && (c.GetGuid() == hr || c.GetGuid() == child));
        Assert.Contains(activity.EnumerateArray(), a => a.GetProperty("kind").GetString() == "space.created");

        // Moving the child out of the restricted parent makes it visible (restrictions follow the tree).
        using (var moved = await MoveAsync(admin, child, null, null, null))
        {
            await ReadAsync(moved);
        }

        Assert.Equal("Salary bands", (await member.GetJsonAsync($"api/v1/content/{child}")).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Group_restrictions_follow_group_membership_immediately()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var group = Id(await admin.PostOkAsync("api/v1/tenant/groups", new { name = "Finance", description = "" }));
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Budget"));
        using (var restrict = await admin.PutAsync($"api/v1/content/{page}/restrictions", new
        {
            view = new object[] { new { subjectType = "Group", subjectId = group }, new { subjectType = "User", subjectId = adminId } },
            edit = Array.Empty<object>(),
        }))
        {
            await ReadAsync(restrict);
        }

        using (var before = await member.GetAsync($"api/v1/content/{page}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        }

        await admin.PostOkAsync($"api/v1/tenant/groups/{group}/members/{memberId}");
        Assert.Equal("Budget", (await member.GetJsonAsync($"api/v1/content/{page}")).GetProperty("title").GetString());

        using (var remove = await admin.DeleteAsync($"api/v1/tenant/groups/{group}/members/{memberId}"))
        {
            Assert.True(remove.IsSuccessStatusCode);
        }

        using var after = await member.GetAsync($"api/v1/content/{page}");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode); // revocation takes effect on the next request
    }

    [Fact]
    public async Task Edit_restrictions_allow_reading_but_not_writing()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Policy"));
        using (var restrict = await admin.PutAsync($"api/v1/content/{page}/restrictions", new { view = Array.Empty<object>(), edit = new[] { new { subjectType = "User", subjectId = adminId } } }))
        {
            await ReadAsync(restrict);
        }

        var (dto, etag) = await GetWithETagAsync(member, $"api/v1/content/{page}");
        Assert.False(dto.GetProperty("rights").GetProperty("canEdit").GetBoolean());
        using (var update = await UpdatePageAsync(member, page, etag, "Hacked", Doc("x")))
        {
            await Browser.AssertStatusAsync(update, HttpStatusCode.Forbidden, "permission.denied");
        }

        using (var label = await member.PostAsync($"api/v1/content/{page}/labels", new { name = "x" }))
        {
            await Browser.AssertStatusAsync(label, HttpStatusCode.Forbidden);
        }

        // Children can still be added under it (Edit restrictions apply to the node only).
        await CreatePageAsync(member, key, "Child of policy", page);
    }

    [Fact]
    public async Task Editors_cannot_lock_themselves_out_and_space_admins_can_recover()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(member, key, "Member page"));

        using (var lockout = await member.PutAsync($"api/v1/content/{page}/restrictions", new { view = new[] { new { subjectType = "User", subjectId = adminId } }, edit = Array.Empty<object>() }))
        {
            await Browser.AssertStatusAsync(lockout, HttpStatusCode.BadRequest, "restriction.excludes_self");
        }

        var memberId = (await member.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        using (var own = await member.PutAsync($"api/v1/content/{page}/restrictions", new { view = new[] { new { subjectType = "User", subjectId = memberId } }, edit = Array.Empty<object>() }))
        {
            await ReadAsync(own);
        }

        // The space administrator cannot read the page …
        using (var read = await admin.GetAsync($"api/v1/content/{page}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        }

        // … but can recover access by managing its restrictions.
        var restrictions = await admin.GetJsonAsync($"api/v1/content/{page}/restrictions");
        Assert.Single(restrictions.GetProperty("view").EnumerateArray());
        using (var recover = await admin.PutAsync($"api/v1/content/{page}/restrictions", new { view = Array.Empty<object>(), edit = Array.Empty<object>() }))
        {
            await ReadAsync(recover);
        }

        Assert.Equal("Member page", (await admin.GetJsonAsync($"api/v1/content/{page}")).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Deny_wins_over_allow_and_spaces_keep_an_administrator()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var group = Id(await admin.PostOkAsync("api/v1/tenant/groups", new { name = "Contractors", description = "" }));
        await admin.PostOkAsync($"api/v1/tenant/groups/{group}/members/{memberId}");
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;

        var grants = new List<object>
        {
            new { subjectType = "User", subjectId = (Guid?)adminId, action = "View", effect = "Allow" },
            new { subjectType = "User", subjectId = (Guid?)adminId, action = "ManagePermissions", effect = "Allow" },
            new { subjectType = "AllMembers", subjectId = (Guid?)null, action = "View", effect = "Allow" },
            new { subjectType = "Group", subjectId = (Guid?)group, action = "View", effect = "Deny" },
        };
        using (var put = await admin.PutAsync($"api/v1/spaces/{key}/permissions", new { grants }))
        {
            await ReadAsync(put);
        }

        using (var denied = await member.GetAsync($"api/v1/spaces/{key}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }

        Assert.DoesNotContain(key, (await member.GetJsonAsync("api/v1/spaces")).EnumerateArray().Select(s => s.GetProperty("key").GetString()));

        // Removing the last user/group administrator is refused.
        using (var orphan = await admin.PutAsync($"api/v1/spaces/{key}/permissions", new { grants = new[] { new { subjectType = "AllMembers", subjectId = (Guid?)null, action = "View", effect = "Allow" } } }))
        {
            await Browser.AssertStatusAsync(orphan, HttpStatusCode.Conflict, "space.last_administrator");
        }

        // Members without administration rights cannot read or change permissions.
        using var memberView = await member.GetAsync($"api/v1/spaces/{key}/permissions");
        Assert.Equal(HttpStatusCode.NotFound, memberView.StatusCode);
    }

    [Fact]
    public async Task Tenant_administrators_recover_private_spaces_without_reading_their_content()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        await AllowMembersToCreateSpacesAsync(admin);
        var space = await CreateSpaceAsync(member, visibility: "Private", name: "My notes");
        var key = space.GetProperty("key").GetString()!;
        var home = space.GetProperty("homePageId").GetGuid();

        Assert.DoesNotContain(key, (await admin.GetJsonAsync("api/v1/spaces")).EnumerateArray().Select(s => s.GetProperty("key").GetString()));
        Assert.Contains(key, (await admin.GetJsonAsync("api/v1/spaces?scope=administration")).EnumerateArray().Select(s => s.GetProperty("key").GetString()));
        Assert.Contains(key, (await member.GetJsonAsync("api/v1/spaces?scope=administration")).EnumerateArray().Select(s => s.GetProperty("key").GetString())); // members just get their visible list

        var permissions = await admin.GetJsonAsync($"api/v1/spaces/{key}/permissions");
        Assert.NotEmpty(permissions.GetProperty("grants").EnumerateArray());
        using (var content = await admin.GetAsync($"api/v1/content/{home}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, content.StatusCode); // no View bypass for administrators
        }

        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var memberId = (await member.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        using (var grant = await admin.PutAsync($"api/v1/spaces/{key}/permissions", new
        {
            grants = new[]
            {
                new { subjectType = "User", subjectId = memberId, action = "ManagePermissions", effect = "Allow" },
                new { subjectType = "User", subjectId = memberId, action = "View", effect = "Allow" },
                new { subjectType = "User", subjectId = adminId, action = "View", effect = "Allow" },
            },
        }))
        {
            await ReadAsync(grant);
        }

        Assert.Equal("My notes", (await admin.GetJsonAsync($"api/v1/content/{home}")).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Permission_subjects_must_belong_to_the_tenant()
    {
        var (tenantA, adminA, _) = await _kit.Identity.TenantWithAdminAsync();
        var (tenantB, adminB, _) = await _kit.Identity.TenantWithAdminAsync();
        _ = tenantA;
        var (_, _, foreignUser) = await _kit.Identity.AddMemberAsync(tenantB, adminB);
        var foreignGroup = Id(await adminB.PostOkAsync("api/v1/tenant/groups", new { name = "B group", description = "" }));
        var adminId = (await adminA.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var key = (await CreateSpaceAsync(adminA)).GetProperty("key").GetString()!;

        foreach (var (type, id) in new[] { ("User", foreignUser), ("Group", foreignGroup) })
        {
            using var put = await adminA.PutAsync($"api/v1/spaces/{key}/permissions", new
            {
                grants = new[]
                {
                    new { subjectType = "User", subjectId = adminId, action = "ManagePermissions", effect = "Allow" },
                    new { subjectType = type, subjectId = id, action = "View", effect = "Allow" },
                },
            });
            await Browser.AssertStatusAsync(put, HttpStatusCode.BadRequest, "permission.subject_not_found");
        }

        var page = Id(await CreatePageAsync(adminA, key, "P"));
        using var restrict = await adminA.PutAsync($"api/v1/content/{page}/restrictions", new { view = new object[] { new { subjectType = "User", subjectId = adminId }, new { subjectType = "Group", subjectId = foreignGroup } }, edit = Array.Empty<object>() });
        await Browser.AssertStatusAsync(restrict, HttpStatusCode.BadRequest, "permission.subject_not_found");

        // The directory never lists principals of another tenant.
        Assert.Empty((await adminA.GetJsonAsync("api/v1/directory?q=B%20group")).EnumerateArray());
    }
}
