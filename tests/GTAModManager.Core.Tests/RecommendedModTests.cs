using System.IO.Compression;
using System.Text.Json;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Library;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class RecommendedModTests : IDisposable
{
    readonly string directory = Directory.CreateTempSubdirectory("gmm-recommendations-").FullName;
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
        string root = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, edition == GameEdition.Legacy ? "GTA5.exe" : "GTA5_Enhanced.exe"), "test");
        var game = GameInstall.Open(root);
        games.Add(game);
        return game;
    }

    string? Fixtures => Environment.GetEnvironmentVariable("GMM_RECOMMENDED_FIXTURES");

    string Source(string id) => Directory.GetFiles(Fixtures!, id + "-*").Single(p => !p.EndsWith(".html"));

    string Libraries => Environment.GetEnvironmentVariable("GMM_LIBRARY_FIXTURES")!;

    void Put(GameInstall game, string relative, byte[] bytes)
    {
        string path = Path.Combine(game.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    void Base(GameInstall game)
    {
        string source = Directory
            .GetFiles(
                Libraries,
                game.Edition == GameEdition.Legacy ? "ScriptHookVDotNet-v*.zip" : "ScriptHookVDotNetEnhanced-*.zip"
            )
            .Single();
        using var zip = ZipFile.OpenRead(source);
        foreach (
            var entry in zip.Entries.Where(e =>
                e.FullName == e.Name
                && (e.Name.EndsWith(".asi") || e.Name is "ScriptHookVDotNet2.dll" or "ScriptHookVDotNet3.dll")
            )
        )
            entry.ExtractToFile(Path.Combine(game.Root, entry.Name));
        foreach (string name in new[] { "ScriptHookV.dll", "dinput8.dll" })
            Put(game, name, "MZ test"u8.ToArray());
    }

    void Library(GameInstall game, string id)
    {
        var c = ScriptLibraries.Get(id);
        using var archive = ModArchive.Open(Path.Combine(Libraries, c.Asset));
        Put(game, c.Target, archive.Read(c.Entry));
    }

    [Fact]
    public void Six_new_recommendations_are_distinct_and_the_save_is_page_only()
    {
        var catalog = DiscoverCatalog.LoadBuiltIn();
        foreach (
            var id in new[]
            {
                "map-editor",
                "andres-map-editor",
                "enable-all-interiors",
                "trafficx-custom",
                "100-save-game",
                "ghost-replay",
            }
        )
            Assert.Equal("recommended", Assert.Single(catalog.Entries, e => e.Id == id).Category);
        Assert.Equal("none", catalog.Entries.Single(e => e.Id == "100-save-game").Install);
        Assert.Equal(
            "https://www.gta5-mods.com/misc/100-save-game",
            catalog.Entries.Single(e => e.Id == "100-save-game").Url
        );
        foreach (var id in new[] { "lemonui", "nativeui", "ifruitaddon2" })
            Assert.Single(catalog.Entries, e => e.Id == id);
    }

    [Fact]
    public void Save_cannot_enter_the_game_root_pipeline()
    {
        var request = new InstallationRequest
        {
            Source = "save.rar",
            RecommendedId = "100-save-game",
            Mappings = new() { ["SGTA50015"] = "SGTA50015" },
            ConfirmOverwrite = true,
        };
        Assert.Throws<InvalidOperationException>(() => InstallationService.Prepare(Game(), request, true));
    }

    [Fact]
    public void Unknown_or_changed_archives_are_rejected_before_installation()
    {
        string source = Path.Combine(directory, "changed.zip");
        File.WriteAllText(source, "not the reviewed release");
        foreach (var id in RecommendedMods.Catalog.Where(c => c.Hash.Length > 0).Select(c => c.Id))
            Assert.Throws<InvalidDataException>(() => RecommendedMods.ValidateArchive(id, source));
    }

    [Fact]
    public void Dependency_plan_refuses_unhandled_or_wrong_edition_components_without_installing_anything()
    {
        var game = Game(GameEdition.Enhanced);
        var needs = new[]
        {
            new DependencyNeed("lemonui", "LemonUI", true, false, "", "https://github.com/LemonUIbyLemon/LemonUI"),
        };
        Assert.Throws<InvalidOperationException>(() =>
            RecommendedMods.CheckAutomaticDependencies(game, "trafficx-custom", needs)
        );
        Assert.Throws<InvalidOperationException>(() =>
            RecommendedMods.CheckAutomaticDependencies(
                game,
                "ghost-replay",
                [new("vc2015-x64", "VC++", true, false, "", "https://microsoft.com")]
            )
        );
        RecommendedMods.CheckAutomaticDependencies(
            game,
            "trafficx-custom",
            needs.Select(d => d with { Present = true })
        );
        Assert.Empty(Directory.GetFiles(game.Root, "*.dll"));
    }

    [Fact]
    public void Known_missing_base_and_bundled_dependencies_are_accepted_without_optional_mods()
    {
        var game = Game();
        var needs = new[] { "shv", "asi", "shvdn", "lemonui", "ifruitaddon2-eai" }.Select(id => new DependencyNeed(
            id,
            id,
            true,
            false,
            "",
            "https://example.com"
        ));
        RecommendedMods.CheckAutomaticDependencies(game, "enable-all-interiors", needs);
        Assert.True(RecommendedMods.ProvidesBundledDependency("enable-all-interiors", "ifruitaddon2-eai"));
        Assert.False(RecommendedMods.ProvidesBundledDependency("trafficx-custom", "ifruitaddon2-eai"));
    }

    [Fact]
    public void Queue_and_request_preserve_the_reviewed_profile_after_restart()
    {
        var game = Game();
        var record = new InstallQueueRecord
        {
            GameRoot = game.Root,
            Source = "TrafficX.zip",
            RecommendedId = "trafficx-custom",
        };
        var store = new InstallQueueStore(game.Root, Path.Combine(directory, "queue"));
        store.Save(record);
        Assert.Equal(record.RecommendedId, store.Load().Single().RecommendedId);
        var request = JsonSerializer.Deserialize<InstallationRequest>(
            JsonSerializer.Serialize(new InstallationRequest { RecommendedId = record.RecommendedId })
        )!;
        Assert.Equal(record.RecommendedId, request.RecommendedId);
    }

    [Theory]
    [InlineData("map-editor", GameEdition.Legacy)]
    [InlineData("andres-map-editor", GameEdition.Legacy)]
    [InlineData("enable-all-interiors", GameEdition.Legacy)]
    [InlineData("enable-all-interiors", GameEdition.Enhanced)]
    [InlineData("trafficx-custom", GameEdition.Legacy)]
    [InlineData("trafficx-custom", GameEdition.Enhanced)]
    [InlineData("ghost-replay", GameEdition.Legacy)]
    public void Official_archive_has_correct_dependencies_paths_backup_and_undo(string id, GameEdition edition)
    {
        if (Fixtures is null)
            return;
        var game = Game(edition);
        Base(game);
        foreach (var library in RecommendedMods.Get(id).Libraries)
            Library(game, library);
        string source = Source(id);
        var request = new InstallationRequest
        {
            Source = source,
            RecommendedId = id,
            SourceHash = FileTransactionEngine.Hash(source),
            ConfirmOverwrite = true,
        };
        var inspection = InstallationService.Inspect(game, source, recommendedId: id);
        Assert.DoesNotContain(inspection.Dependencies, d =>
            d.Id == "AddonToRoad" || d.Id == "heap" || d.Id == "gameconfig"
        );
        if (id == "ghost-replay")
            Assert.DoesNotContain(inspection.Dependencies, d => d.Id is "shvdn" or "lemonui" or "nativeui");
        if (id == "enable-all-interiors")
            Assert.Contains(inspection.Dependencies, d => d.Id == "ifruitaddon2-eai" && !d.Present);
        if (id == "trafficx-custom")
            Assert.Contains(inspection.Dependencies, d =>
                d.Id == "shvdn"
                && d.Present
                && d.Title.Contains(edition == GameEdition.Enhanced ? "3.9.0.0" : "3.7.0.188")
            );
        using (var preview = InstallationService.Prepare(game, request, true))
        {
            Assert.Empty(preview.Errors);
            Assert.True(preview.Preview);
            Assert.NotEmpty(preview.Changes);
            Assert.DoesNotContain(preview.Changes, c =>
                c.RelativePath.EndsWith(".tmp") || c.RelativePath.EndsWith(".pdb")
            );
            Assert.DoesNotContain(preview.Changes, c =>
                c.RelativePath.EndsWith("LemonUI.SHVDN3.dll") || c.RelativePath.EndsWith("NativeUI.dll")
            );
            if (id == "trafficx-custom")
                Assert.Equal(@"scripts\TrafficX Custom.dll", Assert.Single(preview.Changes).RelativePath);
            if (id == "ghost-replay")
                Assert.Contains(preview.Changes, c => c.RelativePath == @"GhostReplay\settings_menu.ini");
        }
        var unavailable = inspection
            .Dependencies.Where(d => d.Required && !d.Present && !RecommendedMods.ProvidesBundledDependency(id, d.Id))
            .ToArray();
        if (unavailable.Length > 0)
        {
            Assert.All(unavailable, d => Assert.StartsWith("vc", d.Id));
            Assert.Throws<InvalidOperationException>(() => InstallationService.Prepare(game, request));
            return;
        }
        string primary =
            id == "ghost-replay" ? "GhostReplay.asi"
            : id == "trafficx-custom" ? @"scripts\TrafficX Custom.dll"
            : id == "enable-all-interiors" ? @"scripts\EnableAllinteriors.dll"
            : @"scripts\MapEditor.dll";
        // Do not fabricate a map-editor family: conflict checks inspect the actual assembly references.
        if (id is not "map-editor" and not "andres-map-editor")
            Put(game, primary, "previous version"u8.ToArray());
        var before = File.Exists(Path.Combine(game.Root, primary))
            ? File.ReadAllBytes(Path.Combine(game.Root, primary))
            : null;
        var libraryStore = BackupLibrary.Create(Path.Combine(directory, "backups"), 100L << 20);
        var installed = InstallationService.Execute(game, request, libraryStore);
        Assert.Equal(TxState.Completed, installed.State);
        using (var archive = ModArchive.Open(source))
        {
            string input = id switch
            {
                "map-editor" => "MapEditor.dll",
                "andres-map-editor" => @"scripts\MapEditor.dll",
                "trafficx-custom" => (
                    edition == GameEdition.Legacy ? @"legacy\TrafficX Custom.dll" : @"Enhanced\TrafficX Custom.dll"
                ),
                "enable-all-interiors" => @"Enable All Interiors Mod FIles\scripts\EnableAllinteriors.dll",
                _ => "GhostReplay.asi",
            };
            Assert.Equal(archive.Read(input), File.ReadAllBytes(Path.Combine(game.Root, primary)));
        }
        Assert.Equal(TxState.Undone, new OivInstaller(game).Undo(installed.Id, libraryStore).State);
        if (before is null)
            Assert.False(File.Exists(Path.Combine(game.Root, primary)));
        else
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(game.Root, primary)));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Map_editors_cannot_overwrite_each_other_or_share_changed_configuration_silently()
    {
        if (Fixtures is null)
            return;
        foreach (
            var (installed, incoming) in new[]
            {
                ("map-editor", "andres-map-editor"),
                ("andres-map-editor", "map-editor"),
            }
        )
        {
            var game = Game();
            using var archive = ModArchive.Open(Source(installed));
            Put(
                game,
                @"scripts\MapEditor.dll",
                archive.Read(installed == "map-editor" ? "MapEditor.dll" : @"scripts\MapEditor.dll")
            );
            Put(game, @"scripts\MapEditor\settings.xml", "user settings"u8.ToArray());
            Assert.Throws<InvalidOperationException>(() =>
                InstallationService.Inspect(game, Source(incoming), recommendedId: incoming)
            );
            Assert.Equal("user settings", File.ReadAllText(Path.Combine(game.Root, @"scripts\MapEditor\settings.xml")));
        }
    }

    [Fact]
    public void EAI_preserves_existing_custom_runtime_and_rejects_Bob74_3_1_1()
    {
        if (Fixtures is null)
            return;
        var game = Game();
        using var archive = ModArchive.Open(Source("enable-all-interiors"));
        Put(
            game,
            @"scripts\iFruitAddon2.dll",
            archive.Read(@"Enable All Interiors Mod FIles\scripts\iFruitAddon2.dll")
        );
        var inspection = InstallationService.Inspect(
            game,
            Source("enable-all-interiors"),
            recommendedId: "enable-all-interiors"
        );
        Assert.Contains(inspection.Dependencies, d => d.Id == "ifruitaddon2-eai" && d.Present);
        using (
            var preview = InstallationService.Prepare(
                game,
                new() { Source = Source("enable-all-interiors"), RecommendedId = "enable-all-interiors" },
                true
            )
        )
            Assert.DoesNotContain(preview.Changes, c => c.RelativePath == @"scripts\iFruitAddon2.dll");
        Library(game, "ifruitaddon2");
        string hash = FileTransactionEngine.Hash(Path.Combine(game.Root, @"scripts\iFruitAddon2.dll"));
        Assert.Throws<InvalidOperationException>(() =>
            InstallationService.Inspect(game, Source("enable-all-interiors"), recommendedId: "enable-all-interiors")
        );
        Assert.Equal(hash, FileTransactionEngine.Hash(Path.Combine(game.Root, @"scripts\iFruitAddon2.dll")));
    }

    [Fact]
    public void Save_archive_contains_profile_slots_not_script_files()
    {
        if (Fixtures is null)
            return;
        using var archive = ModArchive.Open(Source("100-save-game"));
        Assert.Contains(archive.Files, f => Path.GetFileName(f) == "SGTA50015");
        Assert.Contains(archive.Files, f => Path.GetFileName(f) == "SGTA50015.bak");
    }
}
