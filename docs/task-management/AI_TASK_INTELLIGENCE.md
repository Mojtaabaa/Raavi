# AI Task Intelligence (Stage A)

## 1. Reuse (mandatory)

| Capability | Location | Use |
|------------|----------|-----|
| Provider + host policy + key protection | `Raavi.AI` / ADR-034 | No second config |
| `AiService.CompleteAsync` | AI | All completions |
| Usage / caps / rate limits | AI | Feature names `task_*` |
| Prompt Hub | AI | Templates for draft/extract |
| Confirm-to-apply | EKOS-7 pattern | Persist only after confirm |
| View filters / Restricted redaction | Wiki + AI | Before context leaves tenant |

## 2. Capabilities (Stage F order)

1. **NL draft** — propose title, due, priority, type, space, assignees (from directory names the user can see); user confirms create.
2. **Knowledge-to-task** — from page/selection; link `ContentId`; cite source.
3. **Meeting actions** — from authorized meeting notes; dedupe heuristics.
4. **Assignment recommend** — workload + past Responsible (explainable; no auto-assign).
5. **Risk signals** — factual (overdue, no Responsible, blocked) vs model predictions (labeled).
6. **Assistant Q&A** — over tasks the caller can view (+ optional RAG pages).

## 3. Safety rules

- Never invent users; map names via directory search with membership checks.
- Never transition/assign/complete without `confirmed` + ACL.
- Prompt injection in wiki/task notes is untrusted data (same markers as RAG Ask).
- Strip invalid citations if answers cite task/page sources (EKOS-10 pattern).
- Record feature usage (`task_draft`, `task_extract`, …).

## 4. Non-goals early

Autonomous agents writing Jira; silent bulk creates; using personal attributes outside product policy.
