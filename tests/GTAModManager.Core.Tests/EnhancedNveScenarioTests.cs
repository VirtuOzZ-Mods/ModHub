using System.IO.Compression;
using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.NaturalVision;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

/// <summary>
/// GTA V Enhanced with NaturalVision Enhanced in every order: NVE on a clean game, the modding essentials after it,
/// and NVE removed again. GTA5_Enhanced.exe never loads dinput8.dll, so the loader that counts is xinput1_4.dll.
/// </summary>
public sealed class EnhancedNveScenarioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-enh-nve-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "GTA V Enhanced");
    private readonly BackupLibrary _library;

    public EnhancedNveScenarioTests()
    {
        Directory.CreateDirectory(Game);
        File.WriteAllText(Path.Combine(Game, "GTA5_Enhanced.exe"), "stub");
        _library = BackupLibrary.Create(Path.Combine(_root, "backups"), 1L << 30);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private GameInstall Open()
    {
        var g = GameInstall.Open(Game);
        g.RunningCheckOverride = () => false;
        return g;
    }

    /// <summary>A minimal NaturalVision Enhanced download ("Main Package" with its own ASI loader).</summary>
    private string NveSource()
    {
        var main = Path.Combine(_root, "nve", "Main Package");
        Directory.CreateDirectory(Path.Combine(main, @"onigiri\dlcpacks\NVE"));
        Directory.CreateDirectory(Path.Combine(main, @"onigiri\common\data"));
        File.WriteAllText(Path.Combine(main, "NVE.asi"), "MZ nve");
        File.WriteAllText(Path.Combine(main, "onigiri.asi"), "MZ onigiri");
        File.WriteAllText(Path.Combine(main, "NVE.ini"), "config");
        File.WriteAllText(Path.Combine(main, "xinput1_4.dll"), "MZ enhanced asi loader");
        File.WriteAllText(
            Path.Combine(main, @"onigiri\common\data\dlclist.xml"),
            "<SMandatoryPacksData><Paths><Item>dlcpacks:/NVE/</Item></Paths></SMandatoryPacksData>"
        );
        using (var s = File.Create(Path.Combine(main, @"onigiri\dlcpacks\NVE\dlc.rpf")))
            new Rpf7Builder().AddFile("a.txt", new RpfBinarySource([1, 2, 3])).Write(s);
        return Path.Combine(_root, "nve");
    }

    private NveResult InstallNve(GameInstall g, NveService service)
    {
        var source = NveSource();
        using var bundle = new NveBundle(source);
        var catalog = bundle.Analyze();
        return service.Install(
            new()
            {
                Source = source,
                Stamp = catalog.Stamp,
                Components = catalog.Components.Where(c => c.Core).Select(c => c.Id).ToList(),
            }
        );
    }

    private string ScriptHookZip()
    {
        var path = Path.Combine(_root, "ScriptHookV_3889.0_1158.13.zip");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var e in new[] { "bin/ScriptHookV.dll", "bin/dinput8.dll" })
            using (var w = new StreamWriter(z.CreateEntry(e).Open()))
                w.Write("MZ " + e);
        return path;
    }

    private void Install(GameInstall g, InstallPlan plan)
    {
        Assert.Empty(plan.Errors);
        Assert.Equal(TxState.Completed, new OivInstaller(g).Install(plan, _library).State);
    }

    [Fact]
    public void Nve_first_on_a_clean_game_brings_its_loader_then_the_essentials_fit_around_it()
    {
        var g = Open();
        var service = new NveService(g, _library, Path.Combine(_root, "states"));
        var result = InstallNve(g, service);
        Assert.True(result.Success, result.Message);

        // The Enhanced loader is in place, so NVE (and later any .asi) actually loads; onigiri is the mods loader.
        Assert.Equal("MZ enhanced asi loader", File.ReadAllText(Path.Combine(Game, "xinput1_4.dll")));
        var status = ModdingBase.Inspect(g);
        Assert.True(status.AsiLoader);
        Assert.Equal(GameInstall.OnigiriAsi, status.ModsLoaderName);

        // Essentials afterwards: Script Hook V alone, no useless dinput8.dll; no RageOpenV next to onigiri.
        using (var plan = ModdingBase.PrepareScriptHookV(g, ScriptHookZip()))
            Install(g, plan);
        Assert.True(File.Exists(Path.Combine(Game, "ScriptHookV.dll")));
        Assert.False(File.Exists(Path.Combine(Game, "dinput8.dll")));
        var rage = Path.Combine(_root, "RageOpenV.asi");
        File.WriteAllText(rage, "MZ rage");
        using (var plan = ModdingBase.PrepareRageOpenV(g, rage))
            Assert.False(plan.CanInstall);
        Assert.Equal(BaseSetupStage.ScriptHookVDotNet, ModdingBase.NextSetupStage(ModdingBase.Inspect(g)));
    }

    [Fact]
    public void Essentials_first_then_nve_replaces_the_mods_loader_and_removal_restores_it()
    {
        var g = Open();
        // What the essentials put on Enhanced before this fix: the Legacy loader and RageOpenV.
        File.WriteAllText(Path.Combine(Game, "dinput8.dll"), "MZ legacy loader");
        File.WriteAllText(Path.Combine(Game, "ScriptHookV.dll"), "MZ hook");
        File.WriteAllText(Path.Combine(Game, "RageOpenV.asi"), "MZ rage");
        Assert.False(ModdingBase.Inspect(g).AsiLoader);

        var service = new NveService(g, _library, Path.Combine(_root, "states"));
        var result = InstallNve(g, service);
        Assert.True(result.Success, result.Message);
        Assert.Equal("MZ enhanced asi loader", File.ReadAllText(Path.Combine(Game, "xinput1_4.dll")));
        Assert.False(File.Exists(Path.Combine(Game, "RageOpenV.asi")));
        Assert.Equal(GameInstall.OnigiriAsi, g.CheckModLoader().Name);

        var removed = service.Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(Path.Combine(Game, "onigiri.asi")));
        // The mods loader is back, and the Enhanced ASI loader stays so the other mods keep loading.
        Assert.Equal("MZ rage", File.ReadAllText(Path.Combine(Game, "RageOpenV.asi")));
        Assert.Equal("MZ enhanced asi loader", File.ReadAllText(Path.Combine(Game, "xinput1_4.dll")));
        Assert.True(ModdingBase.Inspect(g).AsiLoader);
    }
}
