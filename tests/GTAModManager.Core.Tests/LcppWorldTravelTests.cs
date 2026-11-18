using System.IO.Compression;
using System.Text;
using GTAModManager.Core.NaturalVision;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed partial class LcppTests
{
    const string OldPopZone = "0F BE 54 88 ? EB 5F 41 B8 ? ? ? ? E8";
    const string AnyJumpPopZone = "0F BE 54 88 ? EB ?  41 B8 ? ? ? ? E8";

    static byte[] WorldTravelAsi(string signature) =>
        Encoding.ASCII.GetBytes("MZ\0\0 radio 48 83 EC 28\0" + signature + "\0E8 ? ? ? ? 0F BE 40 06\0");

    void AddWorldTravel(string signature)
    {
        using var zip = ZipFile.Open(Path.Combine(bundle, Name(1)), ZipArchiveMode.Update);
        var entry = zip.GetEntry("assembly.xml")!;
        string text;
        using (var r = new StreamReader(entry.Open()))
            text = r.ReadToEnd();
        entry.Delete();
        using (var w = zip.CreateEntry("content/mod/WorldTravelPatches.asi").Open())
            w.Write(WorldTravelAsi(signature));
        using (var w = new StreamWriter(zip.CreateEntry("content/mod/Levels/Liberty City/IPLs.txt").Open()))
            w.Write("lc_ipl");
        using var output = new StreamWriter(zip.CreateEntry("assembly.xml").Open());
        output.Write(
            text.Replace(
                "</content>",
                "<add source=\"mod\\WorldTravelPatches.asi\">WorldTravelPatches.asi</add>"
                    + "<add source=\"mod\\Levels\\Liberty City\\IPLs.txt\">\\Levels\\Liberty City\\IPLs.txt</add>"
                    + "</content>"
            )
        );
    }

    [Fact]
    public void WorldTravelPatches_popzone_signature_matches_newer_builds_and_removal_leaves_no_trace()
    {
        AddWorldTravel(OldPopZone);
        var before = Snapshot();
        var result = Install();
        Assert.True(result.Success, result.Message);
        byte[] installed = File.ReadAllBytes(Path.Combine(game.Root, "WorldTravelPatches.asi"));
        Assert.Equal(WorldTravelAsi(AnyJumpPopZone), installed);
        Assert.Equal("lc_ipl", File.ReadAllText(Path.Combine(game.Root, @"Levels\Liberty City\IPLs.txt")));
        // The source package on disk is never modified.
        using (var source = ZipFile.OpenRead(Path.Combine(bundle, Name(1))))
        using (var stream = source.GetEntry("content/mod/WorldTravelPatches.asi")!.Open())
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            Assert.Equal(WorldTravelAsi(OldPopZone), copy.ToArray());
        }

        File.WriteAllText(Path.Combine(game.Root, "WorldTravelPatches.log"), "[info] Main");
        var removed = Service().Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Same(before);
        Assert.False(Directory.Exists(Path.Combine(game.Root, "Levels")));
    }

    [Theory]
    [InlineData(AnyJumpPopZone)]
    [InlineData("unrelated build")]
    public void WorldTravelPatches_without_the_old_signature_is_installed_unchanged(string signature)
    {
        AddWorldTravel(signature);
        var result = Install();
        Assert.True(result.Success, result.Message);
        Assert.Equal(WorldTravelAsi(signature), File.ReadAllBytes(Path.Combine(game.Root, "WorldTravelPatches.asi")));
    }

    NveResult InstallUnpatched()
    {
        AddWorldTravel(OldPopZone);
        NveService.PatchOnInstall = false;
        try
        {
            return Install();
        }
        finally
        {
            NveService.PatchOnInstall = true;
        }
    }

    [Fact]
    public void Earlier_install_is_repaired_in_place_and_still_removes_to_the_original_state()
    {
        var before = Snapshot();
        var result = InstallUnpatched();
        Assert.True(result.Success, result.Message);
        string asi = Path.Combine(game.Root, "WorldTravelPatches.asi");
        Assert.Equal(WorldTravelAsi(OldPopZone), File.ReadAllBytes(asi));
        Assert.True(NveService.NeedsWorldTravelRepair(game, State(result.InstallationId)));

        var repair = Service().RepairWorldTravelPatches(new() { InstallationId = result.InstallationId });
        Assert.True(repair.Success, repair.Message);
        Assert.Equal(WorldTravelAsi(AnyJumpPopZone), File.ReadAllBytes(asi));
        Assert.False(NveService.NeedsWorldTravelRepair(game, State(result.InstallationId)));
        Assert.True(Service().RepairWorldTravelPatches(new() { InstallationId = result.InstallationId }).Success);

        // The source archive is not needed: removal undoes the fix first, then the original parts.
        Directory.Delete(bundle, true);
        var removed = Service().Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Same(before);
    }

    const string BaseDlcList =
        "<SMandatoryPacksData><Paths><Item>dlcpacks:/mpbeach/</Item></Paths></SMandatoryPacksData>";

    static string DlcXmlOp(string pack) =>
        "<archive path=\"update\\update.rpf\" type=\"RPF7\" createIfNotExist=\"True\"><xml path=\"common\\data\\dlclist.xml\">"
        + "<add append=\"Last\" xpath=\"/SMandatoryPacksData/Paths\"><Item>dlcpacks:/"
        + pack
        + "/</Item></add></xml></archive>";

    void AddLcppDlcLine()
    {
        using var zip = ZipFile.Open(Path.Combine(bundle, Name(1)), ZipArchiveMode.Update);
        var entry = zip.GetEntry("assembly.xml")!;
        string text;
        using (var r = new StreamReader(entry.Open()))
            text = r.ReadToEnd();
        entry.Delete();
        using var output = new StreamWriter(zip.CreateEntry("assembly.xml").Open());
        output.Write(text.Replace("</content>", DlcXmlOp("lcpack") + "</content>"));
    }

    void InstallOtherDlcMod(string pack)
    {
        string path = Path.Combine(root, pack + ".oiv");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("assembly.xml").Open()))
            writer.Write(
                $"<package version=\"2.2\" target=\"Five\"><metadata><name>{pack}</name></metadata><content>{DlcXmlOp(pack)}</content></package>"
            );
        using var package = OivPackage.Open(path);
        using var plan = new OivInstaller(game).Prepare(pack, package.Ops, package, naturalVision: false);
        Assert.True(plan.CanInstall, string.Join(";", plan.Errors));
        Assert.Equal(TxState.Completed, new OivInstaller(game).Install(plan, library).State);
    }

    [Fact]
    public void Lcpp_removal_keeps_a_later_mods_dlclist_line_in_the_shared_update_rpf()
    {
        Put(
            @"update\update.rpf",
            Rpf(
                new()
                {
                    ["same.txt"] = "original"u8.ToArray(),
                    [@"common\data\gameconfig.xml"] = "<config>original</config>"u8.ToArray(),
                    [@"common\data\dlclist.xml"] = Encoding.UTF8.GetBytes(BaseDlcList),
                }
            )
        );
        AddLcppDlcLine();
        var result = Install();
        Assert.True(result.Success, result.Message);
        Assert.Contains("dlcpacks:/lcpack/", Read(Update, @"common\data\dlclist.xml"));

        // A later mod registers its own pack in the same dlclist.xml (like connecting the Ped Selector pack).
        InstallOtherDlcMod("pedselector");
        Assert.Contains("dlcpacks:/pedselector/", Read(Update, @"common\data\dlclist.xml"));

        Directory.Delete(bundle, true);
        var removed = Service().Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removed.Success, removed.Message);
        string dlc = Read(Update, @"common\data\dlclist.xml");
        Assert.DoesNotContain("dlcpacks:/lcpack/", dlc);
        Assert.Contains("dlcpacks:/pedselector/", dlc);
        Assert.Contains("dlcpacks:/mpbeach/", dlc);
        Assert.Equal("original", Read(Update, "same.txt"));
        Assert.False(File.Exists(Path.Combine(game.Root, Pack)));
    }

    [Fact]
    public void Lcpp_removal_restores_its_file_inside_update_rpf_even_after_another_mod_changed_it()
    {
        var result = Install();
        Assert.True(result.Success, result.Message);
        Edit(Update, "same.txt", "another mod");
        Edit(Update, "foreign.txt", "foreign mod");
        var removed = Service().Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Assert.Equal("original", Read(Update, "same.txt"));
        Assert.Equal("foreign mod", Read(Update, "foreign.txt"));
        Assert.Empty(new NveStore(game, states).Active());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lcpp_removal_keeps_an_unrelated_entry_another_mod_added_to_update_rpf(bool hd)
    {
        if (hd)
            MakeHd();
        var result = Install();
        Assert.True(result.Success, result.Message);
        Edit(Update, "foreign.txt", "foreign mod");
        var removed = Service().Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Assert.Equal("foreign mod", Read(Update, "foreign.txt"));
        Assert.Equal("original", Read(Update, "same.txt"));
        Assert.Empty(new NveStore(game, states).Active());
    }

    [Fact]
    public void Repair_ignores_later_edits_of_other_mods_in_shared_archives()
    {
        var result = InstallUnpatched();
        Assert.True(result.Success, result.Message);
        // For example the Ped Selector pack being registered in update.rpf after LCPP.
        Edit(Update, "other.txt", "another mod");
        var repair = Service().RepairWorldTravelPatches(new() { InstallationId = result.InstallationId });
        Assert.True(repair.Success, repair.Message);
        Assert.Equal(
            WorldTravelAsi(AnyJumpPopZone),
            File.ReadAllBytes(Path.Combine(game.Root, "WorldTravelPatches.asi"))
        );
        Assert.Equal("another mod", Read(Update, "other.txt"));
    }

    [Fact]
    public void Repair_refuses_a_file_changed_by_another_program()
    {
        var result = InstallUnpatched();
        Assert.True(result.Success, result.Message);
        string asi = Path.Combine(game.Root, "WorldTravelPatches.asi");
        File.WriteAllBytes(asi, Encoding.ASCII.GetBytes("MZ other " + OldPopZone));
        Assert.Throws<IOException>(() =>
            Service().RepairWorldTravelPatches(new() { InstallationId = result.InstallationId })
        );
        Assert.Equal("MZ other " + OldPopZone, File.ReadAllText(asi));
    }

    [Fact]
    public void Replacement_signature_keeps_length_and_differs_only_in_the_jump_byte()
    {
        Assert.Equal(OldPopZone.Length, AnyJumpPopZone.Length);
        Assert.Equal(
            OldPopZone.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            AnyJumpPopZone.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length
        );
    }
}
