using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Editor;
using Raavi.Wiki.Persistence;

namespace Raavi.Wiki.Application;

/// <summary>
/// Built-in Decision / Incident page templates (EKOS): structured Raavi Document sections, no separate entity tables.
/// Seeded idempotently as global templates; marker in Description identifies them after rename of Name.
/// </summary>
public static class KnowledgeSystemTemplates
{
    public const string DecisionKey = "decision-record";
    public const string IncidentKey = "incident-record";
    public const string MarkerPrefix = "[raavi:system=";

    public static string Marker(string key) => $"{MarkerPrefix}{key}]";

    public static string? TryParseKey(string? description)
    {
        if (description is null || !description.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var end = description.IndexOf(']', MarkerPrefix.Length);
        return end < 0 ? null : description[MarkerPrefix.Length..end];
    }

    public static async Task EnsureAsync(WikiDbContext db, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await EnsureOneAsync(db, DecisionKey, "Decision Record", "Structured decision record (context, alternatives, outcome).", DecisionBody(), actorId, now, ct).ConfigureAwait(false);
        await EnsureOneAsync(db, IncidentKey, "Incident Record", "Structured incident / postmortem record.", IncidentBody(), actorId, now, ct).ConfigureAwait(false);
    }

    private static async Task EnsureOneAsync(WikiDbContext db, string key, string name, string blurb, RaaviDocument body, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        var marker = Marker(key);
        var existing = await db.Templates.FirstOrDefaultAsync(t => t.SpaceId == null && t.Description != null && t.Description.StartsWith(marker), ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        var description = $"{marker} {blurb}";
        db.Templates.Add(Template.Create(null, name, description, body, actorId, now));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static RaaviDocument DecisionBody() => Sections(
        ("Context", "Background and constraints."),
        ("Problem Statement", "What decision is required?"),
        ("Alternatives", "Options considered."),
        ("Final Decision", "What was decided."),
        ("Rationale", "Why this option."),
        ("Consequences", "Follow-on effects."),
        ("Decision Owner", "Accountable person (also set Owner in governance)."),
        ("Related Content", "Link related pages via Relationships."));

    private static RaaviDocument IncidentBody() => Sections(
        ("Summary", "What happened, in one paragraph."),
        ("Severity", "Impact severity."),
        ("Timeline", "Key events and times."),
        ("Impact", "Who/what was affected."),
        ("Root Cause", "Underlying cause."),
        ("Resolution", "How it was fixed."),
        ("Corrective Actions", "Immediate fixes."),
        ("Preventive Actions", "Stop recurrence."),
        ("Lessons Learned", "What to keep."),
        ("Related Content", "Link related pages via Relationships."));

    private static RaaviDocument Sections(params (string Heading, string Hint)[] sections)
    {
        var content = new JsonArray();
        foreach (var (heading, hint) in sections)
        {
            content.Add(new JsonObject
            {
                ["type"] = "heading",
                ["attrs"] = new JsonObject { ["level"] = 2, ["textAlign"] = null, ["dir"] = null },
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = heading }),
            });
            content.Add(new JsonObject
            {
                ["type"] = "paragraph",
                ["attrs"] = new JsonObject { ["textAlign"] = null, ["dir"] = null },
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = hint }),
            });
        }

        var root = new JsonObject
        {
            ["schema"] = RaaviDocument.SchemaName,
            ["schemaVersion"] = EditorSchema.Current.Info.SchemaVersion,
            ["doc"] = new JsonObject { ["type"] = "doc", ["content"] = content },
        };
        return RaaviDocument.Parse(root.ToJsonString());
    }
}
