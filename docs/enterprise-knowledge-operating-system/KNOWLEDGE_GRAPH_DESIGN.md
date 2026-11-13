# Knowledge Graph Design

## Decision

**Relational edge store in SQL Server** (`content.KnowledgeRelationships`). No Neo4j/Neptune unless later evidence (traversal scale, ops) justifies it — PO gate.

## Node types (v1 registry)

Pages, Spaces, Labels, Users (directory refs), CalendarEvents, Tasks, Whiteboards, Databases, Decisions*, Incidents*, Systems*, JiraIssues (external id + connector).

\* = new asset types in later phases.

## Edge types (controlled)

RelatedTo, References, DependsOn, Implements, Documents, OwnedBy, ApprovedBy, CreatedFrom, DiscussedIn, Supersedes, Blocks, BelongsTo, Resolves, Impacts, TaggedWith (or keep ContentLabels as specialized).

## Provenance

| Value | Meaning |
|-------|---------|
| Manual | User created |
| System | Derived (e.g. editor link extraction job) |
| Integration | Connector (e.g. Jira Remote Link) |

## Security

- Create edge: Edit (or ManageRelationships) on **from** asset; View on **to**  
- Traverse / visualize: only nodes caller can View; never show ghost neighbors  
- Counts/facets: same rule  
- Cross-tenant: impossible via RLS + typed ids  

## UX

- Panel on page: related entities (reuse LinkedJiraPanel pattern)  
- Graph view: force-directed or hierarchical from current node (depth limit 2–3)  
- Click → deep link to entity route  

## Incremental delivery

1. **Done (B2):** Table + API + page panel (manual Content↔Content edges); external columns reserved  
2. **Done (C2):** PageIssueLinks → Integration edges via `IKnowledgeRelationshipProjection` (Labels/Calendar still separate)  
3. Visualization  
4. Search “related to” filter (B3)  
5. AI “what connects X and Y” using authorized subgraph only