using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

/// <summary>
/// ScriptHookVDotNet mods that must sit in their own folder under scripts\ (Autosport Racing System:
/// scripts\AutosportRacingSystem\ARS.dll + Tracks\, Settings\…), next to the plain scripts rules that must not change.
/// </summary>
public sealed class ScriptFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-scriptdir-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "GTA V");
    private BackupLibrary? _lib;
    private BackupLibrary Lib => _lib ??= BackupLibrary.Create(Path.Combine(_root, "backups"), 1L << 30);

    private const string ArsReadme =
        "Autosport Racing System (ARS)\r\n\r\nInstall: extract the AutosportRacingSystem folder into\r\nGrand Theft Auto V\\Scripts\\ (overwrite if updating).\r\n";

    public ScriptFolderTests()
    {
        Directory.CreateDirectory(Game);
        File.WriteAllText(Path.Combine(Game, "GTA5.exe"), "stub");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private GameInstall OpenGame()
    {
        var g = GameInstall.Open(Game);
        g.RunningCheckOverride = () => false;
        return g;
    }

    /// <summary>A minimal managed assembly that references the given ScriptHookVDotNet API.</summary>
    internal static byte[] ShvdnAssembly(string name, string api = "ScriptHookVDotNet2")
    {
        var md = new MetadataBuilder();
        md.AddModule(0, md.GetOrAddString(name + ".dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
        md.AddAssembly(
            md.GetOrAddString(name),
            new Version(1, 0, 0, 0),
            default,
            default,
            default,
            AssemblyHashAlgorithm.Sha1
        );
        md.AddAssemblyReference(md.GetOrAddString(api), new Version(2, 11, 6, 0), default, default, default, default);
        md.AddTypeDefinition(
            default,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1)
        );
        var pe = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
            new MetadataRootBuilder(md),
            new BlobBuilder()
        );
        var blob = new BlobBuilder();
        pe.Serialize(blob);
        return blob.ToArray();
    }

    private static byte[] T(string s) => Encoding.UTF8.GetBytes(s);

    private string Zip(Dictionary<string, byte[]> files)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8] + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in files)
            using (var s = zip.CreateEntry(name).Open())
                s.Write(data);
        return path;
    }

    private Dictionary<string, byte[]> Ars(string prefix) =>
        new()
        {
            [prefix + "ARS.dll"] = ShvdnAssembly("ARS"),
            [prefix + "LemonUI.SHVDN2.dll"] = ShvdnAssembly("LemonUI.SHVDN2"),
            [prefix + "README.txt"] = T(ArsReadme),
            [prefix + "LICENSE"] = T("PolyForm Noncommercial 1.0.0"),
            [prefix + "Drivers/names.txt"] = T("Driver One"),
            [prefix + "Settings/Menu-Race.ini"] = T("[Race]"),
            [prefix + "Settings/tips.txt"] = T("Tip"),
            [prefix + "Tracks/The Lake.xml"] = T("<Track/>"),
            [prefix + "Vehicles/cars.txt"] = T("sultan"),
        };

    private ModAnalysis Analyze(string path)
    {
        using var a = ModArchive.Open(path);
        return ModAnalyzer.Analyze(a, OpenGame());
    }

    private static Dictionary<string, string> Targets(ModAnalysis an) =>
        an.Parts.ToDictionary(p => p.Source.Replace('\\', '/'), p => p.Target, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Script_folder_is_installed_whole_into_scripts_with_its_name()
    {
        var an = Analyze(Zip(Ars("AutosportRacingSystem/")));
        Assert.Empty(an.Errors);
        Assert.Equal("Скрипты", an.Summary);
        var t = Targets(an);
        Assert.Equal(9, t.Count);
        Assert.Equal(@"scripts\AutosportRacingSystem\ARS.dll", t["AutosportRacingSystem/ARS.dll"]);
        Assert.Equal(@"scripts\AutosportRacingSystem\Tracks\The Lake.xml", t["AutosportRacingSystem/Tracks/The Lake.xml"]);
        // Data .txt files are part of the script, not documentation.
        Assert.Equal(@"scripts\AutosportRacingSystem\Drivers\names.txt", t["AutosportRacingSystem/Drivers/names.txt"]);
        Assert.Equal(@"scripts\AutosportRacingSystem\Settings\tips.txt", t["AutosportRacingSystem/Settings/tips.txt"]);
        Assert.All(an.Parts, p => Assert.Equal("Скрипт", p.Kind));
    }

    [Fact]
    public void Script_folder_inside_a_download_wrapper_keeps_only_its_own_name()
    {
        var an = Analyze(Zip(Ars("ARS 0.9.0.1/AutosportRacingSystem/")));
        Assert.Empty(an.Errors);
        Assert.Equal(@"scripts\AutosportRacingSystem\ARS.dll", Targets(an)["ARS 0.9.0.1/AutosportRacingSystem/ARS.dll"]);
    }

    [Fact]
    public void Archive_with_scripts_folder_does_not_double_the_path()
    {
        var an = Analyze(Zip(Ars("Scripts/AutosportRacingSystem/")));
        Assert.Empty(an.Errors);
        var t = Targets(an);
        Assert.Equal(@"scripts\AutosportRacingSystem\ARS.dll", t["Scripts/AutosportRacingSystem/ARS.dll"]);
        Assert.Equal(
            @"scripts\AutosportRacingSystem\Tracks\The Lake.xml",
            t["Scripts/AutosportRacingSystem/Tracks/The Lake.xml"]
        );
        Assert.DoesNotContain(an.Parts, p => p.Target.Contains(@"scripts\scripts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dropped_script_folder_takes_the_name_of_the_folder_itself()
    {
        var dir = Path.Combine(_root, "drop", "AutosportRacingSystem");
        foreach (var (name, data) in Ars(""))
        {
            var file = Path.Combine(dir, name.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, data);
        }
        var an = Analyze(dir);
        Assert.Empty(an.Errors);
        var t = Targets(an);
        Assert.Equal(@"scripts\AutosportRacingSystem\ARS.dll", t["AutosportRacingSystem/ARS.dll"]);
        Assert.Equal(
            @"scripts\AutosportRacingSystem\Settings\Menu-Race.ini",
            t["AutosportRacingSystem/Settings/Menu-Race.ini"]
        );
    }

    [Fact]
    public void Folder_without_resources_needs_the_readme_to_name_it()
    {
        var withReadme = Analyze(
            Zip(new() { ["CoolScript/CoolScript.dll"] = ShvdnAssembly("CoolScript"), ["CoolScript/readme.txt"] = T("Copy the CoolScript folder into your scripts folder.") })
        );
        Assert.Equal(@"scripts\CoolScript\CoolScript.dll", Targets(withReadme)["CoolScript/CoolScript.dll"]);

        // Without resources or an instruction this is a plain download folder: nothing is guessed.
        var bare = Analyze(Zip(new() { ["CoolScript v2/CoolScript.dll"] = ShvdnAssembly("CoolScript") }));
        Assert.Empty(bare.Parts);
        Assert.NotEmpty(bare.Errors);
    }

    [Fact]
    public void Wrapper_with_assembly_and_its_sibling_data_folder_is_not_a_script_folder()
    {
        // N.O.S style: Name.dll + Name\ live directly in scripts\, the outer folder is only the download wrapper.
        var an = Analyze(
            Zip(
                new()
                {
                    ["NOS 1.9.5/NOS.dll"] = ShvdnAssembly("NOS"),
                    ["NOS 1.9.5/NOS.ini"] = T("[NOS]"),
                    ["NOS 1.9.5/NOS/HUD/bar.png"] = [1],
                }
            )
        );
        Assert.DoesNotContain(an.Parts, p => p.Target.StartsWith(@"scripts\NOS 1.9.5", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Non_script_dll_folders_are_not_treated_as_scripts()
    {
        var an = Analyze(Zip(new() { ["Tool/Helper.dll"] = [0x4D, 0x5A, 1, 2], ["Tool/Data/x.xml"] = T("<x/>") }));
        Assert.Empty(an.Parts);
    }

    [Fact]
    public void Plain_scripts_folder_and_asi_still_install_as_before()
    {
        var an = Analyze(
            Zip(
                new()
                {
                    ["Trainer/Trainer.asi"] = [1],
                    ["Trainer/Trainer.ini"] = [2],
                    ["Trainer/scripts/MyScript.dll"] = ShvdnAssembly("MyScript"),
                    ["Trainer/scripts/MyScript.ini"] = [3],
                    ["Trainer/scripts/MyScript/config.xml"] = [4],
                }
            )
        );
        Assert.Empty(an.Errors);
        var t = an.Parts.ToDictionary(p => p.Target, p => p.Kind);
        Assert.Equal("ASI-плагин", t["Trainer.asi"]);
        Assert.Equal("Настройки ASI", t["Trainer.ini"]);
        Assert.Equal("Скрипт", t[@"scripts\MyScript.dll"]);
        Assert.Equal("Скрипт", t[@"scripts\MyScript.ini"]);
        Assert.Equal("Скрипт", t[@"scripts\MyScript\config.xml"]);
        Assert.Equal(5, an.Parts.Count);
    }

    [Fact]
    public void Script_folder_installs_into_the_game_and_undoes()
    {
        var g = OpenGame();
        using var archive = ModArchive.Open(Zip(Ars("AutosportRacingSystem/")));
        var an = ModAnalyzer.Analyze(archive, g);
        Assert.Empty(an.Errors);
        using var plan = new OivInstaller(g).Prepare(an.Title, an.Ops, archive);
        Assert.Empty(plan.Errors);
        var r = new OivInstaller(g).Install(plan, Lib);
        Assert.Equal(TxState.Completed, r.State);
        var dir = Path.Combine(Game, "scripts", "AutosportRacingSystem");
        Assert.True(File.Exists(Path.Combine(dir, "ARS.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "Tracks", "The Lake.xml")));
        Assert.True(File.Exists(Path.Combine(dir, "Drivers", "names.txt")));

        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(r.Id, Lib).State);
        Assert.False(File.Exists(Path.Combine(dir, "ARS.dll")));
    }
}
