using System.IO.Compression;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Peds;
using Xunit;

namespace GTAModManager.Core.Tests;

public class PedAssetsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gmm-pedassets-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    private ModArchive Zip(string name, params (string Path, string Text)[] entries)
    {
        var p = Path.Combine(_dir, name);
        using (var z = ZipFile.Open(p, ZipArchiveMode.Create))
            foreach (var (path, text) in entries)
                using (var w = new StreamWriter(z.CreateEntry(path).Open()))
                    w.Write(text);
        return ModArchive.Open(p);
    }

    private static PedAssetPlan Plan(ModArchive a, PedGender? gender = null) =>
        PedAssets.Plan(a, a.Files.ToList(), PedAssets.Docs(a, a.Files), gender)!;

    [Fact]
    public void Split_game_path_from_readme()
    {
        Assert.Equal(
            ("x64v.rpf", @"models\cdimages\streamedpeds_players.rpf", ""),
            PedAssets.SplitGamePath(@"Grand Theft Auto V\mods\x64v.rpf\models\cdimages\streamedpeds_players.rpf")
        );
        Assert.Equal(
            (@"update\x64\dlcpacks\mppatchesng\dlc.rpf", @"x64\models\cdimages\mppatches.rpf", "mp_f_freemode_01"),
            PedAssets.SplitGamePath(
                @"\mods\update\x64\dlcpacks\mppatchesng\dlc.rpf\x64\models\cdimages\mppatches.rpf\mp_f_freemode_01"
            )
        );
        Assert.Null(PedAssets.SplitGamePath(@"GTA V\menyooStuff\Outfit"));
    }

    [Fact]
    public void Multi_part_instruction_like_trevor_replacement()
    {
        using var a = Zip(
            "dante.zip",
            (
                "INSTALL/How to Install.txt",
                "PART 1 (Main)\nPlace \"player_two folder\" & \"player_two.yft\" into:\nGrand Theft Auto V\\mods\\x64v.rpf\\models\\cdimages\\streamedpeds_players.rpf\n\n"
                    + "PART 2\nPlace \"player_two folder\" into:\nGrand Theft Auto V\\mods\\update\\x64\\dlcpacks\\mppatchesng\\dlc.rpf\\x64\\models\\cdimages\\mppatches.rpf\n\n"
                    + "PART 3\nPlace \"player_two_p folder\" into:\nGrand Theft Auto V\\mods\\x64v.rpf\\models\\cdimages\\streamedpedprops.rpf\n"
            ),
            ("INSTALL/PART 1/player_two.yft", "m"),
            ("INSTALL/PART 1/player_two/hair_000_u.ydd", "h"),
            ("INSTALL/PART 2/player_two/head_000_r.ydd", "p")
        );
        var plan = Plan(a);
        Assert.Equal("Замена по инструкции автора", plan.Kind);
        Assert.Contains(plan.Files, f =>
            f.Outer == "x64v.rpf"
            && f.Nested == @"models\cdimages\streamedpeds_players.rpf"
            && f.Inner == @"player_two\hair_000_u.ydd"
        );
        Assert.Contains(plan.Files, f => f.Inner == "player_two.yft");
        Assert.Contains(plan.Files, f =>
            f.Outer.EndsWith(@"mppatchesng\dlc.rpf") && f.Inner == @"player_two\head_000_r.ydd"
        );
        Assert.DoesNotContain(plan.Files, f => f.Outer.EndsWith(@"mppatchesng\dlc.rpf") && f.Inner.Contains("hair")); // PART 2 takes only its own folder
        Assert.Contains(plan.Notes, n => n.Contains("PART 3") && n.Contains("player_two_p")); // listed but missing in the archive
        var ops = plan.Ops();
        Assert.All(ops, op => Assert.IsType<OivArchive>(op));
    }

    [Fact]
    public void All_from_folder_means_folder_contents()
    {
        using var a = Zip(
            "faces.zip",
            (
                "Mod/readme.txt",
                "By using OpenIV paste all from \"mppatchseng\" folder in:\n\\mods\\update\\x64\\dlcpacks\\mppatchesng\\dlc.rpf\\x64\\models\\cdimages\\mppatches.rpf\\mp_f_freemode_01\n"
            ),
            ("Mod/mppatchseng/head_diff_000_a_whi.ytd", "t")
        );
        var f = Assert.Single(Plan(a).Files);
        Assert.Equal(@"mp_f_freemode_01\head_diff_000_a_whi.ytd", f.Inner);
    }

    [Fact]
    public void Mp_clothing_gender_from_folders_prefix_and_title()
    {
        using var sneakers = Zip(
            "Sneakers [MP Male - MP Female].zip",
            ("MP Male/feet_014_u.ydd", "m"),
            ("MP Male/feet_diff_014_a_uni.ytd", "t"),
            ("MP Female/feet_013_r.ydd", "f")
        );
        var plan = Plan(sneakers);
        Assert.Contains(plan.Files, f =>
            f.Inner == @"mp_m_freemode_01\feet_014_u.ydd" && f.Nested == @"models\cdimages\streamedpeds_mp.rpf"
        );
        Assert.Contains(plan.Files, f => f.Inner == @"mp_f_freemode_01\feet_013_r.ydd");

        using var pendant = Zip(
            "pendant.zip",
            ("x/mp_m_freemode_01^accs_100_u.ydd", "m"),
            ("x/mp_f_freemode_01_mp_f_set^berd_002_u.ydd", "f")
        );
        var p2 = Plan(pendant);
        Assert.Contains(p2.Files, f => f.Inner == @"mp_m_freemode_01\accs_100_u.ydd");
        Assert.Contains(p2.Files, f => f.Inner == @"mp_f_freemode_01\berd_002_u.ydd");

        using var jacket = Zip("27 Jacket (MP Male).zip", ("jbib_005_u.ydd", "j"));
        Assert.Equal(@"mp_m_freemode_01\jbib_005_u.ydd", Assert.Single(Plan(jacket).Files).Inner);

        using var glasses = Zip("Glasses (MP Female).zip", ("p_eyes_007.ydd", "g"));
        var g = Assert.Single(Plan(glasses).Files);
        Assert.Equal(@"mp_f_freemode_01_p\p_eyes_007.ydd", g.Inner);
        Assert.Equal(@"models\cdimages\streamedpedprops.rpf", g.Nested);
    }

    [Fact]
    public void Unknown_gender_asks_and_choice_installs()
    {
        using var a = Zip("varsity.zip", ("jbib_005_u.ydd", "j"), ("jbib_diff_005_a_uni.ytd", "t"));
        var plan = Plan(a);
        Assert.True(plan.NeedsGender);
        Assert.Empty(plan.Files);
        Assert.NotEmpty(plan.Errors);
        var chosen = Plan(a, PedGender.Female);
        Assert.False(chosen.NeedsGender);
        Assert.All(chosen.Files, f => Assert.StartsWith(@"mp_f_freemode_01\", f.Inner));
    }

    [Fact]
    public void Fivem_resource_is_explained_not_installed()
    {
        using var a = Zip(
            "pyn.zip",
            ("R/fxmanifest.lua", "fx_version 'cerulean'"),
            ("R/stream/mp_m_freemode_01_mp_m_pyn^jbib_000_u.ydd", "j")
        );
        var plan = Plan(a);
        Assert.Equal("Набор для FiveM", plan.Kind);
        Assert.Empty(plan.Files);
        Assert.NotEmpty(plan.Errors);
    }

    [Fact]
    public void Accessory_paths_are_described()
    {
        var d = PedAccessories
            .Describe(@"mods\x64v.rpf\models\cdimages\streamedpeds_mp.rpf\mp_m_freemode_01\jbib_005_u.ydd")!
            .Value;
        Assert.Equal("MP-персонаж (мужской)", d.Character);
        Assert.Equal("Верх №5", d.Slot);
        Assert.Equal(
            "Тревор",
            PedAccessories
                .Describe(@"mods\x64v.rpf\models\cdimages\streamedpeds_players.rpf\player_two\hair_000_u.ydd")!
                .Value.Character
        );
        Assert.Null(PedAccessories.Describe(@"mods\update\update.rpf\common\data\dlclist.xml"));
    }
}
