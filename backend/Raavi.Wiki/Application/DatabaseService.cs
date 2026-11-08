using System.Globalization;
using System.Text;
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

public sealed record DbOptionDto(string Id, string Label);

public sealed record DbColumnDto(string Id, string Name, string Type, DbOptionDto[] Options);

public sealed record DatabaseDto(
    Guid Id, string SpaceKey, string SpaceName, string Name, string? Description, DbColumnDto[] Columns, int RowCount, string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Revision, bool CanEdit);

public sealed record DatabaseRowDto(Guid Id, Guid DatabaseId, IReadOnlyDictionary<string, JsonElement> Values, string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Revision);

public sealed record DatabaseRowPage(int Total, IReadOnlyList<DatabaseRowDto> Rows);

public sealed record DbColumnRequest(string? Id, string? Name, string? Type, DbOptionRequest[]? Options);

public sealed record DbOptionRequest(string? Id, string? Label);

public sealed record DatabaseRequest(string? Name, string? Description, DbColumnRequest[]? Columns);

public sealed record DatabaseRowRequest(Dictionary<string, JsonElement>? Values);

public sealed record DatabaseExport(string FileName, byte[] Data);

/// <summary>Space databases (Phase 9c, ADR-028): schema as data, validated rows, server-side sort/filter/paging, CSV export.</summary>
public sealed class DatabaseService(WikiDbContext db, SpaceService spaces, WikiAuthorizer authz, IPrincipalDirectory directory, ICurrentUser current, IClock clock)
{
    public const int MaxPageSize = 500;

    public static readonly Error NotFound = new("db.not_found", "Database not found.", ErrorKind.NotFound);
    public static readonly Error RowNotFound = new("db.row_not_found", "Row not found.", ErrorKind.NotFound);
    public static readonly Error ColumnInvalid = new("db.column_invalid", "The column definition is not valid.", ErrorKind.Validation);
    public static readonly Error RowLimit = new("db.row_limit", $"A database can hold at most {SpaceDatabase.MaxRows} rows.", ErrorKind.Conflict);
    public static readonly Error SortInvalid = new("db.sort_invalid", "The sort column does not exist.", ErrorKind.Validation);

    public async Task<Result<IReadOnlyList<DatabaseDto>>> ListAsync(string spaceKey, CancellationToken ct)
    {
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<IReadOnlyList<DatabaseDto>>.Failure(WikiErrors.SpaceNotFound);
        }

        var rows = await db.SpaceDatabases.AsNoTracking().Where(d => d.SpaceId == space.Space.Id).OrderBy(d => d.Name).Take(500).ToListAsync(ct).ConfigureAwait(false);
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await db.DatabaseRows.AsNoTracking().Where(r => ids.Contains(r.DatabaseId)).GroupBy(r => r.DatabaseId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct).ConfigureAwait(false);
        var list = new List<DatabaseDto>();
        foreach (var d in rows)
        {
            list.Add(await ToDtoAsync(d, space, counts.GetValueOrDefault(d.Id), ct).ConfigureAwait(false));
        }

