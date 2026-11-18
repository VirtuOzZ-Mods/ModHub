using System.IO.Compression;
using GTAModManager.Core.Base;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Mods;
using Xunit;

namespace GTAModManager.Core.Tests;

public class StarterReadinessTests
{
    [Fact]
    public void First_launch_is_detected_by_settings_xml_per_edition()
    {
        var docs = Directory.CreateTempSubdirectory("gmm-docs-").FullName;
        try
        {
            Assert.False(GameReadiness.FirstLaunchDone(GameEdition.Legacy, docs));
            Directory.CreateDirectory(Path.Combine(docs, "GTA V"));
            File.WriteAllText(Path.Combine(docs, "GTA V", "settings.xml"), "<Settings/>");
            Assert.True(GameReadiness.FirstLaunchDone(GameEdition.Legacy, docs));
            Assert.False(GameReadiness.FirstLaunchDone(GameEdition.Enhanced, docs));
            Directory.CreateDirectory(Path.Combine(docs, "GTAV Enhanced"));
            File.WriteAllText(Path.Combine(docs, "GTAV Enhanced", "settings.xml"), "<Settings/>");
            Assert.True(GameReadiness.FirstLaunchDone(GameEdition.Enhanced, docs));
        }
        finally
        {
            Directory.Delete(docs, true);
        }
    }

    [Fact]
    public void BattlEye_state_comes_from_last_launch_of_this_game_folder()
    {
        const string legacy = @"D:\Games\Grand Theft Auto V Legacy";
        string[] log =
        [
            @"[2026-11-13 17:37:42.051] [DISPLAY] [ 9436] [gamelaunch] Launch Path: D:\Games\Grand Theft Auto V Legacy\GTA5_BE.exe",
            @"[2026-11-13 17:40:00.000] [DISPLAY] [ 9436] [gamelaunch] Launch Path: D:\Games\Grand Theft Auto V Enhanced\GTA5_Enhanced.exe",
        ];
        Assert.Equal(BattlEyeState.Enabled, GameReadiness.FromLog(log, legacy));
        Assert.Equal(BattlEyeState.Disabled, GameReadiness.FromLog(log, @"D:\Games\Grand Theft Auto V Enhanced\"));
        Assert.Equal(BattlEyeState.Unknown, GameReadiness.FromLog(log, @"C:\Games\GTA V"));
        string[] later =
        [
            .. log,
            @"[2026-11-13 18:00:00.000] [DISPLAY] [1] [gamelaunch] Launch Path: d:\games\grand theft auto v legacy\GTA5.exe",
        ];
        Assert.Equal(BattlEyeState.Disabled, GameReadiness.FromLog(later, legacy));
    }

    [Fact]
    public void BattlEye_reads_newest_launcher_log_first()
    {
        var docs = Directory.CreateTempSubdirectory("gmm-docs-").FullName;
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(docs, "Launcher")).FullName;
            const string root = @"D:\GTAV";
            Assert.Equal(BattlEyeState.Unknown, GameReadiness.BattlEye(root, docs));
            var old = Path.Combine(dir, "launcher.01.log");
            File.WriteAllText(old, @"[x] [gamelaunch] Launch Path: D:\GTAV\GTA5_BE.exe" + "\n");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
            var current = Path.Combine(dir, "launcher.log");
            File.WriteAllText(current, "[x] [launcher] nothing about games\n");
            Assert.Equal(BattlEyeState.Enabled, GameReadiness.BattlEye(root, docs));
            File.AppendAllText(current, @"[x] [gamelaunch] Launch Path: D:\GTAV\GTA5.exe" + "\n");
            Assert.Equal(BattlEyeState.Disabled, GameReadiness.BattlEye(root, docs));
        }
        finally
        {
            Directory.Delete(docs, true);
        }
    }

    [Fact]
    public void GameConfig_variant_defaults_to_current_more_mods_stock_traffic()
    {
        var root = Directory.CreateTempSubdirectory("gmm-gcfg-").FullName;
        try
        {
            var zip = Path.Combine(root, "config.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (
                    var path in new[]
                    {
                        "Backup/gameconfig.xml",
                        "Old Versions/Gta Config v10.0 For v 1.0.1180.2/1x traffic 1xpeds (Means Gta base)/gameconfig.xml",
                        "Gta Config v37 for The Kortz Center Heist/For Less Mods/Stock Traffic (Means Gta base)/gameconfig.xml",
                        "Gta Config v37 for The Kortz Center Heist/For More Mods/0,5x traffic/gameconfig.xml",
                        "Gta Config v37 for The Kortz Center Heist/For More Mods/Stock Traffic (Means Gta base)/gameconfig.xml",
                    }
                )
                {
                    using var w = new StreamWriter(z.CreateEntry(path).Open());
                    w.Write("<CGameConfig/>");
                }
            }
            using var archive = ModArchive.Open(zip);
            var pick = StarterPackage.PickGameConfig(GameConfigVariants.Find(archive), GameEdition.Legacy);
            Assert.NotNull(pick);
            Assert.Contains("For More Mods", pick!.Source);
            Assert.Contains("Stock Traffic", pick.Source);
            Assert.DoesNotContain("Old Versions", pick.Source);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GameConfig_file_is_chosen_by_edition_label()
    {
        var page = StarterPackage.GameConfigPage;
        const string html = """
            <i class="fa fa-file"></i>&nbsp;V3 The Kortz Center Heist (Enhanced)
            <p><span class="num-downloads">27,852 downloads</span></p>
            <a target="_blank" href="/misc/gta-5-gameconfig-300-cars/download/198684"><i class="fa fa-download"></i></a>
            <i class="fa fa-file"></i>&nbsp;V37 The Kortz Center Heist (Legacy)  <span>(current)</span>
            <a target="_blank" href="/misc/gta-5-gameconfig-300-cars/download/198681"><i class="fa fa-download"></i></a>
            <i class="fa fa-file"></i>&nbsp;LCPP
            <a target="_blank" href="/misc/gta-5-gameconfig-300-cars/download/198678"><i class="fa fa-download"></i></a>
            <a href="/misc/other-mod/download/1">x</a>
            """;
        var versions = Gta5ModsDownloads.FindVersions(html, page);
        Assert.Equal(3, versions.Count);
        Assert.EndsWith("/198684", StarterPackage.PickGameConfigDownload(versions, GameEdition.Enhanced)!.AbsolutePath);
        Assert.EndsWith("/198681", StarterPackage.PickGameConfigDownload(versions, GameEdition.Legacy)!.AbsolutePath);
    }

    [Fact]
    public void PendingLoaderUpdate_IsIncludedInStarterPackageMissingActions()
    {
        var status = new StarterStatus(
            new ModdingBaseStatus
            {
                AsiLoader = true,
                ScriptHookV = true,
                Shvdn = true,
                ModsLoader = true,
                ModsLoaderUpdateAvailable = true,
            },
            [],
            true
        );
        Assert.False(status.Complete);
        Assert.Contains("RageOpenV", Assert.Single(status.Missing));
    }
}
