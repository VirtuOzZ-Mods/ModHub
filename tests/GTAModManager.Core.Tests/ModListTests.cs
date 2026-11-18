using GTAModManager.Core.Game;
using GTAModManager.Core.Inventory;
using Xunit;

namespace GTAModManager.Core.Tests;

public class ModListTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gmm-modlist-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    private static InventoryReport Report(params ModGroup[] groups) => new(groups, [], []);

    private static ModGroup Group(
        string name,
        string kind,
        ModOrigin origin,
        string? url,
        params (string Rel, string Hash)[] files
    )
    {
        var g = new ModGroup
        {
            Name = name,
            Kind = kind,
            Origin = origin,
            Recognized = true,
            Url = url,
        };
        foreach (var (rel, hash) in files)
            g.Files.Add(new ModFileItem(rel, 10, hash, origin));
        return g;
    }

    [Fact]
    public void Save_load_and_transfer_labels()
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "game")).FullName;
        File.WriteAllText(Path.Combine(root, "GTA5_Enhanced.exe"), "x");
        var game = GameInstall.Open(root);
        var report = Report(
            Group(
                "N.O.S.",
                "Установлено через Mod Hub",
                ModOrigin.ModHub,
                "https://www.gta5-mods.com/scripts/n-o-s-nitro-overdrive-system",
                (@"scripts\NOS.dll", "AA")
            ),
            Group("Мой старый мод", "Подписано вами", ModOrigin.External, null, ("old.asi", "BB")),
            Group(
                "Базовые компоненты (ScriptHook, загрузчики, адаптеры)",
                "База для моддинга",
                ModOrigin.External,
                null,
                ("ScriptHookV.dll", "CC")
            )
        );

        var list = ModList.From(report, game);
        Assert.Equal(2, list.Mods.Count); // base components are not in the list: the base package installs them
        Assert.True(list.Mods[0].Downloadable);
        Assert.False(list.Mods[1].Downloadable);

        var path = Path.Combine(_dir, "my.modlist.json");
        list.Save(path);
        var loaded = ModList.Load(path);
        Assert.Equal("Enhanced", loaded.Edition);

        var labels = new ModLabels(Path.Combine(_dir, "labels.json"));
        Assert.Equal(2, loaded.ImportLabels(labels));
        Assert.Equal("Мой старый мод", labels.NameFor("BB"));
        Assert.Equal("https://www.gta5-mods.com/scripts/n-o-s-nitro-overdrive-system", labels.UrlFor("N.O.S."));

        Assert.True(
            ModList.Installed(
                loaded.Mods[0],
                Report(
                    Group(
                        "Другое имя",
                        "x",
                        ModOrigin.ModHub,
                        "https://www.gta5-mods.com/scripts/n-o-s-nitro-overdrive-system/download/1"
                    )
                )
            )
        );
        Assert.False(ModList.Installed(loaded.Mods[0], Report()));
    }

    [Fact]
    public void Foreign_json_is_rejected()
    {
        var path = Path.Combine(_dir, "x.json");
        File.WriteAllText(path, "{\"Kind\":\"something\",\"Mods\":[]}");
        Assert.Throws<InvalidDataException>(() => ModList.Load(path));
    }
}
