namespace Raavi.Wiki.Application;

/// <summary>Installation defaults for EKOS-8 knowledge maintenance scanners (`Raavi:KnowledgeMaintenance`).</summary>
public sealed class KnowledgeMaintenanceOptions
{
    public const string Section = "Raavi:KnowledgeMaintenance";

    /// <summary>Pages not updated for this many days are suggested as stale (1–3650).</summary>
    public int StaleAfterDays { get; set; } = 180;

    /// <summary>Minimum Jaccard similarity of title tokens for near-duplicate pairs (0.5–1).</summary>
    public double NearDuplicateTitleThreshold { get; set; } = 0.75;
}
