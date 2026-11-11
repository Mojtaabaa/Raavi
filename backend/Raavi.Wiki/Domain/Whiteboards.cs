using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

public enum BoardShape
{
    Rect = 1,
    Ellipse = 2,
    Text = 3,
    Sticky = 4,
    Arrow = 5,
    Pen = 6,
    Diamond = 7,
    Triangle = 8,
    Line = 9,
    RoundRect = 10,
    Star = 11,
    Image = 12,
    Connector = 13,
    Group = 14,
    Table = 15,
}

public enum BoardGridStyle
{
    None = 0,
    Dot = 1,
    Line = 2,
}

public enum BoardPort
{
    Center = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
    Left = 4,
}

public enum BoardConnectorRoute
{
    Straight = 0,
    Orthogonal = 1,
    Curve = 2,
}

/// <summary>
/// One element of a whiteboard. Position is the top-left corner (rotation turns the element around it); arrows and pen strokes keep their
/// coordinates in <see cref="Points"/> relative to (X, Y). List order is the stacking order (last = on top).
/// </summary>
public sealed record BoardElement(
    string Id,
    BoardShape Type,
    double X,
    double Y,
    double W,
    double H,
    double Rotation,
    string Stroke,
    string? Fill,
    int StrokeWidth,
    string Text,
    int FontSize,
    bool Rtl,
    double[] Points,
    double CornerRadius = 0,
    Guid? AttachmentId = null,
    string Alt = "",
    bool Locked = false,
    bool Hidden = false,
    string? ParentId = null,
    string? FromId = null,
    string? ToId = null,
    BoardPort FromPort = BoardPort.Center,
    BoardPort ToPort = BoardPort.Center,
    BoardConnectorRoute Route = BoardConnectorRoute.Straight,
    bool StartArrow = false,
    bool EndArrow = true,
    string[]? Children = null,
    int Rows = 0,
    int Cols = 0,
    string[]? Cells = null,
    double[]? ColWidths = null);

/// <summary>Element as sent by a client: every optional field falls back to a default.</summary>
public sealed record BoardElementInput(
    string? Id,
    BoardShape Type,
    double? X,
    double? Y,
    double? W,
    double? H,
    double? Rotation,
    string? Stroke,
    string? Fill,
    int? StrokeWidth,
    string? Text,
    int? FontSize,
    bool? Rtl,
    double[]? Points,
    double? CornerRadius = null,
    Guid? AttachmentId = null,
    string? Alt = null,
    bool? Locked = null,
    bool? Hidden = null,
    string? ParentId = null,
    string? FromId = null,
    string? ToId = null,
    BoardPort? FromPort = null,
    BoardPort? ToPort = null,
    BoardConnectorRoute? Route = null,
    bool? StartArrow = null,
    bool? EndArrow = null,
    string[]? Children = null,
    int? Rows = null,
    int? Cols = null,
    string[]? Cells = null,
    double[]? ColWidths = null);

internal static class BoardJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

