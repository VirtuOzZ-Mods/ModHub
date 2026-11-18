using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using Xunit;

namespace GTAModManager.Core.Tests;

/// <summary>ModHub is portable: everything it keeps for itself lives in its own folder.</summary>
public class AppPathsTests
{
    [Fact]
    public void Marker_points_to_the_program_folder()
    {
        var root = Directory.CreateTempSubdirectory("gmm-portable-").FullName;
        try
        {
            var program = Directory.CreateDirectory(Path.Combine(root, "Program")).FullName;
            Assert.Equal(program, AppPaths.ResolveRoot(program));
            File.WriteAllText(Path.Combine(program, AppPaths.Marker), "Portable\nroot=..\n");
            Assert.Equal(root, AppPaths.ResolveRoot(program));
            // Marker of older builds.
            File.WriteAllText(Path.Combine(program, AppPaths.Marker), "Portable\ndata=..\\ModHub-Data\n");
            Assert.Equal(root, AppPaths.ResolveRoot(program));
            // Absolute paths are ignored: the data must stay inside the program folder.
            File.WriteAllText(Path.Combine(program, AppPaths.Marker), "root=C:\\Elsewhere\n");
            Assert.Equal(program, AppPaths.ResolveRoot(program));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Old_data_is_moved_into_the_program_folder_layout()
    {
        var temp = Directory.CreateTempSubdirectory("gmm-migrate-").FullName;
        try
        {
            string root = Path.Combine(temp, "Mod Hub");
            string old = Path.Combine(temp, "LocalAppData", "GTAModManager");
            string oldPortable = Path.Combine(root, "ModHub-Data");
            Put(old, "settings.json", "{\"BackupDir\":" + System.Text.Json.JsonSerializer.Serialize(Path.Combine(oldPortable, "Backups")) + "}");
            Put(old, @"keys\keys-1.bin", "k");
            Put(old, "magic.dat", "m");
            Put(old, @"webview\EBWebView\Local State", "w");
            Put(old, @"languages\my.json", "{}");
            Put(old, "installed-mods.json", "[]");
            Put(old, @"naturalvision\scope\state.json", "{}");
            Put(old, @"discover\page.json", "{}");
            Put(old, "GMMPedSelector.log", "log");
            Put(old, @"temp\junk.tmp", "x");
            Put(oldPortable, @"Backups\library.json", "{}");
            Put(oldPortable, "mod-labels.json", "{}");

            var result = AppDataMigration.Run(root, [old, oldPortable], deleteSources: true);

            Assert.Equal(0, result.Failed);
            Assert.True(File.Exists(Path.Combine(root, "Settings.json")));
            Assert.True(File.Exists(Path.Combine(root, @"Languages\my.json")));
            Assert.True(File.Exists(Path.Combine(root, @"Data\Game keys\Cache\keys-1.bin")));
            Assert.True(File.Exists(Path.Combine(root, @"Data\Game keys\magic.dat")));
            Assert.True(File.Exists(Path.Combine(root, @"Data\Browser\EBWebView\Local State")));
            Assert.True(File.Exists(Path.Combine(root, @"Data\Library\installed-mods.json")));
            Assert.True(File.Exists(Path.Combine(root, @"Data\Library\mod-labels.json")));
            Assert.True(File.Exists(Path.Combine(root, @"Data\Library\naturalvision\scope\state.json")));
            Assert.True(File.Exists(Path.Combine(root, @"Data\Cache\Mod pages\page.json")));
            Assert.True(File.Exists(Path.Combine(root, @"Data\Backups\library.json")));
            Assert.False(Directory.Exists(old));
            Assert.False(Directory.Exists(oldPortable));
            Assert.Empty(Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories));
            Assert.Contains(
                Path.Combine(root, @"Data\Backups"),
                File.ReadAllText(Path.Combine(root, "Settings.json")).Replace("\\\\", "\\")
            );
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Theory]
    // gmm.exe started by the window inherits TEMP = Data\Temp: the job files there must survive its startup.
    [InlineData(@"D:\Mod Hub\Data\Temp\", @"D:\Mod Hub", null)]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\", @"D:\Mod Hub", @"C:\Users\u\AppData\Local\Temp\ModHub")]
    public void Old_temp_leftovers_are_never_the_programs_own_temp(string temp, string root, string? expected)
    {
        Assert.Equal(expected, AppDataMigration.LegacyTempLeftovers(temp, root));
    }

    [Fact]
    public void Existing_new_data_wins_and_copy_mode_keeps_the_old_folder()
    {
        var temp = Directory.CreateTempSubdirectory("gmm-migrate-").FullName;
        try
        {
            string root = Path.Combine(temp, "Mod Hub");
            string old = Path.Combine(temp, "old");
            Put(old, "settings.json", "old");
            Put(root, "Settings.json", "new");
            Put(old, "custom-cards.json", "cards");

            AppDataMigration.Run(root, [old], deleteSources: false);

            Assert.Equal("new", File.ReadAllText(Path.Combine(root, "Settings.json")));
            Assert.Equal("cards", File.ReadAllText(Path.Combine(root, @"Data\Library\custom-cards.json")));
            Assert.True(File.Exists(Path.Combine(old, "settings.json")));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void Service_folder_is_renamed_and_journal_paths_follow()
    {
        var temp = Directory.CreateTempSubdirectory("gmm-workdir-").FullName;
        try
        {
            string legacy = Path.Combine(temp, ".gmm", "Enhanced");
            string current = Path.Combine(temp, "ModHub", "Enhanced");
            string staging = Path.Combine(legacy, "tx", "1");
            string journal = System.Text.Json.JsonSerializer.Serialize(
                new { StagingDir = staging, Ops = new[] { new { Backup = Path.Combine(staging, "backup", "0") } } }
            );
            Put(legacy, @"journals\1.json", journal);
            Put(legacy, @"disabled-mods\scope\journal.json", journal);
            Put(legacy, @"tx\1\new\0\mod.json", journal); // a mod file: never edited

            Assert.True(WorkDirMigration.Migrate(legacy, current));

            Assert.False(Directory.Exists(Path.Combine(temp, ".gmm")));
            foreach (var state in new[] { @"journals\1.json", @"disabled-mods\scope\journal.json" })
            {
                string moved = File.ReadAllText(Path.Combine(current, state));
                Assert.DoesNotContain(".gmm", moved);
                Assert.Contains(System.Text.Json.JsonSerializer.Serialize(Path.Combine(current, "tx", "1")).Trim('"'), moved);
            }
            Assert.Equal(journal, File.ReadAllText(Path.Combine(current, @"tx\1\new\0\mod.json")));
            Assert.False(File.Exists(Path.Combine(current, "rename-from-gmm-pending.txt")));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void Service_folder_name_on_the_game_drive()
    {
        Assert.Equal(@"D:\ModHub\Legacy", WorkDirMigration.Current(@"D:\Games\GTA V", GameEdition.Legacy));
        Assert.Equal(@"D:\.gmm\Enhanced", WorkDirMigration.Legacy(@"D:\Games\GTA V", GameEdition.Enhanced));
    }

    /// <summary>
    /// Guards the "deleting the folder removes ModHub" promise in the source: user-profile folders and the registry
    /// may only be touched by the files that are allowed to.
    /// </summary>
    [Theory]
    [InlineData(@"SpecialFolder\.(LocalApplicationData|ApplicationData|CommonApplicationData)", "AppDataMigration.cs")]
    [InlineData(@"Registry\.\w+\.(CreateSubKey|DeleteSubKey|DeleteSubKeyTree|DeleteValue)|\.SetValue\(", "FileAssociation.cs")]
    [InlineData(
        @"SpecialFolder\.MyDocuments",
        "AppPaths.cs|GameReadiness.cs|PedDiscoveryService.cs|PedInstaller.cs" // AppPaths: read-only fallback; others only read Rockstar Games
    )]
    [InlineData(@"""\.gmm""", "WorkDirMigration.cs")]
    public void Only_allowed_files_write_outside_the_program_folder(string pattern, string allowed)
    {
        var allowedFiles = allowed.Split('|');
        var offenders = SourceFiles()
            .Where(f => !allowedFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), pattern))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(@"src\GTAModManager.App\App.xaml.cs")]
    [InlineData(@"src\GTAModManager.Cli\Program.cs")]
    public void Entry_points_keep_temp_files_in_the_program_folder(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), file));
        Assert.Contains("AppPaths.UseOwnTemp()", text);
        Assert.Contains("AppDataMigration.Run(", text);
    }

    [Fact]
    public void All_paths_are_inside_the_root()
    {
        string[] paths =
        [
            AppPaths.SettingsFile,
            AppPaths.Languages,
            AppPaths.Data,
            AppPaths.GameKeys,
            AppPaths.KeyCache,
            AppPaths.MagicDat,
            AppPaths.Browser,
            AppPaths.Downloads,
            AppPaths.Cache,
            AppPaths.Library,
            AppPaths.Logs,
            AppPaths.Temp,
        ];
        Assert.All(paths, p => Assert.StartsWith(AppPaths.Root + "\\", p, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SourceFiles() =>
        new[] { "GTAModManager.App", "GTAModManager.Core", "GTAModManager.Cli" }
            .SelectMany(p =>
                Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", p), "*.cs", SearchOption.AllDirectories)
            )
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "GTAModManager.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("GTAModManager.slnx not found above the test folder.");
    }

    private static void Put(string dir, string rel, string text)
    {
        var path = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void TryDelete(string dir, bool onlyIfEmpty = false)
    {
        try
        {
            if (!Directory.Exists(dir))
                return;
            if (onlyIfEmpty && Directory.EnumerateFileSystemEntries(dir).Any())
                return;
            Directory.Delete(dir, true);
        }
        catch (IOException) { }
    }
}
