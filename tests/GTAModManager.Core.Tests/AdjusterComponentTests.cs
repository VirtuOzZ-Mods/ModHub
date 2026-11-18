using System.Buffers.Binary;
using System.IO.Compression;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Library;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class AdjusterComponentTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "gmm-adjuster-tests-" + Guid.NewGuid().ToString("N"));

    public AdjusterComponentTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    GameInstall Game(string edition)
    {
        var p = Path.Combine(root, edition);
        Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, edition == "Legacy" ? "GTA5.exe" : "GTA5_Enhanced.exe"), "stub");
        var g = GameInstall.Open(p);
        g.RunningCheckOverride = () => false;
        return g;
    }

    static byte[] Pe()
    {
        var b = new byte[1024];
        b[0] = 77;
        b[1] = 90;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(60), 128);
        b[128] = 80;
        b[129] = 69;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(132), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(148), 240);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(150), 0x2022);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(152), 0x20b);
        return b;
    }

    string Zip(AdjusterComponent component, bool invalid = false)
    {
        var p = Path.Combine(root, component.Id + ".zip");
        using var z = ZipFile.Open(p, ZipArchiveMode.Create);
        using (var s = z.CreateEntry(component.Asi).Open())
            s.Write(invalid ? [1, 2, 3] : Pe());
        using (var s = new StreamWriter(z.CreateEntry(component.Ini).Open()))
            s.Write("default");
        using (var s = new StreamWriter(z.CreateEntry("LICENSE").Open()))
            s.Write("license");
        return p;
    }

    [Theory]
    [InlineData("Legacy")]
    [InlineData("Enhanced")]
    public void Components_install_separately_preserve_ini_and_can_be_undone(string edition)
    {
        var game = Game(edition);
        string config = Path.Combine(game.Root, "HeapAdjuster.ini");
        File.WriteAllText(config, "my settings");
        var archives = AdjusterComponents.Catalog.ToDictionary(c => c.Id, c => Zip(c));
        var library = BackupLibrary.Create(Path.Combine(root, "backup"), 1L << 28);
        using var plan = AdjusterComponents.Prepare(game, archives);
        Assert.True(plan.CanInstall, string.Join(";", plan.Errors));
        var result = new OivInstaller(game).Install(plan, library);
        Assert.Equal(TxState.Completed, result.State);
        Assert.Empty(AdjusterComponents.Missing(game));
        Assert.Equal("my settings", File.ReadAllText(config));
        Assert.True(File.Exists(Path.Combine(game.Root, @"ModHubCredits\heap\LICENSE")));
        Assert.Equal(TxState.Undone, new OivInstaller(game).Undo(result.Id, library).State);
        Assert.Equal("my settings", File.ReadAllText(config));
        Assert.Equal(
            AdjusterComponents.Catalog.Count(c => c.AppliesTo(game.Edition)),
            AdjusterComponents.Missing(game).Count
        );
    }

    [Fact]
    public void Existing_legacy_and_enhanced_variants_are_not_duplicated()
    {
        var game = Game("Legacy");
        File.WriteAllText(Path.Combine(game.Root, "GTAV.HeapAdjuster.asi"), "MZ");
        File.WriteAllText(Path.Combine(game.Root, "PackfileLimitAdjusterEnhanced.asi"), "MZ");
        Assert.Empty(AdjusterComponents.Missing(game));
        Assert.Throws<InvalidOperationException>(() =>
            AdjusterComponents.Prepare(game, new Dictionary<string, string> { { "heap", "unused.zip" } })
        );
    }

    [Fact]
    public void Damaged_component_does_not_create_game_files()
    {
        var game = Game("Legacy");
        string archive = Zip(AdjusterComponents.Catalog[0], true);
        Assert.ThrowsAny<Exception>(() =>
            AdjusterComponents.Prepare(game, new Dictionary<string, string> { { "heap", archive } })
        );
        Assert.False(File.Exists(Path.Combine(game.Root, "HeapAdjuster.asi")));
    }
}
