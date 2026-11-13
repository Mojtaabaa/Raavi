using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Raavi.AI;
using Raavi.Application.Common;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Tenancy;
using Raavi.Tenancy.Resolution;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Smart search (ADR-049): chunking, scoring and vectors — no SQL needed.</summary>
public sealed class RagUnitTests
{
    [Fact]
    public void Chunker_keeps_paragraphs_whole_merges_small_ones_and_splits_long_ones_with_overlap()
    {
        Assert.Empty(Chunker.Split(null));
        Assert.Empty(Chunker.Split("  \n\r\n  "));
        Assert.Equal(["one\ntwo\nthree"], Chunker.Split("one\ntwo\r\nthree"));

        var paragraphs = Enumerable.Range(0, 30).Select(i => $"Paragraph number {i} " + new string('x', 150)).ToList();
        var merged = Chunker.Split(string.Join('\n', paragraphs));
        Assert.All(merged, c => Assert.InRange(c.Length, 1, Chunker.Max));
        Assert.True(merged.Count is > 3 and < 30);
        Assert.Equal(paragraphs.Count, merged.Sum(c => c.Split('\n').Length)); // every paragraph kept, none cut

        var sentence = "This is a fairly ordinary sentence about wiki pages. ";
        var longText = string.Concat(Enumerable.Repeat(sentence, 60)); // one 3 000-character paragraph
        var parts = Chunker.Split(longText);
        Assert.True(parts.Count >= 4);
        Assert.All(parts, p => Assert.InRange(p.Length, 1, Chunker.Max));
        Assert.All(parts.Take(parts.Count - 1), p => Assert.EndsWith(".", p, StringComparison.Ordinal)); // cut at sentence ends
        Assert.Contains(parts[1][..30], parts[0], StringComparison.Ordinal); // the overlap: the next piece starts inside the previous one

        // no whitespace at all: still split, and never inside a surrogate pair
        var emoji = string.Concat(Enumerable.Repeat("😀", 1500));
        var pieces = Chunker.Split(emoji);
        Assert.All(pieces, p => Assert.DoesNotContain('�', p));
        Assert.All(pieces, p => Assert.False(char.IsLowSurrogate(p[0]) || char.IsHighSurrogate(p[^1])));

        Assert.Equal(Chunker.MaxChunksPerPage, Chunker.Split(string.Join('\n', Enumerable.Range(0, 5000).Select(i => new string('y', 800)))).Count);
    }

    [Fact]
    public void Arabic_and_Persian_spellings_digits_and_marks_fold_to_one_form()
    {
        // ي/ى → ی, ك → ک, digits of every script → ASCII, marks and tatweel removed, ZWNJ → space, case folded
        Assert.Equal(SearchText.Normalize("کتاب"), SearchText.Normalize("كتاب"));
        Assert.Equal(SearchText.Normalize("یک علی"), SearchText.Normalize("يك علي"));
        Assert.Equal(SearchText.Normalize("على"), SearchText.Normalize("علی"));
        Assert.Equal("نسخه 123 ab", SearchText.Normalize("نسخهٔ ۱۲۳ AB".Replace("هٔ", "ه")));
        Assert.Equal("123 456", SearchText.Normalize("١٢٣ ۴۵۶"));
        Assert.Equal(SearchText.Normalize("محمد"), SearchText.Normalize("مُحَمَّد"));      // diacritics
        Assert.Equal(SearchText.Normalize("سلام"), SearchText.Normalize("سلــام"));        // tatweel
        Assert.Equal(SearchText.Normalize("می شود"), SearchText.Normalize("می‌شود")); // ZWNJ
        Assert.Equal(SearchText.Normalize("اسلام"), SearchText.Normalize("إسلام"));
        Assert.Equal(string.Empty, SearchText.Normalize(null));
        Assert.Equal(SearchText.Normalize("Hello"), SearchText.Normalize("hELLO"));
        Assert.Equal(["كتاب", "کتاب"], SearchText.Variants("كتاب").Where(v => v.Contains('ت'))); // the spellings a title may have in the database
        Assert.Equal(["ab"], SearchText.Variants("ab"));

        // and the search words follow suit
        Assert.Equal(RagService.Tokens("کتاب راهنما"), RagService.Tokens("كتاب راهنما"));
        Assert.Equal(["کتاب"], RagService.Tokens("كتاب چیست؟"));
        var score = RagService.KeywordScore("راهنما", "این كتاب را بخوانید", RagService.Tokens("کتاب"), "کتاب");
        Assert.True(score > 0);
    }

