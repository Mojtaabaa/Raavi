using System.Net;
using System.Text.Json;
using Raavi.Domain.Common;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Domain;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 10b: outcome rules of approval requests — no SQL needed.</summary>
public sealed class ApprovalDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static (ApprovalRequest Request, List<ApprovalAssignment> Slots) Make(ApprovalMode mode, int approvers = 2)
    {
        var request = ApprovalRequest.Create(Guid.NewGuid(), Guid.NewGuid(), 3, "please", mode, null, Guid.NewGuid(), Now);
        return (request, Enumerable.Range(0, approvers).Select(_ => ApprovalAssignment.Create(request.Id, Guid.NewGuid())).ToList());
    }

    private static string Code(Action a) => Assert.Throws<DomainException>(a).Code;

    [Fact]
    public void All_needs_every_approval_Any_needs_one_and_one_rejection_ends_it()
    {
        var (all, slots) = Make(ApprovalMode.All);
        slots[0].Decide(ApprovalDecision.Approved, null, Now);
        Assert.False(all.Resolve(slots, Now));
        Assert.Equal(ApprovalStatus.Pending, all.Status);
        slots[1].Decide(ApprovalDecision.Approved, "ok", Now);
        Assert.True(all.Resolve(slots, Now));
        Assert.Equal((ApprovalStatus.Approved, 3), (all.Status, all.Revision)); // every decision bumps the concurrency token
        Assert.False(all.Resolve(slots, Now)); // final: nothing changes any more

        var (any, anySlots) = Make(ApprovalMode.Any, 3);
        anySlots[1].Decide(ApprovalDecision.Approved, null, Now);
        Assert.True(any.Resolve(anySlots, Now));
        Assert.Equal(ApprovalStatus.Approved, any.Status);

        var (rejected, rejSlots) = Make(ApprovalMode.Any, 3);
        rejSlots[0].Decide(ApprovalDecision.Approved, null, Now);
        rejSlots[2].Decide(ApprovalDecision.Rejected, "no", Now);
        Assert.True(rejected.Resolve(rejSlots, Now));
        Assert.Equal(ApprovalStatus.Rejected, rejected.Status); // a rejection wins even over an approval in mode Any

        var (cancelled, _) = Make(ApprovalMode.All);
        cancelled.Cancel(Now);
        Assert.Equal(ApprovalStatus.Cancelled, cancelled.Status);
        Assert.Equal("approval.not_pending", Code(() => cancelled.Cancel(Now)));
    }

    [Fact]
    public void Inputs_are_validated()
    {
        Assert.Equal("approval.note_invalid", Code(() => ApprovalRequest.Create(Guid.NewGuid(), Guid.NewGuid(), 1, new string('n', 501), ApprovalMode.All, null, Guid.NewGuid(), Now)));
        Assert.Equal("approval.mode_invalid", Code(() => ApprovalRequest.Create(Guid.NewGuid(), Guid.NewGuid(), 1, null, (ApprovalMode)9, null, Guid.NewGuid(), Now)));
        Assert.Equal("approval.due_invalid", Code(() => ApprovalRequest.Create(Guid.NewGuid(), Guid.NewGuid(), 1, null, ApprovalMode.All, Now.AddMinutes(-1), Guid.NewGuid(), Now)));
        Assert.Equal("approval.due_invalid", Code(() => ApprovalRequest.Create(Guid.NewGuid(), Guid.NewGuid(), 1, null, ApprovalMode.All, Now.AddYears(2), Guid.NewGuid(), Now)));
        var due = ApprovalRequest.Create(Guid.NewGuid(), Guid.NewGuid(), 1, null, ApprovalMode.All, Now.AddDays(1), Guid.NewGuid(), Now);
        Assert.False(due.IsOverdue(Now));
        Assert.True(due.IsOverdue(Now.AddDays(2)));

        var slot = ApprovalAssignment.Create(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal("approval.decision_invalid", Code(() => slot.Decide(ApprovalDecision.Pending, null, Now)));
        Assert.Equal("approval.comment_required", Code(() => slot.Decide(ApprovalDecision.Rejected, "  ", Now)));
        Assert.Equal("approval.comment_invalid", Code(() => slot.Decide(ApprovalDecision.Approved, new string('c', 1001), Now)));
        slot.Decide(ApprovalDecision.Approved, " fine ", Now);
        Assert.Equal("fine", slot.Comment);
        Assert.Equal("approval.already_decided", Code(() => slot.Decide(ApprovalDecision.Rejected, "late", Now)));
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class ApprovalTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static async Task<Guid> MeAsync(Browser b) => (await b.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();

    private static async Task<JsonElement> RequestAsync(Browser b, Guid page, object body)
    {
        using var r = await b.PostAsync($"api/v1/content/{page}/approvals", body);
        return await ReadAsync(r);
    }

    private static async Task<JsonElement> DecideAsync(Browser b, Guid id, string decision, string? comment = null)
    {
        using var r = await b.PostAsync($"api/v1/approvals/{id}/decision", new { decision, comment });
        return await ReadAsync(r);
    }

    private static async Task<JsonElement[]> InboxAsync(Browser b) => (await b.GetJsonAsync("api/v1/approvals?scope=inbox")).EnumerateArray().ToArray();

    private static async Task<string[]> KindsAsync(Browser b) =>
        (await b.GetJsonAsync("api/v1/notifications")).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("kind").GetString()!).ToArray();

    [Fact]
    public async Task Everyone_must_approve_in_mode_All_and_the_requester_hears_the_outcome()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (a, _, aId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (b, _, bId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Security policy"));

        var request = await RequestAsync(admin, page, new { note = "Ready for sign-off", mode = "All", approverIds = new[] { aId, bId }, dueAt = DateTimeOffset.UtcNow.AddDays(3) });
        var id = request.GetProperty("id").GetGuid();
        Assert.Equal(("Pending", "All", "Security policy"), (request.GetProperty("status").GetString(), request.GetProperty("mode").GetString(), request.GetProperty("contentTitle").GetString()));
        Assert.Equal(2, request.GetProperty("approvers").GetArrayLength());
        Assert.False(request.GetProperty("canDecide").GetBoolean());
        Assert.True(request.GetProperty("canCancel").GetBoolean());

        await _kit.DispatchOutboxAsync();
        Assert.Contains("approval.requested", await KindsAsync(a));
        Assert.Contains("approval.requested", await KindsAsync(b));
        Assert.Equal(id, Assert.Single(await InboxAsync(a)).GetProperty("id").GetGuid());
        Assert.True((await a.GetJsonAsync($"api/v1/approvals/{id}")).GetProperty("canDecide").GetBoolean());

        var afterA = await DecideAsync(a, id, "Approved", "Looks right");
        Assert.Equal("Pending", afterA.GetProperty("status").GetString());
        Assert.Empty(await InboxAsync(a)); // decided: no longer waiting for A
        await _kit.DispatchOutboxAsync();
        Assert.DoesNotContain("approval.approved", await KindsAsync(admin)); // not final yet

        var afterB = await DecideAsync(b, id, "Approved");
        Assert.Equal("Approved", afterB.GetProperty("status").GetString());
        await _kit.DispatchOutboxAsync();
        Assert.Contains("approval.approved", await KindsAsync(admin));

        using (var again = await b.PostAsync($"api/v1/approvals/{id}/decision", new { decision = "Rejected", comment = "changed my mind" }))
        {
            await Browser.AssertStatusAsync(again, HttpStatusCode.Conflict, "approval.not_pending");
        }

        // The page is shown with its history and the "changed since" flag follows the page.
        var listed = (await admin.GetJsonAsync($"api/v1/content/{page}/approvals")).EnumerateArray().ToArray();
        Assert.Equal(("Approved", false), (Assert.Single(listed).GetProperty("status").GetString(), listed[0].GetProperty("pageChanged").GetBoolean()));
        var (_, etag) = await GetWithETagAsync(admin, $"api/v1/content/{page}");
        using (var edit = await UpdatePageAsync(admin, page, etag, "Security policy", Doc("edited after approval")))
        {
            Assert.True(edit.IsSuccessStatusCode);
        }

        Assert.True((await admin.GetJsonAsync($"api/v1/approvals/{id}")).GetProperty("pageChanged").GetBoolean());
        Assert.Equal(id, (await admin.GetJsonAsync("api/v1/approvals?scope=requested"))[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task One_rejection_ends_the_request_and_needs_a_comment_and_cancel_tells_pending_approvers()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (a, _, aId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (b, _, bId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Budget"));

        var id = (await RequestAsync(admin, page, new { mode = "Any", approverIds = new[] { aId, bId } })).GetProperty("id").GetGuid();
        using (var noComment = await a.PostAsync($"api/v1/approvals/{id}/decision", new { decision = "Rejected" }))
        {
            await Browser.AssertStatusAsync(noComment, HttpStatusCode.BadRequest, "approval.comment_required");
        }

        using (var bad = await a.PostAsync($"api/v1/approvals/{id}/decision", new { decision = "Maybe" }))
        {
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest, "approval.decision_invalid");
        }

        var rejected = await DecideAsync(a, id, "Rejected", "Numbers are off");
        Assert.Equal("Rejected", rejected.GetProperty("status").GetString());
        Assert.Equal("Numbers are off", rejected.GetProperty("approvers").EnumerateArray().Single(x => x.GetProperty("userId").GetGuid() == aId).GetProperty("comment").GetString());
        await _kit.DispatchOutboxAsync();
        Assert.Contains("approval.rejected", await KindsAsync(admin));

        // A new request can follow; cancelling it notifies whoever has not decided yet.
        var second = (await RequestAsync(admin, page, new { approverIds = new[] { bId } })).GetProperty("id").GetGuid();
        using (var duplicate = await admin.PostAsync($"api/v1/content/{page}/approvals", new { approverIds = new[] { aId } }))
        {
            await Browser.AssertStatusAsync(duplicate, HttpStatusCode.Conflict, "approval.already_pending");
        }

        using (var cancel = await b.PostAsync($"api/v1/approvals/{second}/cancel", new { }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode); // approvers cannot cancel the requester's request
        }

        using (var ok = await admin.PostAsync($"api/v1/approvals/{second}/cancel", new { }))
        {
            Assert.Equal("Cancelled", (await ReadAsync(ok)).GetProperty("status").GetString());
        }

        await _kit.DispatchOutboxAsync();
        Assert.Contains("approval.cancelled", await KindsAsync(b));
        Assert.Empty(await InboxAsync(b));
    }

    [Fact]
    public async Task Requests_are_validated_and_restricted_to_editors_approvers_and_readers_of_the_page()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (viewer, _, viewerId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (outsider, _, outsiderId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (_, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        var adminId = await MeAsync(admin);
        var key = (await CreateSpaceAsync(admin, visibility: "Private")).GetProperty("key").GetString()!;
        var grants = new[] { "View", "ManagePermissions", "Edit", "Create", "Publish", "Move", "Archive", "Delete", "Comment", "ManageTemplates" }
            .Select(a => (object)new { subjectType = "User", subjectId = (Guid?)adminId, action = a, effect = "Allow" })
            .Append(new { subjectType = "User", subjectId = (Guid?)viewerId, action = "View", effect = "Allow" }).ToList();
        using (var put = await admin.PutAsync($"api/v1/spaces/{key}/permissions", new { grants }))
        {
            await ReadAsync(put);
        }

        var page = Id(await CreatePageAsync(admin, key, "Secret plan"));

        foreach (var (body, code) in new (object, string)[]
        {
            (new { approverIds = Array.Empty<Guid>() }, "approval.approvers_invalid"),
            (new { approverIds = new[] { adminId } }, "approval.self_approver"),
            (new { approverIds = new[] { Guid.NewGuid() } }, "approval.approvers_invalid"),
            (new { approverIds = new[] { outsiderId } }, "approval.approvers_invalid"), // cannot read the private page
            (new { approverIds = new[] { viewerId }, mode = "Majority" }, "approval.mode_invalid"),
            (new { approverIds = new[] { viewerId }, dueAt = DateTimeOffset.UtcNow.AddDays(-1) }, "approval.due_invalid"),
            (new { approverIds = new[] { viewerId }, note = new string('n', 501) }, "approval.note_invalid"),
        })
        {
            using var bad = await admin.PostAsync($"api/v1/content/{page}/approvals", body);
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest, code);
        }

        using (var byViewer = await viewer.PostAsync($"api/v1/content/{page}/approvals", new { approverIds = new[] { adminId } }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, byViewer.StatusCode); // reading is not enough to ask for approvals
        }

        var id = (await RequestAsync(admin, page, new { approverIds = new[] { viewerId } })).GetProperty("id").GetGuid();
        using (var notApprover = await admin.PostAsync($"api/v1/approvals/{id}/decision", new { decision = "Approved" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, notApprover.StatusCode);
        }

        Assert.Single(await InboxAsync(viewer));
        Assert.Empty(await InboxAsync(outsider));
        using (var badScope = await viewer.GetAsync("api/v1/approvals?scope=all"))
        {
            await Browser.AssertStatusAsync(badScope, HttpStatusCode.BadRequest, "approval.scope_invalid");
        }

        foreach (var who in new[] { outsider, otherAdmin })
        {
            foreach (var url in new[] { $"api/v1/approvals/{id}", $"api/v1/content/{page}/approvals" })
            {
                using var r = await who.GetAsync(url);
                Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            }

            using var decide = await who.PostAsync($"api/v1/approvals/{id}/decision", new { decision = "Approved" });
            Assert.Equal(HttpStatusCode.NotFound, decide.StatusCode);
        }
    }
}
