using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;

namespace GTAModManager.Core.Tests;

/// <summary>Installed / Update available status for mods from Recommendations.</summary>
public sealed class InstalledModsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-installed-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "GTA V Legacy");
    private InstalledMods Store => new(Path.Combine(_root, "installed-mods.json"));

    public InstalledModsTests()
    {
        Directory.CreateDirectory(Path.Combine(Game, "scripts"));
        Directory.CreateDirectory(Path.Combine(Game, "update"));
        File.WriteAllText(Path.Combine(Game, "GTA5.exe"), "stub");
        File.WriteAllText(Path.Combine(Game, "update", "update.rpf"), "game archive");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private GameInstall Open() => GameInstall.Open(Game);

    private static CatalogEntry Entry(string id, params string[] detect) =>
        new()
        {
            Id = id,
            Category = "recommended",
            Url = "https://www.gta5-mods.com/scripts/" + id,
            Title = id,
            DetectRules = detect.Length == 0 ? null : string.Join('\n', detect),
        };

    private void Write(string rel, string text)
    {
        var path = Path.Combine(Game, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void InstalledThroughModHub_ThenSiteVersionChanges_IsUpdate()
    {
        Write("Menyoo.asi", "v1");
        Write(@"mods\update\update.rpf", "shared copy"); // a shared game archive is not an anchor file
        var store = Store;
        store.Record(
            Open(),
            "menyoo",
            "1.0",
            ["Menyoo.asi", @"mods\update\update.rpf", @"menyooStuff\menyooConfig.ini"]
        );

        Assert.Equal(["Menyoo.asi"], store.Get(Game, "menyoo")!.Files.Select(f => f.Rel));
        Assert.Equal(CatalogInstallStatus.Installed, store.Resolve(Open(), Entry("menyoo"), "v1.0"));
        Assert.Equal(CatalogInstallStatus.UpdateAvailable, store.Resolve(Open(), Entry("menyoo"), "1.1"));
    }

    [Fact]
    public void UserUpdatedFilesThemselves_IsInstalledWithNewVersion()
    {
        Write("Menyoo.asi", "v1");
        var store = Store;
        store.Record(Open(), "menyoo", "1.0", ["Menyoo.asi"]);
        File.WriteAllText(Path.Combine(Game, "Menyoo.asi"), "v1.1 — другой размер");

        Assert.Equal(CatalogInstallStatus.Installed, store.Resolve(Open(), Entry("menyoo"), "1.1"));
        Assert.Equal("1.1", store.Get(Game, "menyoo")!.Version);
    }

    [Fact]
    public void RemovedMod_IsNotInstalled()
    {
        Write("Menyoo.asi", "v1");
        var store = Store;
        store.Record(Open(), "menyoo", "1.0", ["Menyoo.asi"]);
        File.Delete(Path.Combine(Game, "Menyoo.asi"));

        Assert.Equal(CatalogInstallStatus.NotInstalled, store.Resolve(Open(), Entry("menyoo"), "1.1"));
        Assert.Null(store.Get(Game, "menyoo"));
    }

    [Fact]
    public void InstalledElsewhere_IsDetected_ThenUpdateIsTrackedFromThere()
    {
        Write(@"scripts\N.O.S - Nitro Overdrive System.dll", "nos");
        var store = Store;
        var entry = Entry("nos", @"scripts\N.O.S - Nitro Overdrive System.dll");

        Assert.Equal(CatalogInstallStatus.Installed, store.Resolve(Open(), entry, "1.9.5"));
        Assert.Equal("detected", store.Get(Game, "nos")!.Source);
        Assert.Equal(CatalogInstallStatus.UpdateAvailable, store.Resolve(Open(), entry, "1.9.6"));
    }

    [Fact]
    public void UnknownVersionAtDetection_BecomesBaselineLater()
    {
        Write("TrainerV.asi", "t");
        var store = Store;
        var entry = Entry("simple-trainer", "TrainerV.asi");
        Assert.Equal(CatalogInstallStatus.Installed, store.Resolve(Open(), entry, null));
        Assert.Equal(CatalogInstallStatus.Installed, store.Resolve(Open(), entry, "6.5")); // the first known version is the baseline
        Assert.Equal(CatalogInstallStatus.UpdateAvailable, store.Resolve(Open(), entry, "6.6"));
    }

    [Fact]
    public void NotInstalledAnywhere_IsNotInstalled() =>
        Assert.Equal(CatalogInstallStatus.NotInstalled, Store.Resolve(Open(), Entry("menyoo", "Menyoo.asi"), "1.0"));

    [Fact]
    public void DetectRule_ProductCondition()
    {
        Write("ScriptHookVDotNet.asi", "not a PE file: empty product name");
        var g = Open();
        Assert.Null(InstalledMods.Match(g, "ScriptHookVDotNet.asi|product~Enhanced"));
        Assert.Equal("ScriptHookVDotNet.asi", InstalledMods.Match(g, "ScriptHookVDotNet.asi|product!~Enhanced"));
        Assert.Null(InstalledMods.Match(g, "Missing.asi"));
    }

    [Fact]
    public void CatalogDetectEntries_AreLoaded()
    {
        var catalog = DiscoverCatalog.LoadBuiltIn();
        Assert.Contains(catalog.Entries, e => e.Id == "menyoo-2-0" && e.Detect.SequenceEqual(new[] { "Menyoo.asi" }));
        Assert.Contains(catalog.Entries, e =>
            e.Id == "shvdn-enhanced" && e.Detect.Single().EndsWith("product~Enhanced")
        );
    }
}
