using System.Buffers.Binary;
using System.Text;
using GTAModManager.Core.Dlc;
using GTAModManager.Core.Game;
using GTAModManager.Core.Inventory;
using GTAModManager.Core.Library;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

/// <summary>
/// Add-on cars and engine sound packs dropped as unpacked folders without a README: the pack kind comes from the
/// dlc.rpf contents, the pack is registered in dlclist.xml, and a sound can be given to a car and taken back.
/// The packs are tiny synthetic copies of the layout real packs use (vehicles.meta + vehicles.rpf; *_game.dat151.rel + .awc).
/// </summary>
public sealed class AddonDlcTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-addon-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "GTA V Enhanced");
    private BackupLibrary? _lib;
    private BackupLibrary Lib => _lib ??= BackupLibrary.Create(Path.Combine(_root, "backups"), 1L << 30);

    private const string DlcListXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SMandatoryPacksData>\r\n\t<Paths>\r\n\t\t<Item>dlcpacks:/mpluxe/</Item>\r\n\t</Paths>\r\n</SMandatoryPacksData>\r\n";

    public AddonDlcTests()
    {
        Directory.CreateDirectory(Game);
        File.WriteAllText(Path.Combine(Game, "GTA5_Enhanced.exe"), "stub");
        File.WriteAllText(Path.Combine(Game, "OpenRPF.asi"), "MZ stub loader");
        Directory.CreateDirectory(Path.Combine(Game, "mods", "update"));
        using var fs = File.Create(Path.Combine(Game, "mods", "update", "update.rpf"));
        new Rpf7Builder()
            .AddFile(
                @"common\data\dlclist.xml",
                new RpfBinarySource(Encoding.UTF8.GetBytes(DlcListXml), Compress: true)
            )
            .AddFile(@"common\data\levels\gta5\vehicles.meta", Text(VehiclesMeta("elegy2", "ELEGY2")))
            .AddFile(
                @"dlc_patch\mpheist4\common\data\levels\gta5\vehicles.meta",
                Text(VehiclesMeta("toreador", "TOREADOR"))
            )
            .AddFile(@"dlc_patch\mpheist4\common\data\levels\gta5\handling.meta", Text("<CHandlingDataMgr />"))
            .Write(fs);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private GameInstall OpenGame()
    {
        var g = GameInstall.Open(Game);
        g.RunningCheckOverride = () => false;
        return g;
    }

    private static RpfBinarySource Text(string s) => new(Encoding.UTF8.GetBytes(s), Compress: true);

    private static string VehiclesMeta(string model, string? audio) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<CVehicleModelInfo__InitDataList>\r\n  <residentTxd>vehshare</residentTxd>\r\n  <InitDatas>\r\n    <Item>\r\n"
        + $"      <modelName>{model}</modelName>\r\n      <txdName>{model}</txdName>\r\n      <handlingId>{model.ToUpperInvariant()}</handlingId>\r\n"
        + (audio is null ? "" : $"      <audioNameHash>{audio}</audioNameHash>\r\n")
        + "      <type>VEHICLE_TYPE_CAR</type>\r\n    </Item>\r\n  </InitDatas>\r\n</CVehicleModelInfo__InitDataList>\r\n";

    /// <summary>A car pack laid out like the common ones: data\*.meta, models in a nested x64\vehicles.rpf.</summary>
    private static byte[] CarPack(string pack, string model, string? audio, string metaDir = "data")
    {
        var models = new MemoryStream();
        new Rpf7Builder()
            .AddFile(model + ".yft", new RpfBinarySource([1, 2, 3]))
            .AddFile(model + "_hi.yft", new RpfBinarySource([1, 2, 3]))
            .AddFile(model + ".ytd", new RpfBinarySource([4, 5]))
            .Write(models);
        var ms = new MemoryStream();
        new Rpf7Builder()
            .AddFile(
                "setup2.xml",
                Text($"<SSetupData><deviceName>dlc_{pack}</deviceName><nameHash>{pack}</nameHash></SSetupData>")
            )
            .AddFile(
                "content.xml",
                Text(
                    "<CDataFileMgr__ContentsOfDataFileXml><dataFiles><Item><fileType>VEHICLE_METADATA_FILE</fileType></Item></dataFiles></CDataFileMgr__ContentsOfDataFileXml>"
                )
            )
            .AddFile(metaDir + @"\vehicles.meta", Text(VehiclesMeta(model, audio)))
            .AddFile(metaDir + @"\handling.meta", Text("<CHandlingDataMgr />"))
            .AddFile(metaDir + @"\carvariations.meta", Text("<CVehicleModelInfoVariation />"))
            .AddFile(@"x64\vehicles.rpf", new RpfBinarySource(models.ToArray()))
            .Write(ms);
        return ms.ToArray();
    }

    /// <summary>A *_game.dat151.rel with a vehicle object (type 3) and an engine (type 4) and no name table.</summary>
    private static byte[] GameRel(params (uint Hash, byte Type)[] items)
    {
        var data = new byte[items.Length * 8];
        for (int i = 0; i < items.Length; i++)
            data[i * 8] = items[i].Type;
        var rel = new MemoryStream();
        var w = new BinaryWriter(rel);
        w.Write(151u);
        w.Write((uint)data.Length);
        w.Write(data);
        w.Write(4u); // name table: only its count
        w.Write(0u);
        w.Write((uint)items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            w.Write(items[i].Hash);
            w.Write((uint)(i * 8));
            w.Write(8u);
        }
        return rel.ToArray();
    }

    /// <summary>An engine sound pack: x64\audio\&lt;pack&gt;_game.dat151.rel and the wave bank in sfx\dlc_&lt;pack&gt;.</summary>
    private static byte[] SoundPack(string pack, string sound, bool nameTable = false)
    {
        var builder = new Rpf7Builder()
            .AddFile(
                "setup2.xml",
                Text($"<SSetupData><deviceName>dlc_{pack}</deviceName><nameHash>{pack}</nameHash></SSetupData>")
            )
            .AddFile(
                "content.xml",
                Text(
                    "<CDataFileMgr__ContentsOfDataFileXml><dataFiles><Item><fileType>AUDIO_GAMEDATA</fileType></Item></dataFiles></CDataFileMgr__ContentsOfDataFileXml>"
                )
            )
            .AddFile(
                $@"x64\audio\{pack}_game.dat151.rel",
                new RpfBinarySource(
                    GameRel((DlcInspector.Joaat(sound), 3), (DlcInspector.Joaat(sound + "_engine"), 4)),
                    Compress: true
                )
            )
            .AddFile($@"x64\audio\sfx\dlc_{pack}\{pack}.awc", new RpfBinarySource(new byte[64]));
        if (nameTable)
            builder.AddFile(
                $@"x64\audio\{pack}_game.dat151.nametable",
                new RpfBinarySource(
                    Encoding.ASCII.GetBytes(sound.ToUpperInvariant() + "\0" + sound.ToUpperInvariant() + "_ENGINE\0")
                )
            );
        var ms = new MemoryStream();
        builder.Write(ms);
        return ms.ToArray();
    }

    /// <summary>An unpacked mod folder as downloaded: a top folder, the pack folder inside, no README.</summary>
    private string Folder(string top, string pack, byte[] dlc)
    {
        var dir = Path.Combine(_root, "drop", top);
        Directory.CreateDirectory(Path.Combine(dir, pack));
        File.WriteAllBytes(Path.Combine(dir, pack, "dlc.rpf"), dlc);
        return dir;
    }

    private string Install(string folder)
    {
        var g = OpenGame();
        using var archive = ModArchive.Open(folder);
        var an = ModAnalyzer.Analyze(archive, g);
        Assert.Empty(an.Errors);
        var installer = new OivInstaller(g);
        using var plan = installer.Prepare(an.Title, an.Ops, archive);
        Assert.Empty(plan.Errors);
        var r = installer.Install(plan, Lib);
        Assert.Equal(TxState.Completed, r.State);
        return r.Id;
    }

    private string DlcList()
    {
        using var a = Rpf7Archive.OpenRead(Path.Combine(Game, "mods", "update", "update.rpf"));
        return Encoding.UTF8.GetString(a.ReadContent((RpfBinaryFile)a.Find(@"common\data\dlclist.xml")!));
    }

    private string InstalledMeta(string pack, string metaDir = "data")
    {
        using var a = Rpf7Archive.OpenRead(Path.Combine(Game, "mods", "update", "x64", "dlcpacks", pack, "dlc.rpf"));
        return Encoding.UTF8.GetString(a.ReadContent((RpfBinaryFile)a.Find(metaDir + @"\vehicles.meta")!));
    }

    [Fact]
    public void Joaat_matches_the_game_hash_of_a_real_engine_sound()
    {
        // The vehicle object of a real engine sound pack ("a80ffeng") is stored under this hash.
        Assert.Equal(0x2513D75Du, DlcInspector.Joaat("a80ffeng"));
        Assert.Equal(DlcInspector.Joaat("A80FFENG"), DlcInspector.Joaat("a80ffeng"));
    }

    [Fact]
    public void Car_folder_without_readme_is_recognized_and_registered_in_dlclist()
    {
        var folder = Folder("Seraph_XS_1.1", "stew_seraphxs", CarPack("stew_seraphxs", "seraph3", "SULTAN"));
        using (var archive = ModArchive.Open(folder))
        {
            Assert.True(archive.IsFolder);
            var an = ModAnalyzer.Analyze(archive, OpenGame());
            Assert.Equal("Seraph_XS_1.1", an.Title);
            Assert.Empty(an.Errors);
            Assert.Equal(ModAnalyzer.KindLabel(DlcPackKind.Vehicles), an.Summary);
            var pack = Assert.Single(an.DlcPacks);
            Assert.Equal(DlcPackKind.Vehicles, pack.Kind);
            var car = Assert.Single(pack.Vehicles);
            Assert.Equal("seraph3", car.Model);
            Assert.Equal("SULTAN", car.EffectiveAudio);
            Assert.Contains(an.Notes, n => n.Contains("seraph3"));
        }
        Install(folder);
        Assert.True(File.Exists(Path.Combine(Game, "mods", "update", "x64", "dlcpacks", "stew_seraphxs", "dlc.rpf")));
        Assert.Contains("dlcpacks:/stew_seraphxs/", DlcList());
    }

    [Fact]
    public void Car_models_come_from_vehicles_rpf_when_no_meta_is_readable()
    {
        var models = new MemoryStream();
        new Rpf7Builder()
            .AddFile("kanjo.yft", new RpfBinarySource([1]))
            .AddFile("kanjo_hi.yft", new RpfBinarySource([1]))
            .Write(models);
        var ms = new MemoryStream();
        new Rpf7Builder()
            .AddFile("setup2.xml", Text("<SSetupData><nameHash>kanjo</nameHash></SSetupData>"))
            .AddFile(@"x64\vehicles.rpf", new RpfBinarySource(models.ToArray()))
            .Write(ms);
        ms.Position = 0;
        var content = DlcInspector.Inspect(ms, "kanjo");
        Assert.Equal(DlcPackKind.Vehicles, content.Kind);
        var car = Assert.Single(content.Vehicles);
        Assert.Equal("kanjo", car.Model);
        Assert.False(car.CanChangeSound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Engine_sound_folder_is_recognized_with_its_sound_name(bool nameTable)
    {
        // Without a name table the name is found by hash among the pack, file and bank names; with one, from the table.
        string sound = nameTable ? "myv8" : "a80ffeng";
        var folder = Folder("A80 Engine Sound", "a80ffeng", SoundPack("a80ffeng", sound, nameTable));
        using var archive = ModArchive.Open(folder);
        var an = ModAnalyzer.Analyze(archive, OpenGame());
        Assert.Empty(an.Errors);
        Assert.Equal(ModAnalyzer.KindLabel(DlcPackKind.EngineSounds), an.Summary);
        var pack = Assert.Single(an.DlcPacks);
        var engine = Assert.Single(pack.EngineSounds);
        Assert.True(engine.NameKnown);
        Assert.Equal(sound, engine.Name);
        Assert.Contains(an.Ops.OfType<OivArchive>(), o => o.Path == @"update\update.rpf");
    }

    [Theory]
    [InlineData(true, "data")]
    [InlineData(false, @"common\data\levels\gta5")]
    public void Sound_is_given_to_a_car_shown_on_its_card_and_restored(bool hasAudioTag, string metaDir)
    {
        var carId = Install(
            Folder(
                "Nightwalker Jester",
                "nw_jester",
                CarPack("nw_jester", "nwjester", hasAudioTag ? "JESTER" : null, metaDir)
            )
        );
        Install(Folder("A80 Engine Sound", "a80ffeng", SoundPack("a80ffeng", "a80ffeng")));
        var before = InstalledMeta("nw_jester", metaDir);
        var g = OpenGame();

        var packs = EngineSounds.Packs(g);
        Assert.Contains(EngineSounds.Choices(packs), c => c.Name == "a80ffeng" && c.Source == DlcPackKind.EngineSounds);
        var target = Assert.Single(EngineSounds.Targets(packs));
        var result = EngineSounds.Assign(
            g,
            Lib,
            new()
            {
                ArchiveRel = target.ArchiveRel,
                Model = "nwjester",
                Sound = "a80ffeng",
            }
        );
        Assert.True(result.Success, result.Message);
        var meta = InstalledMeta("nw_jester", metaDir);
        Assert.Contains("<audioNameHash>a80ffeng</audioNameHash>", meta);
        Assert.DoesNotContain("JESTER</audioNameHash>", meta);
        Assert.Equal("a80ffeng", Assert.Single(EngineSounds.Targets(EngineSounds.Packs(g))).Vehicle.EffectiveAudio);

        // My Mods: the change sits on the car's card, the car keeps its pack, sections come from the pack contents.
        var report = InstalledModList.Read(g, new ModLabels(Path.Combine(_root, "labels.json")), Lib);
        var car = report.Groups.Single(x => x.Name == "Nightwalker Jester");
        var engine = report.Groups.Single(x => x.Name == "A80 Engine Sound");
        Assert.Equal(2, report.Groups.Count);
        Assert.Equal(ModCategory.Vehicles, car.Category);
        Assert.Equal(ModCategory.Sounds, engine.Category);
        Assert.Contains(car.Files, f => f.Rel.EndsWith(@"nw_jester\dlc.rpf", StringComparison.OrdinalIgnoreCase));
        var change = Assert.Single(car.SoundAssignments);
        Assert.Equal(("nwjester", "a80ffeng"), (change.Model, change.Sound));
        Assert.Contains(change.Id, car.Operations);
        Assert.Contains(carId, car.Operations);

        // "Restore original" undoes the sound change only; the car stays installed.
        var undo = ModRemoval.Remove(g, Lib, new() { Name = "sound", Operations = [change.Id] });
        Assert.True(undo.Success, undo.Message);
        Assert.Equal(before, InstalledMeta("nw_jester", metaDir));
        Assert.Contains("dlcpacks:/nw_jester/", DlcList());
        Assert.Empty(
            InstalledModList
                .Read(g, new ModLabels(Path.Combine(_root, "labels.json")), Lib)
                .Groups.Single(x => x.Name == "Nightwalker Jester")
                .SoundAssignments
        );
    }

    [Fact]
    public void Several_cars_get_a_sound_at_once_and_one_goes_back_to_its_recorded_original()
    {
        Install(Folder("Jester", "nw_jester", CarPack("nw_jester", "nwjester", "JESTER")));
        Install(Folder("Elegy", "nw_elegy", CarPack("nw_elegy", "nwelegy", null, "data")));
        Install(Folder("A80 Engine Sound", "a80ffeng", SoundPack("a80ffeng", "a80ffeng")));
        var g = OpenGame();
        var targets = EngineSounds.Targets(EngineSounds.Packs(g));
        Assert.Equal(2, targets.Count);

        var batch = new EngineSoundBatchRequest
        {
            Assign =
            [
                .. targets.Select(t => new EngineSoundRequest
                {
                    ArchiveRel = t.ArchiveRel,
                    Model = t.Vehicle.Model,
                    Sound = "a80ffeng",
                }),
            ],
        };
        var result = EngineSounds.AssignMany(g, Lib, batch);
        Assert.True(result.Success, string.Join("\n", result.Messages));
        Assert.All(EngineSounds.Targets(EngineSounds.Packs(g)), t => Assert.Equal("a80ffeng", t.Vehicle.EffectiveAudio));

        // The sounds before the first change are remembered per car (no audio tag: the model name plays).
        var originals = EngineSounds.LoadOriginals(g);
        var jester = EngineSounds.Targets(EngineSounds.Packs(g)).Single(t => t.Vehicle.Model == "nwjester");
        var elegy = EngineSounds.Targets(EngineSounds.Packs(g)).Single(t => t.Vehicle.Model == "nwelegy");
        Assert.Equal("JESTER", EngineSounds.Original(g, originals, jester));
        Assert.Equal("nwelegy", EngineSounds.Original(g, originals, elegy));

        // Back to the original for one car only: the other keeps its new sound.
        var back = EngineSounds.AssignMany(
            g,
            Lib,
            new EngineSoundBatchRequest
            {
                Assign = [new() { ArchiveRel = jester.ArchiveRel, Model = "nwjester", Sound = "JESTER" }],
            }
        );
        Assert.True(back.Success, string.Join("\n", back.Messages));
        var now = EngineSounds.Targets(EngineSounds.Packs(g)).ToDictionary(t => t.Vehicle.Model, t => t.Vehicle.EffectiveAudio);
        Assert.Equal("JESTER", now["nwjester"]);
        Assert.Equal("a80ffeng", now["nwelegy"]);
        // A second change keeps the first recorded original.
        Assert.Equal("JESTER", EngineSounds.Original(g, EngineSounds.LoadOriginals(g), jester));
    }

    [Fact]
    public void Removing_the_car_also_undoes_its_sound_change()
    {
        Install(Folder("Jester", "nw_jester", CarPack("nw_jester", "nwjester", "JESTER")));
        var g = OpenGame();
        var target = Assert.Single(EngineSounds.Targets(EngineSounds.Packs(g)));
        Assert.True(
            EngineSounds
                .Assign(
                    g,
                    Lib,
                    new()
                    {
                        ArchiveRel = target.ArchiveRel,
                        Model = "nwjester",
                        Sound = "elegy",
                    }
                )
                .Success
        );
        var car = Assert.Single(
            InstalledModList.Read(g, new ModLabels(Path.Combine(_root, "labels.json")), Lib).Groups
        );
        Assert.Equal(2, car.Operations.Count);
        var removed = ModRemoval.Remove(g, Lib, new() { Name = car.Name, Operations = [.. car.Operations] });
        Assert.True(removed.Success, removed.Message);
        Assert.False(Directory.Exists(Path.Combine(Game, "mods", "update", "x64", "dlcpacks", "nw_jester")));
        Assert.DoesNotContain("nw_jester", DlcList());
    }

    private string StockMeta(string inner)
    {
        using var a = Rpf7Archive.OpenRead(Path.Combine(Game, "mods", "update", "update.rpf"));
        return Encoding.UTF8.GetString(a.ReadContent((RpfBinaryFile)a.Find(inner)!));
    }

    [Fact]
    public void Stock_cars_get_a_sound_through_update_rpf_on_their_own_card_and_it_is_undone()
    {
        Install(Folder("A80 Engine Sound", "a80ffeng", SoundPack("a80ffeng", "a80ffeng")));
        var g = OpenGame();
        var stock = EngineSounds.StockTargets(g);
        Assert.Equal(["elegy2", "toreador"], stock.Select(t => t.Vehicle.Model));
        Assert.All(stock, t => Assert.True(t.Stock));
        Assert.Equal("mpheist4", stock[1].Pack);
        const string patch = @"dlc_patch\mpheist4\common\data\levels\gta5\vehicles.meta";
        var before = StockMeta(patch);

        var result = EngineSounds.Assign(
            g,
            Lib,
            new()
            {
                ArchiveRel = EngineSounds.StockArchive,
                Model = "toreador",
                Sound = "a80ffeng",
            }
        );
        Assert.True(result.Success, result.Message);
        Assert.Contains("<audioNameHash>a80ffeng</audioNameHash>", StockMeta(patch));
        Assert.Contains("ELEGY2", StockMeta(@"common\data\levels\gta5\vehicles.meta"));
        Assert.Contains("dlcpacks:/a80ffeng/", DlcList());

        var report = InstalledModList.Read(g, new ModLabels(Path.Combine(_root, "labels.json")), Lib);
        var sounds = report.Groups.Single(x => x.Name == InstalledModList.EngineSoundsName);
        Assert.Equal(ModCategory.Sounds, sounds.Category);
        Assert.Equal(("toreador", "a80ffeng"), (sounds.SoundAssignments[0].Model, sounds.SoundAssignments[0].Sound));
        // The sound pack's card keeps only its own installation.
        Assert.Single(report.Groups.Single(x => x.Name == "A80 Engine Sound").Operations);

        var undo = ModRemoval.Remove(g, Lib, new() { Name = sounds.Name, Operations = [.. sounds.Operations] });
        Assert.True(undo.Success, undo.Message);
        Assert.Equal(before, StockMeta(patch));
        Assert.Contains("dlcpacks:/a80ffeng/", DlcList());
        Assert.DoesNotContain(
            InstalledModList.Read(g, new ModLabels(Path.Combine(_root, "labels.json")), Lib).Groups,
            x => x.Name == InstalledModList.EngineSoundsName
        );
    }

    [Theory]
    [InlineData("a80ffeng\"]|//x")]
    [InlineData("")]
    [InlineData("two words")]
    public void Sound_names_that_could_break_the_meta_are_refused(string sound)
    {
        Assert.False(EngineSounds.ValidName(sound));
        var target = new SoundTarget(
            "p",
            @"mods\update\x64\dlcpacks\p\dlc.rpf",
            new DlcVehicle("car", "", "", [@"data\vehicles.meta"])
        );
        Assert.Throws<ArgumentException>(() => EngineSounds.Ops(target, sound));
    }

    [Fact]
    public void Sound_change_titles_are_recognized_in_every_language()
    {
        foreach (
            var lang in new[]
            {
                "Звук двигателя: nwjester → a80ffeng",
                "Engine sound: nwjester → a80ffeng",
                "Звук двигуна: nwjester → a80ffeng",
            }
        )
            Assert.Equal(("nwjester", "a80ffeng"), EngineSounds.Parse(lang));
        Assert.Null(EngineSounds.Parse("Seraph XS"));
    }

    [Fact]
    public void Replace_backup_fivem_and_handling_folders_beside_an_addon_pack_are_not_installed()
    {
        var dir = Folder(@"Elegy Drift Missile\Add-On", "Elegy_DM", CarPack("Elegy_DM", "elegy3", "elegy"));
        var top = Path.GetDirectoryName(dir)!;
        foreach (
            var (folder, file) in new[]
            {
                ("Replace", "elegy.yft"),
                ("Back Up", "elegy.yft"),
                (@"FiveM\elegy_dm\stream", "elegy3.yft"),
                (@"Different Handling\Drift Handling", "handling.meta"),
                (@"Different Handling\Race Handling", "handling.meta"),
            }
        )
        {
            Directory.CreateDirectory(Path.Combine(top, folder));
            File.WriteAllText(Path.Combine(top, folder, file), "x");
        }
        using var archive = ModArchive.Open(top);
        var an = ModAnalyzer.Analyze(archive, OpenGame());
        Assert.Empty(an.Errors);
        Assert.Empty(an.PendingChoices);
        Assert.Equal(ModAnalyzer.KindLabel(DlcPackKind.Vehicles), an.Summary);
        var part = Assert.Single(an.Parts);
        Assert.Equal(@"mods\update\x64\dlcpacks\Elegy_DM\dlc.rpf", part.Target);
    }

    private static byte[] MapPack(string pack)
    {
        var ms = new MemoryStream();
        new Rpf7Builder()
            .AddFile("setup2.xml", Text($"<SSetupData><nameHash>{pack}</nameHash></SSetupData>"))
            .AddFile(@"x64\levels\gta5\town.ymap", new RpfBinarySource([1, 2, 3]))
            .Write(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Dropped_batch_puts_the_prevailing_kind_first_and_other_packs_after_it()
    {
        // The drop order mixes a sound pack and a map in between cars.
        var paths = new[]
        {
            Folder("Engine A80", "a80ffeng", SoundPack("a80ffeng", "a80ffeng")),
            Folder("Seraph", "stew_seraphxs", CarPack("stew_seraphxs", "seraph3", "SULTAN")),
            Folder("Chiliad Town", "town", MapPack("town")),
            Folder("Virgo", "virgore", CarPack("virgore", "virgore", "VIRGO3")),
            Folder("Type 1", "type1", CarPack("type1", "type1", null)),
        };
        var plan = Core.Installation.DropBatch.Plan(paths, OpenGame());
        Assert.Equal(Core.Installation.BatchKind.Vehicles, plan.Main);
        Assert.Equal(3, plan.MainCount);
        Assert.Equal(["Seraph", "Virgo", "Type 1", "Engine A80", "Chiliad Town"], plan.Items.Select(i => i.Title));
        Assert.Equal([false, false, false, true, true], plan.Items.Select(i => i.Moved));
        Assert.All(plan.Items, i => Assert.Null(i.Problem));
        Assert.All(plan.Items, i => Assert.Equal("mod", i.Verb));

        // Every item installs on its own: cars, then the sound pack, then the map, each in dlclist.
        foreach (var item in plan.Items)
            Install(item.Path);
        foreach (var pack in new[] { "stew_seraphxs", "virgore", "type1", "a80ffeng", "town" })
            Assert.Contains($"dlcpacks:/{pack}/", DlcList());
    }

    [Fact]
    public void Batch_items_that_need_a_choice_are_flagged_not_guessed()
    {
        var variants = Path.Combine(_root, "drop", "Two versions");
        foreach (var v in new[] { "Version 1", "Version 2" })
        {
            Directory.CreateDirectory(Path.Combine(variants, v, "scripts"));
            File.WriteAllText(Path.Combine(variants, v, "scripts", "mod.dll"), v);
        }
        var plan = Core.Installation.DropBatch.Plan(
            [variants, Folder("Seraph", "stew_seraphxs", CarPack("stew_seraphxs", "seraph3", "SULTAN"))],
            OpenGame()
        );
        Assert.Equal("Seraph", plan.Items[0].Title);
        Assert.NotNull(plan.Items.Single(i => i.Title == "Two versions").Problem);
    }

    /// <summary>A queue worker on a background thread that installs and undoes the way the elevated one does.</summary>
    private (Task<int> Worker, string Dir) StartQueue(params string[] paths)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "queue-" + Guid.NewGuid().ToString("N"))).FullName;
        var request = new Core.Installation.BatchQueueRequest
        {
            ControlDir = dir,
            Items = paths.Select(p => new Core.Installation.BatchQueueEntry { Path = p }).ToList(),
        };
        var worker = Task.Run(() =>
            Core.Installation.BatchChannel.Serve(
                request,
                entry =>
                {
                    var id = Install(entry.Path);
                    return (true, "Completed", (IReadOnlyList<string>)[id]);
                },
                made =>
                {
                    var r = ModRemoval.Remove(OpenGame(), Lib, new() { Name = "queue", Operations = [.. made] });
                    return (r.Success, r.Message);
                },
                idle: TimeSpan.FromSeconds(20),
                poll: TimeSpan.FromMilliseconds(20)
            )
        );
        return (worker, dir);
    }

    [Fact]
    public async Task Queue_cancel_undoes_the_mod_just_installed_and_keeps_the_earlier_ones()
    {
        var (worker, dir) = StartQueue(
            Folder("Seraph", "stew_seraphxs", CarPack("stew_seraphxs", "seraph3", "SULTAN")),
            Folder("Virgo", "virgore", CarPack("virgore", "virgore", "VIRGO3"))
        );
        Func<bool> alive = () => !worker.IsCompleted;
        Assert.True((await Core.Installation.BatchChannel.SendAsync(dir, 1, "install 0", alive))!.Ok);
        Assert.True((await Core.Installation.BatchChannel.SendAsync(dir, 2, "install 1", alive))!.Ok);
        Assert.Contains("dlcpacks:/virgore/", DlcList());

        // "Cancel" while Virgo was installing: it finished, then is undone; Seraph stays.
        var undo = await Core.Installation.BatchChannel.SendAsync(dir, 3, "undo 1", alive);
        Assert.True(undo!.Ok, undo.Message);
        Assert.DoesNotContain("virgore", DlcList());
        Assert.False(Directory.Exists(Path.Combine(Game, "mods", "update", "x64", "dlcpacks", "virgore")));
        Assert.Contains("dlcpacks:/stew_seraphxs/", DlcList());
        Assert.True(File.Exists(Path.Combine(Game, "mods", "update", "x64", "dlcpacks", "stew_seraphxs", "dlc.rpf")));

        await Core.Installation.BatchChannel.SendAsync(dir, 4, "stop", alive);
        Assert.Equal(0, await worker);
    }

    [Theory]
    [InlineData("install 0", "install 0")] // the same entry twice
    [InlineData("install 0", "undo 1")] // undo of something this worker did not install
    [InlineData("install 0", "install 5")] // not in the approved list
    [InlineData("install 0", @"install C:\Windows\evil.zip")]
    public async Task Queue_worker_refuses_commands_outside_the_approved_list(string first, string second)
    {
        var (worker, dir) = StartQueue(
            Folder("Seraph", "stew_seraphxs", CarPack("stew_seraphxs", "seraph3", "SULTAN")),
            Folder("Virgo", "virgore", CarPack("virgore", "virgore", "VIRGO3"))
        );
        Func<bool> alive = () => !worker.IsCompleted;
        Assert.True((await Core.Installation.BatchChannel.SendAsync(dir, 1, first, alive))!.Ok);
        var refused = await Core.Installation.BatchChannel.SendAsync(dir, 2, second, alive);
        Assert.False(refused!.Ok);
        Assert.Equal(1, await worker);
        Assert.DoesNotContain("virgore", DlcList());
    }

    [Fact]
    public void Folder_mode_keeps_the_folder_name_and_skips_junk()
    {
        var dir = Path.Combine(_root, "drop", "virgore");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "dlc.rpf"), CarPack("virgore", "virgore", "VIRGO3"));
        File.WriteAllText(Path.Combine(dir, "Thumbs.db"), "x");
        using var archive = ModArchive.Open(dir);
        Assert.Equal([@"virgore\dlc.rpf"], archive.Files);
        var an = ModAnalyzer.Analyze(archive, OpenGame());
        Assert.Contains(an.Parts, p => p.Target == @"mods\update\x64\dlcpacks\virgore\dlc.rpf");
    }

    [Theory]
    [InlineData(ModCategory.Scripts, @"scripts\Trainer.dll")]
    [InlineData(ModCategory.Scripts, "NativeTrainer.asi")]
    [InlineData(
        ModCategory.Accessories,
        @"mods\x64v.rpf\models\cdimages\streamedpeds_mp.rpf\mp_m_freemode_01\jbib_003_u.ydd"
    )]
    [InlineData(
        ModCategory.Characters,
        @"mods\x64v.rpf\models\cdimages\streamedpeds_players.rpf\player_zero\player_zero.yft"
    )]
    [InlineData(ModCategory.Graphics, "reshade.ini")]
    [InlineData(ModCategory.Sounds, @"mods\x64\audio\sfx\RESIDENT.rpf\vehicles.awc")]
    [InlineData(ModCategory.Other, "readme_mod.txt")]
    public void Mods_are_sorted_into_sections_by_their_files(ModCategory expected, string file)
    {
        Assert.Equal(expected, ModCategories.Classify("Some mod", [file], []));
    }

    [Fact]
    public void Pack_contents_decide_the_section_before_helper_files()
    {
        var car = new DlcContent { Pack = "car" };
        car.Vehicles.Add(new("car", "", "", [@"data\vehicles.meta"]));
        var map = new DlcContent { Pack = "town", HasMapData = true };
        Assert.Equal(ModCategory.Vehicles, ModCategories.Classify("Car", [@"scripts\helper.dll"], [car]));
        Assert.Equal(ModCategory.Maps, ModCategories.Classify("Town", [], [map]));
        Assert.Equal(ModCategory.Graphics, ModCategories.Classify("NaturalVision Evolved", [], []));
        Assert.Equal(ModCategory.Maps, ModCategories.Classify("Liberty City Preservation Project", [], []));
        Assert.Equal(ModCategory.Characters, ModCategories.Classify(InstalledModList.PedSelectorName, [], []));
    }
}
