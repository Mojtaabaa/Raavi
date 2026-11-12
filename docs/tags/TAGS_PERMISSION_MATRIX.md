# Tags Permission Matrix

Mapped to Raavi conventions (no parallel Tags.* ACL in V1).

| Capability | Who | How |
|------------|-----|-----|
| View labeled pages / tag results | Authenticated member | Space/page **View** + `FilterViewableAsync` |
| Suggest labels | Authenticated member | Only labels used on viewable pages |
| Assign / remove on page | Page editor | Content **Edit** |
| Create label while assigning | Page editor | Edit (existing) |
| List all labels (admin) | Tenant admin | `RaaviPolicies.TenantAdministrator` |
| Create / edit / archive / restore | Tenant admin | TenantAdministrator |
| Aliases / hierarchy / merge / delete unused | Tenant admin | TenantAdministrator |
| Analytics | Tenant admin | TenantAdministrator; page counts still view-safe where exposed to members |

## Security rules

- Never trust frontend for authz.
- Foreign-tenant ids → 404 via RLS + service checks.
- No existence leak via counts, related tags, or suggestions.
- Archived labels not offered for new assign.
