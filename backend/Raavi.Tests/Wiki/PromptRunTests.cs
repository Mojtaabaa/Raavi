using System.Net;
using System.Text.Json;
using Raavi.AI.PromptHub;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Prompt Hub phases 2–4 (ADR-052): templates, context, preview/copy/run, usage numbers, ratings, forks and versions.</summary>
public sealed class PromptTemplateTests
{
    [Fact]
    public void Variables_defaults_and_conditions_render_deterministically_and_nothing_is_evaluated()
    {
        var p = PromptTemplate.Parse("Hi {{user.name}}. {{#if input.extra}}Extra: {{input.extra}}.{{/if}}{{#unless input.extra}}No extra.{{/unless}} Tone: {{input.tone|neutral}}. {{page.content}}");
        Assert.Empty(p.Errors);
        Assert.Equal(["input.extra", "input.tone", "page.content", "user.name"], p.Names.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(["page.content", "user.name"], p.Required.OrderBy(x => x, StringComparer.Ordinal).ToArray()); // conditional and defaulted ones are optional
        var values = new Dictionary<string, string> { ["user.name"] = "Sara", ["page.content"] = "BODY" };
        Assert.Equal("Hi Sara. No extra. Tone: neutral. BODY", PromptTemplate.Render(p, values));
        values["input.extra"] = "more";
        Assert.Equal("Hi Sara. Extra: more. Tone: neutral. BODY", PromptTemplate.Render(p, values));
        // values are data: a value that looks like a tag is not expanded, and code-like text is inert
        values["page.content"] = "{{user.name}} {{#if x}} ${1+1} <script>";
        Assert.Contains("{{user.name}} {{#if x}} ${1+1} <script>", PromptTemplate.Render(p, values), StringComparison.Ordinal);
        Assert.Equal("false", PromptTemplate.Render(PromptTemplate.Parse("{{input.flag}}"), new Dictionary<string, string> { ["input.flag"] = "false" }));
        Assert.Equal("", PromptTemplate.Render(PromptTemplate.Parse("{{#if input.flag}}shown{{/if}}"), new Dictionary<string, string> { ["input.flag"] = "false" })); // "false" is not truthy
    }

    [Fact]
    public void Broken_templates_are_reported()
    {
        Assert.Contains(PromptTemplate.Parse("{{#if a}}open").Errors, e => e.StartsWith("template.unclosed", StringComparison.Ordinal));
        Assert.Contains(PromptTemplate.Parse("close{{/if}}").Errors, e => e.StartsWith("template.unexpected_close", StringComparison.Ordinal));
        Assert.Contains(PromptTemplate.Parse("{{ not a name }}").Errors, e => e.StartsWith("template.invalid_name", StringComparison.Ordinal));
        Assert.Empty(PromptTemplate.Parse("plain text with { braces } only").Errors);
        Assert.Equal(1, PromptTemplate.EstimateTokens("abc"));
    }

    [Fact]
    public void Diff_marks_added_and_removed_lines()
    {
        var d = Raavi.AI.PromptHub.PromptCommunityService.Diff(["a", "b", "c"], ["a", "x", "c", "d"]);
        Assert.Equal(["same:a", "del:b", "add:x", "same:c", "add:d"], d.Select(l => l.Kind + ":" + l.Text).ToArray());
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class PromptRunTests(SqlServerFixture fx)
{
    private const string AiUrl = "https://ai.example.test/v1";
    private readonly WikiTestKit _kit = new(fx);

    private static async Task<JsonElement> PromptAsync(Browser b, string title, string text, string[]? contexts = null, object? variables = null, int modes = 7)
    {
        using var r = await b.PostAsync("api/v1/prompts", new { title, contexts = contexts ?? ["page"], modes, content = Doc(text), variables });
        return await ReadAsync(r);
    }

    private static Guid PId(JsonElement detail) => detail.GetProperty("prompt").GetProperty("id").GetGuid();

    private static async Task<JsonElement> PublishAsync(Browser b, Guid id, bool share = false)
    {
        using (var p = await b.PostAsync($"api/v1/prompts/{id}/publish", new { })) { await ReadAsync(p); }
        if (!share)
        {
            return await b.GetJsonAsync($"api/v1/prompts/{id}");
        }

        using var v = await b.PostAsync($"api/v1/prompts/{id}/visibility", new { visibility = "Tenant" });
        return await ReadAsync(v);
    }

    private static async Task<HttpResponseMessage> RenderAsync(Browser b, Guid id, object body) => await b.PostAsync($"api/v1/prompts/{id}/render", body);

    private static async Task SaveAiAsync(Browser admin)
    {
        var (current, etag) = await GetWithETagAsync(admin, "api/v1/ai/settings");
        var payload = new { enabled = true, baseUrl = AiUrl, model = "wiki-model", apiKey = "sk-test", monthlyTokenCap = 0, maxOutputTokens = 300, embeddingModel = (string?)null };
        using var r = current.GetProperty("revision").GetInt32() == 0 ? await admin.PutAsync("api/v1/ai/settings", payload) : await admin.SendAsync(HttpMethod.Put, "api/v1/ai/settings", payload, withXsrf: true, ifMatch: etag);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Publishing_rejects_unsupported_variables_and_broken_templates()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var bad = await PromptAsync(admin, "Bad", "Use {{page.titel}} and {{input.nothing}}");
        using (var r = await admin.PostAsync($"api/v1/prompts/{PId(bad)}/publish", new { }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            var body = await r.Content.ReadAsStringAsync();
            Assert.Contains("page.titel", body, StringComparison.Ordinal);
            Assert.Contains("input.nothing", body, StringComparison.Ordinal);
        }

        var broken = await PromptAsync(admin, "Broken", "{{#if input.a}}never closed");
        using (var r = await admin.PostAsync($"api/v1/prompts/{PId(broken)}/publish", new { })) { Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); }
        var catalog = await admin.GetJsonAsync("api/v1/prompt-variables");
        Assert.Contains(catalog.GetProperty("variables").EnumerateArray(), v => v.GetProperty("name").GetString() == "page.content");
    }

    [Fact]
    public async Task Rendering_uses_only_context_the_person_may_read_and_validates_typed_inputs()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var open = (await CreateSpaceAsync(admin, name: "Open")).GetProperty("key").GetString()!;
        var secret = (await CreateSpaceAsync(admin, visibility: "Private", name: "Secret")).GetProperty("key").GetString()!;
        var openPage = await CreatePageAsync(admin, open, "Roadmap", body: Doc("Ship the thing in March."));
        var secretPage = await CreatePageAsync(admin, secret, "Salaries", body: Doc("Confidential numbers."));
        using (var pub = await admin.PostAsync($"api/v1/content/{Id(openPage)}/publish", new { })) { _ = pub.StatusCode; }
        using (var pub = await admin.PostAsync($"api/v1/content/{Id(secretPage)}/publish", new { })) { _ = pub.StatusCode; }

        var vars = new object[] { new { name = "input.audience", label = "Audience", type = "select", required = true, options = new[] { "team", "board" } }, new { name = "input.words", type = "number", required = false, @default = "100" } };
        var created = await PromptAsync(member, "Summarise", "For {{input.audience}} in {{input.words}} words: {{page.title}} — {{page.content}} ({{page.url}})", variables: vars);
        var id = PId(created);
        await PublishAsync(member, id);

        using (var ok = await RenderAsync(member, id, new { context = new { kind = "page", pageId = Id(openPage) }, inputs = new { } }))
        {
            var r = await ReadAsync(ok);
            Assert.False(r.GetProperty("ready").GetBoolean());
            Assert.Contains("input.audience", r.GetProperty("missing").EnumerateArray().Select(x => x.GetString()));
        }

        using (var ok = await RenderAsync(member, id, new { context = new { kind = "page", pageId = Id(openPage) }, inputs = new Dictionary<string, object> { ["input.audience"] = "board" } }))
        {
            var r = await ReadAsync(ok);
            Assert.True(r.GetProperty("ready").GetBoolean());
            var text = r.GetProperty("text").GetString()!;
            Assert.Contains("For board in 100 words: Roadmap — Ship the thing in March.", text, StringComparison.Ordinal); // default applied
            Assert.Contains($"/spaces/{open.ToUpperInvariant()}/pages/{Id(openPage)}", text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("page", r.GetProperty("sources")[0].GetProperty("kind").GetString());
            Assert.True(r.GetProperty("tokenEstimate").GetInt32() > 0);
        }

        using (var invalid = await RenderAsync(member, id, new { context = new { kind = "page", pageId = Id(openPage) }, inputs = new Dictionary<string, object> { ["input.audience"] = "everyone" } })) { Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        using (var number = await RenderAsync(member, id, new { context = new { kind = "page", pageId = Id(openPage) }, inputs = new Dictionary<string, object> { ["input.audience"] = "team", ["input.words"] = "many" } })) { Assert.Equal(HttpStatusCode.BadRequest, number.StatusCode); }
        // a page the person may not read is "not found" — and nothing of it is rendered
        using (var denied = await RenderAsync(member, id, new { context = new { kind = "page", pageId = Id(secretPage) }, inputs = new Dictionary<string, object> { ["input.audience"] = "team" } }))
        {
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.DoesNotContain("Confidential", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // a kind the prompt does not declare, and a kind that has no integration
        using (var other = await RenderAsync(member, id, new { context = new { kind = "space", spaceKey = open } })) { Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode); }
        var report = await PromptAsync(member, "Report", "Analyse {{templar.report}}", contexts: ["templar-report"]);
        using (var no = await RenderAsync(member, PId(report), new { context = new { kind = "templar-report" } })) { Assert.True(no.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict); }
    }

    [Fact]
    public async Task Selection_section_and_space_contexts_fill_their_variables()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin, name: "Docs")).GetProperty("key").GetString()!;
        var page = await CreatePageAsync(admin, key, "Guide", body: Doc("Intro text.", "More text."));
        using (var pub = await admin.PostAsync($"api/v1/content/{Id(page)}/publish", new { })) { _ = pub.StatusCode; }
        var sel = await PromptAsync(admin, "Explain", "Explain: {{selected.text}} (from {{page.title}})", contexts: ["selection"]);
        await PublishAsync(admin, PId(sel));
        using (var r = await RenderAsync(admin, PId(sel), new { context = new { kind = "selection", pageId = Id(page), selectedText = "Intro text." } }))
        {
            Assert.Equal("Explain: Intro text. (from Guide)", (await ReadAsync(r)).GetProperty("text").GetString());
        }

        using (var empty = await RenderAsync(admin, PId(sel), new { context = new { kind = "selection", pageId = Id(page), selectedText = "" } })) { Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode); }
        var space = await PromptAsync(admin, "Overview", "{{space.name}}: {{space.pages}}", contexts: ["space"]);
        await PublishAsync(admin, PId(space));
        using (var r = await RenderAsync(admin, PId(space), new { context = new { kind = "space", spaceKey = key } }))
        {
            var text = (await ReadAsync(r)).GetProperty("text").GetString()!;
            Assert.StartsWith("Docs:", text, StringComparison.Ordinal);
            Assert.Contains("- Guide", text, StringComparison.Ordinal);
        }

        var generic = await PromptAsync(admin, "Free", "Translate: {{context.text}}", contexts: ["generic"]);
        await PublishAsync(admin, PId(generic));
        using (var r = await RenderAsync(admin, PId(generic), new { context = new { kind = "generic", text = "hello" } })) { Assert.Equal("Translate: hello", (await ReadAsync(r)).GetProperty("text").GetString()); }
        Assert.NotNull(tenant);
    }

    [Fact]
    public async Task Preview_and_copy_never_call_the_AI_and_are_counted_apart_from_runs()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        await SaveAiAsync(admin);
        fx.Ai.Reset();
        var p = await PromptAsync(admin, "Copyable", "Say {{context.text}}", contexts: ["generic"]);
        var id = PId(p);
        await PublishAsync(admin, id);

        for (var i = 0; i < 3; i++)
        {
            using var r = await RenderAsync(admin, id, new { context = new { kind = "generic", text = "hi" } }); // previews
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        var before = await admin.GetJsonAsync($"api/v1/prompts/{id}/stats");
        Assert.Equal(0, before.GetProperty("totalUses").GetInt32());
        Assert.Equal(0, before.GetProperty("manualCopies").GetInt32());

        using (var c = await admin.PostAsync($"api/v1/prompts/{id}/copied", new { context = "generic" })) { Assert.Equal(HttpStatusCode.NoContent, c.StatusCode); }
        using (var c = await admin.PostAsync($"api/v1/prompts/{id}/copied", new { context = "generic" })) { _ = c.StatusCode; }
        var afterCopy = await admin.GetJsonAsync($"api/v1/prompts/{id}/stats");
        Assert.Equal(2, afterCopy.GetProperty("manualCopies").GetInt32());
        Assert.Equal(0, afterCopy.GetProperty("totalUses").GetInt32()); // copies are not uses
        Assert.Equal(0, afterCopy.GetProperty("automaticExecutions").GetInt32());
        using (var m = await admin.PostAsync($"api/v1/prompts/{id}/manual-use", new { context = "generic" })) { Assert.Equal(HttpStatusCode.NoContent, m.StatusCode); }
        var confirmed = await admin.GetJsonAsync($"api/v1/prompts/{id}/stats");
        Assert.Equal(1, confirmed.GetProperty("confirmedManualUses").GetInt32());
        Assert.Equal(1, confirmed.GetProperty("totalUses").GetInt32());
        Assert.Equal(1, confirmed.GetProperty("uniqueUsers").GetInt32());
        Assert.Empty(fx.Ai.Calls); // nothing above talked to a model
    }

    [Fact]
    public async Task Automatic_execution_needs_consent_runs_through_Managed_AI_records_usage_and_keeps_results_private()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (other, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        var p = await PromptAsync(admin, "Runner", "Reply to: {{context.text}}", contexts: ["generic"]);
        var id = PId(p);
        await PublishAsync(admin, id, share: true);
        var request = new { render = new { context = new { kind = "generic", text = "ping" } }, confirmed = true };

        using (var notConfigured = await admin.PostAsync($"api/v1/prompts/{id}/execute", request)) { Assert.Equal(HttpStatusCode.Conflict, notConfigured.StatusCode); }
        await SaveAiAsync(admin);
        fx.Ai.Reset();
        fx.Ai.Reply = "pong";
        using (var noConsent = await admin.PostAsync($"api/v1/prompts/{id}/execute", new { render = new { context = new { kind = "generic", text = "ping" } }, confirmed = false })) { Assert.Equal(HttpStatusCode.BadRequest, noConsent.StatusCode); }
        Assert.Empty(fx.Ai.Calls);

        using var run = await member.PostAsync($"api/v1/prompts/{id}/execute", request);
        var done = await ReadAsync(run);
        Assert.Equal("Completed", done.GetProperty("status").GetString());
        Assert.Equal("pong", done.GetProperty("result").GetString());
        Assert.Equal(1, done.GetProperty("versionNumber").GetInt32());
        var call = Assert.Single(fx.Ai.Calls);
        var sent = call.Body.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Assert.Contains("Reply to: <<<context.text\nping\ncontext.text>>>", sent, StringComparison.Ordinal); // untrusted context is fenced
        Assert.DoesNotContain("sk-test", call.Body.ToString(), StringComparison.Ordinal);

        var stats = await admin.GetJsonAsync($"api/v1/prompts/{id}/stats");
        Assert.Equal(1, stats.GetProperty("automaticExecutions").GetInt32());
        Assert.Equal(1, stats.GetProperty("successfulExecutions").GetInt32());
        Assert.Equal(1, stats.GetProperty("totalUses").GetInt32());
        Assert.Equal(150, stats.GetProperty("tokensUsed").GetInt64());
        Assert.Equal(1, stats.GetProperty("versions")[0].GetProperty("completed").GetInt32());

        // the result belongs to the person who ran it; another workspace cannot see it at all
        var execId = done.GetProperty("id").GetGuid();
        Assert.Equal("pong", (await member.GetJsonAsync($"api/v1/prompt-executions/{execId}")).GetProperty("result").GetString());
        using (var theirs = await admin.GetAsync($"api/v1/prompt-executions/{execId}")) { Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode); }
        using (var foreign = await otherAdmin.GetAsync($"api/v1/prompt-executions/{execId}")) { Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); }
        Assert.Single((await member.GetJsonAsync("api/v1/prompt-executions")).EnumerateArray());

