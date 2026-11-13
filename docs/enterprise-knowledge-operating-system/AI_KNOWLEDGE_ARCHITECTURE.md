# AI Knowledge Architecture

## Existing spine (reuse)

- `AiService.CompleteAsync` — single door (Prompt Hub + assistant)  
- Per-tenant OpenAI-compatible settings (`ai.Settings`); keys never in SPA  
- Context readers: `IPageTextReader`, space/issue readers  
- RAG: chunks + embeddings; ACL at query  
- Prompt Hub: templates, versions, ratings (ADR-052)

## Target layers

| Layer | Role |
|-------|------|
| Assistant | Ask across authorized corpus with citations |
| Editor tools | Rewrite/summarize/… — apply only on user confirm |
| Maintenance | Stale/dupe/contradiction **suggestions** + review tasks — no silent publish |
| Extraction | Docs/meetings → structured draft assets + human approve |
| Agents | Named roles, tools, source allow-lists, approval policy, audit |

## Agent framework (design only until Phase 5 of EKOS roadmap)

`IKnowledgeAgent` + tool registry bound to `IPermissionEvaluator` and content readers. Execution as user or constrained service principal. Outbox events for runs. **No autonomous writes to Jira/externals without confirm policy.**

## Data governance

- Min necessary context; classification-aware redaction later  
- Tenant policy: allow/deny external AI  
- Usage metering already partially present — extend for agents  
- Do not log full prompts containing secrets/PII by default  

## Extension points already / to add

- `ILabelSuggestionProvider` (deterministic now; AI later)  
- Prompt Hub context providers  
- `IKnowledgeMaintenanceScanner` (EKOS-8 on-demand)
- Future: `IEditorAiAction`