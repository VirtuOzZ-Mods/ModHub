using System.Net;
using System.Text;
using GTAModManager.Core.Discover;
using Xunit;

namespace GTAModManager.Core.Tests;

public sealed class DiscoverParserTests
{
    // A synthetic page with the same structure as GTA5-Mods (not a copy of a real one).
    private const string Page = """
        <html><head>
        <meta property="og:title" content="Cool Mod [Legacy + Enhanced]">
        <meta property="og:image" content="https://img.gta5-mods.com/q75/images/cool-mod/abc-cover.jpg">
        </head><body>
        <div id="file" class="container"><h1>
          Cool Mod
          <span class="version">2.1 [Legacy + Enhanced]</span>
        </h1>
        <div id="file-container" class="row">
          <div class="user-panel row"><a class="username" href="/users/Author">Author</a>
            <div class="user-social">
              <a title="YouTube" target="_blank" href="https://www.youtube.com/channel/UC123"><span class="fa fa-youtube"></span></a>
              <a title="Steam" target="_blank" href="http://steamcommunity.com/id/x"><span></span></a>
              <a title="Discord Server" target="_blank" href="https://discord.gg/abc"><span></span></a>
              <a title="Patreon" target="_blank" href="javascript:alert(1)"><span></span></a>
            </div>
            <a href="https://www.patreon.com/Author?utm_source=GTA5-Mods" class="patreon-button" target="_blank">Support me</a>
          </div>
          <div class="file-version-container"><span class="num-downloads">999 downloads</span></div>
          <div class="pull-left file-stats">
            <div class="file-stat file-downloads pull-left">
              <span class="num-downloads">1,301</span><label>Downloads</label></div>
            <div class="file-stat file-likes pull-left">
              <span class="num-likes">46</span><label>Likes</label></div>
          </div>
          <div id=tag-list><div><a href="/all/tags/asi/most-downloaded">Asi</a></div>
            <div><a href="/all/tags/enhanced/most-downloaded">Enhanced</a></div></div>
          <div id="file-dates"></div>
          <div id="comments">
            <div class="comment">Join my server https://discord.gg/spam and <a href="https://patreon.com/spam">patreon</a></div>
          </div>
        </div></div>
        <script type="application/ld+json">
        { "@context": "http://schema.org", "@type": "CreativeWork",
          "aggregateRating": { "@type": "AggregateRating", "ratingValue": "4.36", "reviewCount": "7" },
          "author": "Author", "comment_count": "46", "dateModified": "2026-11-12T18:37:58Z", "name": "Cool Mod &amp; Co" }
        </script>
        </body></html>
        """;

