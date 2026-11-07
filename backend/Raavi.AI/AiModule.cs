namespace Raavi.AI;

/// <summary>
/// AI module (Phase 11). Provider abstractions are built on Microsoft.Extensions.AI (IChatClient, IEmbeddingGenerator)
/// per ADR-016. Every AI context is assembled only from content that passed IPermissionEvaluator for the current user.
/// </summary>
public static class AiModule
{
    public const string Name = "ai";
}
