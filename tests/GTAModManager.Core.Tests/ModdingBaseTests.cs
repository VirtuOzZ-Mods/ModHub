using System.IO.Compression;
using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class ModArchiveExtrasTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gmm-extras-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void Optional_asi_next_to_oiv_is_offered_and_manual_copy_is_ignored()
    {
        Directory.CreateDirectory(_dir);
        var root = Path.Combine(_dir, "game");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "GTA5.exe"), "synthetic");
        var game = GameInstall.Open(root);
        var zip = Path.Combine(_dir, "mod.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(text);
            }
            Add("Mod/GTA_V_Legacy/Mod_Install.oiv", "PK");
            Add("Mod/GTA_V_Legacy/Mod_Uninstall.oiv", "PK");
            Add("Mod/Manual Installation/Graphics.asi", "MZ manual copy");
            Add("Mod/Screen Filters ASI/Graphics.asi", "MZ filters");
            Add(
                "Mod/Screen Filters ASI/README_EN.txt",
                "Graphics.asi adds extra screen filters. It is optional: if you do not need it, do not install it."
            );
            Add("Mod/Screen Filters ASI/README_RU.txt", "Graphics.asi добавляет экранные фильтры. Он необязателен.");
        }
        using var archive = GTAModManager.Core.Mods.ModArchive.Open(zip);
        var a = GTAModManager.Core.Mods.ModAnalyzer.Analyze(archive, game);
        Assert.Single(a.NestedOiv);
        var extra = Assert.Single(a.Extras);
        Assert.Contains("Screen Filters ASI", extra.Source);
        Assert.True(extra.Optional);
        Assert.Contains("экранные фильтры", extra.Note); // Russian README first
    }
}

