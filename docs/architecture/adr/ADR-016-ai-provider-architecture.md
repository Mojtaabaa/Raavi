# ADR-016: AI Provider Architecture

**Status:** Accepted · **Date:** 2026-11-06

## Context
Support OpenAI-compatible APIs, Azure OpenAI, Anthropic, DeepSeek and local models, on-prem, with strict permission-aware context.

## Decision
- Build on **Microsoft.Extensions.AI** abstractions (`IChatClient`, `IEmbeddingGenerator`) with provider adapters; Raavi adds `IRerankClient`, model metadata (context window, cost, capabilities) and usage accounting.
- Providers defined globally (endpoints, keys via secret store); tenants select allowed providers/models and may supply their own keys where global policy permits (stored encrypted).
- **RAG**: chunks + embeddings stored in OpenSearch k-NN fields with `tenantId`, content id and ACL tokens; retrieval goes through the same ACL-enforcing query builder (ADR-013), then permission post-check. Prompt context contains only authorized chunks; citations link back to content.
- Embedding model id/version stored per chunk; model change → re-embedding job per tenant.
- Usage (`AIUsage`: tenant, user, feature, model, tokens in/out, latency) recorded for quotas and analytics; unanswered questions logged per tenant (metadata only by default).
- API keys never reach the browser; all AI calls are server-side with SSRF-guarded clients and timeouts.
- Prompt templates are versioned per tenant; system prompts include injection-resistance instructions; content from pages is treated as data.

## Alternatives considered
- Semantic Kernel as the abstraction — heavier; may be used for orchestration later on top of Microsoft.Extensions.AI.
- Separate vector database — rejected: OpenSearch k-NN avoids another on-prem component.

## Consequences
Retrieval quality depends on chunking/normalization (shared with search). AI features are off by default (`feature-flags.json`).