/// <summary>
/// A whiteboard of a space: free canvas of shapes, text, sticky notes, arrows, pen strokes and images, stored as validated JSON and saved
/// as a whole with one ETag. Permissions come from the space (read = View; write = Edit on an active space).
/// </summary>
public sealed partial class Whiteboard : Entity, ITenantOwned
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MaxElements = 2000;
    public const int MaxTextLength = 2000;
    public const int MaxAltLength = 200;
    public const int MaxPointValues = 4000;
    public const int MaxTableRows = 20;
    public const int MaxTableCols = 12;
    public const int MaxGroupChildren = 200;
    public const double MaxCoordinate = 100_000;
    public const int MaxLiveStateBytes = 8 * 1024 * 1024;
    public const string DefaultStroke = "#1f2937";
    public const string DefaultBackground = "#ffffff";

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex IdPattern();

    private Whiteboard() { }

    public Guid TenantId { get; private set; }

    public Guid SpaceId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>JSON array of <see cref="BoardElement"/>.</summary>
    public string ElementsJson { get; private set; } = "[]";

    public string Background { get; private set; } = DefaultBackground;

    public BoardGridStyle GridStyle { get; private set; } = BoardGridStyle.Dot;

    public Guid CreatedBy { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    /// <summary>Full Yjs state of the live session (ADR-035); null when nobody has edited live since the last REST save.</summary>
    public byte[]? YjsState { get; private set; }

    public IReadOnlyList<BoardElement> Elements => JsonSerializer.Deserialize<List<BoardElement>>(ElementsJson, BoardJson.Options) ?? [];

    public static Whiteboard Create(Guid spaceId, string? title, IReadOnlyList<BoardElementInput>? elements, Guid userId, DateTimeOffset now, string? description = null, string? background = null, BoardGridStyle? gridStyle = null)
    {
        var board = new Whiteboard { SpaceId = spaceId, CreatedBy = userId, UpdatedBy = userId, CreatedAt = now, UpdatedAt = now };
        board.Set(title, elements ?? [], description, background, gridStyle);
        return board;
    }

    /// <summary>Replaces the title and the whole element list (order = stacking order).</summary>
    public void Update(string? title, IReadOnlyList<BoardElementInput>? elements, Guid userId, DateTimeOffset now, string? description = null, string? background = null, BoardGridStyle? gridStyle = null)
    {
        Set(title, elements ?? [], description ?? Description, background ?? Background, gridStyle ?? GridStyle);
        YjsState = null;
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }

    /// <summary>Renames without touching the elements or the live state.</summary>
    public void Rename(string? title, Guid userId, DateTimeOffset now)
    {
        Set(title, Elements.Select(ToInput).ToList(), Description, Background, GridStyle);
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }

    public void SetDescription(string? description, Guid userId, DateTimeOffset now)
    {
        Description = CleanDescription(description);
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }

    public void SetAppearance(string? background, BoardGridStyle? gridStyle, Guid userId, DateTimeOffset now)
    {
        Background = NormalizeBackground(background ?? Background);
        if (gridStyle.HasValue)
        {
            GridStyle = gridStyle.Value;
        }

        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }

    public void Archive(DateTimeOffset now, Guid userId)
    {
        if (ArchivedAt is not null)
        {
            return;
        }

        ArchivedAt = now;
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }

    public void Restore(Guid userId, DateTimeOffset now)
    {
        if (ArchivedAt is null)
        {
            return;
        }

        ArchivedAt = null;
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }

    /// <summary>Stores what the live collaboration server persisted: the validated elements and the Yjs state they came from.</summary>
    public void SetLiveState(IReadOnlyList<BoardElementInput> elements, byte[] state, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Length is 0 or > MaxLiveStateBytes)
        {
            throw new DomainException("board.live_state_invalid", "The live state is empty or too large.");
        }

        Set(Title, elements, Description, Background, GridStyle);
        YjsState = state;
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }

    /// <summary>Copies title/description/elements/appearance into a new board in the same space (images are dropped — attachments are not copied).</summary>
    public Whiteboard Duplicate(string? title, Guid userId, DateTimeOffset now)
    {
        var elements = Elements.Where(e => e.Type != BoardShape.Image).Select(ToInput).ToList();
        return Create(SpaceId, string.IsNullOrWhiteSpace(title) ? $"{Title} (copy)" : title, elements, userId, now, Description, Background, GridStyle);
    }

    public static BoardElementInput ToInput(BoardElement e) => new(
        e.Id, e.Type, e.X, e.Y, e.W, e.H, e.Rotation, e.Stroke, e.Fill, e.StrokeWidth, e.Text, e.FontSize, e.Rtl, e.Points,
        e.CornerRadius, e.AttachmentId, e.Alt, e.Locked, e.Hidden, e.ParentId, e.FromId, e.ToId, e.FromPort, e.ToPort, e.Route,
        e.StartArrow, e.EndArrow, e.Children, e.Rows, e.Cols, e.Cells, e.ColWidths);

    private void Set(string? title, IReadOnlyList<BoardElementInput> input, string? description, string? background, BoardGridStyle? gridStyle)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length is 0 or > MaxTitleLength || t.Any(char.IsControl))
        {
            throw new DomainException("board.title_invalid", $"A whiteboard needs a title of 1–{MaxTitleLength} characters.");
        }

        if (input.Count > MaxElements)
        {
            throw new DomainException("board.too_many", $"A whiteboard can have at most {MaxElements} elements.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<BoardElement>(input.Count);
        foreach (var e in input)
        {
            result.Add(Normalise(e, seen));
        }

        ValidateReferences(result);
        Title = t;
        Description = CleanDescription(description);
        Background = NormalizeBackground(background);
        GridStyle = gridStyle is { } g && Enum.IsDefined(g) ? g : BoardGridStyle.Dot;
        ElementsJson = JsonSerializer.Serialize(result, BoardJson.Options);
    }

    private static void ValidateReferences(List<BoardElement> elements)
    {
        var ids = elements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var e in elements)
        {
            if (e.ParentId is { } p && !ids.Contains(p))
            {
                throw Invalid("A group parent must exist on the same board.");
            }

            if (e.Type == BoardShape.Connector)
            {
                if (e.FromId is null || e.ToId is null || !ids.Contains(e.FromId) || !ids.Contains(e.ToId) || e.FromId == e.ToId)
                {
                    throw Invalid("A connector needs two different existing endpoints.");
                }
            }

            if (e.Type == BoardShape.Group)
            {
                foreach (var child in e.Children ?? [])
                {
                    if (!ids.Contains(child))
                    {
                        throw Invalid("A group can only reference elements on the same board.");
                    }
                }
            }
        }
    }

    private static BoardElement Normalise(BoardElementInput e, HashSet<string> seen)
    {
        if (!Enum.IsDefined(e.Type))
        {
            throw Invalid("The element type is not supported.");
        }

        var id = (e.Id ?? string.Empty).Trim();
        if (!IdPattern().IsMatch(id) || !seen.Add(id))
        {
            throw Invalid("Element ids must be unique and use letters, digits, '-' or '_' (up to 40 characters).");
        }

        var x = Coordinate(e.X ?? 0, signed: true);
        var y = Coordinate(e.Y ?? 0, signed: true);
        var sized = e.Type is BoardShape.Rect or BoardShape.Ellipse or BoardShape.Sticky or BoardShape.Text or BoardShape.Diamond
            or BoardShape.Triangle or BoardShape.RoundRect or BoardShape.Star or BoardShape.Image or BoardShape.Group or BoardShape.Table;
        var w = sized ? Coordinate(e.W ?? 0, signed: false) : 0;
        var h = sized ? Coordinate(e.H ?? 0, signed: false) : 0;
        if ((e.Type is BoardShape.Rect or BoardShape.Ellipse or BoardShape.Sticky or BoardShape.Diamond or BoardShape.Triangle
            or BoardShape.RoundRect or BoardShape.Star or BoardShape.Image or BoardShape.Table) && (w <= 0 || h <= 0))
        {
            throw Invalid("Shapes and sticky notes need a width and a height above zero.");
        }

        var rotation = e.Rotation ?? 0;
        if (!double.IsFinite(rotation) || Math.Abs(rotation) > 36_000)
        {
            throw Invalid("The rotation is not valid.");
        }

        rotation = Math.Round(rotation % 360, 2);
        var stroke = Color(e.Stroke) ?? DefaultStroke;
        var fill = string.IsNullOrWhiteSpace(e.Fill) ? null : Color(e.Fill) ?? throw Invalid("The fill colour must look like #rrggbb.");
        var width = e.StrokeWidth ?? 2;
        if (width is < 1 or > 24)
        {
            throw Invalid("The line width must be between 1 and 24.");
        }

        var font = e.FontSize ?? 20;
        if (font is < 8 or > 200)
        {
            throw Invalid("The font size must be between 8 and 200.");
        }

        var hasText = e.Type is BoardShape.Text or BoardShape.Sticky or BoardShape.Connector;
        var text = hasText || e.Type == BoardShape.Table ? Multiline(e.Text) : string.Empty;
        if (e.Type == BoardShape.Text && string.IsNullOrWhiteSpace(text))
        {
            throw Invalid("A text element cannot be empty.");
        }

        var corner = e.Type == BoardShape.RoundRect ? Math.Clamp(Math.Round(e.CornerRadius ?? Math.Min(w, h) / 5, 2), 0, Math.Min(w, h) / 2) : 0;
        Guid? attachmentId = null;
        var alt = string.Empty;
        if (e.Type == BoardShape.Image)
        {
            if (e.AttachmentId is null || e.AttachmentId == Guid.Empty)
            {
                throw Invalid("An image needs a board attachment id.");
            }

            attachmentId = e.AttachmentId;
            alt = CleanAlt(e.Alt);
        }

        string? fromId = null, toId = null;
        var fromPort = BoardPort.Center;
        var toPort = BoardPort.Center;
        var route = BoardConnectorRoute.Straight;
        var startArrow = false;
        var endArrow = true;
        double[] points;
        if (e.Type == BoardShape.Connector)
        {
            fromId = RequireId(e.FromId, "from");
            toId = RequireId(e.ToId, "to");
            fromPort = e.FromPort is { } fp && Enum.IsDefined(fp) ? fp : BoardPort.Center;
            toPort = e.ToPort is { } tp && Enum.IsDefined(tp) ? tp : BoardPort.Center;
            route = e.Route is { } r && Enum.IsDefined(r) ? r : BoardConnectorRoute.Straight;
            startArrow = e.StartArrow ?? false;
            endArrow = e.EndArrow ?? true;
            points = OptionalPolyline(e.Points);
            text = Multiline(e.Text);
        }
        else
        {
            points = Points(e);
        }

        string[]? children = null;
        if (e.Type == BoardShape.Group)
        {
            children = (e.Children ?? []).Select(c => RequireId(c, "child")).Distinct(StringComparer.Ordinal).Take(MaxGroupChildren).ToArray();
            if (children.Length == 0)
            {
                throw Invalid("A group needs at least one child.");
            }
        }

        var rows = 0;
        var cols = 0;
        string[]? cells = null;
        double[]? colWidths = null;
        if (e.Type == BoardShape.Table)
        {
            rows = e.Rows ?? 2;
            cols = e.Cols ?? 2;
            if (rows is < 1 or > MaxTableRows || cols is < 1 or > MaxTableCols)
            {
                throw Invalid($"Tables are limited to {MaxTableRows}×{MaxTableCols} cells.");
            }

            cells = new string[rows * cols];
            var source = e.Cells ?? [];
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = i < source.Length ? Multiline(source[i]) : string.Empty;
            }

            colWidths = new double[cols];
            var widths = e.ColWidths ?? [];
            var fallback = w / cols;
            for (var i = 0; i < cols; i++)
            {
                var cw = i < widths.Length ? widths[i] : fallback;
                colWidths[i] = Coordinate(cw <= 0 ? fallback : cw, signed: false);
            }
        }

        var parentId = string.IsNullOrWhiteSpace(e.ParentId) ? null : RequireId(e.ParentId, "parent");
        return new BoardElement(
            id, e.Type, x, y, w, h, rotation, stroke, fill, width, text, font, hasText && (e.Rtl ?? false), points,
            corner, attachmentId, alt, e.Locked ?? false, e.Hidden ?? false, parentId,
            fromId, toId, fromPort, toPort, route, startArrow, endArrow, children, rows, cols, cells, colWidths);
    }

    private static string RequireId(string? value, string kind)
    {
        var id = (value ?? string.Empty).Trim();
        if (!IdPattern().IsMatch(id))
        {
            throw Invalid($"A connector {kind} id is not valid.");
        }

        return id;
    }

    private static double[] OptionalPolyline(double[]? points)
    {
        var p = points ?? [];
        if (p.Length == 0)
        {
            return [];
        }

        if (p.Length is < 4 or > MaxPointValues || p.Length % 2 != 0 || p.Any(v => !double.IsFinite(v) || Math.Abs(v) > MaxCoordinate))
        {
            throw Invalid($"Connector points must be 2–{MaxPointValues / 2} finite coordinates.");
        }

        return p.Select(v => Math.Round(v, 2)).ToArray();
    }

    private static double[] Points(BoardElementInput e)
    {
        var points = e.Points ?? [];
        if (e.Type is not (BoardShape.Arrow or BoardShape.Pen or BoardShape.Line))
        {
            return [];
        }

        var valid = e.Type is BoardShape.Arrow or BoardShape.Line ? points.Length == 4 : points.Length is >= 4 and <= MaxPointValues && points.Length % 2 == 0;
        if (!valid || points.Any(p => !double.IsFinite(p) || Math.Abs(p) > MaxCoordinate))
        {
            throw Invalid(e.Type is BoardShape.Arrow or BoardShape.Line ? "An arrow or line needs exactly two points." : $"A pen stroke needs 2–{MaxPointValues / 2} points.");
        }

        return points.Select(p => Math.Round(p, 2)).ToArray();
    }

    private static double Coordinate(double value, bool signed)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > MaxCoordinate || (!signed && value < 0))
        {
            throw Invalid("A position or size is out of range.");
        }

        return Math.Round(value, 2);
    }

    private static string? Color(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ColorPattern().IsMatch(value.Trim()) ? value.Trim().ToLower(CultureInfo.InvariantCulture) : throw Invalid("Colours must look like #rrggbb.");
    }

    private static string NormalizeBackground(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            return "transparent";
        }

        return Color(value) ?? DefaultBackground;
    }

    private static string? CleanDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (text.Length > MaxDescriptionLength || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
        {
            throw new DomainException("board.description_invalid", $"Description is limited to {MaxDescriptionLength} characters.");
        }

        return text;
    }

    private static string CleanAlt(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length > MaxAltLength || text.Any(char.IsControl))
        {
            throw Invalid($"Image alt text is limited to {MaxAltLength} characters.");
        }

        return text;
    }

    private static string Multiline(string? value)
    {
        var text = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n');
        if (text.Length > MaxTextLength || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
        {
            throw Invalid($"Text is limited to {MaxTextLength} characters without control characters.");
        }

        return text;
    }

    private static DomainException Invalid(string message) => new("board.element_invalid", message);
}

