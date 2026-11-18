using System.Text;
using GTAModManager.Core.Discover;

namespace GTAModManager.Core.Tests;

public sealed class CustomCardsGalleryTests
{
    // Synthetic markup shaped like a GTA5-Mods file block (not a copy of a real page).
    private const string Page = """
        <html><head>
        <meta property="og:title" content="Cool Mod">
        <meta property="og:image" content="https://img.gta5-mods.com/q75/images/cool-mod/aaa-cover.jpg">
        </head><body>
        <div id="file"><h1>Cool Mod</h1>
          <a target="_blank" class="thumbnail mfp-image cover-media" title="Cool Mod" href="https://img.gta5-mods.com/q95/images/cool-mod/aaa-cover.jpg"><img src="https://img.gta5-mods.com/q85-w800/images/cool-mod/aaa-cover.jpg" /></a>
          <a target="_blank" class="thumbnail mfp-image" title="Cool Mod" href="https://img.gta5-mods.com/q95/images/cool-mod/bbb-shot%201.png"><img src="https://img.gta5-mods.com/q75-w350-h233-cfill/images/cool-mod/bbb-shot%201.png" /></a>
          <a target="_blank" class="thumbnail mfp-image" title="Cool Mod" href="https://img.gta5-mods.com/q95/images/cool-mod/aaa-cover.jpg"><img src="x" /></a>
          <a target="_blank" class="thumbnail mfp-iframe" href="https://www.youtube.com/watch?v=abc"><img src="y" /></a>
          <a target="_blank" class="thumbnail mfp-image" href="https://evil.example.com/q95/images/x.jpg"></a>
          <a target="_blank" class="thumbnail mfp-image" href="javascript:alert(1)"></a>
        </div>
        <div class="more-mods"><img src="https://img.gta5-mods.com/q75-w500-h333-cfill/images/other-mod/ccc.jpg"></div>
        </body></html>
        """;

    [Fact]
    public void Gallery_lists_screenshots_of_the_mod_as_card_sized_site_images()
    {
        var images = Gta5ModsPageParser.ReadGallery(Page);
        Assert.Equal(
            [
                "https://img.gta5-mods.com/q85-w800/images/cool-mod/aaa-cover.jpg",
                "https://img.gta5-mods.com/q85-w800/images/cool-mod/bbb-shot%201.png",
            ],
            images
        );
    }

    [Fact]
    public void Gallery_falls_back_to_the_og_image_cover()
    {
        const string html = """
            <meta property="og:title" content="Bare">
            <meta property="og:image" content="https://img.gta5-mods.com/q75/images/bare/x.jpg">
            """;
        Assert.Equal(["https://img.gta5-mods.com/q85-w800/images/bare/x.jpg"], Gta5ModsPageParser.ReadGallery(html));
        Assert.Empty(Gta5ModsPageParser.ReadGallery("<html>nothing</html>"));
    }

    [Theory]
    [InlineData("https://img.gta5-mods.com/q95/images/a/b.jpg", "https://img.gta5-mods.com/q85-w800/images/a/b.jpg")]
    [InlineData(
        "https://img.gta5-mods.com/q85-w800/images/a/b.jpg",
        "https://img.gta5-mods.com/q85-w800/images/a/b.jpg"
    )]
    [InlineData("https://www.gta5-mods.com/scripts/a", null)]
    [InlineData("https://img.example.com/q95/images/a/b.jpg", null)]
    [InlineData("http://img.gta5-mods.com/q95/images/a/b.jpg", null)]
    [InlineData(null, null)]
    public void Card_images_come_only_from_the_site_image_host(string? url, string? expected) =>
        Assert.Equal(expected, Gta5ModsPageParser.CardImage(url));
}

