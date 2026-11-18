using System.Text;
using System.Xml.Linq;
using GTAModManager.Core.Game;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed partial class OnigiriOverlayTests
{
    private string InstallPackage(string content, Dictionary<string, byte[]> files, bool nve = false)
    {
        using var pkg = OivPackage.Open(MakeOiv(content, files));
        using var plan = new OivInstaller(OpenGame()).Prepare("compat test", pkg.Ops, pkg, naturalVision: nve);
        Assert.Empty(plan.Errors);
        var result = new OivInstaller(OpenGame()).Install(plan, Lib);
        Assert.Equal(TxState.Completed, result.State);
        return result.Id;
    }

    private string InstallProfile(int value = 500)
    {
        return InstallPackage(
            """
            <add source="dlc.xml">onigiri\common\data\dlclist.xml</add>
            <add source="config.xml">onigiri\common\data\gameconfig.xml</add>
            <add source="asi">onigiri.asi</add>
            <add source="nve">NVE.asi</add>
            """,
            new()
            {
                ["dlc.xml"] = Encoding.UTF8.GetBytes(OnigiriDlcList),
                ["config.xml"] = Encoding.UTF8.GetBytes(GameConfig(value)),
                ["asi"] = "MZ fake"u8.ToArray(),
                ["nve"] = "MZ nve"u8.ToArray(),
            },
            true
        );
    }

    private string InstallAddon() => InstallPackage(NosLike, new() { ["dlc.rpf"] = DlcRpf() });

    private int Pool() =>
        int.Parse(
            XDocument
                .Parse(Onigiri("gameconfig.xml"))
                .Descendants("Item")
                .First(e => e.Element("PoolName")?.Value == "PtFxAssetStore")
                .Element("PoolSize")!
                .Attribute("value")!
                .Value
        );

    [Fact]
    public void Profile_DoesNotImportUnrelatedHistoricalPools()
    {
        WriteUpdate(UpdateDlcList, GameConfig(15000));
        InstallProfile();
        Assert.Equal(500, Pool());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProfileAndAddon_BothOrdersPreserveExplicitRequirementAndCanonicalPack(bool addonFirst)
    {
        Directory.Delete(Path.Combine(Game, "onigiri"), true);
        if (addonFirst)
        {
            InstallAddon();
            InstallProfile();
        }
        else
        {
            InstallProfile();
            InstallAddon();
        }
        Assert.Equal(800, Pool());
        Assert.Contains("dlcpacks:/nos_overdrive/", Onigiri("dlclist.xml"));
        Assert.True(File.Exists(Path.Combine(Game, "mods/update/x64/dlcpacks/nos_overdrive/dlc.rpf")));
        Assert.True(File.Exists(Path.Combine(Game, "onigiri/dlcpacks/nos_overdrive/dlc.rpf")));
    }

    [Fact]
    public void AddonRemoval_AfterProfileRestoresProfileValues()
    {
        Directory.Delete(Path.Combine(Game, "onigiri"), true);
        var addon = InstallAddon();
        InstallProfile();
        var result = new OivInstaller(OpenGame()).Undo(addon, Lib);
        Assert.Equal(TxState.Undone, result.State);
        Assert.Equal(500, Pool());
        Assert.DoesNotContain("nos_overdrive", Onigiri("dlclist.xml"));
        Assert.False(File.Exists(Path.Combine(Game, "onigiri/dlcpacks/nos_overdrive/dlc.rpf")));
    }

    [Fact]
    public void ProfileRemoval_LeavesAddonUsableAndThenRemovable()
    {
        Directory.Delete(Path.Combine(Game, "onigiri"), true);
        var profile = InstallProfile();
        var addon = InstallAddon();
        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(profile, Lib).State);
        Assert.True(File.Exists(Path.Combine(Game, "mods/update/x64/dlcpacks/nos_overdrive/dlc.rpf")));
        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(addon, Lib).State);
    }

    [Fact]
    public void ForeignSharedXmlChange_RejectsWholeRemoval()
    {
        var addon = InstallAddon();
        File.AppendAllText(Path.Combine(Game, "onigiri/common/data/gameconfig.xml"), "<!-- foreign -->");
        Assert.Equal(TxState.Rejected, new OivInstaller(OpenGame()).Undo(addon, Lib).State);
        Assert.True(File.Exists(Path.Combine(Game, "mods/update/x64/dlcpacks/nos_overdrive/dlc.rpf")));
    }

    [Fact]
    public void Profile_OrdersHeapPluginInSameTransaction_AndUndoRestoresName()
    {
        File.WriteAllText(Path.Combine(Game, "HeapAdjuster.asi"), "MZ heap");
        File.WriteAllText(Path.Combine(Game, "HeapAdjuster.ini"), "[HEAP_SETTINGS]\nHEAP_SIZE=1024");
        var profile = InstallProfile();
        Assert.False(File.Exists(Path.Combine(Game, "HeapAdjuster.asi")));
        Assert.True(File.Exists(Path.Combine(Game, "ZHeapAdjuster.asi")));
        Assert.True(GTAModManager.Core.Installation.AdjusterComponents.Present(OpenGame(), "heap"));
        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(profile, Lib).State);
        Assert.True(File.Exists(Path.Combine(Game, "HeapAdjuster.asi")));
        Assert.False(File.Exists(Path.Combine(Game, "ZHeapAdjuster.asi")));
    }

    [Fact]
    public void SharedRequirements_DeletingLowerRequirementKeepsHigherAndThenRestoresBase()
    {
        InstallProfile();
        var first = InstallAddon();
        var second = InstallPackage(
            NosLike.Replace("nos_overdrive", "another").Replace("800", "1200"),
            new() { ["dlc.rpf"] = DlcRpf() }
        );
        Assert.Equal(1200, Pool());
        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(first, Lib).State);
        Assert.Equal(1200, Pool());
        Assert.Contains("another", Onigiri("dlclist.xml"));
        Assert.DoesNotContain("nos_overdrive", Onigiri("dlclist.xml"));
        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(second, Lib).State);
        Assert.Equal(500, Pool());
    }

    [Fact]
    public void UnknownWholeConfigReplacement_IsRejectedBeforeWriting()
    {
        InstallProfile();
        var before = Onigiri("gameconfig.xml");
        using var pkg = OivPackage.Open(
            MakeOiv(
                """<archive path="update\update.rpf" type="RPF7" createIfNotExist="False"><add source="config">common\data\gameconfig.xml</add></archive>""",
                new() { ["config"] = Encoding.UTF8.GetBytes(GameConfig(30000)) }
            )
        );
        using var plan = new OivInstaller(OpenGame()).Prepare(pkg);
        Assert.NotEmpty(plan.Errors);
        Assert.Empty(plan.Changes);
        Assert.Equal(before, Onigiri("gameconfig.xml"));
    }

    [Fact]
    public void EqualForeignMirror_IsNotDeletedByUninstall()
    {
        File.WriteAllText(Path.Combine(Game, "onigiri.asi"), "MZ");
        var dir = Path.Combine(Game, "onigiri/dlcpacks/nos_overdrive");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "dlc.rpf"), DlcRpf());
        var addon = InstallAddon();
        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(addon, Lib).State);
        Assert.True(File.Exists(Path.Combine(dir, "dlc.rpf")));
    }

    [Fact]
    public void ConflictingHeapAlias_RejectsWholeProfile()
    {
        File.WriteAllText(Path.Combine(Game, "HeapAdjuster.asi"), "MZ first");
        File.WriteAllText(Path.Combine(Game, "ZHeapAdjuster.asi"), "MZ other");
        using var pkg = OivPackage.Open(
            MakeOiv("""<add source="nve">NVE.asi</add>""", new() { ["nve"] = "MZ nve"u8.ToArray() })
        );
        using var plan = new OivInstaller(OpenGame()).Prepare(pkg);
        Assert.NotEmpty(plan.Errors);
        Assert.Empty(plan.Changes);
        Assert.False(File.Exists(Path.Combine(Game, "NVE.asi")));
    }

    [Theory]
    [InlineData("after-backup:0")]
    [InlineData("after-apply:1")]
    [InlineData("verified")]
    public void InterruptedCompatibilityInstall_RecoversFilesAndReceiptsTogether(string point)
    {
        using var pkg = OivPackage.Open(MakeOiv(NosLike, new() { ["dlc.rpf"] = DlcRpf() }));
        using var plan = new OivInstaller(OpenGame()).Prepare(pkg);
        Assert.Empty(plan.Errors);
        var before = Directory
            .EnumerateFiles(Game, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Game, f), FileTransactionEngine.Hash);
        string work = Path.Combine(_root, "crash-transactions");
        var engine = new FileTransactionEngine(Game, work)
        {
            FaultPoint = p =>
            {
                if (p == point)
                    throw new SimulatedCrashException(p);
            },
        };
        Assert.Throws<SimulatedCrashException>(() => engine.Execute(plan.Changes));
        Assert.Equal(TxState.RolledBack, Assert.Single(new FileTransactionEngine(Game, work).Recover()).State);
        var after = Directory
            .EnumerateFiles(Game, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Game, f), FileTransactionEngine.Hash);
        Assert.Equal(before.OrderBy(p => p.Key), after.OrderBy(p => p.Key));
    }

    [Fact]
    public void PoolRequirements_DoNotCrossXPathSelectedSections()
    {
        string Configs(int debug, int release) =>
            "<fwAllConfigs><ConfigArray>"
            + GameConfig(debug)
                .Split("<ConfigArray>")[1]
                .Split("</ConfigArray>")[0]
                .Replace("<Item><Config", "<Item><Build>debug</Build><Config")
            + GameConfig(release)
                .Split("<ConfigArray>")[1]
                .Split("</ConfigArray>")[0]
                .Replace("<Item><Config", "<Item><Build>release</Build><Config")
            + "</ConfigArray></fwAllConfigs>";
        Directory.Delete(Path.Combine(Game, "onigiri"), true);
        WriteUpdate(UpdateDlcList, Configs(1500, 400));
        InstallPackage(
            NosLike
                .Replace("//PoolSizes/Entries[", "//ConfigArray/Item[Build='release']/Config/PoolSizes/Entries[")
                .Replace(
                    "//PoolSizes/Entries/Item[",
                    "//ConfigArray/Item[Build='release']/Config/PoolSizes/Entries/Item["
                ),
            new() { ["dlc.rpf"] = DlcRpf() }
        );
        InstallPackage(
            """<add source="config">onigiri\common\data\gameconfig.xml</add>""",
            new() { ["config"] = Encoding.UTF8.GetBytes(Configs(100, 500)) },
            true
        );
        var doc = XDocument.Parse(Onigiri("gameconfig.xml"));
        Assert.Equal(
            "100",
            doc.Descendants("Item")
                .First(e => e.Element("Build")?.Value == "debug")
                .Descendants("PoolSize")
                .First()
                .Attribute("value")!
                .Value
        );
        Assert.Equal(
            "800",
            doc.Descendants("Item")
                .First(e => e.Element("Build")?.Value == "release")
                .Descendants("PoolSize")
                .First()
                .Attribute("value")!
                .Value
        );
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Bases\":null}")]
    public void CorruptReceipt_RejectsWithoutTouchingFiles(string broken)
    {
        var addon = InstallAddon();
        var receipt = Directory.GetFiles(Path.Combine(Game, "ModHubState/compatibility"), "*.json").Single();
        File.WriteAllText(receipt, broken);
        var result = new OivInstaller(OpenGame()).Undo(addon, Lib);
        Assert.Equal(TxState.Rejected, result.State);
        Assert.True(File.Exists(Path.Combine(Game, "mods/update/x64/dlcpacks/nos_overdrive/dlc.rpf")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Legacy_ProfileAndAddon_BothOrdersAndBothRemovals(bool addonFirst)
    {
        File.Delete(Path.Combine(Game, "GTA5_Enhanced.exe"));
        File.WriteAllText(Path.Combine(Game, "GTA5.exe"), "stub");
        File.WriteAllText(Path.Combine(Game, "RageOpenV.asi"), "MZ");
        Directory.Delete(Path.Combine(Game, "onigiri"), true);
        WriteUpdate(UpdateDlcList, GameConfig(15000));
        string Profile() =>
            InstallPackage(
                """
                <archive path="update\update.rpf" type="RPF7" createIfNotExist="False">
                <add source="config">common\data\gameconfig.xml</add>
                <xml path="common\data\dlclist.xml"><add append="Last" xpath="/SMandatoryPacksData/Paths"><Item>dlcpacks:/NVE/</Item></add></xml>
                </archive><add source="nve">NVE.asi</add>
                """,
                new() { ["config"] = Encoding.UTF8.GetBytes(GameConfig(500)), ["nve"] = "MZ nve"u8.ToArray() },
                true
            );
        int CurrentPool()
        {
            using var r = GTAModManager.Core.Rpf.Rpf7Archive.OpenRead(Path.Combine(Game, "mods/update/update.rpf"));
            var e = (GTAModManager.Core.Rpf.RpfBinaryFile)r.Find(@"common\data\gameconfig.xml")!;
            return int.Parse(
                XDocument
                    .Parse(Encoding.UTF8.GetString(r.ReadContent(e)))
                    .Descendants("PoolSize")
                    .First()
                    .Attribute("value")!
                    .Value
            );
        }
        string addon,
            profile;
        if (addonFirst)
        {
            addon = InstallAddon();
            profile = Profile();
        }
        else
        {
            profile = Profile();
            addon = InstallAddon();
        }
        Assert.Equal(800, CurrentPool());
        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(addonFirst ? addon : profile, Lib).State);
        Assert.Equal(addonFirst ? 500 : 15000, CurrentPool());
        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(addonFirst ? profile : addon, Lib).State);
        Assert.Equal(15000, CurrentPool());
    }

    [Fact]
    public void CurrentNosPackage_DeclaresMinimumEvenWithoutGameconfigOperations()
    {
        InstallProfile();
        string dlcOnly = """
            <add source="dlc.rpf">update\x64\dlcpacks\nos_overdrive\dlc.rpf</add>
            <add source="second">update\x64\dlcpacks\nos_lsc_entrance\dlc.rpf</add>
            <add source="script">scripts\N.O.S - Nitro Overdrive System.dll</add>
            <archive path="update\update.rpf" type="RPF7" createIfNotExist="False"><xml path="common\data\dlclist.xml">
            <add append="Last" xpath="/SMandatoryPacksData/Paths"><Item>dlcpacks:/nos_overdrive/</Item><Item>dlcpacks:/nos_lsc_entrance/</Item></add>
            </xml></archive>
            """;
        var addon = InstallPackage(
            dlcOnly,
            new()
            {
                ["dlc.rpf"] = DlcRpf(),
                ["second"] = DlcRpf(),
                ["script"] = "MZ script"u8.ToArray(),
            }
        );
        Assert.Equal(800, Pool());
        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(addon, Lib).State);
        Assert.Equal(500, Pool());
    }

    [Fact]
    public void ProfileBaselineChange_IsSavedEvenIfAddonKeepsEffectiveFileUnchanged()
    {
        var addon = InstallAddon();
        InstallPackage(
            """<add source="config">onigiri\common\data\gameconfig.xml</add>""",
            new() { ["config"] = Encoding.UTF8.GetBytes(GameConfig(700)) },
            true
        );
        Assert.Equal(800, Pool());
        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(addon, Lib).State);
        Assert.Equal(700, Pool());
    }
}
