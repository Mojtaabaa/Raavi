using System.Runtime.CompilerServices;
using Raavi.Migration.Confluence;

namespace Raavi.Tests.Migration;

/// <summary>In-memory Confluence used by migration tests (no network). Data is mutable so tests can shape each scenario.</summary>
public sealed class FakeConfluence : IConfluenceSourceFactory
{
    public sealed record FakePage(SourcePage Page, List<SourceVersion> Versions);

    public List<SourceSpace> Spaces { get; } = [];

    public Dictionary<string, List<FakePage>> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Source content ids whose version download fails with a (non-fatal) source error.</summary>
    public HashSet<string> BrokenPages { get; } = [];

    public List<string> VersionDownloads { get; } = [];

    /// <summary>Files per source page id (latest versions) and their bytes.</summary>
    public Dictionary<string, List<(SourceAttachment Meta, byte[] Data)>> Attachments { get; } = [];

    public HashSet<string> BrokenAttachments { get; } = [];

    public Dictionary<string, SourceRestrictions> Restrictions { get; } = [];

    public Dictionary<string, List<SourceComment>> Comments { get; } = [];

    /// <summary>Source users by key (name, e-mail…). Authors without an entry are unknown to the source.</summary>
    public Dictionary<string, SourceUser> Users { get; } = [];

    public List<string> AttachmentDownloads { get; } = [];

    /// <summary>A tiny but real PNG (signature + payload): the importer sniffs the type from the bytes, not from the source.</summary>
    public static byte[] Png(int size = 64) => [.. new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, .. new byte[size]];

    public void AddAttachment(string pageId, string fileName, byte[] data, long? declaredSize = null)
    {
        if (!Attachments.TryGetValue(pageId, out var list))
        {
            Attachments[pageId] = list = [];
        }

        list.Add((new SourceAttachment("a" + list.Count + pageId, fileName, "image/png", declaredSize ?? data.Length, 1, new DateTimeOffset(2023, 4, 1, 0, 0, 0, TimeSpan.Zero), "sara", "/download/attachments/" + pageId + "/" + fileName), data));
    }

    public string Version { get; set; } = "10.2.13";

    public string? ExpectedToken { get; set; }

    public IConfluenceSourceProvider Create(Uri baseUrl, string token) => new Source(this, token);

    public void Reset()
    {
        Spaces.Clear();
        Pages.Clear();
        BrokenPages.Clear();
        Attachments.Clear();
        BrokenAttachments.Clear();
        Restrictions.Clear();
        Comments.Clear();
        Users.Clear();
        AttachmentDownloads.Clear();
        VersionDownloads.Clear();
        ExpectedToken = null;
    }

    private sealed class Source(FakeConfluence data, string token) : IConfluenceSourceProvider
    {
        public ConfluenceSourceKind Kind => ConfluenceSourceKind.RestApi;

        public Task<ConfluenceSourceInfo> DetectAsync(CancellationToken cancellationToken)
        {
            Check();
            return Task.FromResult(new ConfluenceSourceInfo(ConfluenceEdition.Unknown, data.Version, 1, ConfluenceSourceKind.RestApi));
        }

        public Task<IReadOnlyList<SourceSpace>> ListSpacesAsync(CancellationToken cancellationToken)
        {
            Check();
            return Task.FromResult<IReadOnlyList<SourceSpace>>([.. data.Spaces]);
        }

        public async IAsyncEnumerable<SourcePage> StreamPagesAsync(string spaceKey, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Check();
            await Task.Yield();
            foreach (var p in data.Pages.GetValueOrDefault(spaceKey, []))
            {
                yield return p.Page;
            }
        }

        public async IAsyncEnumerable<SourceVersion> StreamVersionsAsync(string sourceContentId, [EnumeratorCancellation] CancellationToken cancellationToken, IReadOnlySet<int>? skip = null)
        {
            Check();
            await Task.Yield();
            if (data.BrokenPages.Contains(sourceContentId))
            {
                throw new ConfluenceSourceException("source.error", "The source answered HTTP 500.");
            }

            var page = data.Pages.Values.SelectMany(x => x).First(p => p.Page.SourceContentId == sourceContentId);
            foreach (var v in page.Versions.OrderBy(v => v.SourceVersionNumber))
            {
                if (skip?.Contains(v.SourceVersionNumber) == true)
                {
                    continue;
                }

                data.VersionDownloads.Add($"{sourceContentId}:{v.SourceVersionNumber}");
                yield return v;
            }
        }

        public Task<IReadOnlyList<SourceComment>> ListCommentsAsync(string sourceContentId, CancellationToken cancellationToken)
        {
            Check();
            return Task.FromResult<IReadOnlyList<SourceComment>>([.. data.Comments.GetValueOrDefault(sourceContentId, [])]);
        }

        public Task<SourceRestrictions> GetRestrictionsAsync(string sourceContentId, CancellationToken cancellationToken)
        {
            Check();
            return Task.FromResult(data.Restrictions.GetValueOrDefault(sourceContentId, SourceRestrictions.None));
        }

