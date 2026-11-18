using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

/// <summary>Detection and installation of regular mods from archives (a synthetic game in a temp folder).</summary>
public sealed class ModArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-mod-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "GTA V Enhanced");
    private BackupLibrary? _lib;
    private BackupLibrary Lib => _lib ??= BackupLibrary.Create(Path.Combine(_root, "backups"), 1L << 30);

    private const string DlcListXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SMandatoryPacksData>\r\n\t<Paths>\r\n\t\t<Item>dlcpacks:/mpluxe/</Item>\r\n\t</Paths>\r\n</SMandatoryPacksData>\r\n";

    public ModArchiveTests()
    {
        Directory.CreateDirectory(Game);
        File.WriteAllText(Path.Combine(Game, "GTA5_Enhanced.exe"), "stub");
        File.WriteAllText(Path.Combine(Game, "OpenRPF.asi"), "MZ stub loader");
        Directory.CreateDirectory(Path.Combine(Game, "mods", "update"));
        using (var fs = File.Create(Path.Combine(Game, "mods", "update", "update.rpf")))
            new Rpf7Builder()
                .AddFile(
                    @"common\data\dlclist.xml",
                    new RpfBinarySource(Encoding.UTF8.GetBytes(DlcListXml), Compress: true)
                )
                .Write(fs);
        // An official Rockstar DLC with an encrypted table of contents.
        Directory.CreateDirectory(Path.Combine(Game, "update", "x64", "dlcpacks", "mpluxe"));
        var ng = new byte[512];
        new RpfHeader(Rpf7Format.Magic, 1, 16, (uint)RpfEncryption.Ng).Write(ng);
        File.WriteAllBytes(Path.Combine(Game, "update", "x64", "dlcpacks", "mpluxe", "dlc.rpf"), ng);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
        // WorkDir is shared; fixture cleanup must not delete other transactions.
    }

    private GameInstall OpenGame()
    {
        var g = GameInstall.Open(Game);
        g.RunningCheckOverride = () => false;
        return g;
    }

    private static byte[] Dlc(string nameHash)
    {
        var ms = new MemoryStream();
        new Rpf7Builder()
            .AddFile(
                "setup2.xml",
                new RpfBinarySource(
                    Encoding.UTF8.GetBytes(
                        $"<SSetupData><deviceName>dlc_{nameHash}</deviceName><nameHash>{nameHash}</nameHash></SSetupData>"
                    )
                )
            )
            .AddFile(
                "content.xml",
                new RpfBinarySource(Encoding.UTF8.GetBytes("<CDataFileMgr__ContentsOfDataFileXml/>"))
            )
            .Write(ms);
        return ms.ToArray();
    }

    private string Zip(Dictionary<string, byte[]> files)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8] + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in files)
            using (var s = zip.CreateEntry(name).Open())
                s.Write(data);
        return path;
    }

    private ModAnalysis Analyze(string zip)
    {
        using var a = ModArchive.Open(zip);
        return ModAnalyzer.Analyze(a, OpenGame());
    }

    private string ReadDlcList()
    {
        using var a = Rpf7Archive.OpenRead(Path.Combine(Game, "mods", "update", "update.rpf"));
        return Encoding.UTF8.GetString(a.ReadContent((RpfBinaryFile)a.Find(@"common\data\dlclist.xml")!));
    }

    [Fact]
    public void Addon_dlc_installs_into_mods_registers_in_dlclist_and_undoes()
    {
        var zip = Zip(
            new()
            {
                ["Skyline R34/readme.txt"] = "Install: add to dlclist"u8.ToArray(),
                ["Skyline R34/skyline/dlc.rpf"] = Dlc("skyline"),
            }
        );
        var g = OpenGame();
        using var archive = ModArchive.Open(zip);
        var an = ModAnalyzer.Analyze(archive, g);
        Assert.Empty(an.Errors);
        Assert.Equal("Add-on DLC", an.Summary);
        Assert.Contains("Install: add to dlclist", an.Description);

        using var plan = new OivInstaller(g).Prepare(an.Title, an.Ops, archive);
        Assert.Empty(plan.Errors);
        var r = new OivInstaller(g).Install(plan, Lib);
        Assert.Equal(TxState.Completed, r.State);

        Assert.True(File.Exists(Path.Combine(Game, "mods", "update", "x64", "dlcpacks", "skyline", "dlc.rpf")));
        Assert.Contains("dlcpacks:/skyline/", ReadDlcList());

        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(r.Id, Lib).State);
        Assert.False(File.Exists(Path.Combine(Game, "mods", "update", "x64", "dlcpacks", "skyline", "dlc.rpf")));
        Assert.DoesNotContain("skyline", ReadDlcList());
    }

    [Fact]
    public void Generic_folder_name_falls_back_to_setup2_namehash()
    {
        var an = Analyze(Zip(new() { ["dlc/dlc.rpf"] = Dlc("supercar") }));
        Assert.Equal(@"mods\update\x64\dlcpacks\supercar\dlc.rpf", an.Parts.Single().Target);
    }

    [Fact]
    public void Edition_variant_is_chosen_by_folder_name()
    {
        var an = Analyze(
            Zip(new() { ["Car (Legacy)/car/dlc.rpf"] = Dlc("car"), ["Car (Enhanced)/car/dlc.rpf"] = Dlc("car") })
        );
        Assert.Empty(an.Errors);
        Assert.Equal("Car (Enhanced)/car/dlc.rpf".Replace('/', '\\'), an.Parts.Single().Source);
    }

    [Fact]
    public void Asi_with_config_and_scripts_folder_are_mapped_and_exe_ignored()
    {
        var an = Analyze(
            Zip(
                new()
                {
                    ["Trainer/Trainer.asi"] = [1],
                    ["Trainer/Trainer.ini"] = [2],
                    ["Trainer/scripts/MyScript.dll"] = [3],
                    ["Trainer/scripts/MyScript/config.xml"] = [4],
                    ["Trainer/setup.exe"] = [5],
                }
            )
        );
        Assert.Empty(an.Errors);
        var targets = an.Parts.ToDictionary(p => p.Target, p => p.Kind);
        Assert.Equal("ASI-плагин", targets["Trainer.asi"]);
        Assert.Equal("Настройки ASI", targets["Trainer.ini"]);
        Assert.Equal("Скрипт", targets[@"scripts\MyScript.dll"]);
        Assert.Equal("Скрипт", targets[@"scripts\MyScript\config.xml"]);
        Assert.DoesNotContain(an.Parts, p => p.Source.EndsWith(".exe"));
        Assert.Contains(an.Notes, n => n.Contains("setup.exe"));
    }

    [Fact]
    public void Name_of_official_rockstar_dlc_is_refused()
    {
        var an = Analyze(Zip(new() { ["mpluxe/dlc.rpf"] = Dlc("mpluxe") }));
        Assert.Contains(an.Errors, e => e.Contains("Rockstar"));
    }

    [Fact]
    public void Unsafe_paths_are_skipped()
    {
        var path = Path.Combine(_root, "evil.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("../../evil.asi").Open())
                s.Write([1]);
            using (var s = zip.CreateEntry("good.asi").Open())
                s.Write([2]);
        }
        var an = Analyze(path);
        Assert.Equal(["good.asi"], an.Parts.Select(p => p.Target));
        Assert.Contains(an.Notes, n => n.Contains("небезопасный"));
    }

    [Fact]
    public void Nested_oiv_for_our_edition_is_detected_and_uninstaller_skipped()
    {
        var an = Analyze(
            Zip(
                new()
                {
                    ["Mod/Legacy/Install.oiv"] = [1],
                    ["Mod/Enhanced/Install.oiv"] = [2],
                    ["Mod/Enhanced/Uninstall.oiv"] = [3],
                    ["Mod/Enhanced/NOS_1.9.5_Enhanced_Uninstall.oiv"] = [4],
                }
            )
        );
        Assert.Equal([@"Mod\Enhanced\Install.oiv"], an.NestedOiv);
    }

    [Fact]
    public void Other_edition_files_are_not_reported_as_unknown()
    {
        var an = Analyze(
            Zip(
                new()
                {
                    ["Tool/Enhanced/scripts/Tool.dll"] = [1],
                    ["Tool/Legacy/scripts/Tool.dll"] = [2],
                    ["Tool/Legacy/Tool.asi"] = [3],
                }
            )
        );
        Assert.Equal([@"scripts\Tool.dll"], an.Parts.Select(p => p.Target));
        Assert.DoesNotContain(an.Notes, n => n.Contains("Не распознано"));
        Assert.Contains(an.Notes, n => n.Contains("другой версии"));
    }

    [Fact]
    public void Replace_only_mod_is_reported_not_guessed()
    {
        var an = Analyze(Zip(new() { ["adder.yft"] = [1], ["adder.ytd"] = [2] }));
        Assert.Empty(an.Ops);
        Assert.NotEmpty(an.Errors);
        Assert.Contains(an.Notes, n => n.Contains("замены моделей"));
    }
}