/// <summary>Board-scoped file (images). Bytes live in SQL like page attachments until object storage (7b) lands.</summary>
public sealed class WhiteboardAttachment : Entity, ITenantOwned
{
    public const int MaxFileNameLength = 255;

    private WhiteboardAttachment() { }

    public Guid TenantId { get; private set; }

    public Guid WhiteboardId { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public string MediaType { get; private set; } = "application/octet-stream";

    public long Size { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    public byte[] Data { get; private set; } = [];

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsImage => MediaType.StartsWith("image/", StringComparison.Ordinal);

    public static WhiteboardAttachment Create(Guid whiteboardId, string fileName, byte[] data, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);
        var media = Attachment.Sniff(data);
        if (!media.StartsWith("image/", StringComparison.Ordinal))
        {
            throw new DomainException("board.attachment_not_image", "Whiteboard attachments must be images (PNG, JPEG, GIF or WebP).");
        }

        return new WhiteboardAttachment
        {
            WhiteboardId = whiteboardId,
            FileName = Attachment.CleanFileName(fileName),
            MediaType = media,
            Size = data.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)),
            Data = data,
            CreatedBy = userId,
            CreatedAt = now,
        };
    }
}

/// <summary>Per-user favourite marker for a whiteboard (tenant-scoped).</summary>
public sealed class WhiteboardFavorite : Entity, ITenantOwned
{
    private WhiteboardFavorite() { }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid WhiteboardId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static WhiteboardFavorite Create(Guid userId, Guid whiteboardId, DateTimeOffset now) =>
        new() { UserId = userId, WhiteboardId = whiteboardId, CreatedAt = now };
}

