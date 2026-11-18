using System.IO.Compression;
using System.Net;
using System.Text.Json;
using GTAModManager.Core.Base;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Library;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class ScriptLibraryTests : IDisposable
{
    readonly string directory = Directory.CreateTempSubdirectory("gmm-script-libraries-").FullName;
    readonly List<GameInstall> games = [];

    public void Dispose()
    {
        foreach (var game in games)
        foreach (
            var journal in game.CreateEngine()
                .History()
                .Where(j => j.TargetRoot.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
        )
            File.Delete(Path.Combine(game.WorkDir, "journals", journal.Id + ".json"));
        Directory.Delete(directory, true);
    }

    GameInstall Game(GameEdition edition = GameEdition.Legacy)
    {
        var root = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, edition == GameEdition.Legacy ? "GTA5.exe" : "GTA5_Enhanced.exe"), "test");
        var game = GameInstall.Open(root);
        games.Add(game);
        return game;
    }

    [Fact]
    public void Essentials_contains_three_libraries_with_explicit_compatibility()
    {
        var catalog = DiscoverCatalog.LoadBuiltIn();
        foreach (var id in new[] { "lemonui", "nativeui", "ifruitaddon2" })
        {
            var entry = Assert.Single(catalog.Entries, e => e.Id == id);
            Assert.Equal("essentials", entry.Category);
            Assert.Equal("script-library", entry.Install);
            Assert.True(entry.Compat!.Legacy);
            Assert.Equal(id == "ifruitaddon2", entry.Compat.Enhanced);
        }
    }

    [Fact]
    public void LemonUI_selects_only_SHVDN3_without_examples_or_other_runtimes()
    {
        string[] files =
        [
            @"SHVDN2\LemonUI.SHVDN2.dll",
            @"SHVDN3\LemonUI.SHVDN3.dll",
            @"FiveM\LemonUI.FiveM.dll",
            @"SHVDNC\LemonUI.SHVDNC.dll",
            "Example.dll",
        ];
        Assert.Equal(files[1], ScriptLibraries.SelectFile("lemonui", files));
        Assert.Equal(3, ScriptLibraries.Get("lemonui").Api);
        Assert.Equal(2, ScriptLibraries.Get("nativeui").Api);
        Assert.Throws<InvalidDataException>(() => ScriptLibraries.SelectFile("lemonui", [files[0]]));
        Assert.Throws<InvalidDataException>(() => ScriptLibraries.SelectFile("lemonui", [files[1], files[1]]));
    }

    [Theory]
    [InlineData("lemonui")]
    [InlineData("nativeui")]
    public void Enhanced_cannot_install_unconfirmed_libraries_even_with_custom_mappings(string id)
    {
        var request = new InstallationRequest
        {
            Source = "missing.zip",
            ScriptLibraryId = id,
            Mappings = new() { ["other.dll"] = "scripts\\other.dll" },
            ConfirmOverwrite = true,
        };
        Assert.Throws<InvalidOperationException>(() =>
            InstallationService.Prepare(Game(GameEdition.Enhanced), request)
        );
    }

    [Fact]
    public void ASI_alone_does_not_satisfy_a_managed_library_dependency()
    {
        var game = Game();
        File.WriteAllText(Path.Combine(game.Root, "ScriptHookVDotNet.asi"), "MZ stub");
        Assert.True(ModdingBase.Inspect(game).Shvdn);
        Assert.False(ScriptLibraries.Dependencies(game, "lemonui").Single(d => d.Id == "shvdn").Present);
        Assert.Throws<InvalidOperationException>(() => ScriptLibraries.Prepare(game, "lemonui", "missing.zip", false));
    }

    [Fact]
    public void A_DLL_with_the_right_name_but_wrong_payload_is_rejected()
    {
        string zip = Path.Combine(directory, "fake.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("NativeUI.dll").Open();
            stream.Write("MZ not an assembly"u8);
        }
        Assert.Throws<InvalidDataException>(() => ScriptLibraries.Inspect(Game(), "nativeui", zip));
    }

    [Fact]
    public void Queue_preserves_library_identity_for_resume()
    {
        var job = new InstallQueueRecord
        {
            GameRoot = Game().Root,
            Source = "LemonUI.zip",
            ScriptLibraryId = "lemonui",
        };
        var store = new InstallQueueStore(job.GameRoot, Path.Combine(directory, "queue"));
        store.Save(job);
        Assert.Equal("lemonui", store.Load().Single().ScriptLibraryId);
    }

    sealed class Fake(string json) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content =
                        request.RequestUri.Host == "api.github.com"
                            ? new StringContent(json)
                            : new ByteArrayContent([80, 75, 3, 4, 0, 0, 0, 0]),
                }
            );
        }
    }

    [Theory]
    [InlineData("lemonui")]
    [InlineData("nativeui")]
    [InlineData("ifruitaddon2")]
    public async Task Downloads_only_the_reviewed_official_release_asset(string id)
    {
        var c = ScriptLibraries.Get(id);
        string url = $"https://github.com/{c.Repository}/releases/download/{c.Tag}/{c.Asset}";
        var json = JsonSerializer.Serialize(
            new
            {
                tag_name = c.Tag,
                draft = false,
                prerelease = false,
                assets = new[] { new { name = c.Asset, browser_download_url = url } },
            }
        );
        var fake = new Fake(json);
        using var http = BaseDownloads.CreateClient(fake);
        string file = await BaseDownloads.DownloadScriptLibraryAsync(http, id, directory, CancellationToken.None);
        Assert.Equal(c.Asset, Path.GetFileName(file));
        Assert.Equal(url, fake.Requests[1]);
        Assert.EndsWith("/releases/tags/" + c.Tag, fake.Requests[0]);
    }

    [Fact]
    public async Task A_wrong_tag_or_redirect_to_an_unrelated_asset_is_rejected()
    {
        foreach (
            var (tag, url) in new[]
            {
                ("v3.0.1", "https://github.com/Bob74/iFruitAddon2/releases/download/v3.1.1/iFruitAddon2-3.1.1.zip"),
                ("v3.1.1", "https://example.org/iFruitAddon2-3.1.1.zip"),
            }
        )
        {
            var json = JsonSerializer.Serialize(
                new
                {
                    tag_name = tag,
                    draft = false,
                    prerelease = false,
                    assets = new[] { new { name = "iFruitAddon2-3.1.1.zip", browser_download_url = url } },
                }
            );
            var fake = new Fake(json);
            using var http = BaseDownloads.CreateClient(fake);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BaseDownloads.DownloadScriptLibraryAsync(http, "ifruitaddon2", directory, CancellationToken.None)
            );
            Assert.Single(fake.Requests);
        }
    }

    // Optional official binaries are inspected/copied, never loaded or executed.
    [Theory]
    [InlineData("lemonui", GameEdition.Legacy)]
    [InlineData("nativeui", GameEdition.Legacy)]
    [InlineData("ifruitaddon2", GameEdition.Legacy)]
    [InlineData("ifruitaddon2", GameEdition.Enhanced)]
    public void Official_archives_install_only_one_DLL_with_backup_and_undo(string id, GameEdition edition)
    {
        string? fixtures = Environment.GetEnvironmentVariable("GMM_LIBRARY_FIXTURES");
        if (fixtures is null)
            return;
        var game = Game(edition);
        var component = ScriptLibraries.Get(id);
        string shvdn = Directory
            .GetFiles(
                fixtures,
                edition == GameEdition.Legacy ? "ScriptHookVDotNet-v*.zip" : "ScriptHookVDotNetEnhanced-*.zip"
            )
            .Single();
        using (var zip = ZipFile.OpenRead(shvdn))
            foreach (
                var entry in zip.Entries.Where(e =>
                    e.Name == e.FullName
                    && (e.Name.EndsWith(".asi") || e.Name is "ScriptHookVDotNet2.dll" or "ScriptHookVDotNet3.dll")
                )
            )
                entry.ExtractToFile(Path.Combine(game.Root, entry.Name));
        foreach (var name in new[] { "dinput8.dll", "ScriptHookV.dll" })
            File.WriteAllText(Path.Combine(game.Root, name), "MZ test loader");
        Assert.DoesNotContain(ScriptLibraries.Dependencies(game, id), d => d.Required && !d.Present);
        var apiHash = FileTransactionEngine.Hash(Path.Combine(game.Root, $"ScriptHookVDotNet{component.Api}.dll"));
        string source = Path.Combine(fixtures, component.Asset);
        var inspection = ScriptLibraries.Inspect(game, id, source);
        Assert.Equal([component.Target], inspection.Targets);
        Directory.CreateDirectory(Path.Combine(game.Root, "scripts"));
        string target = Path.Combine(game.Root, component.Target);
        File.WriteAllText(target, "previous library");
        var request = new InstallationRequest
        {
            Source = source,
            SourceHash = FileTransactionEngine.Hash(source),
            ScriptLibraryId = id,
            ConfirmOverwrite = true,
        };
        using (var plan = InstallationService.Prepare(game, request))
        {
            Assert.True(plan.CanInstall, string.Join(";", plan.Errors));
            Assert.Equal(component.Target, Assert.Single(plan.Changes).RelativePath);
        }
        var library = BackupLibrary.Create(Path.Combine(directory, "backups"), 100L << 20);
        var result = InstallationService.Execute(game, request, library);
        Assert.Equal(TxState.Completed, result.State);
        Assert.Equal(
            apiHash,
            FileTransactionEngine.Hash(Path.Combine(game.Root, $"ScriptHookVDotNet{component.Api}.dll"))
        );
        Assert.Equal(TxState.Undone, new OivInstaller(game).Undo(result.Id, library).State);
        Assert.Equal("previous library", File.ReadAllText(target));
        Assert.True(File.Exists(source)); // external archives are never deleted by the pipeline
    }
}