    [Fact]
    public void Parses_metadata_from_json_ld_and_header()
    {
        var m = Gta5ModsPageParser.Parse(Page)!;
        Assert.Equal("Cool Mod & Co", m.Title);
        Assert.Equal("Author", m.Author);
        Assert.Equal("2.1 [Legacy + Enhanced]", m.Version);
        Assert.Equal(4.36, m.Rating);
        Assert.Equal(7, m.Votes);
        Assert.Equal(46, m.Comments);
        Assert.Equal(1301, m.Downloads); // the total counter, not a single version's counter
        Assert.Equal(46, m.Likes);
        Assert.Equal("https://img.gta5-mods.com/q75/images/cool-mod/abc-cover.jpg", m.ImageUrl);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 18, 37, 58, TimeSpan.Zero), m.Updated);
    }

    [Fact]
    public void Author_links_only_from_profile_panel_and_only_https_known_platforms()
    {
        var links = Gta5ModsPageParser.Parse(Page)!.Links;
        // Steam is listed with http:// on the page; known platforms are upgraded to https, javascript: is dropped.
        Assert.Equal([LinkKind.YouTube, LinkKind.Steam, LinkKind.Discord, LinkKind.Patreon], links.Select(l => l.Kind));
        Assert.Equal("https://steamcommunity.com/id/x", links.Single(l => l.Kind == LinkKind.Steam).Url);
        Assert.DoesNotContain(links, l => l.Url.Contains("spam"));
    }

    [Fact]
    public void Compatibility_comes_from_author_markers_or_site_tag()
    {
        Assert.Equal((true, true), Compat(Gta5ModsPageParser.Parse(Page)!.Compat));
        var none = new HashSet<string>();
        Assert.Equal((true, false), Compat(Gta5ModsPageParser.DeriveCompat("N.O.S. 1.9.5 [Legacy ONLY]", none)));
        Assert.Equal((true, true), Compat(Gta5ModsPageParser.DeriveCompat("Menyoo 2.0 (Enhanced & Legacy)", none)));
        Assert.Equal(
            (null, true),
            Compat(
                Gta5ModsPageParser.DeriveCompat("Packfile Limit Adjuster Enhanced", new HashSet<string> { "enhanced" })
            )
        );
        Assert.Null(Gta5ModsPageParser.DeriveCompat("HeapAdjuster", new HashSet<string> { "asi" }));
    }

    private static (bool?, bool?) Compat(CompatInfo? c) => (c?.Legacy, c?.Enhanced);

    [Fact]
    public void Missing_values_stay_null_instead_of_zero()
    {
        const string html = """<meta property="og:title" content="Bare"><h1>Bare</h1>""";
        var m = Gta5ModsPageParser.Parse(html)!;
        Assert.Equal("Bare", m.Title);
        Assert.Null(m.Rating);
        Assert.Null(m.Votes);
        Assert.Null(m.Downloads);
        Assert.Null(m.Likes);
        Assert.Null(m.Comments);
        Assert.Null(m.Version);
        Assert.Null(m.ImageUrl);
        Assert.Empty(m.Links);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Checking your browser…</body></html>")]
    public void Non_mod_pages_are_not_recognized(string html) => Assert.Null(Gta5ModsPageParser.Parse(html));

    [Fact]
    public void Cover_from_foreign_host_is_ignored()
    {
        var html = Page.Replace(
            "https://img.gta5-mods.com/q75/images/cool-mod/abc-cover.jpg",
            "https://evil.example.com/x.jpg"
        );
        Assert.Null(Gta5ModsPageParser.Parse(html)!.ImageUrl);
    }

    [Fact]
    public void Devc_and_github_sources()
    {
        var shv = DevCPageParser.Parse(
            "<p>Script Hook V</p><b>Released</b> 15 Jul 2026 <b>Version</b> v3889.0 / 1158.13 <b>Supported patches</b>"
        )!;
        Assert.Equal("v3889.0 / 1158.13", shv.Version);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero), shv.Updated);
        Assert.Null(shv.Downloads);

        var gh = GitHubReleaseParser.Parse(
            """{"tag_name":"v1.1.0.6","published_at":"2026-07-15T19:59:23Z"}""",
            "Chiheb-Bacha/ScriptHookVDotNetEnhanced"
        )!;
        Assert.Equal("v1.1.0.6", gh.Version);
        Assert.Equal("Chiheb-Bacha", gh.Author);
        Assert.Equal(LinkKind.GitHub, Assert.Single(gh.Links).Kind);
    }

    [Theory]
    [InlineData("https://www.gta5-mods.com/scripts/x", true)]
    [InlineData("http://www.gta5-mods.com/scripts/x", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("https://127.0.0.1/x", false)]
    [InlineData("https://user:pw@example.com/", false)]
    [InlineData("\\\\server\\share\\a.exe", false)]
    [InlineData("C:\\Windows\\notepad.exe", false)]
    public void Only_https_web_links_are_allowed(string url, bool ok) => Assert.Equal(ok, LinkSafety.IsSafeWebUrl(url));

    [Fact]
    public void Website_label_requires_explicit_title_and_known_platforms_go_by_host()
    {
        Assert.Equal(LinkKind.Website, LinkSafety.Classify("Website", "https://mysite.example.org/")!.Kind);
        Assert.Equal(LinkKind.Twitter, LinkSafety.Classify("Twitter", "https://x.com/someone")!.Kind);
        // Only known platforms are upgraded from http; an unknown http site stays rejected.
        Assert.Null(LinkSafety.Classify("Website", "http://mysite.example.org/"));
        // Labeled "YouTube" but the URL is not YouTube: the type comes from the real host, not the label.
        Assert.Null(LinkSafety.Classify("YouTube", "https://youtube.com.evil.example/"));
    }
}

