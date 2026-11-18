using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Inventory;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class ModPagesTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("gmm-modpages-").FullName;

    public void Dispose() => Directory.Delete(root, true);

    const string AuthorPanel = """
        <div class="panel-body">
          <div class="user-panel row">
            <div class="col-xs-3">
              <a href="/users/Author">
                <img class="img-responsive" src="https://img.gta5-mods.com/q75-w100-h100-cfill/avatars/1/a.jpg" />
              </a>
            </div>
            <div class="col-xs-9">
              <a class="username" href="/users/Author">Author</a>
            </div>
          </div>
        </div>
        """;

    [Fact]
    public void Author_avatar_and_profile_come_from_the_uploader_panel()
    {
        string html =
            """<meta property="og:title" content="Some Mod" />"""
            + """<script type="application/ld+json">{"@type":"CreativeWork","name":"Some Mod","author":"Author"}</script>"""
            + AuthorPanel;
        var m = Gta5ModsPageParser.Parse(html)!;
        Assert.Equal("https://img.gta5-mods.com/q75-w100-h100-cfill/avatars/1/a.jpg", m.AuthorAvatarUrl);
        Assert.Equal("https://www.gta5-mods.com/users/Author", m.AuthorProfileUrl);
    }

    [Theory]
    [InlineData("https://evil.example/a.jpg")]
    [InlineData("http://img.gta5-mods.com/a.jpg")]
    [InlineData("javascript:alert(1)")]
    public void Avatar_from_another_host_or_scheme_is_ignored(string src)
    {
        string html = AuthorPanel.Replace("https://img.gta5-mods.com/q75-w100-h100-cfill/avatars/1/a.jpg", src);
        Assert.Null(Gta5ModsPageParser.ReadAvatar(html));
    }

    [Theory]
    [InlineData("N.O.S - Nitro Overdrive System", "N.O.S. - Nitro Overdrive System [Legacy + Enhanced]", true)]
    [InlineData("Lara V 1.5a", "Lara V", true)]
    [InlineData("T&P Recorder", "T&amp;P Recorder (Enhanced)", true)]
    [InlineData("Simple Trainer", "Simple Trainer for GTA V", false)]
    [InlineData("AB", "AB", false)]
    public void Titles_match_only_when_identical_without_decoration(string name, string title, bool same) =>
        Assert.Equal(same, ModTitles.Same(name, title));

    const string SearchPage = """
        <div class="file-list-obj">
          <a href="/scripts/n-o-s-nitro-overdrive-system" title="N.O.S. - Nitro Overdrive System [Legacy + Enhanced]" class="preview empty">
        </div>
        <div class="file-list-obj">
          <a href="/scripts/nos-addon" title="NOS Addon Pack" class="preview">
        </div>
        <div class="file-list-obj">
          <a href="/users/someone" title="N.O.S - Nitro Overdrive System" class="preview">
        </div>
        """;

    [Fact]
    public void Search_results_pick_the_single_exact_title_and_ignore_non_file_links()
    {
        var hits = Gta5ModsSearchParser.Parse(SearchPage);
        Assert.Equal(2, hits.Count);
        Assert.Equal(
            "https://www.gta5-mods.com/scripts/n-o-s-nitro-overdrive-system",
            Gta5ModsSearchParser.Pick(hits, "N.O.S - Nitro Overdrive System")
        );
        Assert.Null(Gta5ModsSearchParser.Pick(hits, "Nitro"));
    }

    [Fact]
    public void Two_different_pages_with_the_same_title_are_ambiguous()
    {
        var hits = new[]
        {
            new ModSearchHit("https://www.gta5-mods.com/scripts/a", "Car Pack"),
            new ModSearchHit("https://www.gta5-mods.com/vehicles/b", "Car Pack [Add-On]"),
        };
        Assert.Null(Gta5ModsSearchParser.Pick(hits, "Car Pack"));
    }

    [Fact]
    public void Not_found_is_remembered_for_a_week_and_found_pages_forever()
    {
        var pages = new ModPages(Path.Combine(root, "pages.json"));
        var now = DateTimeOffset.UtcNow;
        Assert.True(pages.NeedsSearch("A", now));
        pages.Set("A", null, now);
        pages.Set("B", "https://www.gta5-mods.com/scripts/b", now);
        Assert.False(pages.NeedsSearch("A", now.AddDays(6)));
        Assert.True(pages.NeedsSearch("A", now.AddDays(8)));
        Assert.False(pages.NeedsSearch("B", now.AddYears(1)));
        Assert.Equal(
            "https://www.gta5-mods.com/scripts/b",
            new ModPages(Path.Combine(root, "pages.json")).Get("b")?.Url
        );
    }

    [Fact]
    public void Recommendations_install_is_linked_by_its_files_not_by_title()
    {
        var installed = new[]
        {
            new InstalledCatalogMod
            {
                CatalogId = "menyoo",
                GameRoot = "G",
                Files = [new(@"Menyoo.asi", 1, 1)],
            },
            new InstalledCatalogMod
            {
                CatalogId = "trainer",
                GameRoot = "G",
                Files = [new(@"TrainerV.asi", 1, 1)],
            },
        };
        Assert.Equal("menyoo", InstalledMods.CatalogIdFor(installed, [@"menyooStuff\a.xml", "MENYOO.asi"]));
        Assert.Null(InstalledMods.CatalogIdFor(installed, ["Other.asi"]));
        Assert.Null(InstalledMods.CatalogIdFor(installed, ["Menyoo.asi", "TrainerV.asi"]));
    }

    GameInstall Game()
    {
        var path = Path.Combine(root, "game");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "GTA5.exe"), "stub");
        var g = GameInstall.Open(path);
        g.RunningCheckOverride = () => false;
        return g;
    }

    void Install(GameInstall game, string title, string rel)
    {
        var source = Path.Combine(root, Guid.NewGuid() + ".bin");
        File.WriteAllText(source, "payload");
        var r = game.CreateEngine()
            .Execute([new(rel, FileTransactionEngine.HashOrNull(Path.Combine(game.Root, rel)), source)], title);
        Assert.Equal(TxState.Completed, r.State);
    }

    [Fact]
    public void Mod_hub_components_are_builtin_without_links()
    {
        var g = Game();
        var labels = new ModLabels(Path.Combine(root, "labels.json"));
        labels.SetUrl(InstalledModList.PedSelectorName, "https://www.gta5-mods.com/scripts/x");
        Install(g, "Установка GMM PED Selector", "GMMPedSelector.asi");
        Install(g, "База для моддинга: ScriptHookV", "ScriptHookV.dll");
        Install(g, "T&P Recorder 1.1", @"scripts\T&P Recorder.dll");
        Install(g, "Подключение пака Ped Selector", "GMMPedSelector.models.txt");
        var groups = InstalledModList.Read(g, labels).Groups.ToDictionary(x => x.Name);
        Assert.Equal(3, groups.Count);
        Assert.Equal(2, groups[InstalledModList.PedSelectorName].Files.Count);
        Assert.True(groups[InstalledModList.PedSelectorName].Builtin);
        Assert.Null(groups[InstalledModList.PedSelectorName].Url);
        Assert.True(groups[InstalledModList.BaseName].Builtin);
        Assert.Equal("ScriptHookV.dll", Assert.Single(groups[InstalledModList.BaseName].Files).Rel);
        Assert.False(groups["Traffic and Player Recorder"].Builtin);
    }
}

public sealed class CatalogMarkerTests
{
    [Fact]
    public void Mod_installed_from_any_archive_is_linked_by_the_catalog_marker_file()
    {
        var catalog = new[]
        {
            new Discover.CatalogEntry
            {
                Id = "traffic-player-recorder",
                Category = "virtuozz",
                Url = "https://www.gta5-mods.com/scripts/traffic-player-recorder-net-machinima-tool-beta",
                Title = "Traffic & Player Recorder.NET",
                SourceName = "gta5mods",
                DetectRules = @"scripts\T&P Recorder.dll",
            },
        };
        Assert.Equal(
            "https://www.gta5-mods.com/scripts/traffic-player-recorder-net-machinima-tool-beta",
            Discover.ModPages.FromCatalogFiles(catalog, [@"scripts\t&p recorder.dll", "T&P Recorder.VehicleLimit.asi"])
        );
        Assert.Null(Discover.ModPages.FromCatalogFiles(catalog, ["Other.asi"]));
    }
}