public sealed class CustomCardsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gmm-custom-cards-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "custom-cards.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData("https://www.gta5-mods.com/scripts/menyoo-pc-sp", "https://www.gta5-mods.com/scripts/menyoo-pc-sp")]
    [InlineData("https://www.gta5-mods.com/scripts/Menyoo-PC-SP/", "https://www.gta5-mods.com/scripts/menyoo-pc-sp")]
    [InlineData(
        "  www.gta5-mods.com/scripts/menyoo-pc-sp?x=1#comments ",
        "https://www.gta5-mods.com/scripts/menyoo-pc-sp"
    )]
    [InlineData(
        "http://gta5-mods.com/scripts/menyoo-pc-sp/download/12345",
        "https://www.gta5-mods.com/scripts/menyoo-pc-sp"
    )]
    [InlineData("https://www.gta5-mods.com/ru/scripts/menyoo-pc-sp", "https://www.gta5-mods.com/scripts/menyoo-pc-sp")]
    [InlineData("https://www.gta5-mods.com/uk/scripts/menyoo-pc-sp/", "https://www.gta5-mods.com/scripts/menyoo-pc-sp")]
    [InlineData(
        "https://www.gta5-mods.com/pt-br/scripts/menyoo-pc-sp/download/1",
        "https://www.gta5-mods.com/scripts/menyoo-pc-sp"
    )]
    [InlineData("https://ru.gta5-mods.com/scripts/menyoo-pc-sp", "https://www.gta5-mods.com/scripts/menyoo-pc-sp")]
    [InlineData("ua.gta5-mods.com/scripts/menyoo-pc-sp", "https://www.gta5-mods.com/scripts/menyoo-pc-sp")]
    [InlineData("https://www.gta5-mods.com/ru/users/VirtuOzZ", null)]
    [InlineData("https://www.gta5-mods.com/ru/scripts", null)]
    [InlineData("https://www.gta5-mods.com/users/VirtuOzZ", null)]
    [InlineData("https://www.gta5-mods.com/scripts", null)]
    [InlineData("https://img.gta5-mods.com/images/a", null)]
    [InlineData("https://files.gta5-mods.com/scripts/a", null)]
    [InlineData("https://www.gta5-mods.com.evil.com/scripts/a", null)]
    [InlineData("file:///C:/Windows/scripts/a", null)]
    [InlineData("", null)]
    public void Pasted_links_normalize_to_mod_pages(string url, string? expected) =>
        Assert.Equal(expected, CustomCards.Normalize(url));

    /// <summary>Every language of the site's language menu (English has no code).</summary>
    public static TheoryData<string> SiteLanguages =>
        new([
            "id",
            "ms",
            "bg",
            "ca",
            "cs",
            "da",
            "de",
            "el",
            "es",
            "fr",
            "gl",
            "ko",
            "hi",
            "it",
            "hu",
            "mk",
            "nl",
            "no",
            "pl",
            "pt",
            "ro",
            "ru",
            "sl",
            "fi",
            "sv",
            "vi",
            "tr",
            "uk",
            "zh",
        ]);

    [Theory]
    [MemberData(nameof(SiteLanguages))]
    public void Every_site_language_is_dropped_from_the_link(string code)
    {
        const string page = "https://www.gta5-mods.com/scripts/menyoo-pc-sp";
        Assert.Equal(page, CustomCards.Normalize($"https://www.gta5-mods.com/{code}/scripts/menyoo-pc-sp"));
        Assert.Equal(page, CustomCards.Normalize($"https://{code}.gta5-mods.com/scripts/menyoo-pc-sp/"));
    }

    [Fact]
    public void Cards_survive_a_restart_and_can_be_edited_and_removed()
    {
        var store = new CustomCards(FilePath);
        var saved = store.Save(
            new CustomCard
            {
                Url = "https://www.gta5-mods.com/scripts/cool-mod?utm=1",
                Title = "  Cool   Mod ",
                Author = "Author",
                CoverUrl = "https://img.gta5-mods.com/q95/images/cool-mod/bbb.png",
                Legacy = true,
                Enhanced = true,
                ShvdnNightly = true,
                ShvdnLegacyEnhanced = true,
            }
        );
        Assert.Equal("user:scripts/cool-mod", saved.Id);
        store.Save(
            new CustomCard
            {
                Collection = CustomCardCollection.Recommended,
                Url = "https://www.gta5-mods.com/scripts/other",
                Title = "Other",
                Enhanced = true,
                // Not meaningful without Legacy: dropped.
                ShvdnNightly = true,
            }
        );

        // A new instance reads the file again, as after a restart.
        var reloaded = new CustomCards(FilePath).Load();
        Assert.Equal(2, reloaded.Count);
        var user = Assert.Single(reloaded, c => c.Collection == CustomCardCollection.User);
        Assert.Equal("https://www.gta5-mods.com/scripts/cool-mod", user.Url);
        Assert.Equal("Cool Mod", user.Title);
        Assert.Equal("https://img.gta5-mods.com/q85-w800/images/cool-mod/bbb.png", user.CoverUrl);
        Assert.True(user.Legacy && user.Enhanced && user.ShvdnNightly && user.ShvdnLegacyEnhanced);
        var draft = Assert.Single(reloaded, c => c.Collection == CustomCardCollection.Recommended);
        Assert.False(draft.ShvdnNightly);
        Assert.Equal("draft:scripts/other", draft.Id);

        // Saving the same page again edits the card in place (order and date are kept).
        var edited = store.Save(user with { Title = "Renamed", Legacy = false, AddedUtc = default });
        Assert.Equal(user.Order, edited.Order);
        Assert.Equal(user.AddedUtc, edited.AddedUtc);
        var afterEdit = new CustomCards(FilePath).Load(CustomCardCollection.User);
        var one = Assert.Single(afterEdit);
        Assert.Equal("Renamed", one.Title);
        Assert.False(one.Legacy);
        Assert.False(one.ShvdnNightly);

        Assert.True(store.Remove(one.Id));
        Assert.False(store.Remove(one.Id));
        Assert.Empty(new CustomCards(FilePath).Load(CustomCardCollection.User));
        Assert.Single(new CustomCards(FilePath).Load(CustomCardCollection.Recommended));
    }

    [Fact]
    public void Broken_entries_in_the_file_are_skipped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            FilePath,
            """
            {"version":1,"cards":[
              {"url":"https://www.gta5-mods.com/scripts/good","title":"Good","coverUrl":"https://evil.example.com/x.jpg"},
              {"url":"https://evil.example.com/scripts/bad","title":"Bad"},
              {"url":"https://www.gta5-mods.com/scripts/no-title","title":"   "},
              null,
              {"url":"https://www.gta5-mods.com/scripts/good","title":"Duplicate"}
            ]}
            """
        );
        var card = Assert.Single(new CustomCards(FilePath).Load());
        Assert.Equal("Good", card.Title);
        Assert.Null(card.CoverUrl);

        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(new CustomCards(FilePath).Load());
    }

    [Fact]
    public void Card_becomes_a_catalog_entry_with_editions_and_shvdn()
    {
        var card = new CustomCard
        {
            Url = "https://www.gta5-mods.com/scripts/cool-mod",
            Title = "Cool Mod",
            Legacy = true,
            ShvdnLegacyEnhanced = true,
        };
        var entry = card.ToCatalogEntry();
        Assert.Equal(CustomCards.UserCategory, entry.Category);
        Assert.Equal(MetaSource.Gta5Mods, entry.Source);
        Assert.Equal("download", entry.Install);
        Assert.Equal((true, false), (entry.Compat!.Legacy, entry.Compat.Enhanced));
        Assert.False(entry.ShvdnNightly);
        Assert.True(entry.ShvdnLegacyEnhanced);
        Assert.Null((card with { Legacy = false }).ToCatalogEntry().Compat);
    }

    [Fact]
    public void Exported_drafts_load_back_as_built_in_catalog_entries()
    {
        var drafts = new[]
        {
            new CustomCard
            {
                Collection = CustomCardCollection.Recommended,
                Url = "https://www.gta5-mods.com/scripts/cool-mod",
                Title = "Cool «Mod»",
                Author = "Author",
                CoverUrl = "https://img.gta5-mods.com/q85-w800/images/cool-mod/bbb.png",
                Legacy = true,
                Enhanced = true,
                ShvdnNightly = true,
                ShvdnLegacyEnhanced = true,
                Order = 2,
            },
            new CustomCard
            {
                Collection = CustomCardCollection.Recommended,
                Url = "https://www.gta5-mods.com/vehicles/fast-car",
                Title = "Fast Car",
                Enhanced = true,
                Order = 1,
            },
            new CustomCard
            {
                Collection = CustomCardCollection.Recommended,
                Url = "https://www.gta5-mods.com/scripts/maybe",
                Title = "Maybe",
                EditionUnsure = true,
                ShvdnUnsure = true,
                Order = 3,
            },
        };
        // The tab order set with the arrows mixes built-in cards and drafts; cards never moved go last.
        var export = System.Text.Json.Nodes.JsonNode.Parse(
            CustomCards.ExportCatalogEntries(drafts, ["lspdfr", "draft:vehicles/fast-car", "menyoo"])
        )!;
        Assert.Equal(1, (int)export["order"]!["lspdfr"]!);
        Assert.Equal(3, (int)export["order"]!["menyoo"]!);
        var entries = export["entries"]!.ToJsonString();
        var json = $$"""
            {"version":1,"categories":[{"id":"recommended","title":"Recommended"}],"entries":{{entries}}}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var catalog = DiscoverCatalog.Parse(stream);

        Assert.Equal(["fast-car", "cool-mod", "maybe"], catalog.Entries.Select(e => e.Id));
        Assert.Equal("Cool «Mod»", catalog.Entries[1].Title);
        var car = catalog.Entries[0];
        Assert.Equal(2, car.Order);
        Assert.Equal((false, true), (car.Compat!.Legacy, car.Compat.Enhanced));
        Assert.False(car.ShvdnNightly || car.ShvdnLegacyEnhanced);
        Assert.Null(car.CoverUrl);

        var mod = catalog.Entries[1];
        Assert.Equal(4, mod.Order);
        Assert.Equal("recommended", mod.Category);
        Assert.Equal(MetaSource.Gta5Mods, mod.Source);
        Assert.Equal("https://www.gta5-mods.com/scripts/cool-mod", mod.Url);
        Assert.Equal("Author", mod.Author);
        Assert.Equal("https://img.gta5-mods.com/q85-w800/images/cool-mod/bbb.png", mod.CoverUrl);
        Assert.Equal((true, true), (mod.Compat!.Legacy, mod.Compat.Enhanced));
        Assert.True(mod.ShvdnNightly && mod.ShvdnLegacyEnhanced);
        Assert.False(mod.EditionUnsure || mod.ShvdnUnsure);

        var maybe = catalog.Entries[2];
        Assert.Equal(5, maybe.Order);
        Assert.Null(maybe.Compat);
        Assert.True(maybe.EditionUnsure && maybe.ShvdnUnsure);
    }

    [Fact]
    public void Not_sure_replaces_the_specific_choices_of_its_group()
    {
        var card = new CustomCard
        {
            Url = "https://www.gta5-mods.com/scripts/maybe",
            Title = "Maybe",
            Legacy = true,
            EditionUnsure = true,
            ShvdnNightly = true,
            ShvdnUnsure = true,
        }.Consistent();
        Assert.False(card.Legacy || card.Enhanced || card.ShvdnNightly);
        Assert.True(card.EditionUnsure && card.ShvdnUnsure);
        var entry = card.ToCatalogEntry();
        Assert.Null(entry.Compat);
        Assert.True(entry.EditionUnsure && entry.ShvdnUnsure);

        // Enhanced only: the ScriptHookVDotNet choice does not apply at all.
        var enhanced = (card with { EditionUnsure = false, Enhanced = true }).Consistent();
        Assert.False(enhanced.ShvdnUnsure || enhanced.ShvdnNightly);

        var stored = new CustomCards(FilePath).Save(card with { Legacy = true });
        Assert.False(stored.Legacy);
        var reloaded = Assert.Single(new CustomCards(FilePath).Load());
        Assert.True(reloaded.EditionUnsure && reloaded.ShvdnUnsure);
    }

    [Fact]
    public void Tab_order_survives_a_restart_and_forgets_removed_cards()
    {
        var store = new CustomCards(FilePath);
        var draft = store.Save(
            new CustomCard
            {
                Collection = CustomCardCollection.Recommended,
                Url = "https://www.gta5-mods.com/scripts/a",
                Title = "A",
                Enhanced = true,
            }
        );
        List<string> order = ["lspdfr", draft.Id, "menyoo"];
        Assert.True(CustomCards.Move(order, draft.Id, -1));
        Assert.False(CustomCards.Move(order, draft.Id, -1)); // already first
        Assert.False(CustomCards.Move(order, "missing", 1));
        Assert.Equal([draft.Id, "lspdfr", "menyoo"], order);
        store.SaveOrder(CustomCards.RecommendedCategory, order);

        // Saving a card keeps the order; a new instance reads it back as after a restart.
        store.Save(draft with { Title = "A2" });
        Assert.Equal(order, new CustomCards(FilePath).LoadOrder(CustomCards.RecommendedCategory));
        Assert.Empty(new CustomCards(FilePath).LoadOrder(CustomCards.UserCategory));

        store.Remove(draft.Id);
        Assert.Equal(["lspdfr", "menyoo"], new CustomCards(FilePath).LoadOrder(CustomCards.RecommendedCategory));
    }

    [Fact]
    public void Removed_built_in_cards_survive_a_restart_leave_the_order_and_can_be_restored()
    {
        var store = new CustomCards(FilePath);
        store.SaveOrder(CustomCards.RecommendedCategory, ["lspdfr", "menyoo"]);
        store.Hide(CustomCards.RecommendedCategory, "menyoo");
        store.Hide(CustomCards.RecommendedCategory, "menyoo"); // twice is the same

        var reloaded = new CustomCards(FilePath);
        Assert.Equal(["menyoo"], reloaded.LoadHidden(CustomCards.RecommendedCategory));
        Assert.Empty(reloaded.LoadHidden(CustomCards.UserCategory));
        Assert.Equal(["lspdfr"], reloaded.LoadOrder(CustomCards.RecommendedCategory));

        var export = System.Text.Json.Nodes.JsonNode.Parse(
            CustomCards.ExportCatalogEntries([], ["lspdfr"], reloaded.LoadHidden(CustomCards.RecommendedCategory))
        )!;
        Assert.Equal("menyoo", export["hidden"]![0]!.GetValue<string>());

        Assert.Equal(1, reloaded.RestoreHidden(CustomCards.RecommendedCategory));
        Assert.Empty(new CustomCards(FilePath).LoadHidden(CustomCards.RecommendedCategory));
        Assert.Equal(0, reloaded.RestoreHidden(CustomCards.RecommendedCategory));
    }

    [Fact]
    public void Catalog_rejects_a_cover_from_another_host()
    {
        const string json = """
            {"version":1,"categories":[{"id":"recommended","title":"R"}],"entries":[
              {"id":"a","category":"recommended","source":"gta5mods","url":"https://www.gta5-mods.com/scripts/a","title":"A",
               "coverUrl":"https://evil.example.com/x.jpg","shvdn":["NIGHTLY","bogus"]}]}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var entry = Assert.Single(DiscoverCatalog.Parse(stream).Entries);
        Assert.Null(entry.CoverUrl);
        Assert.True(entry.ShvdnNightly);
        Assert.False(entry.ShvdnLegacyEnhanced);
    }
}