/// <summary>
/// Check against real saved pages (not included in the repository): the folder is set by GMM_DISCOVER_PAGES.
/// Without the variable the test does nothing.
/// </summary>
public sealed class DiscoverRealPagesTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Real_pages_parse()
    {
        var dir = Environment.GetEnvironmentVariable("GMM_DISCOVER_PAGES");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;
        foreach (var f in Directory.GetFiles(dir, "*.html").Order())
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var html = File.ReadAllText(f);
            var m = name == "shv" ? DevCPageParser.Parse(html) : Gta5ModsPageParser.Parse(html);
            Assert.NotNull(m);
            output.WriteLine(
                $"{name}: «{m!.Title}» by {m.Author} v={m.Version} ★{m.Rating}({m.Votes}) 👍{m.Likes} ⬇{m.Downloads} 💬{m.Comments} "
                    + $"upd={m.Updated:yyyy-MM-dd} img={(m.ImageUrl is null ? "-" : "ok")} compat={m.Compat?.Legacy}/{m.Compat?.Enhanced} "
                    + $"links={string.Join(",", m.Links.Select(l => l.Kind))}"
            );
            if (name != "shv")
            {
                Assert.NotNull(m.Rating);
                Assert.NotNull(m.Downloads);
                Assert.NotNull(m.Likes);
                Assert.NotNull(m.ImageUrl);
            }
        }
    }
}

public sealed class DiscoverCatalogTests
{
    [Fact]
    public void Built_in_catalog_has_required_entries_and_excludes_forbidden_ones()
    {
        var c = DiscoverCatalog.LoadBuiltIn();
        Assert.Equal(["essentials", "virtuozz", "recommended"], c.Categories.Select(x => x.Id));
        string[] essentials =
        [
            "scripthookv",
            "shvdn-nightly",
            "shvdn-enhanced",
            "heapadjuster",
            "heapadjuster-enhanced",
            "packfile-limit-adjuster",
            "packfile-limit-adjuster-enhanced",
            "gameconfig-dilapidated",
        ];
        Assert.All(essentials, id => Assert.Equal("essentials", c.Entries.Single(e => e.Id == id).Category));
        Assert.Equal(
            ["nos", "traffic-player-recorder", "quick-dialogue-system", "tesla-roadster-rcs"],
            c.Entries.Where(e => e.Category == "virtuozz").OrderBy(e => e.Order).Select(e => e.Id)
        );
        Assert.Equal(
            [
                "simple-trainer",
                "menyoo-2-0",
                "naturalvision-evolved",
                "naturalvision-enhanced",
                "scene-director",
                "map-editor",
                "andres-map-editor",
                "enable-all-interiors",
                "trafficx-custom",
                "100-save-game",
                "ghost-replay",
                "autosport-racing-system",
            ],
            c.Entries.Where(e => e.Category == "recommended").OrderBy(e => e.Order).Select(e => e.Id)
        );
        // Don Ped and the outdated Menyoo PC are not in the catalog (the author added Tesla Roadster RCS to their own Mod Pack).
        Assert.DoesNotContain(c.Entries, e =>
            e.Url.Contains("don-ped", StringComparison.OrdinalIgnoreCase)
            || e.Url.EndsWith("/menyoo-pc-sp", StringComparison.OrdinalIgnoreCase)
        );
        Assert.All(c.Entries, e => Assert.True(LinkSafety.IsSafeWebUrl(e.Url)));
        Assert.All(c.Entries, e => Assert.NotEqual(MetaSource.None, e.Source));
    }

    [Fact]
    public void Broken_entries_are_skipped()
    {
        const string json = """
            {"version":1,"categories":[{"id":"a","title":"A"}],
             "entries":[{"id":"ok","category":"a","url":"https://example.com/x","title":"Ok","source":"gta5mods"},
                        {"id":"bad-url","category":"a","url":"file:///c:/x.exe","title":"Bad"},
                        {"id":"bad-cat","category":"zzz","url":"https://example.com/y","title":"Bad"},
                        {"id":"ok","category":"a","url":"https://example.com/z","title":"Dup"}]}
            """;
        var c = DiscoverCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("ok", Assert.Single(c.Entries).Id);
    }
}