    [Fact]
    public void Vectors_are_stored_as_unit_length_float32_and_compared_by_dot_product()
    {
        var a = Vectors.Pack([3f, 4f]);
        Assert.Equal(8, a.Length);
        Assert.Equal(1f, Vectors.Dot(a, a), 4);
        Assert.Equal(0f, Vectors.Dot(Vectors.Pack([1f, 0f]), Vectors.Pack([0f, 1f])), 4);
        Assert.Equal(0.6f, Vectors.Dot(Vectors.Pack([1f, 0f]), Vectors.Pack([3f, 4f])), 4);
        Assert.Equal(float.NegativeInfinity, Vectors.Dot(a, Vectors.Pack([1f, 2f, 3f]))); // another model's dimension never matches
        Assert.Equal(0f, Vectors.Dot(Vectors.Pack([0f, 0f]), Vectors.Pack([1f, 1f])), 4);
    }

    [Fact]
    public void Keyword_scoring_prefers_titles_phrases_and_passages_with_more_of_the_words()
    {
        var q = "deploy checklist";
        var tokens = RagService.Tokens(q);
        Assert.Equal(["deploy", "checklist"], tokens);
        Assert.Equal(["ab", "12", "سلام"], RagService.Tokens("AB 12 x سلام ab"));
        Assert.Equal(["acquisition", "price"], RagService.Tokens("What is the acquisition price?")); // question words are dropped
        Assert.Equal(["what", "is"], RagService.Tokens("what is")); // unless nothing else is left

        var both = RagService.KeywordScore("Runbook", "The deploy checklist is short.", tokens, q);
        var one = RagService.KeywordScore("Runbook", "The deploy step is short.", tokens, q);
        var none = RagService.KeywordScore("Runbook", "Nothing relevant here.", tokens, q);
        var title = RagService.KeywordScore("Deploy checklist", "Nothing relevant here.", tokens, q);
        Assert.True(both > one && one > none && none == 0);
        Assert.True(title > one);
    }

    [Fact]
    public void Snippets_are_centred_on_the_first_hit_and_marked_when_cut()
    {
        var text = string.Concat(Enumerable.Repeat("filler words go here ", 40)) + "the secret deploy window is Friday " + string.Concat(Enumerable.Repeat("more filler ", 40));
        var s = RagService.Snippet(text, ["deploy"]);
        Assert.Contains("deploy", s, StringComparison.Ordinal);
        Assert.StartsWith("…", s, StringComparison.Ordinal);
        Assert.EndsWith("…", s, StringComparison.Ordinal);
        Assert.True(s.Length <= 250);
        Assert.Equal("short text", RagService.Snippet("short   text", ["x"]));
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class RagTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private const string Url = "https://ai.example.test/v1";

    private AsyncServiceScope Scope(IdentityTestKit.TestTenant t) =>
        fx.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(new ResolvedTenant(t.Id, t.Code, t.Slug, TenantResolutionSource.BackgroundJob, null));

    private async Task<int> IndexAsync(IdentityTestKit.TestTenant tenant)
    {
        await using var scope = Scope(tenant);
        return await scope.ServiceProvider.GetRequiredService<RagIndexer>().RunAsync(CancellationToken.None);
    }

    private static async Task<JsonElement> PublishedAsync(Browser b, string space, string title, params string[] paragraphs)
    {
        using var r = await b.PostAsync($"api/v1/spaces/{space}/content", new { title, body = Doc(paragraphs), publish = true });
        return await ReadAsync(r);
    }

    private static async Task SaveAiAsync(Browser admin, string key, string? embeddingModel)
    {
        var (current, etag) = await GetWithETagAsync(admin, "api/v1/ai/settings");
        var payload = new { enabled = true, baseUrl = Url, model = "wiki-model", apiKey = key, monthlyTokenCap = 0, maxOutputTokens = 300, embeddingModel };
        using var r = current.GetProperty("revision").GetInt32() == 0 ? await admin.PutAsync("api/v1/ai/settings", payload) : await admin.SendAsync(HttpMethod.Put, "api/v1/ai/settings", payload, withXsrf: true, ifMatch: etag);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> SearchAsync(Browser b, string q, string? space = null) =>
        await b.GetJsonAsync($"api/v1/ai/search?q={Uri.EscapeDataString(q)}" + (space is null ? string.Empty : $"&space={space}"));

    [Fact]
    public async Task Keyword_search_works_without_any_AI_and_ranks_the_right_page_first()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin, name: "Operations")).GetProperty("key").GetString()!;
        var deploy = await PublishedAsync(admin, key, "Release runbook", "To deploy the billing service, run the deploy checklist and tell the on-call engineer.");
        await PublishedAsync(admin, key, "Lunch menu", "On Friday the cafeteria serves rice and kebab.");
        await PublishedAsync(admin, key, "Onboarding", "New people receive a laptop. The deploy process is covered in week two.");
        Assert.Equal(4, await IndexAsync(tenant)); // three pages + the space home page
        Assert.Equal(0, await IndexAsync(tenant)); // nothing changed: nothing to do

