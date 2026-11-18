using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

/// <summary>
/// onigiri (NaturalVision Enhanced) never reads mods\: what mods change inside the game archives must also be put
/// loose into onigiri\common / onigiri\platform, recomputed on every installation and undo.
/// </summary>
public sealed class OnigiriLayerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "gmm-onigiri-layer-" + Guid.NewGuid().ToString("N")
    );
    private string Game => Path.Combine(_root, "GTA V Enhanced");
    private BackupLibrary? _lib;
    private BackupLibrary Lib => _lib ??= BackupLibrary.Create(Path.Combine(_root, "backups"), 1L << 30);

    private const string DlcList =
        "<SMandatoryPacksData><Paths><Item>dlcpacks:/mpluxe/</Item></Paths></SMandatoryPacksData>";
    private const string GameConfig =
        "<fwAllConfigs><ConfigArray><Item><Config type=\"CGameConfig\"><PoolSizes><Entries><Item><PoolName>A</PoolName><PoolSize value=\"1\" /></Item></Entries></PoolSizes></Config></Item></ConfigArray></fwAllConfigs>";

    public OnigiriLayerTests()
    {
        Directory.CreateDirectory(Path.Combine(Game, "update", "x64", "dlcpacks", "mpluxe"));
        File.WriteAllText(Path.Combine(Game, "GTA5_Enhanced.exe"), "stub");
        File.WriteAllText(Path.Combine(Game, "OpenRPF.asi"), "MZ stub loader");
        using var fs = File.Create(Path.Combine(Game, "update", "update.rpf"));
        new Rpf7Builder()
            .AddFile(@"common\data\dlclist.xml", Text(DlcList, compress: true))
            .AddFile(@"common\data\gameconfig.xml", Text(GameConfig, compress: true))
            .AddFile(@"x64\data\handling.meta", Text("original handling", compress: true))
            .AddFile(@"x64\data\carcols.ymt", Text("original carcols"))
            .AddFile(@"x64\levels\v.rpf", new RpfBinarySource(Nested(("base.txt", "base"))))
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

    private static RpfBinarySource Text(string text, bool compress = false) =>
        new(Encoding.UTF8.GetBytes(text), compress);

    private static byte[] Nested(params (string Name, string Text)[] files)
    {
        var builder = new Rpf7Builder();
        foreach (var (name, text) in files)
            builder.AddFile(name, Text(text));
        var ms = new MemoryStream();
        builder.Write(ms);
        return ms.ToArray();
    }

    private void InstallOnigiriByHand()
    {
        File.WriteAllText(Path.Combine(Game, "onigiri.asi"), "MZ stub");
        var data = Path.Combine(Game, "onigiri", "common", "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "dlclist.xml"), DlcList);
        File.WriteAllText(Path.Combine(data, "gameconfig.xml"), GameConfig);
    }

    private GameInstall OpenGame()
    {
        var g = GameInstall.Open(Game);
        g.RunningCheckOverride = () => false;
        return g;
    }

    private string Oiv(string content, Dictionary<string, string> files)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".oiv");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("assembly.xml").Open()))
            w.Write(
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?><package version=\"2.2\" target=\"Five\"><metadata><name>Test</name></metadata><content>{content}</content></package>"
            );
        foreach (var (name, text) in files)
            using (var s = zip.CreateEntry("content/" + name).Open())
                s.Write(Encoding.UTF8.GetBytes(text));
        return path;
    }

    private static string InUpdate(string ops) =>
        $"""<archive path="update\update.rpf" createIfNotExist="False" type="RPF7">{ops}</archive>""";

    private (string Id, InstallPlan Plan) Install(string oiv)
    {
        var g = OpenGame();
        using var pkg = OivPackage.Open(oiv);
        var plan = new OivInstaller(g).Prepare(pkg);
        Assert.Empty(plan.Errors);
        var result = new OivInstaller(g).Install(plan, Lib);
        Assert.Equal(TxState.Completed, result.State);
        plan.Dispose();
        return (result.Id, plan);
    }

    private void Undo(string id) => Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(id, Lib).State);

    private string Layer(string rel) => Path.Combine(Game, "onigiri", rel);

    private static string[] NestedNames(string path)
    {
        using var archive = Rpf7Archive.OpenRead(path);
        return archive.Files.Select(f => f.Path).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void ModAfterNve_FilesInUpdateRpf_AreCopiedToOnigiri_AndUndoRemovesThem()
    {
        InstallOnigiriByHand();
        var (id, plan) = Install(
            Oiv(
                InUpdate(
                    """<add source="h.meta">x64\data\handling.meta</add><add source="foo.xml">common\data\foo.xml</add>"""
                ),
                new() { ["h.meta"] = "mod handling", ["foo.xml"] = "<foo/>" }
            )
        );

        Assert.Equal("mod handling", File.ReadAllText(Layer(@"platform\data\handling.meta")));
        Assert.Equal("<foo/>", File.ReadAllText(Layer(@"common\data\foo.xml")));
        Assert.Contains(plan.Items, i => i.Action == "onigiri" && i.Target == @"onigiri\platform\data\handling.meta");
        // Untouched game files and the shared documents are not copied.
        Assert.False(File.Exists(Layer(@"platform\data\carcols.ymt")));
        Assert.Equal(DlcList, File.ReadAllText(Layer(@"common\data\dlclist.xml")));

        Undo(id);

        Assert.False(File.Exists(Layer(@"platform\data\handling.meta")));
        Assert.False(File.Exists(Layer(@"common\data\foo.xml")));
        Assert.Equal(DlcList, File.ReadAllText(Layer(@"common\data\dlclist.xml")));
        Assert.True(File.Exists(Path.Combine(Game, @"ModHubState\onigiri-layer.json")));
    }

    [Fact]
    public void TwoModsInOneNestedArchive_UndoingFirstKeepsSecondInTheCopy()
    {
        InstallOnigiriByHand();
        var (first, _) = Install(
            Oiv(InUpdate("""<add source="a.txt">x64\levels\v.rpf\a.txt</add>"""), new() { ["a.txt"] = "A" })
        );
        Install(Oiv(InUpdate("""<add source="b.txt">x64\levels\v.rpf\b.txt</add>"""), new() { ["b.txt"] = "B" }));

        var copy = Layer(@"platform\levels\v.rpf");
        Assert.Equal(["a.txt", "b.txt", "base.txt"], NestedNames(copy));

        Undo(first);

        Assert.Equal(["b.txt", "base.txt"], NestedNames(copy));
    }

    [Fact]
    public void NveOwnFile_IsNeverOverwritten_AndThePlanSaysSo()
    {
        InstallOnigiriByHand();
        Directory.CreateDirectory(Layer(@"platform\data"));
        File.WriteAllText(Layer(@"platform\data\carcols.ymt"), "nve carcols");

        var (id, plan) = Install(
            Oiv(InUpdate("""<add source="c.ymt">x64\data\carcols.ymt</add>"""), new() { ["c.ymt"] = "mod carcols" })
        );

        Assert.Equal("nve carcols", File.ReadAllText(Layer(@"platform\data\carcols.ymt")));
        Assert.Contains(plan.Items, i => i.Action == "onigiri" && i.Target == @"onigiri\platform\data\carcols.ymt");

        Undo(id);
        Assert.Equal("nve carcols", File.ReadAllText(Layer(@"platform\data\carcols.ymt")));
    }

    [Fact]
    public void NveInstalledAfterMods_CopiesExistingModsContent_TakesOverLoader_AndUndoRestoresEverything()
    {
        Install(
            Oiv(
                InUpdate("""<add source="h.meta">x64\data\handling.meta</add>"""),
                new() { ["h.meta"] = "mod handling" }
            )
        );
        Assert.False(Directory.Exists(Path.Combine(Game, "onigiri")));
        Assert.Equal("OpenRPF.asi", OpenGame().CheckModLoader().Name);

        var (nve, plan) = Install(
            Oiv(
                """<add source="onigiri.asi">onigiri.asi</add><add source="dlclist.xml">onigiri\common\data\dlclist.xml</add>""",
                new() { ["onigiri.asi"] = "MZ onigiri", ["dlclist.xml"] = DlcList }
            )
        );

        Assert.Equal("mod handling", File.ReadAllText(Layer(@"platform\data\handling.meta")));
        Assert.False(File.Exists(Path.Combine(Game, "OpenRPF.asi")));
        Assert.Contains(plan.Items, i => i.Action == "compatibility" && i.Target == "OpenRPF.asi");
        var loader = OpenGame().CheckModLoader();
        Assert.True(loader.Installed);
        Assert.Equal("onigiri.asi", loader.Name);
        Assert.Null(loader.DisabledReason);

        Undo(nve);

        Assert.False(File.Exists(Layer(@"platform\data\handling.meta")));
        Assert.False(File.Exists(Path.Combine(Game, @"ModHubState\onigiri-layer.json")));
        Assert.True(File.Exists(Path.Combine(Game, "OpenRPF.asi")));
        Assert.Equal("OpenRPF.asi", OpenGame().CheckModLoader().Name);
        using var mods = Rpf7Archive.OpenRead(Path.Combine(Game, @"mods\update\update.rpf"));
        Assert.Equal(
            "mod handling",
            Encoding.UTF8.GetString(mods.ReadContent((RpfBinaryFile)mods.Find(@"x64\data\handling.meta")!))
        );
    }

    [Fact]
    public void GamePackReplacedInMods_IsCopiedWholeToOnigiriDlcpacks()
    {
        InstallOnigiriByHand();
        var pack = Path.Combine(Game, "update", "x64", "dlcpacks", "mpluxe", "dlc.rpf");
        File.WriteAllBytes(pack, Nested(("setup2.xml", "<SSetupData/>")));

        Install(
            Oiv(
                """<archive path="update\x64\dlcpacks\mpluxe\dlc.rpf" createIfNotExist="False" type="RPF7"><add source="x.txt">x.txt</add></archive>""",
                new() { ["x.txt"] = "X" }
            )
        );

        Assert.Equal(["setup2.xml", "x.txt"], NestedNames(Layer(@"dlcpacks\mpluxe\dlc.rpf")));
    }

    [Fact]
    public void WithoutOnigiri_NoLayerIsWritten()
    {
        Install(Oiv(InUpdate("""<add source="h.meta">x64\data\handling.meta</add>"""), new() { ["h.meta"] = "mod" }));
        Assert.False(Directory.Exists(Path.Combine(Game, "onigiri")));
        Assert.False(File.Exists(Path.Combine(Game, @"ModHubState\onigiri-layer.json")));
    }
}
