using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Raavi.Wiki.Domain;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Raavi.Wiki.Application;

/// <summary>
/// Writes a slide deck as a PowerPoint file (16:9) with the Open XML SDK: one master, one blank layout, a text-only shape set per slide and
/// real speaker notes. Right-to-left decks get RTL paragraphs, right alignment and a Persian language tag. The output is schema-validated in
/// the tests; it has not been opened in PowerPoint itself.
/// </summary>
internal static class PptxWriter
{
    private const long SlideWidth = 12192000;
    private const long SlideHeight = 6858000;
    private const long Margin = 640080;

    private const string Navy = "1F3A5F";
    private const string Ink = "1B2430";
    private const string White = "FFFFFF";
    private const string Muted = "D6E0EE";

    public static byte[] Write(string title, TextDirection direction, IReadOnlyList<Slide> slides, string creator)
    {
        var rtl = direction == TextDirection.Rtl;
        using var stream = new MemoryStream();
        using (var doc = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, autoSave: true))
        {
            doc.PackageProperties.Title = title;
            doc.PackageProperties.Creator = creator;

            var presentation = doc.AddPresentationPart();

            var master = presentation.AddNewPart<SlideMasterPart>();
            var theme = master.AddNewPart<ThemePart>();
            theme.Theme = BuildTheme();
            presentation.AddPart(theme);

            var layout = master.AddNewPart<SlideLayoutPart>();
            layout.AddPart(master);
            layout.SlideLayout = new P.SlideLayout(
                new P.CommonSlideData(Tree()) { Name = "Blank" },
                new P.ColorMapOverride(new A.MasterColorMapping())) { Type = P.SlideLayoutValues.Blank };

            master.SlideMaster = new P.SlideMaster(
                new P.CommonSlideData(Tree()),
                new P.ColorMap
                {
                    Background1 = A.ColorSchemeIndexValues.Light1,
                    Text1 = A.ColorSchemeIndexValues.Dark1,
                    Background2 = A.ColorSchemeIndexValues.Light2,
                    Text2 = A.ColorSchemeIndexValues.Dark2,
                    Accent1 = A.ColorSchemeIndexValues.Accent1,
                    Accent2 = A.ColorSchemeIndexValues.Accent2,
                    Accent3 = A.ColorSchemeIndexValues.Accent3,
                    Accent4 = A.ColorSchemeIndexValues.Accent4,
                    Accent5 = A.ColorSchemeIndexValues.Accent5,
                    Accent6 = A.ColorSchemeIndexValues.Accent6,
                    Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                    FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
                },
                new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = master.GetIdOfPart(layout) }));

            var notesMaster = presentation.AddNewPart<NotesMasterPart>();
            var notesTheme = notesMaster.AddNewPart<ThemePart>();
            notesTheme.Theme = BuildTheme();
            notesMaster.NotesMaster = BuildNotesMaster();

            var slideIds = new P.SlideIdList();
            uint nextId = 256;
            foreach (var slide in slides)
            {
                var part = presentation.AddNewPart<SlidePart>();
                part.AddPart(layout);
                part.Slide = BuildSlide(slide, rtl);
                if (slide.Notes.Length > 0)
                {
                    var notes = part.AddNewPart<NotesSlidePart>();
                    notes.AddPart(notesMaster);
                    notes.AddPart(part);
                    notes.NotesSlide = BuildNotes(slide.Notes, rtl);
                }

                slideIds.Append(new P.SlideId { Id = nextId++, RelationshipId = presentation.GetIdOfPart(part) });
            }

            presentation.Presentation = new P.Presentation(
                new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648U, RelationshipId = presentation.GetIdOfPart(master) }),
                new P.NotesMasterIdList(new P.NotesMasterId { Id = presentation.GetIdOfPart(notesMaster) }),
                slideIds,
                new P.SlideSize { Cx = (int)SlideWidth, Cy = (int)SlideHeight },
                new P.NotesSize { Cx = 6858000, Cy = 9144000 });
        }

        return stream.ToArray();
    }

    // ── Slides ─────────────────────────────────────────────────────────────────────────────────

    private static P.Slide BuildSlide(Slide slide, bool rtl)
    {
        var lang = rtl ? "fa-IR" : "en-US";
        var lines = slide.Body.Length == 0 ? [] : slide.Body.Split('\n');
        var shapes = new List<OpenXmlElement>();
        P.Background? background = null;

        switch (slide.Layout)
        {
            case SlideLayout.Title:
                background = new P.Background(new P.BackgroundProperties(new A.SolidFill(new A.RgbColorModelHex { Val = Navy }), new A.EffectList()));
                shapes.Add(Box(2, "Title", Margin, 2150000, SlideWidth - (2 * Margin), 1500000, A.TextAnchoringTypeValues.Bottom,
                    [Paragraph(slide.Title, 4800, true, White, A.TextAlignmentTypeValues.Center, rtl, lang, bullet: false)]));
                if (lines.Length > 0)
                {
                    shapes.Add(Box(3, "Subtitle", Margin, 3850000, SlideWidth - (2 * Margin), 1400000, A.TextAnchoringTypeValues.Top,
                        lines.Select(l => Paragraph(l, 2400, false, Muted, A.TextAlignmentTypeValues.Center, rtl, lang, bullet: false))));
                }

                break;

            case SlideLayout.Bullets:
            case SlideLayout.Text:
                var align = rtl ? A.TextAlignmentTypeValues.Right : A.TextAlignmentTypeValues.Left;
                shapes.Add(Box(2, "Title", Margin, 380000, SlideWidth - (2 * Margin), 1000000, A.TextAnchoringTypeValues.Center,
                    [Paragraph(slide.Title, 3600, true, Navy, align, rtl, lang, bullet: false)]));
                var size = lines.Length > 14 ? 1400 : lines.Length > 10 ? 1800 : lines.Length > 6 ? 2200 : 2600;
                var bullets = slide.Layout == SlideLayout.Bullets;
                shapes.Add(Box(3, "Body", Margin, 1550000, SlideWidth - (2 * Margin), SlideHeight - 1550000 - 500000, A.TextAnchoringTypeValues.Top,
                    lines.Select(l => Paragraph(l, size, false, Ink, align, rtl, lang, bullets && l.Length > 0))));
                break;
        }

        var tree = Tree();
        foreach (var s in shapes)
        {
            tree.Append(s);
        }

        var data = new P.CommonSlideData();
        if (background is not null)
        {
            data.Append(background);
        }

        data.Append(tree);
        return new P.Slide(data, new P.ColorMapOverride(new A.MasterColorMapping()));
    }

    private static P.NotesSlide BuildNotes(string notes, bool rtl)
    {
        var lang = rtl ? "fa-IR" : "en-US";
        var paragraphs = notes.Split('\n').Select(l => Paragraph(l, 1200, false, Ink, rtl ? A.TextAlignmentTypeValues.Right : A.TextAlignmentTypeValues.Left, rtl, lang, bullet: false)).ToList();
        return new P.NotesSlide(
            new P.CommonSlideData(Tree(
                Placeholder(2, "Slide Image Placeholder 1", P.PlaceholderValues.SlideImage, null),
                Placeholder(3, "Notes Placeholder 2", P.PlaceholderValues.Body, 1U, paragraphs))),
            new P.ColorMapOverride(new A.MasterColorMapping()));
    }

    private static P.NotesMaster BuildNotesMaster() =>
        new(
            new P.CommonSlideData(Tree(
                Placeholder(2, "Slide Image Placeholder 1", P.PlaceholderValues.SlideImage, null, offset: (685800, 1143000, 5486400, 3086100)),
                Placeholder(3, "Notes Placeholder 2", P.PlaceholderValues.Body, 1U, offset: (685800, 4400550, 5486400, 3600450)))),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
            });

    // ── Building blocks ────────────────────────────────────────────────────────────────────────

    private static P.ShapeTree Tree(params OpenXmlElement[] shapes)
    {
        var tree = new P.ShapeTree();
        foreach (var h in GroupHeader())
        {
            tree.Append(h);
        }

        foreach (var s in shapes)
        {
            tree.Append(s);
        }

        return tree;
    }

    private static OpenXmlElement[] GroupHeader() =>
    [
        new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty }, new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
        new P.GroupShapeProperties(new A.TransformGroup(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 0, Cy = 0 }, new A.ChildOffset { X = 0, Y = 0 }, new A.ChildExtents { Cx = 0, Cy = 0 })),
    ];

    private static P.Shape Placeholder(uint id, string name, P.PlaceholderValues type, uint? index, IEnumerable<A.Paragraph>? text = null, (long X, long Y, long Cx, long Cy)? offset = null)
    {
        var ph = new P.PlaceholderShape { Type = type };
        if (index is { } i)
        {
            ph.Index = i;
        }

        var shape = new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties(ph)),
            offset is { } o
                ? new P.ShapeProperties(new A.Transform2D(new A.Offset { X = o.X, Y = o.Y }, new A.Extents { Cx = o.Cx, Cy = o.Cy }))
                : new P.ShapeProperties());
        if (text is not null)
        {
            shape.Append(Body(new A.BodyProperties(), text));
        }

        return shape;
    }

    private static P.Shape Box(uint id, string name, long x, long y, long cx, long cy, A.TextAnchoringTypeValues anchor, IEnumerable<A.Paragraph> paragraphs) =>
        new(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties { TextBox = true },
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                new A.NoFill()),
            Body(new A.BodyProperties(new A.NormalAutoFit()) { Wrap = A.TextWrappingValues.Square, Anchor = anchor }, paragraphs));

    private static P.TextBody Body(A.BodyProperties properties, IEnumerable<A.Paragraph> paragraphs)
    {
        var body = new P.TextBody(properties, new A.ListStyle());
        foreach (var p in paragraphs)
        {
            body.Append(p);
        }

        return body;
    }

    private static A.Paragraph Paragraph(string text, int size, bool bold, string color, A.TextAlignmentTypeValues align, bool rtl, string lang, bool bullet)
    {
        var props = new A.ParagraphProperties { Alignment = align, RightToLeft = rtl };
        if (bullet)
        {
            props.LeftMargin = 342900;
            props.Indent = -342900;
            props.Append(new A.SpaceBefore(new A.SpacingPoints { Val = 600 }));
            props.Append(new A.BulletFont { Typeface = "Arial" });
            props.Append(new A.CharacterBullet { Char = "•" });
        }
        else
        {
            props.Append(new A.NoBullet());
        }

        var paragraph = new A.Paragraph(props);
        if (text.Length == 0)
        {
            paragraph.Append(new A.EndParagraphRunProperties { Language = lang, FontSize = size });
            return paragraph;
        }

        paragraph.Append(new A.Run(
            new A.RunProperties(
                new A.SolidFill(new A.RgbColorModelHex { Val = color }),
                new A.LatinFont { Typeface = "Calibri" },
                new A.ComplexScriptFont { Typeface = "Tahoma" })
            { Language = lang, FontSize = size, Bold = bold, Dirty = false },
            new A.Text(text)));
        return paragraph;
    }

    private static A.Theme BuildTheme()
    {
        static A.SolidFill Phc() => new(new A.SchemeColor { Val = A.SchemeColorValues.PhColor });
        static A.Outline Line(long w) => new(Phc(), new A.PresetDash { Val = A.PresetLineDashValues.Solid }) { Width = (int)w };

        return new A.Theme(
            new A.ThemeElements(
                new A.ColorScheme(
                    new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
                    new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
                    new A.Dark2Color(new A.RgbColorModelHex { Val = Navy }),
                    new A.Light2Color(new A.RgbColorModelHex { Val = "EEF2F7" }),
                    new A.Accent1Color(new A.RgbColorModelHex { Val = "2F6FDE" }),
                    new A.Accent2Color(new A.RgbColorModelHex { Val = "1F9D8F" }),
                    new A.Accent3Color(new A.RgbColorModelHex { Val = "E0A100" }),
                    new A.Accent4Color(new A.RgbColorModelHex { Val = "D6455D" }),
                    new A.Accent5Color(new A.RgbColorModelHex { Val = "7A5AF8" }),
                    new A.Accent6Color(new A.RgbColorModelHex { Val = "5B6B7F" }),
                    new A.Hyperlink(new A.RgbColorModelHex { Val = "1D5FD1" }),
                    new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "6B4BC8" })) { Name = "Raavi" },
                new A.FontScheme(
                    new A.MajorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = string.Empty }, new A.ComplexScriptFont { Typeface = "Tahoma" }),
                    new A.MinorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = string.Empty }, new A.ComplexScriptFont { Typeface = "Tahoma" })) { Name = "Raavi" },
                new A.FormatScheme(
                    new A.FillStyleList(Phc(), Phc(), Phc()),
                    new A.LineStyleList(Line(6350), Line(12700), Line(19050)),
                    new A.EffectStyleList(new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList())),
                    new A.BackgroundFillStyleList(Phc(), Phc(), Phc())) { Name = "Raavi" }))
        { Name = "Raavi" };
    }
}
