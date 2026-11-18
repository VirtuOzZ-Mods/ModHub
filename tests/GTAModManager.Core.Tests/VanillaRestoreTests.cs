using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Rpf;
using Xunit;

namespace GTAModManager.Core.Tests;

public class VanillaRestoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gmm-vanilla-").FullName;
    private string Root => Path.Combine(_dir, "game");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    private GameInstall Build()
    {
        Directory.CreateDirectory(Root);
        foreach (
            var f in new[]
            {
                "GTA5.exe",
                "GTA5_BE.exe",
                "PlayGTAV.exe",
                "bink2w64.dll",
                "common.rpf",
                "x64a.rpf",
                "steam_api64.dll",
                "ScriptHookV.dll",
                "dinput8.dll",
                "RageOpenV.asi",
                "HeapAdjuster.ini",
                "commandline.txt",
                "OpenIV.asi.gmm-off",
            }
        )
            File.WriteAllText(Path.Combine(Root, f), "x");
        Directory.CreateDirectory(Path.Combine(Root, "mods", "update"));
        File.WriteAllText(Path.Combine(Root, "mods", "update", "update.rpf"), "copy");
        Directory.CreateDirectory(Path.Combine(Root, "scripts"));
        Directory.CreateDirectory(Path.Combine(Root, "Installers"));
        // Original DLC: NG-encrypted table of contents. Mod: an archive with an unencrypted table of contents directly in update\.
        var ngDir = Directory.CreateDirectory(Path.Combine(Root, @"update\x64\dlcpacks\patchday8ng")).FullName;
        var ng = new byte[512];
        new RpfHeader(Rpf7Format.Magic, 1, 16, (uint)RpfEncryption.Ng).Write(ng);
        File.WriteAllBytes(Path.Combine(ngDir, "dlc.rpf"), ng);
        var modDir = Directory.CreateDirectory(Path.Combine(Root, @"update\x64\dlcpacks\mycar")).FullName;
        using (var fs = File.Create(Path.Combine(modDir, "dlc.rpf")))
            new Rpf7Builder()
                .AddFile("setup2.xml", new RpfBinarySource(Encoding.UTF8.GetBytes("<SSetupData/>")))
                .Write(fs);
        File.WriteAllText(Path.Combine(Root, @"update\x64\metadata.dat"), "game");
        return GameInstall.Open(Root);
    }

    [Fact]
    public void Plan_lists_only_mod_files()
    {
        var plan = VanillaRestore.Inspect(Build());
        var rels = plan.Items.Select(i => i.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (
            var mod in new[]
            {
                "ScriptHookV.dll",
                "dinput8.dll",
                "RageOpenV.asi",
                "HeapAdjuster.ini",
                "commandline.txt",
                "OpenIV.asi.gmm-off",
                "mods",
                "scripts",
                @"update\x64\dlcpacks\mycar\dlc.rpf",
            }
        )
            Assert.Contains(mod, rels);
        foreach (
            var game in new[]
            {
                "GTA5.exe",
                "GTA5_BE.exe",
                "PlayGTAV.exe",
                "bink2w64.dll",
                "common.rpf",
                "x64a.rpf",
                "steam_api64.dll",
                "Installers",
                "update",
                @"update\x64\dlcpacks\patchday8ng\dlc.rpf",
            }
        )
            Assert.DoesNotContain(game, rels);
    }

    [Fact]
    public void Execute_moves_mods_away_and_keeps_game_files()
    {
        var game = Build();
        var result = VanillaRestore.Execute(game, VanillaRestore.Inspect(game));
        var folder = result.Folder;
        Assert.Empty(result.Failed);
        try
        {
            Assert.False(Directory.Exists(Path.Combine(Root, "mods")));
            Assert.False(File.Exists(Path.Combine(Root, "dinput8.dll")));
            Assert.False(Directory.Exists(Path.Combine(Root, @"update\x64\dlcpacks\mycar")));
            Assert.True(File.Exists(Path.Combine(Root, @"update\x64\dlcpacks\patchday8ng\dlc.rpf")));
            Assert.True(File.Exists(Path.Combine(Root, "GTA5.exe")));
            Assert.True(File.Exists(Path.Combine(folder, "files", "mods", "update", "update.rpf")));
            Assert.True(File.Exists(Path.Combine(folder, "restore.json")));
            Assert.True(VanillaRestore.Inspect(game).Empty);
        }
        finally
        {
            VanillaRestore.DeleteQuarantine(game, folder);
        }
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Busy_item_is_skipped_and_reported_the_rest_is_moved()
    {
        var game = Build();
        VanillaResult result;
        using (
            new FileStream(
                Path.Combine(Root, "mods", "update", "update.rpf"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.None
            )
        )
            result = VanillaRestore.Execute(game, VanillaRestore.Inspect(game));
        try
        {
            Assert.Contains(result.Failed, f => f.StartsWith("mods"));
            Assert.True(Directory.Exists(Path.Combine(Root, "mods")));
            Assert.False(File.Exists(Path.Combine(Root, "dinput8.dll")));
            Assert.DoesNotContain("\"mods\"", File.ReadAllText(Path.Combine(result.Folder, "restore.json")));
        }
        finally
        {
            VanillaRestore.DeleteQuarantine(game, result.Folder);
        }
    }

    [Fact]
    public void Quarantine_delete_refuses_foreign_folders()
    {
        var game = Build();
        Assert.Throws<InvalidOperationException>(() =>
            VanillaRestore.DeleteQuarantine(game, Path.Combine(Root, "mods"))
        );
    }
}