/// <summary>Immutable snapshot of a board after a REST save (Phase 4).</summary>
public sealed class WhiteboardRevision : Entity, ITenantOwned
{
    public const int DefaultRetention = 50;

    private WhiteboardRevision() { }

    public Guid TenantId { get; private set; }

    public Guid WhiteboardId { get; private set; }

    public int BoardRevision { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string ElementsJson { get; private set; } = "[]";

    public string? Description { get; private set; }

    public string Background { get; private set; } = Whiteboard.DefaultBackground;

    public BoardGridStyle GridStyle { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static WhiteboardRevision Capture(Whiteboard board, Guid userId, DateTimeOffset now) =>
        new()
        {
            WhiteboardId = board.Id,
            BoardRevision = board.Revision,
            Title = board.Title,
            ElementsJson = board.ElementsJson,
            Description = board.Description,
            Background = board.Background,
            GridStyle = board.GridStyle,
            CreatedBy = userId,
            CreatedAt = now,
        };
}

/// <summary>Reusable board template (system or tenant/space scoped).</summary>
public sealed class WhiteboardTemplate : Entity, ITenantOwned
{
    public const int MaxNameLength = 200;

    private WhiteboardTemplate() { }

    public Guid TenantId { get; private set; }

    public Guid? SpaceId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string ElementsJson { get; private set; } = "[]";

    public string Background { get; private set; } = Whiteboard.DefaultBackground;

    public BoardGridStyle GridStyle { get; private set; } = BoardGridStyle.Dot;

    public bool IsSystem { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static WhiteboardTemplate Create(Guid? spaceId, string name, string? description, IReadOnlyList<BoardElement> elements, string background, BoardGridStyle grid, Guid userId, DateTimeOffset now, bool system = false)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length is 0 or > MaxNameLength)
        {
            throw new DomainException("board.template_name_invalid", $"Template name must be 1–{MaxNameLength} characters.");
        }

        return new WhiteboardTemplate
        {
            SpaceId = spaceId,
            Name = n,
            Description = description,
            ElementsJson = JsonSerializer.Serialize(elements, BoardJson.Options),
            Background = background,
            GridStyle = grid,
            IsSystem = system,
            CreatedBy = userId,
            CreatedAt = now,
        };
    }

    public IReadOnlyList<BoardElement> Elements => JsonSerializer.Deserialize<List<BoardElement>>(ElementsJson, BoardJson.Options) ?? [];
}

/// <summary>Comment thread anchored to a board object or a point (Phase 4).</summary>
public sealed class WhiteboardComment : Entity, ITenantOwned
{
    public const int MaxBodyLength = 4000;