        var found = await SearchAsync(member, "deploy checklist");
        Assert.Equal("keyword", found.GetProperty("mode").GetString());
        var hits = found.GetProperty("hits").EnumerateArray().ToList();
        Assert.Equal(2, hits.Count); // the lunch page does not match
        Assert.Equal(deploy.GetProperty("id").GetGuid(), hits[0].GetProperty("contentId").GetGuid()); // both words + the title context
        Assert.Contains("deploy", hits[0].GetProperty("snippet").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(key.ToUpperInvariant(), hits[0].GetProperty("spaceKey").GetString()!.ToUpperInvariant());

        // filter by space, odd input, and a query that matches nothing
        Assert.Equal(2, (await SearchAsync(member, "deploy", key)).GetProperty("hits").GetArrayLength());
        Assert.Equal(0, (await SearchAsync(member, "deploy", "NOSUCHSPACE")).GetProperty("hits").GetArrayLength());
        Assert.Equal(0, (await SearchAsync(member, "zzzqqq")).GetProperty("hits").GetArrayLength());
        foreach (var bad in new[] { "a", new string('x', 201) })
        {
            using var r = await member.GetAsync($"api/v1/ai/search?q={bad}");
            await Browser.AssertStatusAsync(r, HttpStatusCode.BadRequest, "ai.query_invalid");
        }

        // LIKE wildcards in the query are ordinary characters
        Assert.Equal(0, (await SearchAsync(member, "100%_[x]")).GetProperty("hits").GetArrayLength());
    }

