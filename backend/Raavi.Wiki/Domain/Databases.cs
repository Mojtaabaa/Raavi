using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

public enum DbColumnType
{
    Text = 1,
    Number = 2,
    Date = 3,
    Checkbox = 4,
    Select = 5,
    Url = 6,
}

public sealed record DbOption(string Id, string Label);

/// <summary>A column of a database. The id is stable: renaming a column or its options never touches stored rows.</summary>
public sealed record DbColumn(string Id, string Name, DbColumnType Type, IReadOnlyList<DbOption> Options);

/// <summary>Column definition as sent by a client: an empty id means "new column".</summary>
public sealed record DbColumnInput(string? Id, string? Name, DbColumnType Type, IReadOnlyList<DbOptionInput>? Options);

public sealed record DbOptionInput(string? Id, string? Label);

internal static class DbJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

/// <summary>
/// A metadata-driven database of a space (Phase 9c): typed columns defined as data, rows stored as validated JSON keyed by column id. Permissions
/// come from the space (read = View; write = Edit on an active space).
/// </summary>
public sealed class SpaceDatabase : Entity, ITenantOwned
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MaxColumns = 30;
    public const int MaxOptions = 50;
    public const int MaxRows = 5000;

    private SpaceDatabase() { }

    public Guid TenantId { get; private set; }

    public Guid SpaceId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>JSON array of <see cref="DbColumn"/>.</summary>
    public string ColumnsJson { get; private set; } = "[]";

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public IReadOnlyList<DbColumn> Columns => JsonSerializer.Deserialize<List<DbColumn>>(ColumnsJson, DbJson.Options) ?? [];

    public static SpaceDatabase Create(Guid spaceId, string? name, string? description, IReadOnlyList<DbColumnInput>? columns, Guid userId, DateTimeOffset now)
    {
        var db = new SpaceDatabase { SpaceId = spaceId, CreatedBy = userId, CreatedAt = now, UpdatedAt = now };
        db.SetText(name, description);
        db.SetColumns(columns ?? []);
        return db;
    }

    /// <summary>Replaces name, description and schema. Columns keep their ids; a column cannot change its type (that would invalidate its data).</summary>
    public void Update(string? name, string? description, IReadOnlyList<DbColumnInput>? columns, DateTimeOffset now)
    {
        SetText(name, description);
        SetColumns(columns ?? []);
        UpdatedAt = now;
        Revision++;
    }

    private void SetText(string? name, string? description)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length is 0 or > MaxNameLength || n.Any(char.IsControl))
        {
            throw new DomainException("db.name_invalid", $"A database needs a name of 1–{MaxNameLength} characters.");
        }

        var d = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (d is { Length: > MaxDescriptionLength })
        {
            throw new DomainException("db.description_too_long", $"The description is limited to {MaxDescriptionLength} characters.");
        }

        Name = n;
        Description = d;
    }

    private void SetColumns(IReadOnlyList<DbColumnInput> input)
    {
        if (input.Count > MaxColumns)
        {
            throw new DomainException("db.too_many_columns", $"A database can have at most {MaxColumns} columns.");
        }

        var existing = Columns.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var result = new List<DbColumn>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in input)
        {
            var name = (c.Name ?? string.Empty).Trim();
            if (name.Length is 0 or > 100 || name.Any(char.IsControl) || !seenNames.Add(name))
            {
                throw new DomainException("db.column_invalid", "Each column needs a unique name of 1–100 characters.");
            }

            if (!Enum.IsDefined(c.Type))
            {
                throw new DomainException("db.column_invalid", "The column type is not supported.");
            }

            var id = string.IsNullOrWhiteSpace(c.Id) ? NewId() : c.Id.Trim();
            if (!seenIds.Add(id))
            {
                throw new DomainException("db.column_invalid", "Column ids must be unique.");
            }

            DbColumn? previous = null;
            if (!string.IsNullOrWhiteSpace(c.Id) && !existing.TryGetValue(id, out previous))
            {
                throw new DomainException("db.column_unknown", "A column with that id does not exist.");
            }

            if (previous is not null && previous.Type != c.Type)
            {
                throw new DomainException("db.column_type_locked", "The type of an existing column cannot be changed; add a new column instead.");
            }

            result.Add(new DbColumn(id, name, c.Type, c.Type == DbColumnType.Select ? Options(c, previous) : []));
        }

        ColumnsJson = JsonSerializer.Serialize(result, DbJson.Options);
    }

    private static List<DbOption> Options(DbColumnInput c, DbColumn? previous)
    {
        var options = c.Options ?? [];
        if (options.Count == 0 || options.Count > MaxOptions)
        {
            throw new DomainException("db.column_invalid", $"A select column needs 1–{MaxOptions} options.");
        }

        var known = previous?.Options.Select(o => o.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<DbOption>();
        foreach (var o in options)
        {
            var label = (o.Label ?? string.Empty).Trim();
            if (label.Length is 0 or > 100 || label.Any(char.IsControl) || !labels.Add(label))
            {
                throw new DomainException("db.column_invalid", "Options need unique labels of 1–100 characters.");
            }

            var id = string.IsNullOrWhiteSpace(o.Id) ? NewId() : o.Id.Trim();
            if (!ids.Add(id) || (!string.IsNullOrWhiteSpace(o.Id) && !known.Contains(id)))
            {
                throw new DomainException("db.column_invalid", "The option id is not valid.");
            }

            list.Add(new DbOption(id, label));
        }

        return list;
    }

    private static string NewId() => "c" + Guid.NewGuid().ToString("N")[..8];
}

