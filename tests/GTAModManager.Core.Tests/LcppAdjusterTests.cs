using System.IO.Compression;
using System.Text;
using GTAModManager.Core.NaturalVision;

namespace GTAModManager.Core.Tests;

public sealed partial class LcppTests
{
    void AddAdjusters(bool hd)
    {
        if (hd)
            MakeHd();
        using var zip = ZipFile.Open(Path.Combine(bundle, hd ? HdName(1) : Name(1)), ZipArchiveMode.Update);
        var entry = zip.GetEntry("assembly.xml")!;
        string text;
        using (var r = new StreamReader(entry.Open()))
            text = r.ReadToEnd();
        entry.Delete();
        string ops = "";
        foreach (
            var name in new[]
            {
                "HeapAdjuster.asi",
                "HeapAdjuster.ini",
                "PackfileLimitAdjuster.asi",
                "PackfileLimitAdjuster.ini",
            }
        )
        {
            using (var w = new StreamWriter(zip.CreateEntry("content/" + name).Open()))
                w.Write("MZ lcpp " + name);
            ops += $"<add source=\"{name}\">{name}</add>";
        }
        using var output = new StreamWriter(zip.CreateEntry("assembly.xml").Open());
        output.Write(text.Replace("</content>", ops + "</content>"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingAdjusters_ArePreserved_AndExplicitReplacementRemainsPartOfSafeRemoval(bool hd)
    {
        AddAdjusters(hd);
        Put("NVE.asi", "MZ nve"u8.ToArray());
        Put("ZHeapAdjuster.asi", "MZ original heap"u8.ToArray());
        Put("HeapAdjuster.ini", "original heap settings"u8.ToArray());
        Put("PackfileLimitAdjusterEnhanced.asi", "MZ original pack"u8.ToArray());
        Put("PackfileLimitAdjusterEnhanced.ini", "original pack settings"u8.ToArray());
        var result = Install();
        Assert.True(result.Success, result.Message);
        Assert.Equal("MZ original heap", File.ReadAllText(Path.Combine(game.Root, "ZHeapAdjuster.asi")));
        Assert.False(File.Exists(Path.Combine(game.Root, "HeapAdjuster.asi")));
        Assert.Equal("original heap settings", File.ReadAllText(Path.Combine(game.Root, "HeapAdjuster.ini")));
        Assert.False(File.Exists(Path.Combine(game.Root, "PackfileLimitAdjuster.asi")));
        Assert.Equal(4, State(result.InstallationId).PreservedAdjusters.Count);
        var replace = Service().InstallPreservedAdjusters(new() { InstallationId = result.InstallationId });
        Assert.True(replace.Success, replace.Message);
        Assert.Equal("MZ lcpp HeapAdjuster.asi", File.ReadAllText(Path.Combine(game.Root, "ZHeapAdjuster.asi")));
        Assert.False(File.Exists(Path.Combine(game.Root, "PackfileLimitAdjusterEnhanced.asi")));
        Assert.True(File.Exists(Path.Combine(game.Root, "PackfileLimitAdjuster.asi")));
        Assert.True(Service().Remove(new() { InstallationId = result.InstallationId }).Success);
        Assert.Equal("MZ original heap", File.ReadAllText(Path.Combine(game.Root, "ZHeapAdjuster.asi")));
        Assert.Equal(
            "MZ original pack",
            File.ReadAllText(Path.Combine(game.Root, "PackfileLimitAdjusterEnhanced.asi"))
        );
        Assert.False(File.Exists(Path.Combine(game.Root, "PackfileLimitAdjuster.asi")));
    }

    [Fact]
    public void WithoutExistingAdjusters_BundledVersionsInstallNormally()
    {
        AddAdjusters(false);
        var result = Install();
        Assert.True(result.Success, result.Message);
        Assert.Empty(State(result.InstallationId).PreservedAdjusters);
        Assert.True(File.Exists(Path.Combine(game.Root, "HeapAdjuster.asi")));
        Assert.True(File.Exists(Path.Combine(game.Root, "PackfileLimitAdjuster.asi")));
    }

    [Fact]
    public void PreservedRuntimeCacheTampering_RejectsReplacementBeforeAnyGameWrite()
    {
        AddAdjusters(false);
        Put("HeapAdjuster.asi", "MZ original"u8.ToArray());
        var result = Install();
        Assert.True(result.Success, result.Message);
        var state = State(result.InstallationId);
        File.AppendAllText(NveStore.Cache(state, "preserved-adjusters.oiv"), "changed");
        Assert.Throws<IOException>(() => Service().InstallPreservedAdjusters(new() { InstallationId = state.Id }));
        Assert.Equal("MZ original", File.ReadAllText(Path.Combine(game.Root, "HeapAdjuster.asi")));
        Assert.Equal("Installed", State(state.Id).Phase);
    }

    [Fact]
    public void ReplacementCanBeUndoneSeparately_AndAppliedAgain()
    {
        AddAdjusters(false);
        Put("HeapAdjuster.asi", "MZ original"u8.ToArray());
        var result = Install();
        Assert.True(result.Success, result.Message);
        Assert.True(Service().InstallPreservedAdjusters(new() { InstallationId = result.InstallationId }).Success);
        Assert.True(
            Service().Remove(new() { InstallationId = result.InstallationId, Components = ["lcpp-adjusters"] }).Success
        );
        Assert.Equal("MZ original", File.ReadAllText(Path.Combine(game.Root, "HeapAdjuster.asi")));
        Assert.Equal("Installed", State(result.InstallationId).Phase);
        Assert.True(Service().InstallPreservedAdjusters(new() { InstallationId = result.InstallationId }).Success);
    }
}
