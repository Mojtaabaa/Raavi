namespace Raavi.AI;

/// <summary>Global (installation-wide) AI settings, <c>Raavi:Ai</c>. Tenants choose their provider inside these limits.</summary>
public sealed class AiOptions
{
    public const string Section = "Raavi:Ai";

    /// <summary>Hosts a tenant may point its AI provider to (exact name or <c>*.suffix</c>). Empty = AI is switched off for every tenant (SSRF guard).</summary>
    public IList<string> AllowedHosts { get; } = [];

    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Characters of page text sent to the model; longer pages are cut and the model is told so.</summary>
    public int MaxInputChars { get; set; } = 24000;

    public int MaxRequestsPerUserPerHour { get; set; } = 20;

    /// <summary>Passages of other pages that may go into one "ask the wiki" prompt.</summary>
    public int MaxRagPassages { get; set; } = 8;
}
