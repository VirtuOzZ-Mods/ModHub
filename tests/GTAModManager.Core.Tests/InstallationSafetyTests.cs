using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;

namespace GTAModManager.Core.Tests;

public sealed class InstallationSafetyTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "gmm-install-safe-" + Guid.NewGuid().ToString("N"));

    public InstallationSafetyTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    GameInstall Game(string edition = "Legacy")
    {
        string p = Path.Combine(root, edition);
        Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, edition == "Legacy" ? "GTA5.exe" : "GTA5_Enhanced.exe"), "stub");
        var g = GameInstall.Open(p);
        g.RunningCheckOverride = () => false;
        return g;
    }

    const string Dlc = "<SMandatoryPacksData><Paths><Item>dlcpacks:/existing/</Item></Paths></SMandatoryPacksData>";

    [Fact]
    public void Dlc_merge_preserves_existing_and_deduplicates_incoming()
    {
        string incoming =
            "<SMandatoryPacksData><Paths><Item>dlcpacks:/newpack/</Item><Item>dlcpacks:/NEWPACK/</Item></Paths></SMandatoryPacksData>";
        var diff = InstallDocuments.CompareDlc(Dlc, incoming);
        Assert.Single(diff.Added);
        Assert.Single(diff.Removed);
        Assert.Single(diff.Duplicates);
        var merged = InstallDocuments.MergeDlc(Dlc, incoming);
        Assert.Contains("existing", merged);
        Assert.Single(InstallDocuments.Parse(merged).Descendants("Item"), e =>
            e.Value.Contains("newpack", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void Content_detection_rejects_Dtd_and_unsafe_Dlc_paths()
    {
        Assert.Equal(XmlDocumentKind.DlcList, InstallDocuments.Kind(Dlc));
        Assert.Throws<System.Xml.XmlException>(() =>
            InstallDocuments.Parse("<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///C:/secret'>]><a>&x;</a>")
        );
        Assert.ThrowsAny<Exception>(() =>
            InstallDocuments.Kind(
                "<SMandatoryPacksData><Paths><Item>dlcpacks:/../../x/</Item></Paths></SMandatoryPacksData>"
            )
        );
    }

    [Fact]
    public void Stock_platform_entries_are_valid_and_preserved()
    {
        string stock =
            "<SMandatoryPacksData><Paths><Item>platform:/dlcPacks/mpBeach/</Item><Item>dlcpacks:/existing/</Item></Paths></SMandatoryPacksData>";
        Assert.Equal(XmlDocumentKind.DlcList, InstallDocuments.Kind(stock));
        string merged = InstallDocuments.MergeDlc(
            stock,
            "<SMandatoryPacksData><Paths><Item>platform:/dlcPacks/mpChristmas/</Item><Item>dlcpacks:/new/</Item></Paths></SMandatoryPacksData>"
        );
        Assert.Contains("platform:/dlcPacks/mpBeach/", merged);
        Assert.Contains("platform:/dlcPacks/mpChristmas/", merged);
    }

    [Theory]
    [InlineData("Legacy")]
    [InlineData("Enhanced")]
    public void Dlc_install_only_changes_selected_game_mods_and_can_be_undone(string edition)
    {
        var game = Game(edition);
        var other = Game(edition == "Legacy" ? "Enhanced" : "Legacy");
        File.WriteAllText(Path.Combine(game.Root, "RageOpenV.asi"), "MZ loader");
        File.WriteAllText(
            Path.Combine(game.Root, edition == "Enhanced" ? "xinput1_4.dll" : "dinput8.dll"),
            "MZ loader"
        );
        string original = Path.Combine(game.Root, @"update\update.rpf");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        using (var s = File.Create(original))
            new GTAModManager.Core.Rpf.Rpf7Builder()
                .AddFile(
                    @"common\data\dlclist.xml",
                    new GTAModManager.Core.Rpf.RpfBinarySource(Encoding.UTF8.GetBytes(Dlc))
                )
                .Write(s);
        string source = Path.Combine(root, Guid.NewGuid() + ".xml");
        File.WriteAllText(
            source,
            "<SMandatoryPacksData><Paths><Item>dlcpacks:/newmod/</Item></Paths></SMandatoryPacksData>"
        );
        string hash = GTAModManager.Core.Transactions.FileTransactionEngine.Hash(original);
        var library = GTAModManager.Core.Library.BackupLibrary.Create(Path.Combine(root, "library"), 1L << 28);
        var result = InstallationService.Execute(
            game,
            new()
            {
                Source = source,
                PrepareMods = true,
                ConfirmOverwrite = true,
            },
            library
        );
        Assert.Equal(GTAModManager.Core.Transactions.TxState.Completed, result.State);
        Assert.Equal(hash, GTAModManager.Core.Transactions.FileTransactionEngine.Hash(original));
        string edited = Encoding.UTF8.GetString(
            new RpfWorkspace(game).Read(@"mods\update\update.rpf", @"common\data\dlclist.xml")
        );
        Assert.Contains("existing", edited);
        Assert.Contains("newmod", edited);
        Assert.False(Directory.Exists(other.ModsDir));
        Assert.Equal(
            GTAModManager.Core.Transactions.TxState.Undone,
            new OivInstaller(game).Undo(result.Id, library).State
        );
        Assert.Equal(hash, GTAModManager.Core.Transactions.FileTransactionEngine.Hash(original));
    }

    [Theory]
    [InlineData("Legacy")]
    [InlineData("Enhanced")]
    public void GameConfig_install_succeeds_without_adjusters_and_prepares_dlc_folders(string edition)
    {
        var game = Game(edition);
        File.WriteAllText(Path.Combine(game.Root, "RageOpenV.asi"), "MZ loader");
        File.WriteAllText(
            Path.Combine(game.Root, edition == "Enhanced" ? "xinput1_4.dll" : "dinput8.dll"),
            "MZ loader"
        );
        const string config =
            "<fwAllConfigs><ConfigArray><Item><Config type=\"CGameConfig\"><Value value=\"1\"/></Config></Item></ConfigArray></fwAllConfigs>";
        string original = Path.Combine(game.Root, @"update\update.rpf");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        using (var stream = File.Create(original))
            new GTAModManager.Core.Rpf.Rpf7Builder()
                .AddFile(
                    @"common\data\gameconfig.xml",
                    new GTAModManager.Core.Rpf.RpfBinarySource(Encoding.UTF8.GetBytes(config))
                )
                .AddFile("untouched.txt", new GTAModManager.Core.Rpf.RpfBinarySource([1, 2, 3]))
                .Write(stream);
        string hash = GTAModManager.Core.Transactions.FileTransactionEngine.Hash(original);
        string archivePath = Path.Combine(root, "config-" + edition + ".zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("variant/gameconfig.xml").Open()))
                writer.Write(config.Replace("value=\"1\"", "value=\"2\""));
            using (var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open()))
                writer.Write("Required: Heap Adjuster and Packfile Limit Adjuster");
        }
        var library = GTAModManager.Core.Library.BackupLibrary.Create(Path.Combine(root, "library"), 1L << 28);
        var result = InstallationService.Execute(
            game,
            new()
            {
                Source = archivePath,
                PrepareMods = true,
                ConfirmOverwrite = true,
                ConfirmUnverified = true,
                Mappings = new() { [@"variant\gameconfig.xml"] = @"update\update.rpf|common\data\gameconfig.xml" },
            },
            library
        );
        Assert.Equal(GTAModManager.Core.Transactions.TxState.Completed, result.State);
        Assert.Contains(
            "value=\"2\"",
            Encoding.UTF8.GetString(
                new RpfWorkspace(game).Read(@"mods\update\update.rpf", @"common\data\gameconfig.xml")
            )
        );
        Assert.Equal(new byte[] { 1, 2, 3 }, new RpfWorkspace(game).Read(@"mods\update\update.rpf", "untouched.txt"));
        Assert.Equal(hash, GTAModManager.Core.Transactions.FileTransactionEngine.Hash(original));
        Assert.True(Directory.Exists(Path.Combine(game.ModsDir, @"update\x64\dlcpacks")));
        Assert.False(File.Exists(Path.Combine(game.Root, "HeapAdjuster.asi")));
        Assert.False(File.Exists(Path.Combine(game.Root, "PackfileLimitAdjuster.asi")));
    }

    [Fact]
    public void Negated_dependency_is_not_required()
    {
        var needs = DocumentRequirements.Inspect(Game(), "Script Hook V is not required.\nHeap Adjuster optional", []);
        Assert.DoesNotContain(needs, n => n.Required);
    }

    [Fact]
    public void Copy_never_overwrites_existing_modded_archive()
    {
        var game = Game();
        string original = Path.Combine(game.Root, @"update\update.rpf");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllText(original, "original");
        string modded = Path.Combine(game.Root, @"mods\update\update.rpf");
        Directory.CreateDirectory(Path.GetDirectoryName(modded)!);
        File.WriteAllText(modded, "modded");
        Assert.Throws<InvalidOperationException>(() => new RpfWorkspace(game).PrepareCopy(@"update\update.rpf"));
        Assert.Equal("modded", File.ReadAllText(modded));
    }

    [Fact]
    public void Downloads_cannot_overwrite_own_receipt()
    {
        var downloads = new ManagedDownloads(Path.Combine(root, "downloads"));
        Assert.Throws<ArgumentException>(() => downloads.Begin("receipt.json", "https://example.com/mod", root));
    }

    [Fact]
    public void Completed_download_can_follow_explicit_game_selection()
    {
        var legacy = Game();
        var enhanced = Game("Enhanced");
        var downloads = new ManagedDownloads(Path.Combine(root, "downloads"));
        var receipt = downloads.Begin("config.zip", "https://example.com/mod", legacy.Root);
        File.WriteAllText(receipt.File, "archive");
        downloads.Complete(receipt.Id);
        downloads.Retarget(receipt.Id, legacy.Root, enhanced.Root);
        var changed = downloads.Load(receipt.Id);
        Assert.Equal(enhanced.Root, changed.GameRoot);
        Assert.True(changed.Completed);
        Assert.Equal("archive", File.ReadAllText(changed.File));
    }

    [Fact]
    public void Changed_download_cannot_be_retargeted()
    {
        var legacy = Game();
        var enhanced = Game("Enhanced");
        var downloads = new ManagedDownloads(Path.Combine(root, "downloads"));
        var receipt = downloads.Begin("config.zip", "https://example.com/mod", legacy.Root);
        File.WriteAllText(receipt.File, "archive");
        downloads.Complete(receipt.Id);
        File.WriteAllText(receipt.File, "changed");
        Assert.Throws<InvalidOperationException>(() => downloads.Retarget(receipt.Id, legacy.Root, enhanced.Root));
        Assert.Equal(legacy.Root, downloads.Load(receipt.Id).GameRoot);
    }

    [Fact]
    public void Completed_download_cleanup_is_owned_and_hash_checked()
    {
        var d = new ManagedDownloads(Path.Combine(root, "downloads"));
        var r = d.Begin("mod.zip", "https://example.com/mod", root);
        string unrelated = Path.Combine(root, "unrelated.zip");
        File.WriteAllText(unrelated, "keep");
        File.WriteAllText(r.File, "archive");
        d.Complete(r.Id);
        File.WriteAllText(r.File, "changed");
        Assert.Throws<InvalidOperationException>(() => d.Finish(r.Id, false, null));
        Assert.True(File.Exists(r.File));
        File.WriteAllText(r.File, "archive");
        d.Finish(r.Id, false, null);
        Assert.False(File.Exists(r.File));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Queue_records_are_scoped_to_selected_game()
    {
        var a = Game();
        var b = Game("Enhanced");
        var dir = Path.Combine(root, "queue");
        var sa = new InstallQueueStore(a.Root, dir);
        sa.Save(
            new()
            {
                GameRoot = a.Root,
                Source = "dlclist.xml",
                Phase = InstallPhase.AwaitingConfirmation,
            }
        );
        Assert.Single(new InstallQueueStore(a.Root, dir).Load());
        Assert.Empty(new InstallQueueStore(b.Root, dir).Load());
    }

    [Fact]
    public async Task Concurrent_queue_updates_do_not_lose_jobs()
    {
        var directory = Path.Combine(root, "parallel-queue");
        await Task.WhenAll(
            Enumerable
                .Range(0, 20)
                .Select(i =>
                    Task.Run(() =>
                        new InstallQueueStore(root, directory).Save(new() { Source = i.ToString(), GameRoot = root })
                    )
                )
        );
        Assert.Equal(20, new InstallQueueStore(root, directory).Load().Count);
    }

    [Fact]
    public void Config_compatibility_requires_exact_edition_build_and_file_hash()
    {
        string source = Path.Combine(root, "config.xml");
        File.WriteAllText(source, "first");
        string hash = GTAModManager.Core.Transactions.FileTransactionEngine.Hash(source);
        VerifiedConfig[] rules = [new("Legacy", "1.0.3889.0", hash, "https://example.com/publisher")];
        Assert.True(GameConfigCompatibility.Verified(source, GameEdition.Legacy, "1.0.3889.0", rules));
        Assert.False(GameConfigCompatibility.Verified(source, GameEdition.Enhanced, "1.0.3889.0", rules));
        Assert.False(GameConfigCompatibility.Verified(source, GameEdition.Legacy, "1.0.9999.0", rules));
        File.WriteAllText(source, "changed");
        Assert.False(GameConfigCompatibility.Verified(source, GameEdition.Legacy, "1.0.3889.0", rules));
    }

    [Fact]
    public void Stale_confirmed_target_is_rejected_before_changes()
    {
        var game = Game();
        string existing = Path.Combine(game.Root, "mod.ini");
        File.WriteAllText(existing, "current");
        string source = Path.Combine(root, "source.ini");
        File.WriteAllText(source, "incoming");
        Assert.Throws<IOException>(() =>
            InstallationService.Prepare(
                game,
                new()
                {
                    Source = source,
                    ExpectedTargets = new() { ["mod.ini"] = "outdated" },
                }
            )
        );
        Assert.Equal("current", File.ReadAllText(existing));
    }

    [Fact]
    public void Archive_dependency_urls_are_recognized()
    {
        var needs = DocumentRequirements.Inspect(
            Game(),
            "https://www.gta5-mods.com/tools/heapadjuster\nhttps://www.gta5-mods.com/tools/packfile-limit-adjuster",
            []
        );
        Assert.Contains(needs, n => n.Id == "heap" && n.Required);
        Assert.Contains(needs, n => n.Id == "packfile" && n.Required);
    }

    [Fact]
    public void Nested_archive_sources_are_readable()
    {
        byte[] inner;
        using (var mem = new MemoryStream())
        {
            using (var zip = new ZipArchive(mem, ZipArchiveMode.Create, true))
            {
                using var s = zip.CreateEntry("scripts/Nitro.asi").Open();
                s.Write([77, 90]);
            }
            inner = mem.ToArray();
        }
        string p = Path.Combine(root, "outer.zip");
        using (var zip = ZipFile.Open(p, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("release.zip").Open();
            s.Write(inner);
        }
        using var archive = ModArchive.OpenExpanded(p);
        Assert.Equal(new byte[] { 77, 90 }, archive.Read(@"release.zip\scripts\Nitro.asi"));
    }

    [Fact]
    public void GameConfig_variants_exclude_backup_and_keep_author_traffic()
    {
        string p = Path.Combine(root, "config.zip");
        using (var zip = ZipFile.Open(p, ZipArchiveMode.Create))
        {
            zip.CreateEntry("Backup/gameconfig.xml");
            zip.CreateEntry("Gta Config v37/For More Mods/2x traffic/gameconfig.xml");
            zip.CreateEntry("Gta Config v37/For Less Mods/Stock Traffic/gameconfig.xml");
        }
        using var archive = ModArchive.Open(p);
        var variants = GameConfigVariants.Find(archive);
        Assert.Equal(2, variants.Count);
        Assert.Contains(variants, v => v.Preset == "For More Mods" && v.Traffic == "2x traffic");
        Assert.All(variants, v => Assert.Null(v.EditionHint));
    }
}
