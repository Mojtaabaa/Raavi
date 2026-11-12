using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>EKOS: Decision / Incident as seeded global page templates (no separate entities).</summary>
[Collection(SqlTestGroup.Name)]
public sealed class KnowledgeSystemTemplateTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    [Fact]
    public async Task Listing_space_templates_seeds_decision_and_incident_once()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var space = await CreateSpaceAsync(admin);
        var key = space.GetProperty("key").GetString()!;

        var first = await admin.GetJsonAsync($"api/v1/spaces/{key}/templates");
        var systems = first.EnumerateArray().Where(t => t.TryGetProperty("isSystem", out var s) && s.GetBoolean()).ToList();
        Assert.Contains(systems, t => t.GetProperty("systemKey").GetString() == "decision-record");
        Assert.Contains(systems, t => t.GetProperty("systemKey").GetString() == "incident-record");

        var decision = systems.Single(t => t.GetProperty("systemKey").GetString() == "decision-record");
        var body = decision.GetProperty("body").GetRawText();
        Assert.Contains("Problem Statement", body, StringComparison.Ordinal);
        Assert.Contains("Final Decision", body, StringComparison.Ordinal);

        var incident = systems.Single(t => t.GetProperty("systemKey").GetString() == "incident-record");
        Assert.Contains("Root Cause", incident.GetProperty("body").GetRawText(), StringComparison.Ordinal);

        var second = await admin.GetJsonAsync($"api/v1/spaces/{key}/templates");
        Assert.Equal(
            first.EnumerateArray().Count(t => t.TryGetProperty("isSystem", out var s) && s.GetBoolean()),
            second.EnumerateArray().Count(t => t.TryGetProperty("isSystem", out var s) && s.GetBoolean()));

        var page = await CreatePageAsync(admin, key, "ADR-1", templateId: decision.GetProperty("id").GetGuid());
        Assert.Contains("Rationale", page.GetProperty("body").GetRawText(), StringComparison.Ordinal);
    }
}