public sealed class CustomCardsProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gmm-custom-meta-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0];

    private const string Html = """
        <meta property="og:title" content="Cool Mod">
        <meta property="og:image" content="https://img.gta5-mods.com/q75/images/cool/cover.jpg">
        <a class="thumbnail mfp-image cover-media" href="https://img.gta5-mods.com/q95/images/cool/cover.jpg"></a>
        <a class="thumbnail mfp-image" href="https://img.gta5-mods.com/q95/images/cool/shot.jpg"></a>
        <script type="application/ld+json">{"@type":"CreativeWork","name":"Cool Mod","author":"A"}</script>
        """;

    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            lock (Requests)
                Requests.Add(r.RequestUri!.AbsoluteUri);
            HttpContent content = r.RequestUri!.Host.StartsWith("img.")
                ? new ByteArrayContent(Png)
                : new StringContent(Html);
            return Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content, RequestMessage = r }
            );
        }
    }

    [Fact]
    public async Task Picked_cover_is_the_only_image_kept_for_the_card()
    {
        var handler = new Handler();
        using var p = new ModMetadataProvider(_dir, handler, TimeSpan.Zero);
        var page = await p.LoadPageAsync("https://www.gta5-mods.com/scripts/cool-mod", CancellationToken.None);
        Assert.Equal("Cool Mod", page.Data.Title);
        Assert.Equal(2, page.Images.Count);
        Assert.NotNull(await p.DownloadPreviewAsync(page.Images[1], CancellationToken.None));
        Assert.Null(await p.DownloadPreviewAsync("https://evil.example.com/x.png", CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_dir, "img"))); // previews stay in memory

        var card = new CustomCard
        {
            Url = "https://www.gta5-mods.com/scripts/cool-mod",
            Title = "Cool Mod",
            CoverUrl = page.Images[1],
            Enhanced = true,
        };
        var entry = card.ToCatalogEntry();
        var cached = await p.RememberAsync(entry, page.Data, CancellationToken.None);
        Assert.NotNull(p.ImagePath(cached));
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "img")));
        Assert.False(p.NeedsRefresh(entry, ModMetadataProvider.DefaultMaxAge));

        // Another cover picked in the editor: the card is refreshed with that image, the old file goes away.
        var other = (card with { CoverUrl = page.Images[0] }).ToCatalogEntry();
        Assert.True(p.NeedsRefresh(other, ModMetadataProvider.DefaultMaxAge));
        handler.Requests.Clear();
        await p.RefreshAsync([other], force: false, null, CancellationToken.None);
        Assert.Contains("https://img.gta5-mods.com/q85-w800/images/cool/cover.jpg", handler.Requests);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "img")));
        Assert.False(p.NeedsRefresh(other, ModMetadataProvider.DefaultMaxAge));

        p.Forget(other.Id);
        Assert.Null(p.Get(other.Id));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "img")));
    }
}
