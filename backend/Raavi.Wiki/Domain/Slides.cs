using System.Text.Json;
using System.Text.Json.Serialization;
using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

public enum SlideLayout
{
    /// <summary>Large centred title with the first body line as subtitle.</summary>
    Title = 1,

    /// <summary>Title and one bullet per body line.</summary>
    Bullets = 2,

    /// <summary>Title and free paragraphs (one per body line).</summary>
    Text = 3,
}

public enum TextDirection
{
    Ltr = 0,
    Rtl = 1,
}

/// <summary>One slide. The id is stable (server-assigned) so reordering never loses identity.</summary>
public sealed record Slide(string Id, SlideLayout Layout, string Title, string Body, string Notes);

/// <summary>Slide as sent by a client: an empty id means "new slide".</summary>
public sealed record SlideInput(string? Id, SlideLayout Layout, string? Title, string? Body, string? Notes);

internal static class SlideJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

/// <summary>
/// A slide deck of a space (Phase 9d): an ordered list of text slides with speaker notes, stored as validated JSON and exported to PPTX
/// (server) or PDF (browser print). Permissions come from the space (read = View; write = Edit on an active space).
/// </summary>
public sealed class SlideDeck : Entity, ITenantOwned
{
    public const int MaxTitleLength = 200;
    public const int MaxSlides = 100;
    public const int MaxSlideTitleLength = 200;
    public const int MaxBodyLength = 4000;
    public const int MaxBodyLines = 30;
    public const int MaxNotesLength = 2000;

    private SlideDeck() { }

    public Guid TenantId { get; private set; }

    public Guid SpaceId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>Reading direction of the whole deck (Persian/Arabic decks are right-to-left).</summary>
    public TextDirection Direction { get; private set; }

    /// <summary>JSON array of <see cref="Slide"/>.</summary>
    public string SlidesJson { get; private set; } = "[]";

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public IReadOnlyList<Slide> Slides => JsonSerializer.Deserialize<List<Slide>>(SlidesJson, SlideJson.Options) ?? [];

    public static SlideDeck Create(Guid spaceId, string? title, TextDirection direction, IReadOnlyList<SlideInput>? slides, Guid userId, DateTimeOffset now)
    {
        var deck = new SlideDeck { SpaceId = spaceId, CreatedBy = userId, CreatedAt = now, UpdatedAt = now };
        deck.Set(title, direction, slides ?? []);
        return deck;
    }

    /// <summary>Replaces title, direction and the whole slide list (order = list order).</summary>
    public void Update(string? title, TextDirection direction, IReadOnlyList<SlideInput>? slides, DateTimeOffset now)
    {
        Set(title, direction, slides ?? []);
        UpdatedAt = now;
        Revision++;
    }

    private void Set(string? title, TextDirection direction, IReadOnlyList<SlideInput> input)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length is 0 or > MaxTitleLength || t.Any(char.IsControl))
        {
            throw new DomainException("slides.title_invalid", $"A deck needs a title of 1–{MaxTitleLength} characters.");
        }

        if (!Enum.IsDefined(direction))
        {
            throw new DomainException("slides.direction_invalid", "The direction must be Ltr or Rtl.");
        }

        if (input.Count > MaxSlides)
        {
            throw new DomainException("slides.too_many", $"A deck can have at most {MaxSlides} slides.");
        }

        var existing = Slides.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Slide>(input.Count);
        foreach (var s in input)
        {
            if (!Enum.IsDefined(s.Layout))
            {
                throw new DomainException("slides.slide_invalid", "The slide layout is not supported.");
            }

            var id = string.IsNullOrWhiteSpace(s.Id) ? "s" + Guid.NewGuid().ToString("N")[..8] : s.Id.Trim();
            if (!seen.Add(id) || (!string.IsNullOrWhiteSpace(s.Id) && !existing.Contains(id)))
            {
                throw new DomainException("slides.slide_invalid", "The slide id is not valid.");
            }

            var title1 = (s.Title ?? string.Empty).Trim();
            if (title1.Length > MaxSlideTitleLength || title1.Any(char.IsControl))
            {
                throw new DomainException("slides.slide_invalid", $"A slide title is limited to {MaxSlideTitleLength} characters.");
            }

            var body = Multiline(s.Body, MaxBodyLength, "slide text");
            if (body.Length > 0 && body.Split('\n').Length > MaxBodyLines)
            {
                throw new DomainException("slides.slide_invalid", $"A slide can have at most {MaxBodyLines} lines.");
            }

            result.Add(new Slide(id, s.Layout, title1, body, Multiline(s.Notes, MaxNotesLength, "speaker notes")));
        }

        Title = t;
        Direction = direction;
        SlidesJson = JsonSerializer.Serialize(result, SlideJson.Options);
    }

    /// <summary>Normalises line endings, drops blank edge lines and refuses control characters other than newline and tab.</summary>
    private static string Multiline(string? value, int max, string what)
    {
        var text = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n');
        if (text.Length > max || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
        {
            throw new DomainException("slides.slide_invalid", $"The {what} is too long or contains control characters.");
        }

        return text;
    }
}
