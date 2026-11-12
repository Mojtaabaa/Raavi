using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

/// <summary>Controlled relationship types between knowledge assets (ADR-055).</summary>
public static class KnowledgeRelationshipTypes
{
    public const string RelatedTo = "RelatedTo";
    public const string References = "References";
    public const string DependsOn = "DependsOn";
    public const string Documents = "Documents";
    public const string Implements = "Implements";
    public const string Supersedes = "Supersedes";
    public const int MaxLength = 32;

    public static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        RelatedTo, References, DependsOn, Documents, Implements, Supersedes,
    };

    public static string Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length is 0 or > MaxLength)
        {
            throw new DomainException("relationship.type_invalid", "Relationship type is not valid.");
        }

        foreach (var known in BuiltIn)
        {
            if (string.Equals(known, v, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        if (!v.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new DomainException("relationship.type_invalid", "Relationship type is not valid.");
        }

        return v;
    }
}

/// <summary>What the relationship points at (extensible for Jira/Templar without a parallel table).</summary>
public static class KnowledgeTargetKinds
{
    public const string Content = "Content";
    public const string ExternalIssue = "ExternalIssue";
    public const int MaxLength = 32;

    public static string Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length is 0 or > MaxLength || !v.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new DomainException("relationship.target_kind_invalid", "Target kind is not valid.");
        }

        if (string.Equals(v, Content, StringComparison.OrdinalIgnoreCase))
        {
            return Content;
        }

        if (string.Equals(v, ExternalIssue, StringComparison.OrdinalIgnoreCase))
        {
            return ExternalIssue;
        }

        return v;
    }
}

/// <summary>How the edge was created — drives editability policy.</summary>
public static class KnowledgeRelationshipOrigins
{
    public const string Manual = "Manual";
    public const string SystemGenerated = "SystemGenerated";
    public const string Integration = "Integration";
    public const int MaxLength = 32;

    public static string Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (string.Equals(v, Manual, StringComparison.OrdinalIgnoreCase))
        {
            return Manual;
        }

        if (string.Equals(v, SystemGenerated, StringComparison.OrdinalIgnoreCase))
        {
            return SystemGenerated;
        }

        if (string.Equals(v, Integration, StringComparison.OrdinalIgnoreCase))
        {
            return Integration;
        }

        throw new DomainException("relationship.origin_invalid", "Relationship origin is not valid.");
    }

    public static bool UserMayDelete(string origin) =>
        string.Equals(origin, Manual, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Directed edge between knowledge assets (ADR-055). Deleting the edge never deletes either entity.</summary>
public sealed class KnowledgeRelationship : Entity, ITenantOwned
{
    public const int MaxExternalKeyLength = 256;
    public const int MaxTitleLength = 500;
    public const int MaxSourceSystemLength = 64;

    private KnowledgeRelationship() { }

    public Guid TenantId { get; private set; }

    public Guid SourceContentId { get; private set; }

    public string RelationshipType { get; private set; } = KnowledgeRelationshipTypes.RelatedTo;

    public string TargetKind { get; private set; } = KnowledgeTargetKinds.Content;

    public Guid? TargetContentId { get; private set; }

    public string? TargetExternalKey { get; private set; }

    public string? TargetTitle { get; private set; }

    public string Origin { get; private set; } = KnowledgeRelationshipOrigins.Manual;

    public string? SourceSystem { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static KnowledgeRelationship CreateManualContentLink(
        Guid sourceContentId, Guid targetContentId, string relationshipType, Guid userId, DateTimeOffset now) =>
        Create(sourceContentId, KnowledgeRelationshipTypes.Normalize(relationshipType), KnowledgeTargetKinds.Content,
            targetContentId, null, null, KnowledgeRelationshipOrigins.Manual, null, userId, now);

    public static KnowledgeRelationship Create(
        Guid sourceContentId,
        string relationshipType,
        string targetKind,
        Guid? targetContentId,
        string? targetExternalKey,
        string? targetTitle,
        string origin,
        string? sourceSystem,
        Guid userId,
        DateTimeOffset now)
    {
        if (sourceContentId == Guid.Empty)
        {
            throw new DomainException("relationship.source_invalid", "Source content is required.");
        }

        var kind = KnowledgeTargetKinds.Normalize(targetKind);
        var o = KnowledgeRelationshipOrigins.Normalize(origin);
        string? ext = null;
        string? title = null;
        Guid? targetId = null;

        if (kind == KnowledgeTargetKinds.Content)
        {
            if (targetContentId is not { } tid || tid == Guid.Empty)
            {
                throw new DomainException("relationship.target_invalid", "Target content is required.");
            }

            if (tid == sourceContentId)
            {
                throw new DomainException("relationship.self", "A page cannot relate to itself.");
            }

            targetId = tid;
        }
        else
        {
            ext = (targetExternalKey ?? string.Empty).Trim();
            if (ext.Length is 0 or > MaxExternalKeyLength)
            {
                throw new DomainException("relationship.external_key_invalid", "External target key is not valid.");
            }

            title = string.IsNullOrWhiteSpace(targetTitle) ? null : targetTitle.Trim();
            if (title is { Length: > MaxTitleLength })
            {
                title = title[..MaxTitleLength];
            }
        }

        string? system = string.IsNullOrWhiteSpace(sourceSystem) ? null : sourceSystem.Trim();
        if (system is { Length: > MaxSourceSystemLength })
        {
            throw new DomainException("relationship.source_system_invalid", "Source system is not valid.");
        }

        if (o is KnowledgeRelationshipOrigins.Integration && system is null)
        {
            throw new DomainException("relationship.source_system_required", "Integration relationships need a source system.");
        }

        return new KnowledgeRelationship
        {
            Id = Guid.NewGuid(),
            SourceContentId = sourceContentId,
            RelationshipType = KnowledgeRelationshipTypes.Normalize(relationshipType),
            TargetKind = kind,
            TargetContentId = targetId,
            TargetExternalKey = ext,
            TargetTitle = title,
            Origin = o,
            SourceSystem = system,
            CreatedBy = userId,
            CreatedAt = now,
        };
    }
}
