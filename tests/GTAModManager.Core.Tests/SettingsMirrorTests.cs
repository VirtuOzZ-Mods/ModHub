using System.Text.Json.Nodes;
using GTAModManager.Core.Library;
using Xunit;

namespace GTAModManager.Core.Tests;

/// <summary>The backup folder keeps a copy of Mod Hub's settings so a reinstall can pick them up.</summary>
public class SettingsMirrorTests
{
    [Fact]
    public void Copy_round_trips_into_a_fresh_program_folder()
    {
        var temp = Directory.CreateTempSubdirectory("gmm-mirror-").FullName;
        try
        {
            string oldRoot = Path.Combine(temp, "old");
            string newRoot = Path.Combine(temp, "new");
            string backups = Directory.CreateDirectory(Path.Combine(temp, "Backups")).FullName;
            Put(oldRoot, "Settings.json", "{\"BackupDir\":\"X:\\\\Gone\",\"LanguagePackId\":\"official.en\"}");
            Put(oldRoot, @"Data\Library\installed-mods.json", "[1]");
            Put(oldRoot, @"Data\Library\keybind-confirmations\a.json", "{}");
            Put(oldRoot, @"Languages\mine.json", "{}");
            Put(oldRoot, @"Data\Browser\cookies", "secret"); // never copied

            SettingsMirror.Save(backups, oldRoot);
            Put(oldRoot, @"Data\Library\installed-mods.json", "[2]");
            SettingsMirror.Save(backups, oldRoot); // a second save replaces the copy

            Assert.True(SettingsMirror.Exists(backups));
            Assert.False(Directory.Exists(SettingsMirror.For(backups) + ".new"));
            Assert.False(File.Exists(Path.Combine(SettingsMirror.For(backups), "Browser", "cookies")));

            SettingsMirror.Restore(backups, newRoot);

            Assert.Equal("[2]", File.ReadAllText(Path.Combine(newRoot, @"Data\Library\installed-mods.json")));
            Assert.True(File.Exists(Path.Combine(newRoot, @"Data\Library\keybind-confirmations\a.json")));
            Assert.True(File.Exists(Path.Combine(newRoot, @"Languages\mine.json")));
            var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(newRoot, "Settings.json")))!;
            Assert.Equal(backups, settings["BackupDir"]!.GetValue<string>());
            Assert.Equal("official.en", settings["LanguagePackId"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void Backup_reset_tolerates_the_settings_copy()
    {
        var temp = Directory.CreateTempSubdirectory("gmm-mirror-").FullName;
        try
        {
            var library = BackupLibrary.Create(Path.Combine(temp, "Backups"), 1L << 30);
            Put(temp, "Settings.json", "{}");
            SettingsMirror.Save(library.Root, temp);

            library.Reset();

            Assert.True(SettingsMirror.Exists(library.Root));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static void Put(string dir, string rel, string text)
    {
        var path = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
