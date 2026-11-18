using GTAModManager.Core.Game;
using GTAModManager.Core.Inventory;
using GTAModManager.Core.NaturalVision;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class InstalledModListTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("gmm-modlist-").FullName;

    GameInstall Game()
    {
        var path = Path.Combine(root, "game");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "GTA5.exe"), "stub");
        var g = GameInstall.Open(path);
        g.RunningCheckOverride = () => false;
        return g;
    }

    string Install(GameInstall game, string title, string rel)
    {
        var source = Path.Combine(root, Guid.NewGuid() + ".bin");
        File.WriteAllText(source, "payload");
        var target = Path.Combine(game.Root, rel);
        var r = game.CreateEngine().Execute([new(rel, FileTransactionEngine.HashOrNull(target), source)], title);
        Assert.Equal(TxState.Completed, r.State);
        return r.Id;
    }

    ModLabels Labels() => new(Path.Combine(root, "labels.json"));

    [Fact]
    public void RemoveUndoesEveryInstallationOfTheMod_AndKeepsOtherMods()
    {
        var g = Game();
        Install(g, "Simple Trainer", "trainer.asi");
        Install(g, "Simple Trainer", "trainer.asi");
        Install(g, "Other Mod", "other.asi");
        var group = InstalledModList.Read(g, Labels()).Groups.Single(x => x.Name != "Other Mod");
        Assert.Equal(2, group.Operations.Count);
        var result = ModRemoval.Remove(g, null, new() { Name = group.Name, Operations = [.. group.Operations] });
        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.Undone);
        Assert.False(File.Exists(Path.Combine(g.Root, "trainer.asi")));
        Assert.True(File.Exists(Path.Combine(g.Root, "other.asi")));
        Assert.Equal("Other Mod", Assert.Single(InstalledModList.Read(g, Labels()).Groups).Name);
    }

    [Fact]
    public void RuntimeShaderFilesNeverBecomeMods_AndTargetsAreNotHashed()
    {
        var g = Game();
        Install(g, "T&P Recorder 1.1", @"scripts\T&P Recorder.dll");
        var dir = Directory.CreateDirectory(Path.Combine(g.Root, "Shaders/Extract/PixelShadersRaw")).FullName;
        for (int i = 0; i < 64; i++)
            File.WriteAllText(Path.Combine(dir, i + ".ps.dxbc"), "generated shader");
        File.WriteAllText(Path.Combine(g.Root, "scripts/T&P Recorder.dll"), "changed runtime file");
        var report = InstalledModList.Read(g, Labels());
        Assert.Equal("Traffic and Player Recorder", Assert.Single(report.Groups).Name);
        Assert.Empty(report.FrozenJournals);
        Assert.Equal(0, report.UnrecognizedFiles);
    }

    [Theory]
    [InlineData("Горячая клавиша: База для моддинга: ScriptHookVDotNet — Console: F4 → F12")]
    [InlineData("Hotkey: ScriptHookVDotNet — Console: F4 → F12")]
    public void HotkeyChangeIsNotAMod_AndTheFileStaysWithItsInstaller(string hotkeyTitle)
    {
        var g = Game();
        Install(g, "База для моддинга: ScriptHookVDotNet", "ScriptHookVDotNet.ini");
        Install(g, hotkeyTitle, "ScriptHookVDotNet.ini");
        var group = Assert.Single(InstalledModList.Read(g, Labels()).Groups);
        Assert.Equal(InstalledModList.BaseName, group.Name);
        Assert.Contains(group.Files, f => f.Rel == "ScriptHookVDotNet.ini");
    }

    [Fact]
    public void MultiPartSuitesAreOneModEach()
    {
        var g = Game();
        var states = Path.Combine(root, "states");
        foreach (var product in new[] { "nve", "lcpp" })
        {
            var state = new NveState
            {
                Product = product,
                GameRoot = g.Root,
                Edition = "Legacy",
                Phase = "Installed",
            };
            for (int i = 0; i < 3; i++)
            {
                var id = Install(
                    g,
                    state.ProductTitle + " · Part " + i + " [NVE:" + state.Id + ":core-" + i + "]",
                    product + i + ".asi"
                );
                state.Components.Add(
                    new()
                    {
                        Active = true,
                        TransactionId = id,
                        Component = new("core-" + i, "part.oiv", "Part", "", "", "", i, true, 1),
                    }
                );
            }
            new NveStore(g, states).Save(state);
        }
        var report = InstalledModList.Read(g, Labels(), stateDirectory: states);
        Assert.Equal(2, report.Groups.Count);
        Assert.All(report.Groups, x => Assert.True(x.Recognized));
    }

    [Fact]
    public void NitroMainAndGraphicsRemainOneMod_AndUndoneInstallDisappears()
    {
        var g = Game();
        Install(g, "NOS 1.9.5", @"scripts\N.O.S - Nitro Overdrive System.dll");
        Install(g, "Дополнительно: NOS graphics", "NOSUnderground.Graphics.asi");
        var trainer = Install(g, "TrainerV", "TrainerV.asi");
        Assert.Equal(TxState.Undone, g.CreateEngine().Undo(trainer).State);
        Assert.Equal("N.O.S - Nitro Overdrive System", Assert.Single(InstalledModList.Read(g, Labels()).Groups).Name);
    }

    [Fact]
    public void OlderMultiPartJournalWithoutSuiteStateStillShowsOneMod()
    {
        var g = Game();
        for (int i = 1; i <= 3; i++)
            Install(g, "NaturalVision Evolved - Part " + i, "nve" + i + ".asi");
        var mod = Assert.Single(InstalledModList.Read(g, Labels()).Groups);
        Assert.Equal("NaturalVision Evolved", mod.Name);
        Assert.Equal(3, mod.Files.Count);
    }

    public void Dispose()
    {
        Directory.Delete(root, true);
    }
}
