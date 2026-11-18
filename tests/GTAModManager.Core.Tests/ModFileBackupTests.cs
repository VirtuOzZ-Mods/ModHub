using GTAModManager.Core.Game;
using GTAModManager.Core.Inventory;

namespace GTAModManager.Core.Tests;

public sealed class ModFileBackupTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("gmm-mod-backup-").FullName;

    [Fact]
    public void Copies_own_files_and_skips_shared_game_archives()
    {
        string game = Path.Combine(root, "game"),
            backups = Path.Combine(root, "backups");
        void Put(string rel, string text)
        {
            string path = Path.Combine(game, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Put("GTA5.exe", "exe");
        Put(@"update\update.rpf", "vanilla");
        Put(@"mods\update\update.rpf", "modded");
        Put(@"mods\update\x64\dlcpacks\mycar\dlc.rpf", "car");
        Put(@"scripts\Racing\Racing.dll", "dll");

        var result = ModFileBackup.Save(
            GameInstall.Open(game),
            backups,
            "My: Car?",
            [@"mods\update\update.rpf", @"mods\update\x64\dlcpacks\mycar\dlc.rpf", @"scripts\Racing\Racing.dll"]
        );

        string folder = Path.Combine(backups, ModFileBackup.FolderName, "My_ Car_");
        Assert.Equal(folder, result.Folder);
        Assert.Equal(2, result.Copied);
        Assert.Equal(@"mods\update\update.rpf", Assert.Single(result.Skipped));
        Assert.Equal("car", File.ReadAllText(Path.Combine(folder, @"mods\update\x64\dlcpacks\mycar\dlc.rpf")));
        Assert.Equal("dll", File.ReadAllText(Path.Combine(folder, @"scripts\Racing\Racing.dll")));
        Assert.False(File.Exists(Path.Combine(folder, @"mods\update\update.rpf")));
    }

    public void Dispose() => Directory.Delete(root, true);
}
