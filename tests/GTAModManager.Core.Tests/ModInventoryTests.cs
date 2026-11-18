using GTAModManager.Core.Game;
using GTAModManager.Core.Inventory;
using GTAModManager.Core.Transactions;
using Xunit;

namespace GTAModManager.Core.Tests;

public class ModInventoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gmm-inventory-").FullName;
    private string Root => Path.Combine(_dir, "game");
    private GameInstall? _game;

    public void Dispose()
    {
        try
        {
            if (_game is not null)
                foreach (
                    var j in _game
                        .CreateEngine()
                        .History()
                        .Where(j => j.TargetRoot.StartsWith(_dir, StringComparison.OrdinalIgnoreCase))
                )
                    File.Delete(Path.Combine(_game.WorkDir, "journals", j.Id + ".json"));
        }
        catch { }
        try
        {
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    private GameInstall Game()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "GTA5_Enhanced.exe"), "x");
        File.WriteAllText(Path.Combine(Root, "common.rpf"), "x");
        return _game = GameInstall.Open(Root);
    }

    private void Put(string rel, string text)
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    [Fact]
    public void Separates_mod_hub_files_from_manually_added_and_groups_them()
    {
        var game = Game();
        // Installed through Mod Hub: a script (operation journal).
        var src = Path.Combine(_dir, "Nitro.dll");
        File.WriteAllText(src, "MZ nitro");
        var engine = game.CreateEngine();
        Assert.Equal(
            TxState.Completed,
            engine.Execute([new FileChange(@"scripts\Nitro.dll", null, src)], "a1b2c3-Nitro 1.0").State
        );
        // The mod created its own settings; a DLC, an ASI and an unknown file were added from a backup.
        Put(@"scripts\Nitro.ini", "cfg");
        Put(@"scripts\Nitro Data\vehicles.xml", "<x/>");
        Put(@"mods\update\x64\dlcpacks\supercar\dlc.rpf", "rpf");
        Put("Trainer.asi", "MZ asi");
        Put("Trainer.ini", "ini");
        Put("whatever.bin", "?");

        var labels = new ModLabels(Path.Combine(_dir, "labels.json"));
        var report = ModInventory.Scan(game, labels, new HashCache(Path.Combine(_dir, "cache.json")));
        var byName = report.Groups.ToDictionary(g => g.Name);

        var nitro = byName["Nitro 1.0"];
        Assert.Equal(ModOrigin.ModHub, nitro.Origin);
        Assert.Contains(nitro.Files, f => f.Rel == @"scripts\Nitro.ini");
        Assert.Contains(nitro.Files, f => f.Rel == @"scripts\Nitro Data\vehicles.xml");
        Assert.Equal(2, byName["Trainer"].Files.Count);
        Assert.Single(byName["DLC: supercar"].Files);
        Assert.False(byName["whatever"].Recognized);
        Assert.DoesNotContain(report.Groups, g =>
            g.Files.Any(f => f.Rel == "common.rpf" || f.Rel == "GTA5_Enhanced.exe")
        );
        Assert.Empty(report.FrozenJournals);

        // The label is remembered by content: the same file lands in its mod right away next time.
        labels.Assign(byName["whatever"].Files, "Мой старый мод", "https://www.gta5-mods.com/scripts/old");
        var again = ModInventory.Scan(
            game,
            new ModLabels(Path.Combine(_dir, "labels.json")),
            new HashCache(Path.Combine(_dir, "cache.json"))
        );
        var mine = again.Groups.Single(g => g.Name == "Мой старый мод");
        Assert.True(mine.Recognized);
        Assert.Equal("https://www.gta5-mods.com/scripts/old", mine.Url);

        // A Mod Hub file was overwritten manually, so the history entry is frozen.
        Put(@"scripts\Nitro.dll", "MZ other version from backup");
        var changed = ModInventory.Scan(game, labels, new HashCache(Path.Combine(_dir, "cache.json")));
        Assert.Single(changed.FrozenJournals);
        Assert.Contains(changed.Groups, g =>
            g.Origin == ModOrigin.External && g.Files.Any(f => f.Rel == @"scripts\Nitro.dll")
        );
    }

    [Fact]
    public void Stale_update_rpf_copy_is_reported()
    {
        var game = Game();
        Put(@"update\update.rpf", "orig");
        Put(@"mods\update\update.rpf", "copy");
        Assert.Null(ModInventory.StaleUpdateRpf(game));
        File.SetLastWriteTimeUtc(Path.Combine(Root, @"mods\update\update.rpf"), DateTime.UtcNow.AddDays(-30));
        Assert.NotNull(ModInventory.StaleUpdateRpf(game));
    }

    [Theory]
    [InlineData("Name: Super Trainer V\nby someone", "Super Trainer V")]
    [InlineData("=====\n  Cool Cars Pack  \n=====\nInstall: drag", "Cool Cars Pack")]
    [InlineData("https://www.gta5-mods.com\nНазвание: Мой мод", "Мой мод")]
    public void Readme_title(string text, string expected) => Assert.Equal(expected, ReadmeTitle.From(text));
}