        return Result<IReadOnlyList<DatabaseDto>>.Success(list);
    }

    public async Task<Result<DatabaseDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<DatabaseDto>.Failure(loaded.Error!);
        }

        var count = await db.DatabaseRows.AsNoTracking().CountAsync(r => r.DatabaseId == id, ct).ConfigureAwait(false);
        return Result<DatabaseDto>.Success(await ToDtoAsync(loaded.Value.Database, loaded.Value.Space, count, ct).ConfigureAwait(false));
    }

    public async Task<Result<DatabaseDto>> CreateAsync(string spaceKey, DatabaseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<DatabaseDto>.Failure(WikiErrors.SpaceNotFound);
        }

        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<DatabaseDto>.Failure(space.Space.Status == SpaceStatus.Archived ? WikiErrors.SpaceArchived : WikiErrors.Forbidden);
        }

        var columns = ToInputs(request.Columns);
        if (!columns.IsSuccess)
        {
            return Result<DatabaseDto>.Failure(columns.Error!);
        }

        var database = SpaceDatabase.Create(space.Space.Id, request.Name, request.Description, columns.Value, current.UserId, clock.UtcNow);
        db.SpaceDatabases.Add(database);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<DatabaseDto>.Success(await ToDtoAsync(database, space, 0, ct).ConfigureAwait(false));
    }

    public async Task<Result<DatabaseDto>> UpdateAsync(Guid id, int expectedRevision, DatabaseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<DatabaseDto>.Failure(loaded.Error!);
        }

        var (database, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<DatabaseDto>.Failure(WikiErrors.Forbidden);
        }

        if (database.Revision != expectedRevision)
        {
            return Result<DatabaseDto>.Failure(ETags.PreconditionFailed);
        }

        var columns = ToInputs(request.Columns);
        if (!columns.IsSuccess)
        {
            return Result<DatabaseDto>.Failure(columns.Error!);
        }

        database.Update(request.Name, request.Description, columns.Value, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var count = await db.DatabaseRows.AsNoTracking().CountAsync(r => r.DatabaseId == id, ct).ConfigureAwait(false);
        return Result<DatabaseDto>.Success(await ToDtoAsync(database, space, count, ct).ConfigureAwait(false));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        if (!loaded.Value.Space.CanWrite(PermissionAction.Edit))
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        db.DatabaseRows.RemoveRange(await db.DatabaseRows.Where(r => r.DatabaseId == id).ToListAsync(ct).ConfigureAwait(false));
        db.SpaceDatabases.Remove(loaded.Value.Database);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    // ── Rows ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rows filtered by free text (any cell), sorted by one column, paged. Stored order is creation order.</summary>
    public async Task<Result<DatabaseRowPage>> RowsAsync(Guid id, string? q, string? sort, bool descending, int skip, int take, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<DatabaseRowPage>.Failure(loaded.Error!);
        }

        var columns = loaded.Value.Database.Columns;
        DbColumn? sortColumn = null;
        if (!string.IsNullOrWhiteSpace(sort) && (sortColumn = columns.FirstOrDefault(c => c.Id == sort)) is null)
        {
            return Result<DatabaseRowPage>.Failure(SortInvalid);
        }

        var rows = await db.DatabaseRows.AsNoTracking().Where(r => r.DatabaseId == id).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false);
        var parsed = rows.Select(r => (Row: r, Values: r.Values)).ToList();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            parsed = parsed.Where(p => p.Values.Any(kv => Display(columns, kv.Key, kv.Value).Contains(needle, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        if (sortColumn is not null)
        {
            var key = sortColumn;
            var ordered = descending
                ? parsed.OrderByDescending(p => p.Values.ContainsKey(key.Id)).ThenByDescending(p => SortKey(key, p.Values), SortComparer.Instance)
                : parsed.OrderByDescending(p => p.Values.ContainsKey(key.Id)).ThenBy(p => SortKey(key, p.Values), SortComparer.Instance);
            parsed = ordered.ToList(); // rows without a value always come last
        }

        var page = parsed.Skip(Math.Max(0, skip)).Take(Math.Clamp(take <= 0 ? 100 : take, 1, MaxPageSize)).ToList();
        var names = await directory.GetUserNamesAsync(page.Select(p => p.Row.CreatedBy).Distinct().ToList(), ct).ConfigureAwait(false);
        return Result<DatabaseRowPage>.Success(new DatabaseRowPage(parsed.Count, page.Select(p => ToRowDto(p.Row, p.Values, names)).ToList()));
    }

    public async Task<Result<DatabaseRowDto>> AddRowAsync(Guid databaseId, DatabaseRowRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(databaseId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<DatabaseRowDto>.Failure(loaded.Error!);
        }

        if (!loaded.Value.Space.CanWrite(PermissionAction.Edit))
        {
            return Result<DatabaseRowDto>.Failure(WikiErrors.Forbidden);
        }

        if (await db.DatabaseRows.CountAsync(r => r.DatabaseId == databaseId, ct).ConfigureAwait(false) >= SpaceDatabase.MaxRows)
        {
            return Result<DatabaseRowDto>.Failure(RowLimit);
        }

        var row = DatabaseRow.Create(databaseId, loaded.Value.Database.Columns, request.Values, current.UserId, clock.UtcNow);
        db.DatabaseRows.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<DatabaseRowDto>.Success(await ToRowDtoAsync(row, ct).ConfigureAwait(false));
    }

    public async Task<Result<DatabaseRowDto>> UpdateRowAsync(Guid rowId, int expectedRevision, DatabaseRowRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadRowAsync(rowId, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<DatabaseRowDto>.Failure(loaded.Error!);
        }

        var (row, database, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<DatabaseRowDto>.Failure(WikiErrors.Forbidden);
        }

        if (row.Revision != expectedRevision)
        {
            return Result<DatabaseRowDto>.Failure(ETags.PreconditionFailed);
        }

        row.Update(database.Columns, request.Values, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<DatabaseRowDto>.Success(await ToRowDtoAsync(row, ct).ConfigureAwait(false));
    }

    public async Task<Result<DatabaseRowDto>> GetRowAsync(Guid rowId, CancellationToken ct)
    {
        var loaded = await LoadRowAsync(rowId, track: false, ct).ConfigureAwait(false);
        return loaded.IsSuccess ? Result<DatabaseRowDto>.Success(await ToRowDtoAsync(loaded.Value.Row, ct).ConfigureAwait(false)) : Result<DatabaseRowDto>.Failure(loaded.Error!);
    }

    public async Task<Result> DeleteRowAsync(Guid rowId, CancellationToken ct)
    {
        var loaded = await LoadRowAsync(rowId, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        if (!loaded.Value.Space.CanWrite(PermissionAction.Edit))
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        db.DatabaseRows.Remove(loaded.Value.Row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>RFC 4180 CSV (UTF-8 BOM, formula-safe) with a header of column names; select cells show their label.</summary>
    public async Task<Result<DatabaseExport>> ExportCsvAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<DatabaseExport>.Failure(loaded.Error!);
        }

        var columns = loaded.Value.Database.Columns;
        var rows = await db.DatabaseRows.AsNoTracking().Where(r => r.DatabaseId == id).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false);
        var sb = new StringBuilder("﻿");
        sb.Append(string.Join(',', columns.Select(c => TaskService.Csv(c.Name)))).Append("\r\n");
        foreach (var r in rows)
        {
            var values = r.Values;
            sb.Append(string.Join(',', columns.Select(c => TaskService.Csv(values.TryGetValue(c.Id, out var v) ? Display(columns, c.Id, v) : string.Empty)))).Append("\r\n");
        }

        var safe = new string(loaded.Value.Database.Name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return Result<DatabaseExport>.Success(new DatabaseExport($"database-{(safe.Length == 0 ? "export" : safe)}.csv", Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static Result<IReadOnlyList<DbColumnInput>> ToInputs(DbColumnRequest[]? requests)
    {
        var list = new List<DbColumnInput>();
        foreach (var c in requests ?? [])
        {
            if (!Enum.TryParse<DbColumnType>(c.Type, true, out var type) || !Enum.IsDefined(type))
            {
                return Result<IReadOnlyList<DbColumnInput>>.Failure(ColumnInvalid);
            }

            list.Add(new DbColumnInput(c.Id, c.Name, type, c.Options?.Select(o => new DbOptionInput(o.Id, o.Label)).ToList()));
        }

        return Result<IReadOnlyList<DbColumnInput>>.Success(list);
    }

    private static string Display(IReadOnlyList<DbColumn> columns, string columnId, JsonElement v)
    {
        var col = columns.FirstOrDefault(c => c.Id == columnId);
        return col is null
            ? string.Empty
            : col.Type switch
            {
                DbColumnType.Select => col.Options.FirstOrDefault(o => o.Id == v.GetString())?.Label ?? string.Empty,
                DbColumnType.Checkbox => v.ValueKind == JsonValueKind.True ? "true" : "false",
                DbColumnType.Number => v.GetDouble().ToString("R", CultureInfo.InvariantCulture),
                _ => v.GetString() ?? string.Empty,
            };
    }

    private static object SortKey(DbColumn col, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (!values.TryGetValue(col.Id, out var v))
        {
            return string.Empty;
        }

        return col.Type switch
        {
            DbColumnType.Number => v.GetDouble(),
            DbColumnType.Checkbox => v.ValueKind == JsonValueKind.True ? 1d : 0d,
            DbColumnType.Select => col.Options.FirstOrDefault(o => o.Id == v.GetString())?.Label ?? string.Empty,
            _ => v.GetString() ?? string.Empty,
        };
    }

    private sealed class SortComparer : IComparer<object>
    {
        public static readonly SortComparer Instance = new();

        public int Compare(object? x, object? y) =>
            x is double a && y is double b ? a.CompareTo(b) : string.Compare(x?.ToString(), y?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Result<(SpaceDatabase Database, SpaceRights Space)>> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var database = await (track ? db.SpaceDatabases : db.SpaceDatabases.AsNoTracking()).FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        var space = database is null ? null : await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == database.SpaceId, ct).ConfigureAwait(false);
        if (database is null || space is null)
        {
            return Result<(SpaceDatabase, SpaceRights)>.Failure(NotFound);
        }

        // A database of a space the caller cannot view does not exist for them.
        var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        return rights.CanView ? Result<(SpaceDatabase, SpaceRights)>.Success((database, rights)) : Result<(SpaceDatabase, SpaceRights)>.Failure(NotFound);
    }

    private async Task<Result<(DatabaseRow Row, SpaceDatabase Database, SpaceRights Space)>> LoadRowAsync(Guid rowId, bool track, CancellationToken ct)
    {
        var row = await (track ? db.DatabaseRows : db.DatabaseRows.AsNoTracking()).FirstOrDefaultAsync(r => r.Id == rowId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result<(DatabaseRow, SpaceDatabase, SpaceRights)>.Failure(RowNotFound);
        }

        var parent = await LoadAsync(row.DatabaseId, track: false, ct).ConfigureAwait(false);
        return parent.IsSuccess
            ? Result<(DatabaseRow, SpaceDatabase, SpaceRights)>.Success((row, parent.Value.Database, parent.Value.Space))
            : Result<(DatabaseRow, SpaceDatabase, SpaceRights)>.Failure(RowNotFound);
    }

    private async Task<DatabaseDto> ToDtoAsync(SpaceDatabase d, SpaceRights r, int rowCount, CancellationToken ct)
    {
        var names = await directory.GetUserNamesAsync([d.CreatedBy], ct).ConfigureAwait(false);
        return new DatabaseDto(
            d.Id, r.Space.Key, r.Space.Name, d.Name, d.Description, d.Columns.Select(c => new DbColumnDto(c.Id, c.Name, c.Type.ToString(), c.Options.Select(o => new DbOptionDto(o.Id, o.Label)).ToArray())).ToArray(),
            rowCount, names.GetValueOrDefault(d.CreatedBy), d.CreatedAt, d.UpdatedAt, d.Revision, r.CanWrite(PermissionAction.Edit));
    }

    private static DatabaseRowDto ToRowDto(DatabaseRow r, IReadOnlyDictionary<string, JsonElement> values, IReadOnlyDictionary<Guid, string> names) =>
        new(r.Id, r.DatabaseId, values, names.GetValueOrDefault(r.CreatedBy), r.CreatedAt, r.UpdatedAt, r.Revision);

    private async Task<DatabaseRowDto> ToRowDtoAsync(DatabaseRow r, CancellationToken ct) =>
        ToRowDto(r, r.Values, await directory.GetUserNamesAsync([r.CreatedBy], ct).ConfigureAwait(false));
}