/// <summary>A row of a database: values keyed by column id, validated against the schema when written.</summary>
public sealed class DatabaseRow : Entity, ITenantOwned
{
    public const int MaxTextLength = 2000;

    private DatabaseRow() { }

    public Guid TenantId { get; private set; }

    public Guid DatabaseId { get; private set; }

    /// <summary>JSON object: column id → string | number | boolean. Empty cells are absent.</summary>
    public string ValuesJson { get; private set; } = "{}";

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public IReadOnlyDictionary<string, JsonElement> Values => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ValuesJson, DbJson.Options) ?? [];

    public static DatabaseRow Create(Guid databaseId, IReadOnlyList<DbColumn> columns, IReadOnlyDictionary<string, JsonElement>? values, Guid userId, DateTimeOffset now)
    {
        var row = new DatabaseRow { DatabaseId = databaseId, CreatedBy = userId, CreatedAt = now, UpdatedAt = now };
        row.ValuesJson = Normalise(columns, values);
        return row;
    }

    public void Update(IReadOnlyList<DbColumn> columns, IReadOnlyDictionary<string, JsonElement>? values, DateTimeOffset now)
    {
        ValuesJson = Normalise(columns, values);
        UpdatedAt = now;
        Revision++;
    }

    /// <summary>Validates every value against its column, drops empty cells, and refuses unknown columns.</summary>
    public static string Normalise(IReadOnlyList<DbColumn> columns, IReadOnlyDictionary<string, JsonElement>? values)
    {
        var byId = columns.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var clean = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (id, v) in values ?? new Dictionary<string, JsonElement>())
        {
            if (!byId.TryGetValue(id, out var col))
            {
                throw new DomainException("db.column_unknown", "A value refers to a column that does not exist.");
            }

            if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())))
            {
                continue;
            }

            clean[id] = Convert(col, v);
        }

        return JsonSerializer.Serialize(clean, DbJson.Options);
    }

    private static object Convert(DbColumn col, JsonElement v)
    {
        static DomainException Bad(DbColumn c) => new("db.value_invalid", $"The value for “{c.Name}” is not valid.");
        switch (col.Type)
        {
            case DbColumnType.Text:
            {
                var s = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : throw Bad(col);
                return s.Length > MaxTextLength || s.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\r' or '\t')) ? throw Bad(col) : s;
            }

            case DbColumnType.Number:
                return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) && Math.Abs(d) < 1e15 ? d : throw Bad(col);

            case DbColumnType.Date:
                return v.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year is >= 1900 and <= 2200
                    ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : throw Bad(col);

            case DbColumnType.Checkbox:
                return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw Bad(col);

            case DbColumnType.Select:
                return v.ValueKind == JsonValueKind.String && col.Options.Any(o => o.Id == v.GetString()) ? v.GetString()! : throw Bad(col);

            case DbColumnType.Url:
            {
                var s = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : throw Bad(col);
                return s.Length <= MaxTextLength && Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !s.Any(char.IsControl) ? s : throw Bad(col);
            }

            default:
                throw Bad(col);
        }
    }
}
