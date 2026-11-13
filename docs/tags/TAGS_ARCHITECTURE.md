# Tags Architecture

## Placement

All taxonomy code lives in **`Raavi.Wiki`** (Domain, Application `LabelService`, Persistence, Api). No `Raavi.Tags` project. Feature modules must not cross-reference.

## Components

```
Page editor / LabelView / Admin Labels
        │
        ▼
WikiEndpoints (/labels…, /content/…/labels, /admin/labels…)
        │
   ┌────┴────┐
   ▼         ▼
PageService  LabelService ──► WikiDbContext (Labels, LabelAliases, ContentLabels)
   │              │
   └──────┬───────┘
          ▼
   WikiAuthorizer + FilterViewableAsync
          │
          ▼
   DiscoveryService / RagService (query-time view filter)
```

## Product vocabulary

| Layer | Name |
|-------|------|
| Code / DB / API | Labels |
| Docs folder | `docs/tags/` |
| EN UI | Labels |
| FA UI | برچسب |

## Events

- `content.label_added` / `content.label_removed` — page SpaceId (activity feed).
- `label.created|updated|archived|restored|deleted|merged|moved|alias_added|alias_removed` — outbox; space activity skipped when SpaceId empty.
