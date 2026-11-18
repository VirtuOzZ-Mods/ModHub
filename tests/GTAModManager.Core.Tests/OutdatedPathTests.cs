using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.Tests;

/// <summary>
/// The author's instructions point to a nested archive that does not exist in the game (Dante: x64\patch\data\cdimages\scaleform_generic.rpf).
/// Mod Hub does not guess a new path: installation stops, and an archive with the same name is shown only as a hint.
/// </summary>
public sealed class OutdatedPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-outdated-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "GTA V Enhanced");

    public OutdatedPathTests()
    {
        Directory.CreateDirectory(Game);
        File.WriteAllText(Path.Combine(Game, "GTA5_Enhanced.exe"), "stub");
        File.WriteAllText(Path.Combine(Game, "OpenRPF.asi"), "MZ stub loader");
        Directory.CreateDirectory(Path.Combine(Game, "mods", "update"));
        var nested = new MemoryStream();
        new Rpf7Builder().AddFile("player_switch.ytd", new RpfBinarySource([1, 2, 3])).Write(nested);
        using var fs = File.Create(Path.Combine(Game, "mods", "update", "update.rpf"));
        new Rpf7Builder()
            .AddFile(@"x64\data\cdimages\scaleform_generic.rpf", new RpfBinarySource(nested.ToArray()))
            .Write(fs);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    [Fact]
    public void MissingNestedArchive_StopsInstallAndSuggestsSameNamedArchive()
    {
        var g = GameInstall.Open(Game);
        g.RunningCheckOverride = () => false;
        var ops = new List<OivOp>
        {
            new OivArchive(
                @"update\update.rpf",
                false,
                "RPF7",
                [
                    new OivArchive(
                        @"x64\patch\data\cdimages\scaleform_generic.rpf",
                        false,
                        "RPF7",
                        [new OivAdd("player_switch.ytd", "player_switch.ytd")]
                    ),
                ]
            ),
        };
        var source = Path.Combine(_root, "src.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("player_switch.ytd").Open())
            s.Write(Encoding.UTF8.GetBytes("ytd"));
        using var archive = Mods.ModArchive.Open(source);

        using var plan = new OivInstaller(g).Prepare("Dante", ops, archive);

        var error = Assert.Single(plan.Errors);
        Assert.Contains(@"x64\patch\data\cdimages\scaleform_generic.rpf", error);
        Assert.Contains(@"x64\data\cdimages\scaleform_generic.rpf", error);
        Assert.False(plan.CanInstall);
        Assert.Empty(plan.Changes);
    }
}