        // a failing provider is recorded as a failure, with a class and no result
        fx.Ai.Status = HttpStatusCode.InternalServerError;
        using (var failed = await member.PostAsync($"api/v1/prompts/{id}/execute", request)) { Assert.False(failed.IsSuccessStatusCode); }
        fx.Ai.Status = HttpStatusCode.OK;
        var after = await admin.GetJsonAsync($"api/v1/prompts/{id}/stats");
        Assert.Equal(2, after.GetProperty("automaticExecutions").GetInt32());
        Assert.Equal(1, after.GetProperty("failedExecutions").GetInt32());
        Assert.NotNull(other);

        // the modes are respected: a copy-only prompt cannot be run
        var copyOnly = await PromptAsync(admin, "Copy only", "x {{context.text}}", contexts: ["generic"], modes: 2);
        await PublishAsync(admin, PId(copyOnly));
        using (var denied = await admin.PostAsync($"api/v1/prompts/{PId(copyOnly)}/execute", request)) { Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode); }
    }

    [Fact]
    public async Task Ratings_are_one_per_person_only_for_public_prompts_and_never_from_the_owner()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (owner, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (fan1, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (fan2, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var p = await PromptAsync(owner, "Rated", "Hello {{user.name}}", contexts: ["generic"]);
        var id = PId(p);
        await PublishAsync(owner, id);
        using (var early = await fan1.PutAsync($"api/v1/prompts/{id}/rating", new { stars = 5 })) { Assert.Equal(HttpStatusCode.NotFound, early.StatusCode); } // still personal: invisible
        using (var sh = await owner.PostAsync($"api/v1/prompts/{id}/visibility", new { visibility = "Tenant" })) { Assert.Equal(HttpStatusCode.OK, sh.StatusCode); }

        using (var own = await owner.PutAsync($"api/v1/prompts/{id}/rating", new { stars = 5 })) { Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode); }
        using (var bad = await fan1.PutAsync($"api/v1/prompts/{id}/rating", new { stars = 6 })) { Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); }
        using (var one = await fan1.PutAsync($"api/v1/prompts/{id}/rating", new { stars = 4, feedback = "useful", comment = "nice" })) { Assert.Equal(4, (await ReadAsync(one)).GetProperty("mine").GetInt32()); }
        using (var again = await fan1.PutAsync($"api/v1/prompts/{id}/rating", new { stars = 2, feedback = "outdated" })) { Assert.Equal(1, (await ReadAsync(again)).GetProperty("count").GetInt32()); } // changed, not added
        using (var two = await fan2.PutAsync($"api/v1/prompts/{id}/rating", new { stars = 5 }))
        {
            var r = await ReadAsync(two);
            Assert.Equal(2, r.GetProperty("count").GetInt32());
            Assert.Equal(3.5, r.GetProperty("average").GetDouble());
            Assert.Equal(1, r.GetProperty("distribution")[1].GetInt32());
            Assert.Single(r.GetProperty("entries").EnumerateArray()); // a fan sees only their own entry, not other people's comments
        }

        var asOwner = await owner.GetJsonAsync($"api/v1/prompts/{id}/ratings");
        Assert.Equal(2, asOwner.GetProperty("entries").GetArrayLength());
        var listed = (await fan1.GetJsonAsync("api/v1/prompts?scope=public&sort=rated")).GetProperty("items")[0];
        Assert.Equal(3.5, listed.GetProperty("ratingAverage").GetDouble());
        Assert.Equal(2, listed.GetProperty("myRating").GetInt32());
        using (var removed = await fan1.DeleteAsync($"api/v1/prompts/{id}/rating")) { Assert.Equal(1, (await ReadAsync(removed)).GetProperty("count").GetInt32()); }
    }

    [Fact]
    public async Task A_fork_is_an_independent_personal_copy_and_versions_can_be_compared_and_restored()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (owner, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (forker, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var p = await PromptAsync(owner, "Original", "Line one\nLine two", contexts: ["generic"]);
        var id = PId(p);
        var v1 = (await PublishAsync(owner, id, share: true)).GetProperty("published").GetProperty("id").GetGuid();

        using var forkResponse = await forker.PostAsync($"api/v1/prompts/{id}/fork");
        var fork = await ReadAsync(forkResponse);
        var forkId = PId(fork);
        Assert.NotEqual(id, forkId);
        Assert.Equal("Personal", fork.GetProperty("prompt").GetProperty("visibility").GetString());
        Assert.Equal(id, fork.GetProperty("prompt").GetProperty("forkedFromId").GetGuid());
        Assert.Equal("Original", fork.GetProperty("prompt").GetProperty("forkedFromTitle").GetString());
        Assert.True(fork.GetProperty("prompt").GetProperty("isOwner").GetBoolean());
        using (var own = await owner.PostAsync($"api/v1/prompts/{forkId}/fork")) { Assert.Equal(HttpStatusCode.NotFound, own.StatusCode); } // someone else's personal prompt cannot be forked

        // changing the fork leaves the original alone, and changing the original leaves the fork alone
        using (var edit = await forker.PutAsync($"api/v1/prompts/{forkId}", new { title = "My version", content = Doc("Forked text") })) { Assert.Equal(HttpStatusCode.OK, edit.StatusCode); }
        Assert.Equal("Original", (await owner.GetJsonAsync($"api/v1/prompts/{id}")).GetProperty("prompt").GetProperty("title").GetString());
        using (var edit = await owner.PutAsync($"api/v1/prompts/{id}", new { content = Doc("Line one\nLine 2 changed\nLine three") })) { Assert.Equal(HttpStatusCode.OK, edit.StatusCode); }
        var v2Detail = await PublishAsync(owner, id, share: true);
        Assert.Equal(2, v2Detail.GetProperty("published").GetProperty("number").GetInt32());
        Assert.Contains("Forked text", (await forker.GetJsonAsync($"api/v1/prompts/{forkId}")).GetProperty("contentText").GetString(), StringComparison.Ordinal);

        // history (a reader sees published versions only), diff, restore
        var v2 = v2Detail.GetProperty("published").GetProperty("id").GetGuid();
        Assert.Equal(2, (await forker.GetJsonAsync($"api/v1/prompts/{id}/versions")).GetArrayLength());
        var diff = await owner.GetJsonAsync($"api/v1/prompts/{id}/diff?from={v1}&to={v2}");
        Assert.True(diff.GetProperty("added").GetInt32() >= 2);
        Assert.True(diff.GetProperty("removed").GetInt32() >= 1);
        using (var denied = await forker.PostAsync($"api/v1/prompts/{id}/versions/{v1}/restore", new { })) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        using (var restore = await owner.PostAsync($"api/v1/prompts/{id}/versions/{v1}/restore", new { discardDraft = false }))
        {
            var restored = await ReadAsync(restore);
            Assert.Equal(3, restored.GetProperty("draft").GetProperty("number").GetInt32()); // history is not rewritten: a new draft
            Assert.Contains("Line two", restored.GetProperty("contentText").GetString(), StringComparison.Ordinal);
        }

        using (var again = await owner.PostAsync($"api/v1/prompts/{id}/versions/{v1}/restore", new { discardDraft = false })) { Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); } // a draft exists
        // v1 itself is still the text it was
        var old = await owner.GetJsonAsync($"api/v1/prompts/{id}/versions/{v1}");
        Assert.Contains("Line two", old.GetProperty("contentText").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("changed", old.GetProperty("contentText").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workspace_analytics_are_for_administrators_only()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var p = await PromptAsync(member, "Counted", "x {{context.text}}", contexts: ["generic"]);
        await PublishAsync(member, PId(p), share: true);
        using (var c = await admin.PostAsync($"api/v1/prompts/{PId(p)}/copied", new { context = "generic" })) { Assert.Equal(HttpStatusCode.NoContent, c.StatusCode); }
        using (var denied = await member.GetAsync("api/v1/prompt-analytics")) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        var report = await admin.GetJsonAsync("api/v1/prompt-analytics");
        Assert.Equal(1, report.GetProperty("copies").GetInt32());
        Assert.Equal("Counted", report.GetProperty("prompts")[0].GetProperty("title").GetString());
        using (var stats = await admin.GetAsync($"api/v1/prompts/{PId(p)}/stats")) { Assert.Equal(HttpStatusCode.OK, stats.StatusCode); } // administrators may read stats of public prompts
        var other = await PromptAsync(member, "Hidden", "x", contexts: ["generic"]);
        using (var hidden = await admin.GetAsync($"api/v1/prompts/{PId(other)}/stats")) { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
    }
}
