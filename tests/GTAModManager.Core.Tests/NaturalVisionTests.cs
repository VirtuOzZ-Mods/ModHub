using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Library;
using GTAModManager.Core.Mods;
using GTAModManager.Core.NaturalVision;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

[CollectionDefinition("NaturalVision integration", DisableParallelization = true)]
public sealed class NaturalVisionTestCollection { }

[Collection("NaturalVision integration")]
public sealed class NaturalVisionTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "gmm-nve-" + Guid.NewGuid().ToString("N"));
    readonly GameInstall game;
    readonly BackupLibrary library;
    readonly string states,
        bundle;
    const string Update = @"mods\update\update.rpf",
        Dlc = @"mods\update\x64\dlcpacks\NVE\dlc.rpf";

    public NaturalVisionTests()
    {
        Directory.CreateDirectory(root);
        string g = Path.Combine(root, "game");
        Directory.CreateDirectory(g);
        File.WriteAllText(Path.Combine(g, "GTA5.exe"), "stub");
        File.WriteAllText(Path.Combine(g, "RageOpenV.asi"), "MZ");
        File.WriteAllText(Path.Combine(g, "dinput8.dll"), "MZ");
        game = GameInstall.Open(g);
        game.RunningCheckOverride = () => false;
        library = BackupLibrary.Create(Path.Combine(root, "library"), 1L << 29);
        states = Path.Combine(root, "states");
        bundle = Path.Combine(root, "bundle");
        Directory.CreateDirectory(bundle);
        string original = Path.Combine(g, @"update\update.rpf");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllBytes(
            original,
            Rpf(
                new()
                {
                    [@"common\data\dlclist.xml"] = Encoding.UTF8.GetBytes(
                        "<SMandatoryPacksData><Paths><Item>dlcpacks:/vanilla/</Item></Paths></SMandatoryPacksData>"
                    ),
                    ["other.txt"] = Encoding.UTF8.GetBytes("original"),
                }
            )
        );
        byte[] dlc = Rpf(new() { ["visual.txt"] = Encoding.UTF8.GetBytes("base") });
        dlc[500] = 123;
        Oiv(
            @"1. NaturalVision Installer - PART ONE.oiv",
            "NaturalVision Evolved - Part One",
            mainOps,
            new() { ["dlc.rpf"] = dlc }
        );
        Oiv(
            @"2. NaturalVision Installer - PART TWO.oiv",
            "NaturalVision Evolved - Part Two",
            "<add source=\"data\">nve2.txt</add>",
            new() { ["data"] = [2] }
        );
        Oiv(
            @"3. NaturalVision Installer - PART THREE.oiv",
            "NaturalVision Evolved - Part Three",
            "<add source=\"data\">nve3.txt</add>",
            new() { ["data"] = [3] }
        );
        Option("A", "A");
        Option("B", "B");
        Oiv(@"Optional Add-Ons\1. Option A\UNINSTALL - A.oiv", "Uninstall", "<delete>nve2.txt</delete>", []);
        string shaders = Path.Combine(bundle, @"ReShade Shaders\reshade-shaders\Shaders");
        Directory.CreateDirectory(shaders);
        File.WriteAllText(Path.Combine(shaders, "test.fx"), "shader");
        File.WriteAllText(Path.Combine(bundle, @"ReShade Shaders\NVE.ini"), "preset");
    }

    const string mainOps =
        """<add source="dlc.rpf">update\x64\dlcpacks\NVE\dlc.rpf</add><archive path="update\update.rpf" createIfNotExist="False" type="RPF7"><xml path="common\data\dlclist.xml"><add append="Last" xpath="/SMandatoryPacksData/Paths"><Item>dlcpacks:/NVE/</Item></add></xml></archive>""";

    static byte[] Rpf(Dictionary<string, byte[]> files)
    {
        using var s = new MemoryStream();
        var b = new Rpf7Builder();
        foreach (var f in files)
            b.AddFile(f.Key, new RpfBinarySource(f.Value));
        b.Write(s);
        return s.ToArray();
    }

    void Oiv(string name, string title, string ops, Dictionary<string, byte[]> files)
    {
        string p = Path.Combine(bundle, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        using var zip = ZipFile.Open(p, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("assembly.xml").Open()))
            w.Write(
                "<package version=\"2.2\" target=\"Five\"><metadata><name>"
                    + title
                    + "</name><description>Test</description></metadata><content>"
                    + ops
                    + "</content></package>"
            );
        foreach (var f in files)
        {
            using var s = zip.CreateEntry("content/" + f.Key).Open();
            s.Write(f.Value);
        }
    }

    void Option(string name, string value) =>
        Oiv(
            @"Optional Add-Ons\" + (name == "A" ? "1. Option A" : "2. Option B") + @"\INSTALL - " + name + ".oiv",
            name,
            "<archive path=\"update\\x64\\dlcpacks\\NVE\\dlc.rpf\" type=\"RPF7\" createIfNotExist=\"False\"><add source=\"data\">visual.txt</add></archive>",
            new() { ["data"] = Encoding.UTF8.GetBytes(value) }
        );

    NveCatalog Catalog()
    {
        using var b = new NveBundle(bundle);
        return b.Analyze();
    }

    NveService Service() => new(game, library, states);

    NveState State(string id) => new NveStore(game, states).Load(id);

    NveResult Install(params string[] options)
    {
        var c = Catalog();
        var ids = c
            .Components.Where(p => p.Core || options.Contains(p.Title) || options.Contains(p.Id))
            .Select(p => p.Id)
            .ToList();
        return Service()
            .Install(
                new()
                {
                    Source = bundle,
                    Stamp = c.Stamp,
                    Components = ids,
                }
            );
    }

    string Read(string outer, string inner) => Encoding.UTF8.GetString(new RpfWorkspace(game).Read(outer, inner));

    void Edit(string outer, string path, string value)
    {
        string p = Path.Combine(root, "edit-" + Guid.NewGuid() + ".txt");
        File.WriteAllText(p, value);
        using var plan = new RpfWorkspace(game).PrepareEdit(new(outer, path, p));
        Assert.True(plan.CanInstall, string.Join(";", plan.Errors));
        Assert.Equal(TxState.Completed, new OivInstaller(game).Install(plan, library).State);
    }

    [Fact]
    public void Game_root_dlls_are_replaced_on_install_and_restored_on_removal()
    {
        File.WriteAllText(Path.Combine(game.Root, "nvngx_dlss.dll"), "game dlss");
        File.Delete(Path.Combine(bundle, @"2. NaturalVision Installer - PART TWO.oiv"));
        Oiv(
            @"2. NaturalVision Installer - PART TWO.oiv",
            "NaturalVision Evolved - Part Two",
            "<add source=\"data\">nve2.txt</add><add source=\"dlss\">nvngx_dlss.dll</add>"
                + "<add source=\"fsr\">amd_fidelityfx_dx12.dll</add>",
            new()
            {
                ["data"] = [2],
                ["dlss"] = Encoding.UTF8.GetBytes("nve dlss"),
                ["fsr"] = Encoding.UTF8.GetBytes("nve fsr"),
            }
        );
        var r = Install();
        Assert.True(r.Success, r.Message);
        Assert.True(File.Exists(Path.Combine(game.Root, "nve2.txt")));
        Assert.Equal("nve dlss", File.ReadAllText(Path.Combine(game.Root, "nvngx_dlss.dll")));
        Assert.Equal("nve fsr", File.ReadAllText(Path.Combine(game.Root, "amd_fidelityfx_dx12.dll")));

        var removed = Service().Remove(new() { InstallationId = r.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Assert.Equal("game dlss", File.ReadAllText(Path.Combine(game.Root, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root, "amd_fidelityfx_dx12.dll")));
    }

    [Fact]
    public void Previews_read_only_root_icon_from_folder_and_nested_zip_and_honor_cancellation()
    {
        string path = Path.Combine(bundle, "1. NaturalVision Installer - PART ONE.oiv");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using (var texture = zip.CreateEntry("content/icon.png").Open())
                texture.Write(new byte[] { 9 });
            using (var icon = zip.CreateEntry("icon.png").Open())
                icon.Write(new byte[] { 1, 2, 3 });
        }
        using (var b = new NveBundle(bundle))
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, b.ReadIcon("1. NaturalVision Installer - PART ONE.oiv"));
            Assert.Null(b.ReadIcon("2. NaturalVision Installer - PART TWO.oiv"));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                b.ReadIcon("1. NaturalVision Installer - PART ONE.oiv", cancel.Token)
            );
        }
        string packed = Path.Combine(root, "previews.zip");
        ZipFile.CreateFromDirectory(bundle, packed);
        using var nested = new NveBundle(packed);
        Assert.Equal(new byte[] { 1, 2, 3 }, nested.ReadIcon("1. NaturalVision Installer - PART ONE.oiv"));
        Assert.Null(nested.ReadIcon("2. NaturalVision Installer - PART TWO.oiv"));
    }

    [Fact]
    public void Catalog_only_offers_three_core_installers_options_and_reshade()
    {
        var c = Catalog();
        Assert.Equal(3, c.Components.Count(p => p.Core));
        Assert.DoesNotContain(c.Components, p => p.Source.Contains("UNINSTALL"));
        Assert.Contains(c.Components, p => p.Id == "reshade");
        Assert.NotEmpty(c.Validate(["core-1"]));
        string zipPath = Path.Combine(root, "NVE.zip");
        ZipFile.CreateFromDirectory(bundle, zipPath);
        using var b = new NveBundle(zipPath);
        Assert.Equal(c.Components.Count, b.Analyze().Components.Count);
    }

    [Fact]
    public void Removing_earlier_option_replays_later_option_then_full_remove_restores_game()
    {
        string original = FileTransactionEngine.Hash(Path.Combine(game.Root, @"update\update.rpf"));
        var r = Install("A", "B", "reshade");
        Assert.True(r.Success, r.Message);
        Assert.Equal("B", Read(Dlc, "visual.txt"));
        var state = State(r.InstallationId);
        string a = state.Components.Single(c => c.Component.Title == "A").Component.Id,
            b = state.Components.Single(c => c.Component.Title == "B").Component.Id;
        var removed = Service().Remove(new() { InstallationId = r.InstallationId, Components = [a] });
        Assert.True(removed.Success, removed.Message);
        Assert.Equal("B", Read(Dlc, "visual.txt"));
        Assert.False(State(r.InstallationId).Components.Single(c => c.Component.Id == a).Active);
        removed = Service().Remove(new() { InstallationId = r.InstallationId, Components = [b] });
        Assert.True(removed.Success, removed.Message);
        Assert.Equal("base", Read(Dlc, "visual.txt"));
        removed = Service().Remove(new() { InstallationId = r.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(Path.Combine(game.Root, Dlc)));
        Assert.False(File.Exists(Path.Combine(game.Root, "nve2.txt")));
        Assert.Equal(original, FileTransactionEngine.Hash(Path.Combine(game.Root, @"update\update.rpf")));
    }

    [Fact]
    public void Full_remove_keeps_later_dlc_registrations_and_unrelated_rpf_edits()
    {
        var r = Install("A");
        Assert.True(r.Success, r.Message);
        string text = InstallDocuments.MergeDlc(
            Read(Update, @"common\data\dlclist.xml"),
            "<SMandatoryPacksData><Paths><Item>dlcpacks:/foreign/</Item></Paths></SMandatoryPacksData>"
        );
        Edit(Update, @"common\data\dlclist.xml", text);
        Edit(Update, "other.txt", "foreign data");
        var removed = Service().Remove(new() { InstallationId = r.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Assert.Contains("foreign", Read(Update, @"common\data\dlclist.xml"));
        Assert.DoesNotContain("NVE", Read(Update, @"common\data\dlclist.xml"));
        Assert.Equal("foreign data", Read(Update, "other.txt"));
    }

    [Fact]
    public void External_edits_inside_owned_nve_archive_do_not_block_removal()
    {
        var r = Install("A", "B");
        Assert.True(r.Success, r.Message);
        Edit(Dlc, "foreign.txt", "foreign");
        var removed = Service().Remove(new() { InstallationId = r.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Assert.All(State(r.InstallationId).Components, c => Assert.False(c.Active));
        string dlc = Path.Combine(game.Root, Dlc);
        if (File.Exists(dlc))
            using (var archive = ArchiveSession.OpenFile(dlc, "dlc"))
                Assert.Null(archive.ReadFile("visual.txt"));
    }

    [Fact]
    public void Failed_option_rolls_back_completed_core_parts()
    {
        Oiv(
            @"Optional Add-Ons\3. Broken\INSTALL - Broken.oiv",
            "Broken",
            "<add source=\"missing\">broken.txt</add>",
            []
        );
        var r = Install("Broken");
        Assert.False(r.Success);
        Assert.False(File.Exists(Path.Combine(game.Root, Dlc)));
        Assert.False(File.Exists(Path.Combine(game.Root, "nve2.txt")));
        Assert.Empty(new NveStore(game, states).Active());
    }

    [Fact]
    public void Wrong_edition_and_missing_core_are_rejected_without_changes()
    {
        string e = Path.Combine(root, "enhanced");
        Directory.CreateDirectory(e);
        File.WriteAllText(Path.Combine(e, "GTA5_Enhanced.exe"), "stub");
        var enhanced = GameInstall.Open(e);
        enhanced.RunningCheckOverride = () => false;
        var c = Catalog();
        Assert.Throws<InvalidOperationException>(() =>
            new NveService(enhanced, library, states).Install(
                new()
                {
                    Source = bundle,
                    Stamp = c.Stamp,
                    Components = c.Components.Where(p => p.Core).Select(p => p.Id).ToList(),
                }
            )
        );
        Assert.Throws<InvalidOperationException>(() =>
            Service()
                .Install(
                    new()
                    {
                        Source = bundle,
                        Stamp = c.Stamp,
                        Components = ["core-1"],
                    }
                )
        );
        Assert.False(Directory.Exists(enhanced.ModsDir));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Enhanced_profile_installs_own_layout_and_its_asi_loader_which_stays_after_removal(bool shaders)
    {
        string source = Path.Combine(root, "enhanced-source"),
            main = Path.Combine(source, "Main Package");
        Directory.CreateDirectory(Path.Combine(main, @"onigiri\dlcpacks\NVE"));
        File.WriteAllText(Path.Combine(main, "NVE.asi"), "MZ nve");
        File.WriteAllText(Path.Combine(main, "onigiri.asi"), "MZ loader");
        File.WriteAllText(Path.Combine(main, "NVE.ini"), "config");
        File.WriteAllText(Path.Combine(main, "ReShade.asi"), "MZ reshade");
        File.WriteAllText(Path.Combine(main, "ReShade.ini"), "preset");
        File.WriteAllText(Path.Combine(main, "renodx-test.addon64"), "addon");
        File.WriteAllText(Path.Combine(main, "xinput1_4.dll"), "MZ package proxy");
        File.WriteAllBytes(Path.Combine(main, @"onigiri\dlcpacks\NVE\dlc.rpf"), Rpf(new() { ["a.txt"] = [1, 2, 3] }));
        string target = Path.Combine(root, "enhanced-game");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "GTA5_Enhanced.exe"), "stub");
        // The Legacy loader from the Script Hook V archive: GTA5_Enhanced.exe never loads it.
        File.WriteAllText(Path.Combine(target, "dinput8.dll"), "MZ loader");
        File.WriteAllText(Path.Combine(target, "ScriptHookV.dll"), "MZ hook");
        var enhanced = GameInstall.Open(target);
        Assert.False(ModdingBase.Inspect(enhanced).AsiLoader);
        enhanced.RunningCheckOverride = () => false;
        using var package = new NveBundle(source);
        var c = package.Analyze();
        Assert.Equal(GameEdition.Enhanced, c.Edition);
        Assert.Single(c.Components, p => p.Core);
        var service = new NveService(enhanced, library, states);
        var ids = c.Components.Where(p => p.Core || shaders).Select(p => p.Id).ToList();
        var updates = new List<NveProgress>();
        var result = service.Install(
            new()
            {
                Source = source,
                Stamp = c.Stamp,
                Components = ids,
            },
            componentProgress: updates.Add
        );
        Assert.True(result.Success, result.Message);
        Assert.All(updates, update => Assert.Equal(ids.Count, update.Total));
        Assert.Equal(
            Enumerable.Range(1, ids.Count),
            updates.Where(p => p.Phase == NveProgressPhase.Installed).Select(p => p.Completed)
        );
        Assert.Equal(ids.Count, updates.Count(p => p.Phase == NveProgressPhase.Verifying));
        Assert.True(File.Exists(Path.Combine(target, @"onigiri\dlcpacks\NVE\dlc.rpf")));
        Assert.Equal(shaders, File.Exists(Path.Combine(target, "ReShade.asi")));
        Assert.Equal(shaders, File.Exists(Path.Combine(target, "renodx-test.addon64")));
        // The package's Enhanced ASI loader went in as its own step, so NVE and every other .asi can load.
        Assert.Equal("MZ package proxy", File.ReadAllText(Path.Combine(target, "xinput1_4.dll")));
        Assert.True(ModdingBase.Inspect(enhanced).AsiLoader);
        var removed = service.Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(Path.Combine(target, "NVE.asi")));
        Assert.False(File.Exists(Path.Combine(target, "onigiri.asi")));
        // Removing NVE keeps the loader: the other mods keep working.
        Assert.Equal("MZ package proxy", File.ReadAllText(Path.Combine(target, "xinput1_4.dll")));
    }

    [Fact]
    public void Corrupted_option_cache_stops_selective_removal_without_changes()
    {
        var r = Install("A", "B");
        Assert.True(r.Success, r.Message);
        var s = State(r.InstallationId);
        var a = s.Components.Single(p => p.Component.Title == "A");
        var b = s.Components.Single(p => p.Component.Title == "B");
        File.WriteAllText(NveStore.Cache(s, b.CacheFile!), "damaged");
        string hash = FileTransactionEngine.Hash(Path.Combine(game.Root, Dlc));
        Assert.Throws<IOException>(() =>
            Service().Remove(new() { InstallationId = s.Id, Components = [a.Component.Id] })
        );
        Assert.Equal(hash, FileTransactionEngine.Hash(Path.Combine(game.Root, Dlc)));
    }

    [Fact]
    public void Preserved_nve_records_survive_history_cleanup_and_still_remove()
    {
        var result = Install("A");
        Assert.True(result.Success, result.Message);
        var state = State(result.InstallationId);
        var keep = state.Components.Where(c => c.TransactionId is not null).Select(c => c.TransactionId!).ToHashSet();
        game.CreateEngine().ClearHistory(keep);
        library.DeleteRecords(game.Edition.ToString(), keep);
        library.CollectGarbage();
        Assert.All(keep, id => Assert.NotNull(library.LoadRecord(game.Edition.ToString(), id)));
        var removed = Service().Remove(new() { InstallationId = state.Id });
        Assert.True(removed.Success, removed.Message);
    }

    [Fact]
    public void Conflicting_optional_variants_and_nights_are_rejected()
    {
        var original = Catalog();
        var core = original.Components.Where(c => c.Core).ToArray();
        NveComponent[] options =
        [
            new("night", "Optional Add-Ons\\19. Darker Nights\\INSTALL.oiv", "Nights", "", "", "Nights", 100, false, 1),
            new(
                "weather",
                "Optional Add-Ons\\20. NVE 2020 Weather Filter\\INSTALL.oiv",
                "Weather",
                "",
                "",
                "Weather",
                101,
                false,
                1
            ),
            new("v1", "v1", "V1", "", "", "Variant", 102, false, 1),
            new("v2", "v2", "V2", "", "", "Variant", 103, false, 1),
        ];
        var catalog = original with { Components = core.Concat(options).ToArray() };
        Assert.NotEmpty(catalog.Validate(core.Select(c => c.Id).Concat(["night", "weather"])));
        Assert.NotEmpty(catalog.Validate(core.Select(c => c.Id).Concat(["v1", "v2"])));
    }

    [Fact]
    public void Legacy_progress_includes_only_selected_addons_in_total()
    {
        var catalog = Catalog();
        var ids = catalog.Components.Where(c => c.Core || c.Title == "A").Select(c => c.Id).ToList();
        var updates = new List<NveProgress>();
        var result = Service()
            .Install(
                new()
                {
                    Source = bundle,
                    Stamp = catalog.Stamp,
                    Components = ids,
                },
                componentProgress: updates.Add
            );
        Assert.True(result.Success, result.Message);
        Assert.All(updates, p => Assert.Equal(4, p.Total));
        Assert.Equal(
            new[] { 1, 2, 3, 4 },
            updates.Where(p => p.Phase == NveProgressPhase.Installed).Select(p => p.Completed)
        );
        Assert.DoesNotContain(updates, p =>
            p.Component == "B" || p.Component.Contains("ReShade", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void Vanilla_restore_invalidates_nve_options_and_clears_history()
    {
        var result = Install("A", "B", "reshade");
        Assert.True(result.Success, result.Message);
        var restored = VanillaRestore.Execute(game, VanillaRestore.Inspect(game), library, states);
        try
        {
            Assert.Empty(restored.Failed);
            Assert.Empty(new NveStore(game, states).Active());
            Assert.Equal("Removed", State(result.InstallationId).Phase);
            Assert.Empty(game.CreateEngine().History().Where(j => j.TargetRoot == game.Root));
            Assert.Empty(library.Records());
            Assert.True(Service().Remove(new() { InstallationId = result.InstallationId }).Success);
        }
        finally
        {
            VanillaRestore.DeleteQuarantine(game, restored.Folder);
        }
    }

    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void Hand_edited_shared_dlclist_does_not_block_removal_and_keeps_foreign_line()
    {
        var installed = Install();
        Assert.True(installed.Success, installed.Message);
        var path = Path.Combine(game.Root, @"mods\update\update.rpf");
        byte[] bytes;
        using (var archive = ArchiveSession.OpenFile(path, "update"))
        {
            var old = Encoding.UTF8.GetString(archive.ReadFile(@"common\data\dlclist.xml")!);
            archive.Put(
                @"common\data\dlclist.xml",
                Encoding.UTF8.GetBytes(old.Replace("</Paths>", "<Item>dlcpacks:/foreign/</Item></Paths>"))
            );
            using var ms = new MemoryStream();
            archive.WriteTo(ms);
            bytes = ms.ToArray();
        }
        File.WriteAllBytes(path, bytes);
        var removed = Service().Remove(new() { InstallationId = installed.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(Path.Combine(game.Root, "nve2.txt")));
        Assert.False(File.Exists(Path.Combine(game.Root, "nve3.txt")));
        Assert.All(State(installed.InstallationId).Components, c => Assert.False(c.Active));
        if (File.Exists(path))
            using (var archive = ArchiveSession.OpenFile(path, "update"))
                Assert.Contains(
                    "dlcpacks:/foreign/",
                    Encoding.UTF8.GetString(archive.ReadFile(@"common\data\dlclist.xml") ?? [])
                );
    }
}