    [Fact]
    public async Task Persian_search_finds_pages_whatever_the_spelling_of_the_letters_digits_and_half_spaces()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin, name: "راهنما")).GetProperty("key").GetString()!;
        // written with Arabic yeh/kaf, Arabic-Indic digits and a ZWNJ
        await PublishedAsync(admin, key, "كتاب راهنمای نصب", "نسخهٔ ١٤٠٥ از سامانه می‌شود روی سرور نصب شود. علي مسئول است.");
        await PublishedAsync(admin, key, "منوی ناهار", "جمعه چلوکباب داریم.");
        await IndexAsync(tenant);

        async Task<int> Hits(string q) => (await SearchAsync(member, q)).GetProperty("hits").GetArrayLength();
        Assert.Equal(1, await Hits("کتاب"));            // Persian kaf finds Arabic kaf
        Assert.Equal(1, await Hits("كتاب"));            // and the other way round
        Assert.Equal(1, await Hits("علی"));             // Persian yeh finds Arabic yeh
        Assert.Equal(1, await Hits("۱۴۰۵"));           // Persian digits find Arabic-Indic digits
        Assert.Equal(1, await Hits("1405"));           // and ASCII digits
        Assert.Equal(1, await Hits("می شود"));          // a space finds a half-space
        Assert.Equal(1, await Hits("می‌شود"));
        
        Assert.Equal(2, (await SearchAsync(member, "چلوکباب سرور")).GetProperty("hits").GetArrayLength());

        // the title search of quick find (plain SQL) also meets both spellings
        var quick = await member.GetJsonAsync("api/v1/quick-find?q=" + Uri.EscapeDataString("کتاب"));
        Assert.Equal(1, quick.GetProperty("content").GetArrayLength());
    }

    [Fact]
    public async Task Pages_the_user_cannot_read_never_appear_in_results_snippets_or_sources_and_changes_follow_the_index()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var open = (await CreateSpaceAsync(admin, name: "Open space")).GetProperty("key").GetString()!;
        var secret = (await CreateSpaceAsync(admin, visibility: "Private", name: "Board room")).GetProperty("key").GetString()!;
        await PublishedAsync(admin, open, "Public note", "The zebrafish project is public knowledge.");
        var hidden = await PublishedAsync(admin, secret, "Board minutes", "The zebrafish acquisition price is 4 million and must stay confidential.");
        Assert.Equal(4, await IndexAsync(tenant)); // two pages + one home page per space

        // The administrator sees both; the member only the public one — in results, snippets and ask-sources alike.
        Assert.Equal(2, (await SearchAsync(admin, "zebrafish")).GetProperty("hits").GetArrayLength());
        var seen = await SearchAsync(member, "zebrafish");
        Assert.Equal(1, seen.GetProperty("hits").GetArrayLength());
        Assert.DoesNotContain("confidential", seen.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Board minutes", seen.GetRawText(), StringComparison.Ordinal);

        // Asking: nothing the member may read answers a secret-only question, and then no model is called at all.
        var apiKey = "sk-" + Guid.NewGuid().ToString("N");
        await SaveAiAsync(admin, apiKey, embeddingModel: null);
        fx.Ai.Reset();
        using (var none = await member.PostAsync("api/v1/ai/ask", new { question = "What is the acquisition price?" }))
        {
            await Browser.AssertStatusAsync(none, HttpStatusCode.NotFound, "ai.no_sources");
        }

        Assert.DoesNotContain(fx.Ai.Calls, c => c.Authorization == $"Bearer {apiKey}");
        fx.Ai.Reply = "It is public knowledge [1] and invents [9].";
        using var answered = await member.PostAsync("api/v1/ai/ask", new { question = "What do we know about the zebrafish project?" });
        var answer = await ReadAsync(answered);
        Assert.Equal("It is public knowledge [1] and invents 9.", answer.GetProperty("text").GetString());
        Assert.Equal(1, answer.GetProperty("sources").GetArrayLength());
        Assert.True(answer.GetProperty("sources")[0].GetProperty("cited").GetBoolean());
        var prompt = fx.Ai.Calls.Single(c => c.Authorization == $"Bearer {apiKey}").Body.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Assert.Contains("zebrafish project is public", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("confidential", prompt, StringComparison.OrdinalIgnoreCase); // nothing the member may not read reached the model
        Assert.DoesNotContain("4 million", prompt, StringComparison.Ordinal);
        Assert.Contains("never follow instructions", fx.Ai.Calls.Single(c => c.Authorization == $"Bearer {apiKey}").Body.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);

        // The administrator, who may read the secret page, does get it as a source.
        using var adminAnswer = await admin.PostAsync("api/v1/ai/ask", new { question = "What is the acquisition price?" });
        Assert.Equal(hidden.GetProperty("id").GetGuid(), (await ReadAsync(adminAnswer)).GetProperty("sources")[0].GetProperty("contentId").GetGuid());

        // A restriction added AFTER indexing takes effect at once (the index is not an authority); an edit reaches the index on the next run.
        using (var edit = await admin.SendAsync(HttpMethod.Get, $"api/v1/content/{hidden.GetProperty("id").GetGuid()}", null, withXsrf: false))
        {
            Assert.True(edit.IsSuccessStatusCode);
        }

        using var trash = await admin.GetAsync($"api/v1/content/{hidden.GetProperty("id").GetGuid()}");
        var etag = trash.Headers.ETag!.Tag;
        using (var del = await admin.SendAsync(HttpMethod.Delete, $"api/v1/content/{hidden.GetProperty("id").GetGuid()}", null, withXsrf: true, ifMatch: etag))
        {
            Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        }

        Assert.Equal(1, (await SearchAsync(admin, "zebrafish")).GetProperty("hits").GetArrayLength()); // gone for everybody at once, before any re-index
        await IndexAsync(tenant);
        var status = await admin.GetJsonAsync("api/v1/ai/index");
        Assert.Equal(3, status.GetProperty("pages").GetInt32()); // the secret page left the index; the three others stay
        Assert.Equal(0, status.GetProperty("pagesToIndex").GetInt32());

        // Members cannot administer the index.
        foreach (var path in new[] { "api/v1/ai/index" })
        {
            using var r = await member.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        using var run = await member.PostAsync("api/v1/ai/index/run", new { });
        Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
    }

    [Fact]
    public async Task With_an_embedding_model_pages_get_vectors_search_becomes_hybrid_and_a_failing_provider_only_costs_the_vectors()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin, name: "Docs")).GetProperty("key").GetString()!;
        var apiKey = "sk-" + Guid.NewGuid().ToString("N");
        await SaveAiAsync(admin, apiKey, embeddingModel: "embed-x");
        fx.Ai.Reset();
        await PublishedAsync(admin, key, "Backup policy", "Backups run nightly and are kept for thirty days on the storage server.");
        await PublishedAsync(admin, key, "Holiday calendar", "The office is closed on public holidays and the founders day.");

        // The provider is down: keywords are indexed anyway, the vectors wait.
        fx.Ai.EmbeddingStatus = HttpStatusCode.ServiceUnavailable;
        var pagesInSpace = await IndexAsync(tenant); // 2 pages + the home page
        Assert.Equal(3, pagesInSpace);
        var down = await admin.GetJsonAsync("api/v1/ai/index");
        Assert.True(down.GetProperty("chunks").GetInt32() >= 2);
        Assert.Equal(0, down.GetProperty("embeddedChunks").GetInt32());
        Assert.Equal(pagesInSpace - 1, down.GetProperty("pagesToIndex").GetInt32()); // the home page has no text: nothing to embed, nothing pending // still to do: they carry no vectors yet
        Assert.Equal("keyword", (await SearchAsync(admin, "backups nightly")).GetProperty("mode").GetString()); // search never breaks because of the provider
        Assert.Equal(1, (await SearchAsync(admin, "backups nightly")).GetProperty("hits").GetArrayLength());

        // The provider is back: the next run embeds, and nothing is embedded twice.
        fx.Ai.EmbeddingStatus = null;
        fx.Ai.Reset();
        Assert.Equal(pagesInSpace - 1, await IndexAsync(tenant));
        var up = await admin.GetJsonAsync("api/v1/ai/index");
        Assert.Equal(up.GetProperty("chunks").GetInt32(), up.GetProperty("embeddedChunks").GetInt32());
        Assert.Equal(("embed-x", 0), (up.GetProperty("embeddingModel").GetString(), up.GetProperty("pagesToIndex").GetInt32()));
        var embedCalls = fx.Ai.Calls.Count(c => c.Url.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal));
        Assert.True(embedCalls is >= 2 and <= 3); // one call per page that has text
        Assert.Equal(0, await IndexAsync(tenant));
        Assert.Equal(embedCalls, fx.Ai.Calls.Count(c => c.Url.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal)));

        var hybrid = await SearchAsync(admin, "nightly backups storage");
        Assert.Equal("hybrid", hybrid.GetProperty("mode").GetString());
        Assert.Equal("Backup policy", hybrid.GetProperty("hits")[0].GetProperty("title").GetString());
        Assert.True(hybrid.GetProperty("hits")[0].GetProperty("semantic").GetBoolean());
        Assert.Contains(fx.Ai.Calls, c => c.Authorization == $"Bearer {apiKey}" && c.Body.TryGetProperty("input", out var i) && i[0].GetString() == "nightly backups storage"); // the query was embedded

        // Changing the model re-indexes everything with it.
        await SaveAiAsync(admin, apiKey, embeddingModel: "embed-y");
        Assert.Equal(pagesInSpace, (await admin.GetJsonAsync("api/v1/ai/index")).GetProperty("pagesToIndex").GetInt32());

        // The usage list shows the embedding work but no text.
        var usage = await admin.GetJsonAsync("api/v1/ai/usage");
        Assert.Contains(usage.GetProperty("byFeature").EnumerateArray(), f => f.GetProperty("feature").GetString() == "embed");
        Assert.DoesNotContain("nightly", usage.GetRawText(), StringComparison.Ordinal);
    }
}