public sealed partial class ModdingBaseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gmm-base-test-" + Guid.NewGuid().ToString("N"));
    private readonly string files;
    private readonly BackupLibrary library;

    public ModdingBaseTests()
    {
        Directory.CreateDirectory(root);
        files = Path.Combine(Path.GetTempPath(), "gmm-base-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(files);
        library = BackupLibrary.Create(Path.Combine(files, "backups"), 1L << 30);
    }

    public void Dispose()
    {
        foreach (var d in new[] { root, files })
            try
            {
                Directory.Delete(d, true);
            }
            catch (IOException) { }
    }

    private GameInstall Game(GameEdition edition)
    {
        File.WriteAllText(
            Path.Combine(root, edition == GameEdition.Enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe"),
            "synthetic game"
        );
        var g = GameInstall.Open(root);
        g.RunningCheckOverride = () => false;
        return g;
    }

    private string Zip(string name, params string[] entries)
    {
        var path = Path.Combine(files, name);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var e in entries)
        {
            using var s = new StreamWriter(z.CreateEntry(e).Open());
            s.Write(e.EndsWith(".ini") ? "[Settings]\n" : "MZ synthetic " + e);
        }
        return path;
    }

    private void Install(GameInstall g, InstallPlan plan)
    {
        Assert.True(plan.CanInstall, string.Join("; ", plan.Errors));
        Assert.Equal(TxState.Completed, new OivInstaller(g).Install(plan, library).State);
    }

    [Fact]
    public void Script_hook_installs_only_runtime_and_asi_loader_not_trainer()
    {
        var g = Game(GameEdition.Legacy);
        var zip = Zip(
            "ScriptHookV_3889.0_1158.13.zip",
            "bin/ScriptHookV.dll",
            "bin/dinput8.dll",
            "bin/NativeTrainer.asi",
            "readme.txt"
        );
        using (var plan = ModdingBase.PrepareScriptHookV(g, zip))
            Install(g, plan);
        Assert.True(File.Exists(Path.Combine(root, "ScriptHookV.dll")));
        Assert.True(File.Exists(Path.Combine(root, "dinput8.dll")));
        Assert.False(File.Exists(Path.Combine(root, "NativeTrainer.asi")));
        var s = ModdingBase.Inspect(g);
        Assert.True(s.ScriptHookV && s.AsiLoader);
        // Second run: everything is already there.
        using var again = ModdingBase.PrepareScriptHookV(g, zip);
        Assert.False(again.CanInstall);
    }

    [Fact]
    public void Existing_asi_loader_is_not_replaced()
    {
        var g = Game(GameEdition.Legacy);
        File.WriteAllText(Path.Combine(root, "dinput8.dll"), "MZ users own loader");
        using (
            var plan = ModdingBase.PrepareScriptHookV(
                g,
                Zip("ScriptHookV_3889.0_1158.13.zip", "bin/ScriptHookV.dll", "bin/dinput8.dll")
            )
        )
            Install(g, plan);
        Assert.Equal("MZ users own loader", File.ReadAllText(Path.Combine(root, "dinput8.dll")));
    }

    [Theory]
    [InlineData("ScriptHookV_3889.0_1158.13.zip", "1.0.3889.0", true)]
    [InlineData("ScriptHookV_3889.0_1158.13.zip", "1.0.1158.13", true)]
    [InlineData("ScriptHookV_3889.0_1158.13.zip", "1.0.1158.16", true)] // hotfix of the same build
    [InlineData("ScriptHookV_3889.0_1158.13.zip", "1.0.1159.0", false)]
    [InlineData("ScriptHookV_3889.0_1158.13.zip", "1.0.388.0", false)]
    [InlineData("ScriptHookV_3889.0_1158.13.zip", "1.0.3900.0", false)]
    [InlineData("ScriptHookV_3889.0_1158.13.zip", null, true)]
    public void Script_hook_version_must_match_game_patch(string zip, string? version, bool ok) =>
        Assert.Equal(ok, ModdingBase.ScriptHookSupports(zip, version));

    [Fact]
    public void Wrong_archive_is_rejected()
    {
        var g = Game(GameEdition.Legacy);
        using var sdk = ModdingBase.PrepareScriptHookV(g, Zip("ScriptHookV_SDK_1.0.617.1a.zip", "inc/main.h"));
        Assert.NotEmpty(sdk.Errors);
        using var other = ModdingBase.PrepareShvdn(
            g,
            ShvdnVariant.Nightly,
            Zip("Something.zip", "trainer.asi", "a.dll")
        );
        Assert.NotEmpty(other.Errors);
    }

    [Fact]
    public void Shvdn_nightly_copies_runtime_files_and_keeps_user_ini()
    {
        var g = Game(GameEdition.Legacy);
        File.WriteAllText(Path.Combine(root, "ScriptHookVDotNet.ini"), "user settings");
        var zip = Zip(
            "ScriptHookVDotNet-v3.7.0-nightly.204.zip",
            "ScriptHookVDotNet-Loader.asi",
            "ScriptHookVDotNet.dll",
            "ScriptHookVDotNet2.dll",
            "ScriptHookVDotNet3.dll",
            "ScriptHookVDotNet.ini",
            "ScriptHookVDotNet.pdb",
            "Docs/ScriptHookVDotNet3.xml",
            "Licenses/LICENSE.txt"
        );
        using (var plan = ModdingBase.PrepareShvdn(g, ShvdnVariant.Nightly, zip))
            Install(g, plan);
        foreach (
            var f in new[]
            {
                "ScriptHookVDotNet-Loader.asi",
                "ScriptHookVDotNet.dll",
                "ScriptHookVDotNet2.dll",
                "ScriptHookVDotNet3.dll",
            }
        )
            Assert.True(File.Exists(Path.Combine(root, f)), f);
        Assert.False(File.Exists(Path.Combine(root, "ScriptHookVDotNet.pdb")));
        Assert.False(Directory.Exists(Path.Combine(root, "Docs")));
        Assert.Equal("user settings", File.ReadAllText(Path.Combine(root, "ScriptHookVDotNet.ini")));
        Assert.True(ModdingBase.Inspect(g).Shvdn);
        // The second variant is not installed over the first (otherwise SHVDN would load twice).
        using var other = ModdingBase.PrepareShvdn(
            g,
            ShvdnVariant.Enhanced,
            Zip(
                "ScriptHookVDotNetEnhanced-v1.1.0.6.zip",
                "ScriptHookVDotNet.asi",
                "MinHook.x64.dll",
                "ScriptHookVDotNet2.dll",
                "ScriptHookVDotNet3.dll"
            )
        );
        Assert.NotEmpty(other.Errors);
    }

    [Fact]
    public void Enhanced_accepts_only_shvdn_enhanced()
    {
        var g = Game(GameEdition.Enhanced);
        Assert.Equal([ShvdnVariant.Enhanced], ModdingBase.ShvdnChoices(GameEdition.Enhanced));
        using var nightly = ModdingBase.PrepareShvdn(
            g,
            ShvdnVariant.Nightly,
            Zip("ScriptHookVDotNet-v3.7.0-nightly.204.zip", "ScriptHookVDotNet-Loader.asi", "ScriptHookVDotNet.dll")
        );
        Assert.NotEmpty(nightly.Errors);
        using (
            var plan = ModdingBase.PrepareShvdn(
                g,
                ShvdnVariant.Enhanced,
                Zip(
                    "ScriptHookVDotNetEnhanced-v1.1.0.6.zip",
                    "ScriptHookVDotNet.asi",
                    "MinHook.x64.dll",
                    "ScriptHookVDotNet2.dll",
                    "ScriptHookVDotNet3.dll",
                    "Debug/ScriptHookVDotNet.pdb"
                )
            )
        )
            Install(g, plan);
        Assert.True(File.Exists(Path.Combine(root, "MinHook.x64.dll")));
        Assert.False(Directory.Exists(Path.Combine(root, "Debug")));
    }

    [Theory]
    [InlineData(GameEdition.Legacy)]
    [InlineData(GameEdition.Enhanced)]
    public void Setup_steps_follow_installed_components_and_do_not_require_network_for_rage(GameEdition edition)
    {
        var game = Game(edition);
        Assert.Equal(BaseSetupStage.ModsLoader, ModdingBase.NextSetupStage(ModdingBase.Inspect(game)));
        var asi = Path.Combine(files, "RageOpenV.asi");
        File.WriteAllText(asi, "MZ own plugin");
        using (var plan = ModdingBase.PrepareRageOpenV(game, asi))
            Install(game, plan);
        Assert.Equal(BaseSetupStage.ScriptHookV, ModdingBase.NextSetupStage(ModdingBase.Inspect(game)));
        Assert.False(File.Exists(Path.Combine(root, "ScriptHookV.dll")));
        File.WriteAllText(Path.Combine(root, "ScriptHookV.dll"), "MZ hook");
        Assert.Equal(BaseSetupStage.ScriptHookV, ModdingBase.NextSetupStage(ModdingBase.Inspect(game)));
        File.WriteAllText(Path.Combine(root, "dinput8.dll"), "MZ asi loader");
        if (edition == GameEdition.Enhanced)
        {
            // GTA5_Enhanced.exe never loads dinput8.dll: only the Enhanced loader counts.
            Assert.Equal(BaseSetupStage.ScriptHookV, ModdingBase.NextSetupStage(ModdingBase.Inspect(game)));
            File.WriteAllText(Path.Combine(root, "xinput1_4.dll"), "MZ enhanced asi loader");
        }
        Assert.Equal(BaseSetupStage.ScriptHookVDotNet, ModdingBase.NextSetupStage(ModdingBase.Inspect(game)));
        File.WriteAllText(Path.Combine(root, "ScriptHookVDotNet.asi"), "MZ net");
        Assert.Equal(BaseSetupStage.Complete, ModdingBase.NextSetupStage(ModdingBase.Inspect(game)));
    }

    [Theory]
    [InlineData(GameEdition.Legacy, "OpenIV.asi")]
    [InlineData(GameEdition.Enhanced, "OpenRPF.asi")]
    public void Third_party_loader_skips_our_plugin_step(GameEdition edition, string name)
    {
        var game = Game(edition);
        File.WriteAllText(Path.Combine(root, name), "MZ existing");
        Assert.Equal(BaseSetupStage.ScriptHookV, ModdingBase.NextSetupStage(ModdingBase.Inspect(game)));
        var asi = Path.Combine(files, "RageOpenV.asi");
        File.WriteAllText(asi, "MZ our plugin");
        using var plan = ModdingBase.PrepareRageOpenV(game, asi);
        Assert.False(plan.CanInstall);
        Assert.Equal("MZ existing", File.ReadAllText(Path.Combine(root, name)));
        Assert.False(File.Exists(Path.Combine(root, "RageOpenV.asi")));
    }

    [Fact]
    public void Rage_open_v_only_when_no_other_mods_loader()
    {
        var g = Game(GameEdition.Legacy);
        var asi = Path.Combine(files, "RageOpenV.asi");
        File.WriteAllText(asi, "MZ rage");
        using (var plan = ModdingBase.PrepareRageOpenV(g, asi))
            Install(g, plan);
        Assert.True(ModdingBase.Inspect(g).ModsLoader);

        File.Delete(Path.Combine(root, "RageOpenV.asi"));
        File.WriteAllText(Path.Combine(root, "OpenIV.asi"), "MZ openiv");
        using var skip = ModdingBase.PrepareRageOpenV(g, asi);
        Assert.NotEmpty(skip.Errors);
        Assert.False(File.Exists(Path.Combine(root, "RageOpenV.asi")));
    }

    [Fact]
    public void ManagedLoaderUpdate_IsBackedUpAndForeignLoaderIsPreserved()
    {
        var g = Game(GameEdition.Legacy);
        var asi = Path.Combine(files, "RageOpenV.asi");
        File.WriteAllText(asi, "MZ first managed build");
        using (var first = ModdingBase.PrepareRageOpenV(g, asi))
            Install(g, first);
        File.WriteAllText(asi, "MZ newer managed build");
        using (var update = ModdingBase.PrepareRageOpenV(g, asi))
        {
            Assert.True(update.CanInstall, string.Join(";", update.Errors));
            Install(g, update);
        }
        Assert.Equal("MZ newer managed build", File.ReadAllText(Path.Combine(root, "RageOpenV.asi")));
        File.WriteAllText(Path.Combine(root, "RageOpenV.asi"), "MZ foreign modification");
        File.WriteAllText(asi, "MZ third managed build");
        using var foreign = ModdingBase.PrepareRageOpenV(g, asi);
        Assert.NotEmpty(foreign.Errors);
        Assert.Equal("MZ foreign modification", File.ReadAllText(Path.Combine(root, "RageOpenV.asi")));
    }
}
