using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using Xunit;

namespace GTAModManager.Core.Tests;

public class AdjusterHealthTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gmm-adjusters-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    private GameInstall Game(GameEdition edition)
    {
        File.WriteAllText(Path.Combine(_dir, edition == GameEdition.Enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe"), "x");
        return GameInstall.Open(_dir);
    }

    private void Put(string name, string text, DateTime utc)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        File.SetLastWriteTimeUtc(p, utc);
    }

    [Fact]
    public void Modkit_and_static_bounds_are_required_only_for_enhanced()
    {
        Assert.DoesNotContain(AdjusterComponents.Missing(Game(GameEdition.Legacy)), c =>
            c.Id is "modkit" or "staticbounds"
        );
        File.Delete(Path.Combine(_dir, "GTA5.exe"));
        var missing = AdjusterComponents.Missing(Game(GameEdition.Enhanced)).Select(c => c.Id).ToList();
        Assert.Equal(["heap", "packfile", "modkit", "staticbounds"], missing);
    }

    [Fact]
    public void Health_reads_adjuster_logs_after_launch()
    {
        var game = Game(GameEdition.Enhanced);
        var installed = DateTime.UtcNow.AddHours(-1);
        Put("PackfileLimitAdjusterEnhanced.asi", "MZ", installed);
        Put("ModkitLimitAdjusterEnhanced.asi", "MZ", installed);
        Put("HeapAdjuster.asi", "MZ", installed);
        Put("StaticBoundsLimitAdjuster.asi", "MZ", installed);
        Put("HeapAdjuster.log", "Vanilla Heap Size is: 1\nHeap Size Adjusted to: 2", installed.AddHours(-2)); // stale log: the game has not been launched yet
        Put("PackfileLimitAdjusterEnhanced.log", "", DateTime.UtcNow); // failed before success
        Put(
            "ModkitLimitAdjusterEnhanced.log",
            "Vanilla Modkit List Size is: 1024\nModkit List Size Adjusted to: 65536",
            DateTime.UtcNow
        );
        Put("StaticBoundLimitAdjuster.log", "[FAILURE]\nStage  : deferred installation", DateTime.UtcNow);
        var health = AdjusterComponents
            .Health(game, installed.AddMinutes(10))
            .ToDictionary(h => h.Component.Id, h => h.State);
        Assert.Equal(AdjusterState.NotRunYet, health["heap"]);
        Assert.Equal(AdjusterState.Failed, health["packfile"]);
        Assert.Equal(AdjusterState.Working, health["modkit"]);
        Assert.Equal(AdjusterState.Failed, health["staticbounds"]);
    }
}
