using GTAModManager.Core.Game;
using GTAModManager.Core.Keybinds;
using GTAModManager.Core.Library;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Peds;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class PedSelectorComponentTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "gmm-selector-test-" + Guid.NewGuid().ToString("N")
    );
    private readonly GameInstall game;
    private readonly BackupLibrary library;
    private string Component => Path.Combine(root, "packaged.asi");

    public PedSelectorComponentTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "GTA5.exe"), "synthetic game");
        File.WriteAllText(Component, "MZ synthetic selector");
        game = GameInstall.Open(root);
        game.RunningCheckOverride = () => false;
        library = BackupLibrary.Create(Path.Combine(root, "backups"), 1L << 30);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException) { }
    }

    private void Install(InstallPlan plan)
    {
        Assert.True(plan.CanInstall, string.Join("; ", plan.Errors));
        Assert.Equal(TxState.Completed, new OivInstaller(game).Install(plan, library).State);
    }

    [Fact]
    public void Component_installs_transactionally_preserves_ini_and_removes_only_owned_files()
    {
        var component = new PedSelectorComponent(game);
        using (
            var plan = component.PrepareInstall(
                Component,
                [new PedEntry { SpawnName = "foreign", DisplayName = "External PED" }]
            )
        )
            Install(plan);
        Assert.True(component.Inspect().InstalledOwn);
        Assert.Equal(
            PedSelectorComponent.DefaultIni,
            File.ReadAllText(Path.Combine(root, PedSelectorComponent.IniPath))
        );
        Assert.Equal("foreign\tExternal PED\n", File.ReadAllText(Path.Combine(root, PedInstaller.ModelsPath)));
        File.WriteAllText(
            Path.Combine(root, PedSelectorComponent.IniPath),
            "[Menu]\r\nMenuKey=F9\r\n; user settings\r\n"
        );
        using (var plan = component.PrepareInstall(Component))
        {
            // An identical reinstall may be a no-op except for changed model rows.
            Assert.Empty(plan.Errors);
            if (plan.CanInstall)
                Install(plan);
        }
        Assert.Contains("MenuKey=F9", File.ReadAllText(Path.Combine(root, PedSelectorComponent.IniPath)));
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "PedSelector.dll"), "MZ third party");
        using (var plan = component.PrepareRemove())
            Install(plan);
        Assert.False(File.Exists(Path.Combine(root, PedSelectorComponent.AsiPath)));
        Assert.True(File.Exists(Path.Combine(root, PedInstaller.ModelsPath)));
        Assert.True(File.Exists(Path.Combine(root, "scripts", "PedSelector.dll")));
    }

    [Fact]
    public void Unknown_component_is_not_overwritten_or_deleted()
    {
        File.WriteAllText(Path.Combine(root, PedSelectorComponent.AsiPath), "MZ unknown");
        var component = new PedSelectorComponent(game);
        using var install = component.PrepareInstall(Component);
        using var remove = component.PrepareRemove();
        using var sync = component.PrepareSync();
        Assert.False(install.CanInstall);
        Assert.False(remove.CanInstall);
        Assert.False(sync.CanInstall);
        Assert.Equal("MZ unknown", File.ReadAllText(Path.Combine(root, PedSelectorComponent.AsiPath)));
    }

    [Theory]
    [InlineData("AddonPeds.asi")]
    [InlineData("scripts/PedSelector.dll")]
    [InlineData("scripts/PedSelector/PedSelector.dll")]
    public void Existing_third_party_selector_is_detected_read_only(string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "MZ third party");
        var status = new PedSelectorComponent(game).Inspect();
        Assert.True(status.InstalledThirdParty);
        Assert.True(status.HasSelector);
        Assert.False(status.InstalledOwn);
        Assert.NotEmpty(status.MissingDependencies);
        Assert.False(status.Ready);
        Assert.Equal("MZ third party", File.ReadAllText(path));
    }

    [Fact]
    public void Own_status_reports_missing_dependencies_separately()
    {
        var component = new PedSelectorComponent(game);
        using (var plan = component.PrepareInstall(Component))
            Install(plan);
        Assert.True(component.Inspect().HasSelector);
        Assert.Equal(3, component.Inspect().MissingDependencies.Count);
        foreach (var file in new[] { "ScriptHookV.dll", "dinput8.dll", "OpenIV.asi" })
            File.WriteAllText(Path.Combine(root, file), "MZ synthetic dependency");
        Assert.Empty(component.Inspect().MissingDependencies);
        Assert.True(component.Inspect().Ready);
    }

    [Fact]
    public void Enhanced_and_running_game_are_blocked_and_menu_key_is_editable()
    {
        game.RunningCheckOverride = () => true;
        using (var plan = new PedSelectorComponent(game).PrepareInstall(Component))
            Assert.False(plan.CanInstall);
        File.WriteAllText(Path.Combine(root, "GTA5_Enhanced.exe"), "synthetic enhanced");
        var enhanced = GameInstall.Open(root);
        enhanced.RunningCheckOverride = () => false;
        using (var plan = new PedSelectorComponent(enhanced).PrepareSync())
            Assert.Contains(plan.Errors, e => e.Contains("Legacy"));
        var binding = Assert.Single(
            KeybindScanner.ParseFile(PedSelectorComponent.IniPath, PedSelectorComponent.DefaultIni)
        );
        Assert.Equal((int)'L', binding.Chord.Vk);
    }
}