        public Task<SourceUser?> GetUserAsync(string keyOrUserName, CancellationToken cancellationToken)
        {
            Check();
            return Task.FromResult(data.Users.GetValueOrDefault(keyOrUserName));
        }

        public Task<IReadOnlyList<SourceAttachment>> ListAttachmentsAsync(string sourceContentId, CancellationToken cancellationToken)
        {
            Check();
            return Task.FromResult<IReadOnlyList<SourceAttachment>>([.. data.Attachments.GetValueOrDefault(sourceContentId, []).Select(a => a.Meta)]);
        }

        public Task<byte[]> DownloadAttachmentAsync(SourceAttachment attachment, long maxBytes, CancellationToken cancellationToken)
        {
            Check();
            if (data.BrokenAttachments.Contains(attachment.FileName))
            {
                throw new ConfluenceSourceException("source.error", "The source answered HTTP 500.");
            }

            data.AttachmentDownloads.Add(attachment.FileName);
            return Task.FromResult(data.Attachments.Values.SelectMany(x => x).First(a => a.Meta.Id == attachment.Id).Data);
        }

        public void Dispose()
        {
        }

        private void Check()
        {
            if (data.ExpectedToken is { } expected && expected != token)
            {
                throw new ConfluenceSourceException("source.unauthorized", "The source rejected the access token.");
            }
        }
    }

    // ── Builders ───────────────────────────────────────────────────────────────────────────────

    public FakePage AddPage(string spaceKey, string id, string title, string? parent, int position, params (int Number, string Body, string? Author, string? Comment, DateTimeOffset At)[] versions)
    {
        var last = versions.Max(v => v.Number);
        var page = new FakePage(
            new SourcePage(id, spaceKey, title, parent, position, last, []),
            [.. versions.Select(v => new SourceVersion(id, v.Number, v.Number == last ? title : title + $" (v{v.Number})", v.Body, v.Author, v.Author is null ? null : char.ToUpperInvariant(v.Author[0]) + v.Author[1..], v.At, v.Comment, v.Number == last))]);
        if (!Pages.TryGetValue(spaceKey, out var list))
        {
            Pages[spaceKey] = list = [];
        }

        list.Add(page);
        return page;
    }

    public FakePage WithLabels(FakePage page, params string[] labels)
    {
        var index = Pages[page.Page.SpaceKey].IndexOf(page);
        var replaced = page with { Page = page.Page with { Labels = labels } };
        Pages[page.Page.SpaceKey][index] = replaced;
        return replaced;
    }

    /// <summary>A small but representative wiki: hierarchy, three-version history, labels, links (resolvable and dead), macros.</summary>
    public void Seed()
    {
        Reset();
        var t = new DateTimeOffset(2023, 3, 1, 9, 0, 0, TimeSpan.Zero);
        Spaces.Add(new SourceSpace("eng", "Engineering", "Team space", t, HomePageSourceContentId: "100"));
        Spaces.Add(new SourceSpace("ops", "Operations", null, t));

        // Deliberately returned child-first: the importer must order parents before children itself.
        AddPage("eng", "102", "Child", "101", 0, (1, "<p>child</p><ac:structured-macro ac:name=\"contentbylabel\"><ac:parameter ac:name=\"cql\">label = \"x\" and space = \"ENG\"</ac:parameter></ac:structured-macro>", "ali", null, t.AddDays(3)));
        WithLabels(
            AddPage("eng", "101", "Guide", "100", 1,
                (1, "<p>first draft</p>", "sara", "draft", t.AddDays(1)),
                (2, "<p>See <ac:link><ri:page ri:content-title=\"Home\"/><ac:plain-text-link-body><![CDATA[home]]></ac:plain-text-link-body></ac:link> and <ac:link><ri:page ri:content-title=\"Missing\"/></ac:link>.</p><ac:image ac:alt=\"Architecture\"><ri:attachment ri:filename=\"diagram.png\"/></ac:image><ac:image><ri:attachment ri:filename=\"logo.png\"><ri:page ri:content-title=\"Home\"/></ri:attachment></ac:image>", "ali", "added links", t.AddDays(2))),
            "howto", "bad label!");
        AddPage("eng", "100", "Home", null, 0,
            (1, "<h1>Welcome</h1>", "sara", "created", t),
            (2, "<h1>Welcome</h1><p>Second.</p>", "ali", null, t.AddDays(10)),
            (3, "<h1>Welcome</h1><p>Third <ac:link><ri:page ri:content-title=\"Guide\"/></ac:link>.</p>", null, "final", t.AddDays(20)));
        AddAttachment("101", "diagram.png", Png());
        AddAttachment("101", "huge.bin", [1, 2, 3], declaredSize: 50L * 1024 * 1024);
        AddAttachment("100", "logo.png", Png(10));
        AddPage("ops", "200", "Runbook", null, 0, (1, "<ul><li>restart</li></ul>", "sara", null, t.AddDays(5)));
    }
}
