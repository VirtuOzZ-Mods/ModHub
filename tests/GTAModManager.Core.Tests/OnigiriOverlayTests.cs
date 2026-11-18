using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

/// <summary>
/// NaturalVision Enhanced (onigiri) replaces common\data\dlclist.xml and gameconfig.xml with its own copies:
/// add-on DLC registration and raised pools must go there too (the N.O.S. "flame and sound banks not found" case).
/// </summary>
public sealed partial class OnigiriOverlayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-onigiri-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "GTA V Enhanced");
    private BackupLibrary? _lib;
    private BackupLibrary Lib => _lib ??= BackupLibrary.Create(Path.Combine(_root, "backups"), 1L << 30);

    private const string UpdateDlcList =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SMandatoryPacksData>\r\n\t<Paths>\r\n\t\t<Item>dlcpacks:/mpluxe/</Item>\r\n\t</Paths>\r\n</SMandatoryPacksData>\r\n";
    private const string OnigiriDlcList =
        "<SMandatoryPacksData>\r\n<Paths>\r\n<Item>dlcpacks:/mpluxe/</Item>\r\n<Item>dlcpacks:/NVE_vehicles/</Item>\r\n</Paths>\r\n</SMandatoryPacksData>";

    private static string GameConfig(int ptfx) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<fwAllConfigs><ConfigArray><Item><Config type=\"CGameConfig\"><PoolSizes><Entries>\r\n"
        + $"\t<Item><PoolName>PtFxAssetStore</PoolName><PoolSize value=\"{ptfx}\" /></Item>\r\n\t<Item><PoolName>Other</PoolName><PoolSize value=\"10\" /></Item>\r\n"
        + "</Entries></PoolSizes></Config></Item></ConfigArray></fwAllConfigs>\r\n";

    private const string NosLike = """
        <add source="dlc.rpf">update\x64\dlcpacks\nos_overdrive\dlc.rpf</add>
        <archive path="update\update.rpf" createIfNotExist="False" type="RPF7">
          <xml path="common\data\dlclist.xml">
            <add append="Last" xpath="/SMandatoryPacksData/Paths"><Item>dlcpacks:/nos_overdrive/</Item></add>
          </xml>
          <xml path="common\data\gameconfig.xml">
            <add append="Last" xpath="//PoolSizes/Entries[Item/PoolName='PtFxAssetStore']"><Item><PoolName>PtFxAssetStore</PoolName><PoolSize value="800" /></Item></add>
            <remove xpath="//PoolSizes/Entries/Item[PoolName='PtFxAssetStore'][number(PoolSize/@value) &lt; 800]" />
          </xml>
        </archive>
        """;

    public OnigiriOverlayTests()
    {
        Directory.CreateDirectory(Game);
        File.WriteAllText(Path.Combine(Game, "GTA5_Enhanced.exe"), "stub");
        File.WriteAllText(Path.Combine(Game, "OpenRPF.asi"), "MZ stub loader");
        Directory.CreateDirectory(Path.Combine(Game, "mods", "update"));
        WriteUpdate(UpdateDlcList, GameConfig(400));
        Directory.CreateDirectory(Path.Combine(Game, "update", "x64", "dlcpacks", "mpluxe"));
        var data = Path.Combine(Game, "onigiri", "common", "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "dlclist.xml"), OnigiriDlcList);
        File.WriteAllText(Path.Combine(data, "gameconfig.xml"), GameConfig(500));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private void WriteUpdate(string dlclist, string gameconfig)
    {
        using var fs = File.Create(Path.Combine(Game, "mods", "update", "update.rpf"));
        new Rpf7Builder()
            .AddFile(@"common\data\dlclist.xml", new RpfBinarySource(Encoding.UTF8.GetBytes(dlclist), Compress: true))
            .AddFile(
                @"common\data\gameconfig.xml",
                new RpfBinarySource(Encoding.UTF8.GetBytes(gameconfig), Compress: true)
            )
            .Write(fs);
    }

    private GameInstall OpenGame()
    {
        var g = GameInstall.Open(Game);
        g.RunningCheckOverride = () => false;
        return g;
    }

    private string MakeOiv(string content, Dictionary<string, byte[]> files)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".oiv");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("assembly.xml").Open()))
            w.Write(
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?><package version=\"2.2\" target=\"Five\"><metadata><name>Test</name></metadata><content>{content}</content></package>"
            );
        foreach (var (name, data) in files)
            using (var s = zip.CreateEntry("content/" + name).Open())
                s.Write(data);
        return path;
    }

    private static byte[] DlcRpf()
    {
        var ms = new MemoryStream();
        new Rpf7Builder().AddFile("setup2.xml", new RpfBinarySource(Encoding.UTF8.GetBytes("<SSetupData/>"))).Write(ms);
        return ms.ToArray();
    }

    private string Onigiri(string name) => File.ReadAllText(Path.Combine(Game, "onigiri", "common", "data", name));

    private void Install(string oiv)
    {
        var g = OpenGame();
        using var pkg = OivPackage.Open(oiv);
        using var plan = new OivInstaller(g).Prepare(pkg);
        Assert.Empty(plan.Errors);
        Assert.Equal(TxState.Completed, new OivInstaller(g).Install(plan, Lib).State);
    }

    [Fact]
    public void ModInstalledAfterNve_RegistersPackAndRaisesPoolInOnigiriCopies()
    {
        Install(MakeOiv(NosLike, new() { ["dlc.rpf"] = DlcRpf() }));

        var dlc = Onigiri("dlclist.xml");
        Assert.Contains("dlcpacks:/nos_overdrive/", dlc);
        Assert.Contains("dlcpacks:/NVE_vehicles/", dlc); // NVE's own packs stay
        Assert.Contains("PoolSize value=\"800\"", Onigiri("gameconfig.xml"));
        Assert.DoesNotContain("PoolSize value=\"500\"", Onigiri("gameconfig.xml"));
    }

    [Fact]
    public void ReinstallOverAlreadyRegisteredUpdateRpf_StillRepairsOnigiriCopy()
    {
        // User state: update.rpf already has the entries, onigiri does not.
        WriteUpdate(
            UpdateDlcList.Replace("</Paths>", "\t<Item>dlcpacks:/nos_overdrive/</Item>\r\n\t</Paths>"),
            GameConfig(800)
        );
        Directory.CreateDirectory(Path.Combine(Game, "mods", "update", "x64", "dlcpacks", "nos_overdrive"));
        File.WriteAllBytes(
            Path.Combine(Game, "mods", "update", "x64", "dlcpacks", "nos_overdrive", "dlc.rpf"),
            DlcRpf()
        );

        Install(MakeOiv(NosLike, new() { ["dlc.rpf"] = DlcRpf() }));

        Assert.Contains("dlcpacks:/nos_overdrive/", Onigiri("dlclist.xml"));
        Assert.Contains("PoolSize value=\"800\"", Onigiri("gameconfig.xml"));
    }

    [Fact]
    public void NveInstalledAfterMods_KeepsRegisteredAddonPacks()
    {
        WriteUpdate(
            UpdateDlcList.Replace("</Paths>", "\t<Item>dlcpacks:/mycar/</Item>\r\n\t</Paths>"),
            GameConfig(400)
        );
        Directory.CreateDirectory(Path.Combine(Game, "mods", "update", "x64", "dlcpacks", "mycar"));
        File.WriteAllBytes(Path.Combine(Game, "mods", "update", "x64", "dlcpacks", "mycar", "dlc.rpf"), DlcRpf());
        Directory.Delete(Path.Combine(Game, "onigiri"), true);

        var nveList = Encoding.UTF8.GetBytes(OnigiriDlcList);
        Install(
            MakeOiv(
                """<add source="dlclist.xml">onigiri\common\data\dlclist.xml</add>""",
                new() { ["dlclist.xml"] = nveList }
            )
        );

        var dlc = Onigiri("dlclist.xml");
        Assert.Contains("dlcpacks:/mycar/", dlc);
        Assert.Contains("dlcpacks:/NVE_vehicles/", dlc);
        Assert.DoesNotContain("dlcpacks:/mpluxe/\r\n<Item>dlcpacks:/mpluxe/", dlc);
    }

    [Fact]
    public void WithoutOnigiri_NothingExtraIsWritten()
    {
        Directory.Delete(Path.Combine(Game, "onigiri"), true);
        Install(MakeOiv(NosLike, new() { ["dlc.rpf"] = DlcRpf() }));
        Assert.False(Directory.Exists(Path.Combine(Game, "onigiri")));
    }

    [Fact]
    public void WithOnigiri_CanonicalPacksStayInMods_AndMirrorsAreCreated()
    {
        File.WriteAllText(Path.Combine(Game, "onigiri.asi"), "MZ stub");
        var g = OpenGame();
        Assert.Equal(
            @"mods\update\x64\dlcpacks\nos_overdrive\dlc.rpf",
            g.MapWriteTarget(@"update\x64\dlcpacks\nos_overdrive\dlc.rpf")
        );
        Assert.Equal(
            @"mods\update\x64\dlcpacks\nos_overdrive\dlc.rpf",
            g.MapWriteTarget(@"mods\update\x64\dlcpacks\nos_overdrive\dlc.rpf")
        );
        Assert.Equal(
            @"mods\update\x64\dlcpacks\mpluxe\dlc.rpf",
            g.MapWriteTarget(@"update\x64\dlcpacks\mpluxe\dlc.rpf")
        ); // the game's own pack
        Assert.Equal(@"mods\update\update.rpf", g.MapWriteTarget(@"update\update.rpf"));

        Install(MakeOiv(NosLike, new() { ["dlc.rpf"] = DlcRpf() }));
        Assert.True(File.Exists(Path.Combine(Game, "onigiri", "dlcpacks", "nos_overdrive", "dlc.rpf")));
        Assert.Contains("dlcpacks:/nos_overdrive/", Onigiri("dlclist.xml"));
    }

    [Fact]
    public void WithoutOnigiriAsi_PacksStayInMods()
    {
        Assert.Equal(
            @"mods\update\x64\dlcpacks\nos_overdrive\dlc.rpf",
            OpenGame().MapWriteTarget(@"update\x64\dlcpacks\nos_overdrive\dlc.rpf")
        );
    }

    [Fact]
    public void RaisePools_NeverLowers()
    {
        var (text, log) = OnigiriOverlay.RaisePools(GameConfig(900), GameConfig(800));
        Assert.Empty(log);
        Assert.Contains("value=\"900\"", text);
    }

    [Fact]
    public void Fingerprint_TreatsNonRpfNestedFileAsBytes()
    {
        // NVE Legacy: NVE_3\x64\ss.rpf is not RPF7; fingerprinting must not break the installation.
        var path = Path.Combine(_root, "dlc.rpf");
        using (var fs = File.Create(path))
            new Rpf7Builder()
                .AddFile(@"x64\ok.rpf", new RpfBinarySource(DlcRpf()))
                .AddFile(@"x64\ss.rpf", new RpfBinarySource([0x15, 0x4C, 0x06, 0xA9, 1, 2, 3, 4, 5, 6, 7, 8]))
                .Write(fs);
        var type = typeof(Rpf7Archive).Assembly.GetType("GTAModManager.Core.NaturalVision.NveRpfFingerprint")!;
        var hash = (string)type.GetMethod("FileHash")!.Invoke(null, [path, null])!;
        Assert.Equal(64, hash.Length);
    }
}
