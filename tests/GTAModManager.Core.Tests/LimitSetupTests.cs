using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;

namespace GTAModManager.Core.Tests;

/// <summary>Classic adjusters + gameconfig, or Resource Extensions alone: never both, never two copies of one adjuster.</summary>
public sealed class LimitSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-limits-" + Guid.NewGuid().ToString("N"));

    public LimitSetupTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "GTA5_Enhanced.exe"), "stub");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private GameInstall Game() => GameInstall.Open(_root);

    private void Put(string rel, string text = "MZ stub")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, rel))!);
        File.WriteAllText(Path.Combine(_root, rel), text);
    }

    [Fact]
    public void ClassicSet_IsOfferedAndNeedsGameconfig()
    {
        var g = Game();
        Assert.NotEmpty(AdjusterComponents.Missing(g));
        Assert.True(LimitSetup.GameConfigNeeded(g));
        Assert.Empty(LimitSetup.Conflicts(g));
    }

    [Fact]
    public void ResourceExtensions_ReplacesAdjustersAndGameconfig()
    {
        Put(LimitSetup.ResourceExtensionsAsi);
        var g = Game();
        Assert.Empty(AdjusterComponents.Missing(g));
        Assert.False(LimitSetup.GameConfigNeeded(g));
        Assert.Empty(LimitSetup.Conflicts(g));
        var status = StarterPackage.Inspect(g);
        Assert.Empty(status.MissingAdjusters);
        Assert.True(status.GameConfig);
    }

    [Fact]
    public void ResourceExtensions_WithAdjustersAndNveGameconfig_IsReported()
    {
        Put(LimitSetup.ResourceExtensionsAsi);
        Put("PackfileLimitAdjusterEnhanced.asi");
        Put("GTAV.HeapAdjuster.asi");
        Put(@"onigiri\common\data\gameconfig.xml", "<fwAllConfigs/>");
        var conflicts = LimitSetup.Conflicts(Game());
        Assert.Equal(2, conflicts.Count);
        Assert.Contains("PackfileLimitAdjusterEnhanced.asi", conflicts[0]);
        Assert.Contains("GTAV.HeapAdjuster.asi", conflicts[0]);
        Assert.Contains(@"onigiri\common\data\gameconfig.xml", conflicts[1]);
    }

    [Fact]
    public void TwoHeapAdjusters_AreReported()
    {
        Put("HeapAdjuster.asi");
        Put("ZHeapAdjuster.asi");
        var conflict = Assert.Single(LimitSetup.Conflicts(Game()));
        Assert.Contains("ZHeapAdjuster.asi", conflict);
    }

    [Fact]
    public void Onigiri_IsTheLoader_AndItsGameconfigIsEnough()
    {
        Put("RageOpenV.asi");
        Put("onigiri.asi");
        Put(@"onigiri\common\data\gameconfig.xml", "<fwAllConfigs/>");
        var g = Game();
        Assert.Equal("onigiri.asi", g.CheckModLoader().Name);
        Assert.True(ModdingBase.Inspect(g).ModsLoader);
        Assert.False(LimitSetup.GameConfigNeeded(g));
    }

    [Fact]
    public void Legacy_IgnoresResourceExtensions()
    {
        var legacy = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "GTA5.exe"), "stub");
        File.WriteAllText(Path.Combine(legacy, LimitSetup.ResourceExtensionsAsi), "MZ");
        var g = GameInstall.Open(legacy);
        Assert.False(LimitSetup.HasResourceExtensions(g));
        Assert.NotEmpty(AdjusterComponents.Missing(g));
    }
}
