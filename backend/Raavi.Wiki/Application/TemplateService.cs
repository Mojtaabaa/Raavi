using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

/// <summary>
/// Page templates. Space templates are managed by holders of <see cref="PermissionAction.ManageTemplates"/> on the space;
/// global templates (all spaces) by tenant administrators. Readers of a space see its templates plus the global ones.
/// </summary>
public sealed class TemplateService(WikiDbContext db, WikiAuthorizer authz, SpaceService spaces, ICurrentUser current, IClock clock)
{
    public async Task<Result<IReadOnlyList<TemplateDto>>> ListForSpaceAsync(string spaceKey, CancellationToken ct)
    {
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<IReadOnlyList<TemplateDto>>.Failure(WikiErrors.SpaceNotFound);
        }

        var principal = await authz.PrincipalAsync(ct).ConfigureAwait(false);
        var rows = await db.Templates.AsNoTracking().Where(t => t.SpaceId == space.Space.Id || t.SpaceId == null).OrderBy(t => t.SpaceId == null).ThenBy(t => t.Name).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<TemplateDto>>.Success(rows.Select(t => ToDto(t, t.SpaceId is null ? principal.IsTenantAdministrator : space.CanWrite(PermissionAction.ManageTemplates))).ToList());
    }

    public async Task<IReadOnlyList<TemplateDto>> ListGlobalAsync(CancellationToken ct)
    {
        var principal = await authz.PrincipalAsync(ct).ConfigureAwait(false);
        var rows = await db.Templates.AsNoTracking().Where(t => t.SpaceId == null).OrderBy(t => t.Name).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(t => ToDto(t, principal.IsTenantAdministrator)).ToList();
    }

    /// <summary>Creates a space template (<paramref name="spaceKey"/>) or a global one (null).</summary>
    public async Task<Result<TemplateDto>> CreateAsync(string? spaceKey, TemplateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Guid? spaceId = null;
        if (spaceKey is not null)
        {
            var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
            if (space is null)
            {
                return Result<TemplateDto>.Failure(WikiErrors.SpaceNotFound);
            }

            if (!space.CanWrite(PermissionAction.ManageTemplates))
            {
                return Result<TemplateDto>.Failure(WikiErrors.Forbidden);
            }

            spaceId = space.Space.Id;
        }
        else if (!(await authz.PrincipalAsync(ct).ConfigureAwait(false)).IsTenantAdministrator)
        {
            return Result<TemplateDto>.Failure(WikiErrors.Forbidden);
        }

        var body = request.Body is { } b ? RaaviDocument.Parse(b) : RaaviDocument.Empty;
        var template = Template.Create(spaceId, request.Name, request.Description, body, current.UserId, clock.UtcNow);
        db.Templates.Add(template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TemplateDto>.Success(ToDto(template, true));
    }

    public async Task<Result<TemplateDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        return loaded.IsSuccess ? Result<TemplateDto>.Success(ToDto(loaded.Value.Template, loaded.Value.CanManage)) : Result<TemplateDto>.Failure(loaded.Error!);
    }

    public async Task<Result<TemplateDto>> UpdateAsync(Guid id, int expectedRevision, TemplateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TemplateDto>.Failure(loaded.Error!);
        }

        var (template, canManage) = loaded.Value;
        if (!canManage)
        {
            return Result<TemplateDto>.Failure(WikiErrors.Forbidden);
        }

        if (template.Revision != expectedRevision)
        {
            return Result<TemplateDto>.Failure(ETags.PreconditionFailed);
        }

        template.Update(request.Name, request.Description, request.Body is { } b ? RaaviDocument.Parse(b) : RaaviDocument.Empty, current.UserId, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TemplateDto>.Success(ToDto(template, true));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        if (!loaded.Value.CanManage)
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        db.Templates.Remove(loaded.Value.Template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result<(Template Template, bool CanManage)>> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var query = track ? db.Templates : db.Templates.AsNoTracking();
        var template = await query.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
        if (template is null)
        {
            return Result<(Template, bool)>.Failure(WikiErrors.TemplateNotFound);
        }

        if (template.SpaceId is null)
        {
            var principal = await authz.PrincipalAsync(ct).ConfigureAwait(false);
            return principal.IsActiveMember
                ? Result<(Template, bool)>.Success((template, principal.IsTenantAdministrator))
                : Result<(Template, bool)>.Failure(WikiErrors.TemplateNotFound);
        }

        var space = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == template.SpaceId, ct).ConfigureAwait(false);
        var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        return rights.CanView
            ? Result<(Template, bool)>.Success((template, rights.CanWrite(PermissionAction.ManageTemplates)))
            : Result<(Template, bool)>.Failure(WikiErrors.TemplateNotFound);
    }

    private static TemplateDto ToDto(Template t, bool canManage)
    {
        using var doc = JsonDocument.Parse(RaaviDocument.Parse(t.ContentJson).Json);
        return new TemplateDto(t.Id, t.SpaceId, t.Name, t.Description, doc.RootElement.Clone(), t.UpdatedAt, t.Revision, canManage);
    }
}
