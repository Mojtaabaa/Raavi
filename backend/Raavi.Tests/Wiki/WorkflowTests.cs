using System.Net;
using System.Text.Json;
using Raavi.Domain.Common;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Domain;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 10a: definition rules of workflows — no SQL needed.</summary>
public sealed class WorkflowDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static readonly WfActionInput Label = new(WfActionType.AddLabel, "Needs-Review", null, null);

    private static Workflow Make(string? trigger = "content.created", WfConditionInput[]? conditions = null, params WfActionInput[] actions) =>
        Workflow.Create(Guid.NewGuid(), "Intake", true, trigger, conditions ?? [], actions.Length == 0 ? [Label] : actions, Guid.NewGuid(), Now);

    private static string Code(Action a) => Assert.Throws<DomainException>(a).Code;

    [Fact]
    public void Definitions_are_normalised_and_versioned()
    {
        var w = Make(conditions: [new(WfField.Title, WfOperator.Contains, "  RFC "), new(WfField.Label, WfOperator.Has, "Urgent")], actions: [Label, new(WfActionType.Notify, null, WfRecipients.Watchers, " Please review ")]);
        Assert.Equal("RFC", w.Conditions[0].Value);
        Assert.Equal("urgent", w.Conditions[1].Value); // label values are normalised like labels
        Assert.Equal("needs-review", w.Actions[0].Label);
        Assert.Equal(("Please review", WfRecipients.Watchers), (w.Actions[1].Message, w.Actions[1].Recipients));

        w.Update("Intake 2", false, "content.published", [], [Label], Now);
        Assert.Equal((2, false, "content.published"), (w.Revision, w.Enabled, w.Trigger));
    }

    [Fact]
    public void Bad_definitions_are_rejected_with_stable_codes()
    {
        Assert.Equal("workflow.name_invalid", Code(() => Workflow.Create(Guid.NewGuid(), " ", true, "content.created", [], [Label], Guid.NewGuid(), Now)));
        Assert.Equal("workflow.trigger_invalid", Code(() => Make("comment.created")));
        Assert.Equal("workflow.trigger_invalid", Code(() => Make(null)));
        Assert.Equal("workflow.actions_invalid", Code(() => Workflow.Create(Guid.NewGuid(), "n", true, "content.created", [], [], Guid.NewGuid(), Now)));
        Assert.Equal("workflow.actions_invalid", Code(() => Workflow.Create(Guid.NewGuid(), "n", true, "content.created", [], Enumerable.Repeat(Label, 6).ToList(), Guid.NewGuid(), Now)));
        Assert.Equal("workflow.too_many_conditions", Code(() => Make(conditions: Enumerable.Repeat(new WfConditionInput(WfField.Title, WfOperator.Contains, "x"), 11).ToArray())));
        Assert.Equal("workflow.condition_invalid", Code(() => Make(conditions: [new(WfField.Title, WfOperator.Has, "x")])));
        Assert.Equal("workflow.condition_invalid", Code(() => Make(conditions: [new(WfField.ContentType, WfOperator.Contains, "page")])));
        Assert.Equal("workflow.condition_invalid", Code(() => Make(conditions: [new(WfField.Title, WfOperator.Contains, " ")])));
        Assert.Equal("workflow.condition_invalid", Code(() => Make(conditions: [new((WfField)9, WfOperator.Equals, "x")])));
        Assert.Equal("label.invalid", Code(() => Make(actions: new WfActionInput(WfActionType.AddLabel, "has space", null, null))));
        Assert.Equal("workflow.action_invalid", Code(() => Make(actions: new WfActionInput(WfActionType.Notify, null, null, "hi"))));
        Assert.Equal("workflow.action_invalid", Code(() => Make(actions: new WfActionInput(WfActionType.Notify, null, WfRecipients.Actor, " "))));
        Assert.Equal("workflow.action_invalid", Code(() => Make(actions: new WfActionInput(WfActionType.Notify, null, WfRecipients.Actor, new string('m', 201)))));
        Assert.Equal("workflow.action_invalid", Code(() => Make(actions: new WfActionInput((WfActionType)7, null, null, null))));
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class WorkflowTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static async Task<JsonElement> CreateAsync(Browser b, string key, object body)
    {
        using var r = await b.PostAsync($"api/v1/spaces/{key}/workflows", body);
        return await ReadAsync(r);
    }

    private static async Task<JsonElement[]> NotificationsAsync(Browser b) =>
        (await b.GetJsonAsync("api/v1/notifications")).GetProperty("items").EnumerateArray().ToArray();

    private static async Task<string[]> LabelsAsync(Browser b, Guid page) =>
        (await b.GetJsonAsync($"api/v1/content/{page}")).GetProperty("labels").EnumerateArray().Select(l => l.GetString()!).ToArray();

    private static object Intake(bool enabled = true) => new
    {
        name = "RFC intake",
        enabled,
        trigger = "content.created",
        conditions = new object[] { new { field = "Title", @operator = "StartsWith", value = "rfc:" } },
        actions = new object[]
        {
            new { type = "AddLabel", label = "Needs-Review" },
            new { type = "Notify", recipients = "Actor", message = "Your RFC was received" },
        },
    };

    [Fact]
    public async Task A_rule_labels_and_notifies_for_matching_pages_only_and_logs_every_run_once()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var workflow = await CreateAsync(admin, key, Intake());
        var id = workflow.GetProperty("id").GetGuid();
        Assert.Equal(["Title", "StartsWith", "rfc:"], workflow.GetProperty("conditions")[0].EnumerateObject().Select(p => p.Value.GetString()!));
        Assert.Equal("Notify", workflow.GetProperty("actions")[1].GetProperty("type").GetString());

        var match = Id(await CreatePageAsync(admin, key, "RFC: Billing v2"));
        var other = Id(await CreatePageAsync(admin, key, "Meeting notes"));
        await _kit.DispatchOutboxAsync();

        Assert.Equal(["needs-review"], await LabelsAsync(admin, match));
        Assert.Empty(await LabelsAsync(admin, other));
        var inbox = await NotificationsAsync(admin);
        var note = Assert.Single(inbox, n => n.GetProperty("kind").GetString() == "workflow");
        Assert.Equal(("RFC: Billing v2", "Your RFC was received"), (note.GetProperty("title").GetString(), note.GetProperty("excerpt").GetString()));

        var runs = (await admin.GetJsonAsync($"api/v1/workflows/{id}/runs")).EnumerateArray().ToArray();
        Assert.Equal(2, runs.Length);
        var ok = runs.Single(r => r.GetProperty("status").GetString() == "Succeeded");
        Assert.Equal(("RFC: Billing v2", 1), (ok.GetProperty("contentTitle").GetString(), ok.GetProperty("attempts").GetInt32()));
        Assert.Contains("label 'needs-review' added", ok.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("conditions_not_met", runs.Single(r => r.GetProperty("status").GetString() == "Skipped").GetProperty("message").GetString());

        // Dispatching again must not repeat anything (idempotent), and a disabled rule stays quiet.
        await _kit.DispatchOutboxAsync();
        Assert.Equal(2, (await admin.GetJsonAsync($"api/v1/workflows/{id}/runs")).GetArrayLength());
        Assert.Single(await NotificationsAsync(admin), n => n.GetProperty("kind").GetString() == "workflow");

        var (_, etag) = await GetWithETagAsync(admin, $"api/v1/workflows/{id}");
        using (var off = await admin.SendAsync(HttpMethod.Put, $"api/v1/workflows/{id}", Intake(enabled: false), withXsrf: true, ifMatch: etag))
        {
            Assert.False((await ReadAsync(off)).GetProperty("enabled").GetBoolean());
        }

        await CreatePageAsync(admin, key, "RFC: Search");
        await _kit.DispatchOutboxAsync();
        Assert.Equal(2, (await admin.GetJsonAsync($"api/v1/workflows/{id}/runs")).GetArrayLength());
    }

    [Fact]
    public async Task Watchers_are_notified_by_a_rule_but_not_the_actor()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (watcher, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        await CreateAsync(admin, key, new
        {
            name = "Tell watchers",
            trigger = "content.published",
            actions = new object[] { new { type = "Notify", recipients = "Watchers", message = "Page changed" } },
        });
        var page = Id(await CreatePageAsync(admin, key, "Runbook"));
        await _kit.DispatchOutboxAsync();
        using (var watch = await watcher.PutAsync($"api/v1/content/{page}/watch", new { }))
        {
            Assert.True((await ReadAsync(watch)).GetProperty("watching").GetBoolean());
        }

        var (_, etag) = await GetWithETagAsync(admin, $"api/v1/content/{page}");
        using (var update = await UpdatePageAsync(admin, page, etag, "Runbook v2", Doc("changed")))
        {
            Assert.True(update.IsSuccessStatusCode);
        }

        await _kit.DispatchOutboxAsync();
        var note = Assert.Single(await NotificationsAsync(watcher), n => n.GetProperty("kind").GetString() == "workflow");
        Assert.Equal("Page changed", note.GetProperty("excerpt").GetString());
        Assert.DoesNotContain(await NotificationsAsync(admin), n => n.GetProperty("kind").GetString() == "workflow"); // the actor is not told about their own edit
    }

    [Fact]
    public async Task Definitions_are_validated_etagged_and_restricted_to_space_administrators()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (viewer, _, viewerId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (outsider, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (_, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var key = (await CreateSpaceAsync(admin, visibility: "Private")).GetProperty("key").GetString()!;
        var grants = new[] { "View", "ManagePermissions", "Edit", "Create", "Publish", "Move", "Archive", "Delete", "Comment", "ManageTemplates" }
            .Select(a => (object)new { subjectType = "User", subjectId = (Guid?)adminId, action = a, effect = "Allow" })
            .Append(new { subjectType = "User", subjectId = (Guid?)viewerId, action = "View", effect = "Allow" }).ToList();
        using (var put = await admin.PutAsync($"api/v1/spaces/{key}/permissions", new { grants }))
        {
            await ReadAsync(put);
        }

        Assert.Contains("content.published", (await admin.GetJsonAsync("api/v1/workflows/catalog")).GetProperty("triggers").EnumerateArray().Select(t => t.GetString()));
        var id = (await CreateAsync(admin, key, Intake())).GetProperty("id").GetGuid();

        var (_, etag) = await GetWithETagAsync(admin, $"api/v1/workflows/{id}");
        using (var missing = await admin.SendAsync(HttpMethod.Put, $"api/v1/workflows/{id}", Intake(), withXsrf: true))
        {
            await Browser.AssertStatusAsync(missing, HttpStatusCode.PreconditionRequired);
        }

        using (var ok = await admin.SendAsync(HttpMethod.Put, $"api/v1/workflows/{id}", Intake(), withXsrf: true, ifMatch: etag))
        {
            Assert.Equal(2, (await ReadAsync(ok)).GetProperty("revision").GetInt32());
        }

        using (var stale = await admin.SendAsync(HttpMethod.Put, $"api/v1/workflows/{id}", Intake(), withXsrf: true, ifMatch: etag))
        {
            await Browser.AssertStatusAsync(stale, HttpStatusCode.PreconditionFailed);
        }

        foreach (var (body, code) in new (object, string)[]
        {
            (new { name = "", trigger = "content.created", actions = new object[] { new { type = "AddLabel", label = "x" } } }, "workflow.name_invalid"),
            (new { name = "n", trigger = "comment.created", actions = new object[] { new { type = "AddLabel", label = "x" } } }, "workflow.trigger_invalid"),
            (new { name = "n", trigger = "content.created", actions = Array.Empty<object>() }, "workflow.actions_invalid"),
            (new { name = "n", trigger = "content.created", actions = new object[] { new { type = "Webhook" } } }, "workflow.action_invalid"),
            (new { name = "n", trigger = "content.created", conditions = new object[] { new { field = "Author", @operator = "Equals", value = "x" } }, actions = new object[] { new { type = "AddLabel", label = "x" } } }, "workflow.condition_invalid"),
        })
        {
            using var bad = await admin.PostAsync($"api/v1/spaces/{key}/workflows", body);
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest, code);
        }

        // A reader of the space may not even see the rules; everyone else does not know the space exists.
        foreach (var url in new[] { $"api/v1/workflows/{id}", $"api/v1/workflows/{id}/runs", $"api/v1/spaces/{key}/workflows" })
        {
            using var r = await viewer.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        using (var create = await viewer.PostAsync($"api/v1/spaces/{key}/workflows", Intake()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        using (var del = await viewer.DeleteAsync($"api/v1/workflows/{id}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
        }

        foreach (var who in new[] { outsider, otherAdmin })
        {
            foreach (var url in new[] { $"api/v1/workflows/{id}", $"api/v1/workflows/{id}/runs", $"api/v1/spaces/{key}/workflows" })
            {
                using var r = await who.GetAsync(url);
                Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            }
        }

        using (var del = await admin.DeleteAsync($"api/v1/workflows/{id}"))
        {
            Assert.True(del.IsSuccessStatusCode);
        }

        using var gone = await admin.GetAsync($"api/v1/workflows/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }
}
