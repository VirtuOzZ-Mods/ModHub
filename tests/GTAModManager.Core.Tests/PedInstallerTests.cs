using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Peds;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class PedInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-ped-test-" + Guid.NewGuid().ToString("N"));
    private readonly GameInstall _game;
    private readonly BackupLibrary _library;
    private readonly string _failureWork = Path.Combine(
        Path.GetTempPath(),
        "gmm-ped-failure-" + Guid.NewGuid().ToString("N")
    );
    private const string DlcXml =
        "<SMandatoryPacksData><Paths>\n<Item>dlcpacks:/untouched/</Item>\n</Paths></SMandatoryPacksData>";

    public PedInstallerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "GTA5.exe"), "synthetic");
        File.WriteAllText(Path.Combine(_root, "OpenIV.asi"), "MZ synthetic loader");
        Directory.CreateDirectory(Path.Combine(_root, "mods", "update"));
        using (var f = File.Create(Path.Combine(_root, "mods", "update", "update.rpf")))
            new Rpf7Builder()
                .AddFile(@"common\data\dlclist.xml", new RpfBinarySource(Encoding.UTF8.GetBytes(DlcXml)))
                .Write(f);
        _game = GameInstall.Open(_root);
        _game.RunningCheckOverride = () => false;
        _library = BackupLibrary.Create(Path.Combine(_root, "test-backups"), 1L << 30);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
        try
        {
            if (Directory.Exists(_failureWork))
                Directory.Delete(_failureWork, true);
        }
        catch (IOException) { }
    }

    private PedArchiveAnalysis Archive(
        string name = "demo",
        PedLayout layout = PedLayout.Normal,
        string? registration = null
    )
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        string[] files =
            layout == PedLayout.Normal
                ? [name + ".yft", name + ".ymt", name + ".ydd", name + ".ytd"]
                : [name + ".yft", name + ".ymt", name + @"\head_000_r.ydd", name + @"\head_diff_000_a_whi.ytd"];
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var file in files)
                using (var stream = zip.CreateEntry("model/" + file.Replace('\\', '/')).Open())
                    stream.Write(SyntheticResource());
        var candidate = new PedCandidate(
            "one",
            name,
            "default",
            layout,
            files.Select(f => new PedResource("model\\" + f, f, 16)).ToList(),
            []
        )
        {
            RegistrationXml = registration,
        };
        return new(path, [candidate], "readme", [], []);
    }

    private InstallPlan Prepare(PedArchiveAnalysis archive, params PedEntry[] external) =>
        new PedInstaller(_game).PrepareInstall(
            archive,
            [new("one", "Demo\tPed\n", PedGender.Female, archive.Candidates[0].Layout)],
            external
        );

    private void Install(InstallPlan plan)
    {
        Assert.True(plan.CanInstall, string.Join("; ", plan.Errors));
        var result = new OivInstaller(_game).Install(plan, _library);
        Assert.True(result.State == TxState.Completed, result.State + ": " + result.Message);
    }

    private string Metadata()
    {
        using var pack = ArchiveSession.OpenFile(Path.Combine(_root, PedInstaller.PackPath), "pack");
        return Encoding.UTF8.GetString(pack.ReadFile(@"common\data\peds.meta")!);
    }

    [Fact]
    public void Normal_install_is_one_backed_up_transaction_and_preserves_full_registration()
    {
        string xml =
            "<Item custom=\"kept\"><Name>demo</Name><Pedtype>CIVMALE</Pedtype><IsStreamedGfx value=\"false\"/><Unknown flag=\"7\"><Nested>original</Nested></Unknown></Item>";
        using var plan = Prepare(
            Archive(registration: xml),
            new PedEntry
            {
                SpawnName = "foreign",
                DisplayName = "Existing",
                RegistrationXml = "<Item/>",
            }
        );
        Assert.False(File.Exists(Path.Combine(_root, PedInstaller.CatalogPath)));
        Assert.Equal(6, plan.Changes.Count); // DLC, registration list, catalog, selector rows, media cache, compatibility receipt.
        Install(plan);
        var entry = Assert.Single(new PedInstaller(_game).ListManaged().Entries);
        Assert.Equal("CIVMALE", entry.PedType);
        Assert.Contains("custom=\"kept\"", Metadata());
        Assert.Contains("<Nested>original</Nested>", Metadata());
        Assert.Equal(4, entry.ResourcePaths.Count);
        Assert.Equal(
            "demo\tDemo Ped\nforeign\tExisting\n",
            File.ReadAllText(Path.Combine(_root, PedInstaller.ModelsPath))
        );
        using var update = ArchiveSession.OpenFile(Path.Combine(_root, @"mods\update\update.rpf"), "update");
        string dlc = Encoding.UTF8.GetString(update.ReadFile(@"common\data\dlclist.xml")!);
        Assert.Contains("dlcpacks:/untouched/", dlc);
        Assert.Contains("dlcpacks:/pedselector/", dlc);
        Assert.Single(_library.Records(_game.Edition.ToString()));
    }

    [Fact]
    public void Streamed_install_and_removal_preserve_other_models_and_manual_entries()
    {
        using (var plan = Prepare(Archive("stream", PedLayout.Streamed)))
            Install(plan);
        using (var plan = Prepare(Archive("other")))
            Install(plan);
        // Add an unknown registration to the synthetic owned pack to model manual additions.
        string packPath = Path.Combine(_root, PedInstaller.PackPath);
        byte[] replacement;
        using (var pack = ArchiveSession.OpenFile(packPath, "synthetic"))
        {
            var doc = XDocument.Parse(Encoding.UTF8.GetString(pack.ReadFile(@"common\data\peds.meta")!));
            doc.Root!.Element("InitDatas")!
                .Add(new XElement("Item", new XElement("Name", "manual"), new XElement("Unknown", "preserve")));
            pack.Put(@"common\data\peds.meta", Encoding.UTF8.GetBytes(doc.ToString()));
            using var output = new MemoryStream();
            pack.WriteTo(output);
            replacement = output.ToArray();
        }
        File.WriteAllBytes(packPath, replacement);
        using (var plan = new PedInstaller(_game).PrepareRemove("stream"))
            Install(plan);
        Assert.Equal("other", Assert.Single(new PedInstaller(_game).ListManaged().Entries).SpawnName);
        Assert.Contains("<Name>manual</Name>", Metadata());
        Assert.Contains("preserve", Metadata());
        using var final = ArchiveSession.OpenFile(packPath, "pack");
        Assert.Null(final.Nested("peds.rpf", false).Builder.GetSource("stream.yft"));
        Assert.NotNull(final.Nested("peds.rpf", false).Builder.GetSource("other.yft"));
        Assert.NotNull(final.ReadFile(PedInstaller.MarkerPath));
    }

    [Fact]
    public void Conflicting_external_name_or_variant_is_rejected_without_writes()
    {
        var archive = Archive();
        using var conflict = Prepare(archive, new PedEntry { SpawnName = "DEMO" });
        Assert.False(conflict.CanInstall);
        Assert.Contains(conflict.Errors, e => e.Contains("Конфликт"));
        using var variants = new PedInstaller(_game).PrepareInstall(
            archive,
            [new("one", "a", PedGender.Male, PedLayout.Normal), new("one", "b", PedGender.Male, PedLayout.Normal)]
        );
        Assert.False(variants.CanInstall);
        Assert.False(File.Exists(Path.Combine(_root, PedInstaller.PackPath)));
    }

    [Fact]
    public void Ambiguous_layout_cannot_be_installed_by_bypassing_the_ui()
    {
        var analysis = Archive();
        analysis = analysis with { Candidates = [analysis.Candidates[0] with { LayoutAmbiguous = true }] };
        using var plan = Prepare(analysis);
        Assert.False(plan.CanInstall);
        Assert.Contains(plan.Errors, e => e.Contains("смешаны Normal и Streamed"));
        Assert.False(File.Exists(Path.Combine(_root, PedInstaller.PackPath)));
    }

    [Fact]
    public void Preview_checks_source_paths_without_staging_model_resources()
    {
        var analysis = Archive();
        using var plan = new PedInstaller(_game).PrepareInstall(
            analysis,
            [new("one", "Demo", PedGender.Male, PedLayout.Normal)],
            preview: true
        );
        Assert.Empty(plan.Errors);
        Assert.True(plan.Preview);
        Assert.False(plan.CanInstall);
        Assert.Empty(Directory.EnumerateFiles(plan.PrepDir, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(_root, PedInstaller.PackPath)));
    }

    [Fact]
    public void All_35_observed_existing_names_including_parentheses_are_preserved_as_references()
    {
        // Names observed in the user's backup registration. Only identifiers are fixtures;
        // no third-party binaries, metadata parameters, or models are redistributed.
        var external = new[]
        {
            "vito_mafia",
            "superman",
            "Ada",
            "AdaCasualA",
            "AdaCasualB",
            "AdaCasualC",
            "AdaRE6",
            "Harley",
            "HarleyB",
            "HarleySS",
            "Ronin",
            "RoninB",
            "therock(jeans)",
            "therock(nojeans)",
            "Tifa",
            "t_800_mk11",
            "MaiS",
            "Deadpool",
            "Vb",
            "Va",
            "Sam",
            "Lana",
            "ig_roman",
            "Lara",
            "luis_lopez",
            "VinDiesel",
            "player_claude",
            "brian_o",
            "aiden_pearce",
            "JessePinkman",
            "Walter",
            "Stan_Lee_Nuff_Said",
            "jillclassic",
            "Grace",
            "nfsrachel",
        }
            .Select(n => new PedEntry { SpawnName = n, DisplayName = n })
            .ToArray();
        using var plan = Prepare(Archive(), external);
        Install(plan);
        string text = File.ReadAllText(Path.Combine(_root, PedInstaller.ModelsPath));
        Assert.Contains("therock(jeans)\ttherock(jeans)\n", text);
        Assert.Contains("therock(nojeans)\ttherock(nojeans)\n", text);
        foreach (var entry in external)
            Assert.Contains(entry.SpawnName + "\t" + entry.SpawnName + "\n", text);
        Assert.Equal(36, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("therock", Metadata()); // foreign models are referenced, never registered again.
        Assert.Throws<ArgumentException>(() => PedMetadata.ValidateSpawnName("therock(jeans)"));
    }

    [Fact]
    public void Normal_model_with_embedded_textures_does_not_require_separate_ytd()
    {
        var analysis = Archive();
        var candidate = analysis.Candidates[0] with
        {
            Resources = analysis.Candidates[0].Resources.Where(r => !r.TargetPath.EndsWith(".ytd")).ToList(),
        };
        using var plan = Prepare(analysis with { Candidates = [candidate] });
        Install(plan);
        Assert.Equal(3, Assert.Single(new PedInstaller(_game).ListManaged().Entries).ResourcePaths.Count);
    }

    [Fact]
    public void Foreign_owned_pack_name_and_path_traversal_are_protected()
    {
        string path = Path.Combine(_root, PedInstaller.PackPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
            new Rpf7Builder().AddFile("foreign.txt", new RpfBinarySource([1, 2])).Write(file);
        byte[] before = File.ReadAllBytes(path);
        using var foreign = Prepare(Archive());
        Assert.False(foreign.CanInstall);
        Assert.Contains(foreign.Errors, e => e.Contains("маркера"));
        Assert.Equal(before, File.ReadAllBytes(path));
        File.Delete(path);
        var archive = Archive();
        var bad = archive with
        {
            Candidates = [archive.Candidates[0] with { Resources = [new("model\\demo.yft", "..\\escape.yft", 1)] }],
        };
        using var traversal = Prepare(bad);
        Assert.False(traversal.CanInstall);
    }

    [Fact]
    public void Running_enhanced_and_manual_registration_changes_block_operations()
    {
        var archive = Archive();
        _game.RunningCheckOverride = () => true;
        using (var running = Prepare(archive))
            Assert.False(running.CanInstall);
        _game.RunningCheckOverride = () => false;
        File.WriteAllText(Path.Combine(_root, "GTA5_Enhanced.exe"), "synthetic");
        var enhanced = GameInstall.Open(_root);
        enhanced.RunningCheckOverride = () => false;
        using (
            var blocked = new PedInstaller(enhanced).PrepareInstall(
                archive,
                [new("one", "demo", PedGender.Male, PedLayout.Normal)]
            )
        )
            Assert.Contains(blocked.Errors, e => e.Contains("Legacy"));
        File.Delete(Path.Combine(_root, "GTA5_Enhanced.exe"));
        using (var install = Prepare(archive))
            Install(install);
        using var removeForeign = new PedInstaller(_game).PrepareRemove("unmanaged");
        Assert.False(removeForeign.CanInstall);
    }

    [Fact]
    public void Metadata_rejects_entities_and_hashes_ignore_case()
    {
        Assert.Throws<System.Xml.XmlException>(() =>
            PedMetadata.ParseItem(
                "<!DOCTYPE Item [<!ENTITY e SYSTEM 'file:///C:/sensitive'>]><Item><Name>&e;</Name></Item>"
            )
        );
        Assert.Equal(PedMetadata.NameHash("demo"), PedMetadata.NameHash("DEMO"));
        Assert.Throws<ArgumentException>(() => PedMetadata.ValidateSpawnName("bad\tname"));
    }

    [Fact]
    public void Unregistered_model_with_conventional_props_gets_a_matching_default_props_name()
    {
        var analysis = Archive();
        using (var zip = ZipFile.Open(analysis.ArchivePath, ZipArchiveMode.Update))
            foreach (var name in new[] { "demo_p.ydd", "demo_p.ytd" })
                using (var stream = zip.CreateEntry("model/" + name).Open())
                    stream.Write(SyntheticResource());
        var candidate = analysis.Candidates[0] with
        {
            Resources = analysis
                .Candidates[0]
                .Resources.Concat(
                    new[] { "demo_p.ydd", "demo_p.ytd" }.Select(p => new PedResource("model\\" + p, p, 16))
                )
                .ToList(),
        };
        using var plan = Prepare(analysis with { Candidates = [candidate] });
        Install(plan);
        var item = XDocument.Parse(Metadata()).Root!.Element("InitDatas")!.Element("Item")!;
        Assert.Equal("demo_p", item.Element("PropsName")!.Value);
        Assert.Contains("demo_p.ydd", Assert.Single(new PedInstaller(_game).ListManaged().Entries).ResourcePaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepared_ped_transaction_rolls_back_all_resources_metadata_catalog_and_dlclist_after_failure(bool crash)
    {
        using var plan = Prepare(Archive());
        Assert.True(plan.CanInstall, string.Join("; ", plan.Errors));
        var before = plan.Changes.ToDictionary(
            c => c.RelativePath,
            c => FileTransactionEngine.HashOrNull(Path.Combine(_root, c.RelativePath))
        );
        var engine = new FileTransactionEngine(_root, _failureWork)
        {
            IsGameRunning = () => false,
            FaultPoint = point =>
            {
                if (point != "after-apply:1")
                    return;
                if (crash)
                    throw new SimulatedCrashException(point);
                throw new IOException("Synthetic disk failure after two committed targets");
            },
        };
        if (crash)
        {
            Assert.Throws<SimulatedCrashException>(() => engine.Execute(plan.Changes, plan.Title));
            // Recovery reopens the write-ahead journal using the same isolated work path.
            var recovered = new FileTransactionEngine(_root, _failureWork).Recover();
            Assert.Equal(TxState.RolledBack, Assert.Single(recovered).State);
        }
        else
            Assert.Equal(TxState.RolledBack, engine.Execute(plan.Changes, plan.Title).State);
        foreach (var (path, hash) in before)
            Assert.Equal(hash, FileTransactionEngine.HashOrNull(Path.Combine(_root, path)));
        Assert.False(File.Exists(Path.Combine(_root, PedInstaller.PackPath)));
        Assert.False(File.Exists(Path.Combine(_root, PedInstaller.CatalogPath)));
        Assert.False(File.Exists(Path.Combine(_root, PedInstaller.ModelsPath)));
        using var update = ArchiveSession.OpenFile(Path.Combine(_root, @"mods\update\update.rpf"), "update");
        Assert.Equal(DlcXml, Encoding.UTF8.GetString(update.ReadFile(@"common\data\dlclist.xml")!));
    }

    private static bool HasEntry(ArchiveSession pack, string path)
    {
        var (container, inner) = pack.Resolve(path, false);
        return container.Builder.GetSource(inner) is not null;
    }

    private string DlcList()
    {
        using var update = ArchiveSession.OpenFile(Path.Combine(_root, @"mods\update\update.rpf"), "update");
        return Encoding.UTF8.GetString(update.ReadFile(@"common\data\dlclist.xml")!);
    }

    /// <summary>A pack like the original Ped Selector: the character list lives in UserMods\ap_m.xml.</summary>
    private string OriginalPack(string userMods, params string[] models)
    {
        string path = Path.Combine(_root, PedInstaller.PackPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var pack = ArchiveSession.CreateEmpty("original"))
        {
            pack.Put(
                "setup2.xml",
                Encoding.UTF8.GetBytes(
                    "<SSetupData><deviceName>o_pedselector</deviceName><datFile>appdata.xml</datFile><nameHash>pedselector</nameHash></SSetupData>"
                )
            );
            pack.Put(
                "appdata.xml",
                Encoding.UTF8.GetBytes(
                    "<CDataFileMgr__ContentsOfDataFileXml><dataFiles><Item><filename>o_pedselector:/peds.rpf</filename><fileType>PEDSTREAM_FILE</fileType></Item><Item><filename>user:/UserMods/ap_m.xml</filename><fileType>PED_METADATA_FILE</fileType></Item></dataFiles></CDataFileMgr__ContentsOfDataFileXml>"
                )
            );
            foreach (var model in models)
            foreach (var ext in new[] { ".yft", ".ymt", ".ydd" })
                pack.Put(@"peds.rpf\" + model + ext, SyntheticResource());
            using var output = File.Create(path);
            pack.WriteTo(output);
        }
        Directory.CreateDirectory(userMods);
        File.WriteAllText(
            Path.Combine(userMods, "ap_m.xml"),
            "<CPedModelInfo__InitDataList><InitDatas>"
                + string.Concat(
                    models.Append("ghost").Select(m => $"<Item><Name>{m}</Name><Pedtype>CIVFEMALE</Pedtype></Item>")
                )
                + "</InitDatas></CPedModelInfo__InitDataList>"
        );
        return path;
    }

    [Fact]
    public void Pack_has_the_name_and_layout_of_the_original_ped_selector()
    {
        using (var plan = Prepare(Archive()))
            Install(plan);
        using var pack = ArchiveSession.OpenFile(Path.Combine(_root, PedInstaller.PackPath), "pack");
        string setup = Encoding.UTF8.GetString(pack.ReadFile("setup2.xml")!);
        Assert.Contains("<deviceName>o_pedselector</deviceName>", setup);
        Assert.Contains("<datFile>appdata.xml</datFile>", setup);
        Assert.Contains("<nameHash>pedselector</nameHash>", setup);
        // The character list is inside the pack, not in Documents.
        Assert.Contains("o_pedselector:/common/data/peds.meta", Encoding.UTF8.GetString(pack.ReadFile("appdata.xml")!));
        Assert.Null(pack.ReadFile("content.xml"));
    }

    [Fact]
    public void Legacy_gmm_peds_pack_is_moved_to_pedselector_without_reinstalling()
    {
        using (var plan = Prepare(Archive()))
            Install(plan);
        // Reconstruct a pack from the older release, which had no compatibility receipts.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, @"ModHubState\compatibility"), "*.json"))
            File.Delete(file);
        string legacy = Path.Combine(_root, PedInstaller.LegacyPackPath);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.Move(Path.Combine(_root, PedInstaller.PackPath), legacy);
        using (var update = ArchiveSession.OpenFile(Path.Combine(_root, @"mods\update\update.rpf"), "update"))
        {
            update.Put(
                @"common\data\dlclist.xml",
                Encoding.UTF8.GetBytes(DlcList().Replace("pedselector", "gmm_peds"))
            );
            using var output = new MemoryStream();
            update.WriteTo(output);
            update.Dispose();
            File.WriteAllBytes(Path.Combine(_root, @"mods\update\update.rpf"), output.ToArray());
        }
        var installer = new PedInstaller(_game);
        Assert.True(installer.NeedsPackUpdate());
        using (var plan = installer.PrepareUpdatePack())
            Install(plan);
        Assert.False(File.Exists(legacy));
        Assert.False(new PedInstaller(_game).NeedsPackUpdate());
        Assert.Equal("demo", Assert.Single(new PedInstaller(_game).ListManaged().Entries).SpawnName);
        Assert.Contains("<Name>demo</Name>", Metadata());
        using (var pack = ArchiveSession.OpenFile(Path.Combine(_root, PedInstaller.PackPath), "pack"))
            Assert.True(HasEntry(pack, @"peds.rpf\demo.ydd"));
        Assert.Contains("dlcpacks:/pedselector/", DlcList());
        Assert.DoesNotContain("gmm_peds", DlcList());
    }

    [Fact]
    public void Dropped_original_pack_is_connected_unchanged_and_adopted_on_next_install()
    {
        string userMods = Path.Combine(_root, "UserMods");
        string path = OriginalPack(userMods, "lara");
        byte[] before = File.ReadAllBytes(path);
        var installer = new PedInstaller(_game, userMods);
        Assert.True(installer.NeedsPackUpdate());
        using (var plan = installer.PrepareUpdatePack())
            Install(plan);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains("dlcpacks:/pedselector/", DlcList());
        // The menu lists only models whose resources are in the pack.
        Assert.Equal("lara\tlara\n", File.ReadAllText(Path.Combine(_root, PedInstaller.ModelsPath)));

        var archive = Archive();
        using (
            var plan = new PedInstaller(_game, userMods).PrepareInstall(
                archive,
                [new("one", "Demo", PedGender.Female, PedLayout.Normal)]
            )
        )
            Install(plan);
        Assert.Contains("<Name>lara</Name>", Metadata());
        Assert.Contains("<Name>demo</Name>", Metadata());
        Assert.DoesNotContain("ghost", Metadata());
        using var pack = ArchiveSession.OpenFile(path, "pack");
        Assert.True(HasEntry(pack, @"peds.rpf\lara.ydd"));
        Assert.NotNull(pack.ReadFile(PedInstaller.MarkerPath));
    }

    private static byte[] SyntheticResource() => [0x52, 0x53, 0x43, 0x37, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_or_changed_resource_headers_are_rejected_before_any_game_write(bool sizeChanged)
    {
        var analysis = Archive(); // Represents a previously accepted analyzer result.
        using (var zip = ZipFile.Open(analysis.ArchivePath, ZipArchiveMode.Update))
        {
            zip.GetEntry("model/demo.yft")!.Delete();
            using var stream = zip.CreateEntry("model/demo.yft").Open();
            stream.Write(new byte[sizeChanged ? 17 : 16]);
        }
        using var plan = Prepare(analysis);
        Assert.False(plan.CanInstall);
        Assert.NotEmpty(plan.Errors);
        Assert.False(File.Exists(Path.Combine(_root, PedInstaller.PackPath)));
        Assert.False(File.Exists(Path.Combine(_root, PedInstaller.CatalogPath)));
    }
}