public sealed class DiscoverProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "gmm-discover-" + Guid.NewGuid().ToString("N")[..8]
    );

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException) { }
    }

    private static readonly byte[] Jpeg =
    [
        0xFF,
        0xD8,
        0xFF,
        0xE0,
        0,
        0x10,
        (byte)'J',
        (byte)'F',
        (byte)'I',
        (byte)'F',
        0,
        1,
    ];

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var resp = respond(r);
            resp.RequestMessage = r;
            return Task.FromResult(resp);
        }
    }

    private static CatalogEntry Entry() =>
        new()
        {
            Id = "cool",
            Category = "a",
            Url = "https://www.gta5-mods.com/scripts/cool-mod",
            Title = "Cool (fallback)",
            SourceName = "gta5mods",
        };

    private const string Html = """
        <meta property="og:title" content="Cool Mod">
        <meta property="og:image" content="https://img.gta5-mods.com/q75/images/cool/cover.jpg">
        <script type="application/ld+json">{"@type":"CreativeWork","name":"Cool Mod","author":"A","aggregateRating":{"ratingValue":"4.5","reviewCount":"10"}}</script>
        """;

    [Fact]
    public async Task Downloads_once_then_works_offline_from_cache()
    {
        var online = true;
        var h = new FakeHandler(r =>
        {
            if (!online)
                throw new HttpRequestException("нет сети");
            return r.RequestUri!.Host.StartsWith("img.")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Jpeg) { Headers = { ContentType = new("image/jpeg") } },
                }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Html) };
        });
        using (var p = new ModMetadataProvider(_dir, h, TimeSpan.Zero))
        {
            Assert.Equal(0, await p.RefreshAsync([Entry()], force: false, null, CancellationToken.None));
            var c = p.Get("cool")!;
            Assert.Equal("Cool Mod", c.Data.Title);
            Assert.Equal(4.5, c.Data.Rating);
            Assert.NotNull(p.ImagePath(c));
            Assert.Equal(2, h.Calls);

            // Fresh data: no repeated request.
            await p.RefreshAsync([Entry()], force: false, null, CancellationToken.None);
            Assert.Equal(2, h.Calls);
        }

        online = false;
        using (var p = new ModMetadataProvider(_dir, h, TimeSpan.Zero))
        {
            // No network: the error is recorded, but the data and cover from the cache remain.
            Assert.Equal(1, await p.RefreshAsync([Entry()], force: true, null, CancellationToken.None));
            var c = p.Get("cool")!;
            Assert.Equal("Cool Mod", c.Data.Title);
            Assert.NotNull(p.ImagePath(c));
            Assert.NotNull(c.LastError);
            Assert.True(p.CacheSize() > 0);
            p.Clear();
            Assert.False(Directory.Exists(_dir));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Http_errors_are_reported_not_thrown(HttpStatusCode code)
    {
        using var p = new ModMetadataProvider(_dir, new FakeHandler(_ => new HttpResponseMessage(code)), TimeSpan.Zero);
        Assert.Equal(1, await p.RefreshAsync([Entry()], force: true, null, CancellationToken.None));
        Assert.Contains(((int)code).ToString(), p.Get("cool")!.LastError);
        Assert.Null(p.Get("cool")!.Data.Title);
    }

    [Fact]
    public async Task Non_image_response_is_not_saved_as_cover()
    {
        var h = new FakeHandler(r =>
            r.RequestUri!.Host.StartsWith("img.")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>not an image</html>"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Html) }
        );
        using var p = new ModMetadataProvider(_dir, h, TimeSpan.Zero);
        await p.RefreshAsync([Entry()], force: true, null, CancellationToken.None);
        Assert.Null(p.ImagePath(p.Get("cool")));
        Assert.False(
            Directory.Exists(Path.Combine(_dir, "img")) && Directory.EnumerateFiles(Path.Combine(_dir, "img")).Any()
        );
    }

    [Fact]
    public async Task Cancellation_stops_refresh()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var p = new ModMetadataProvider(
            _dir,
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            TimeSpan.Zero
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.RefreshAsync([Entry()], true, null, cts.Token));
        Assert.False(Directory.Exists(_dir)); // nothing was created
    }
}