    private WhiteboardComment() { }

    public Guid TenantId { get; private set; }

    public Guid WhiteboardId { get; private set; }

    public Guid? ParentId { get; private set; }

    public string? ObjectId { get; private set; }

    public double? X { get; private set; }

    public double? Y { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public Guid? ResolvedBy { get; private set; }

    public static WhiteboardComment Create(Guid whiteboardId, string body, Guid userId, DateTimeOffset now, Guid? parentId = null, string? objectId = null, double? x = null, double? y = null)
    {
        var text = (body ?? string.Empty).Trim();
        if (text.Length is 0 or > MaxBodyLength)
        {
            throw new DomainException("board.comment_invalid", $"Comment body must be 1–{MaxBodyLength} characters.");
        }

        return new WhiteboardComment
        {
            WhiteboardId = whiteboardId,
            ParentId = parentId,
            ObjectId = string.IsNullOrWhiteSpace(objectId) ? null : objectId.Trim(),
            X = x,
            Y = y,
            Body = text,
            CreatedBy = userId,
            CreatedAt = now,
        };
    }

    public void Resolve(Guid userId, DateTimeOffset now)
    {
        ResolvedAt = now;
        ResolvedBy = userId;
    }

    public void Reopen()
    {
        ResolvedAt = null;
        ResolvedBy = null;
    }
}
