using System.Text.Encodings.Web;
using System.Text.Json;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Inventory;

public sealed class ModListFile
{
    public string Rel { get; set; } = "";
    public long Size { get; set; }
    public string Hash { get; set; } = "";
}

public sealed class ModListEntry
{
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    public string Kind { get; set; } = "";
    public bool ViaModHub { get; set; }
    public List<ModListFile> Files { get; set; } = [];

    /// <summary>Can be downloaded and reinstalled: there is a link to the mod page on GTA5-Mods.</summary>
    public bool Downloadable => AuthorThanks.IsGta5Mods(Url);
}

/// <summary>
/// Mod list: which mods are installed in the game, with names, GTA5-Mods links and file fingerprints. A small file instead of
/// a multi-gigabyte backup: Mod Hub uses it to download fresh versions from GTA5-Mods (downloads count for the authors)
/// and installs them one by one with confirmation, and file labels are carried over so a backup on another PC is recognized too.
/// </summary>
public sealed class ModList
{
    public const string Format = "modhub-modlist";
    public string Kind { get; set; } = Format;
    public int Version { get; set; } = 1;
    public string Edition { get; set; } = "";
    public string? GameVersion { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public List<ModListEntry> Mods { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A list built from the My Mods inventory. Base components are excluded: the base package installs them.</summary>
    public static ModList From(InventoryReport report, GameInstall game) =>
        new()
        {
            Edition = game.Edition.ToString(),
            GameVersion = Base.ModdingBase.GameVersion(game),
            Mods = report
                .Groups.Where(g =>
                    g.Kind != "База для моддинга"
                    && g.Kind != "Служебные файлы"
                    && !g.Name.StartsWith("База для моддинга", StringComparison.OrdinalIgnoreCase)
                    && !g.Name.StartsWith("Компоненты GameConfig", StringComparison.OrdinalIgnoreCase)
                )
                .Select(g => new ModListEntry
                {
                    Name = g.Name,
                    Url = g.Url,
                    Kind = g.Kind,
                    ViaModHub = g.Origin == ModOrigin.ModHub,
                    Files = g
                        .Files.Select(f => new ModListFile
                        {
                            Rel = f.Rel,
                            Size = f.Size,
                            Hash = f.Hash,
                        })
                        .ToList(),
                })
                .ToList(),
        };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static ModList Load(string path)
    {
        if (new FileInfo(path).Length > 8L << 20)
            throw new InvalidDataException(L.T("core.inventory.modListFileTooLarge"));
        var list = JsonSerializer.Deserialize<ModList>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException(L.T("core.inventory.emptyModList"));
        if (list.Kind != Format)
            throw new InvalidDataException(L.T("core.inventory.notModHubModList"));
        list.Mods.RemoveAll(m => string.IsNullOrWhiteSpace(m.Name));
        foreach (var m in list.Mods)
            if (m.Url is not null && !Uri.TryCreate(m.Url, UriKind.Absolute, out _))
                m.Url = null;
        return list;
    }

    /// <summary>Imports file labels and links into Mod Hub's memory. Returns how many new labels were added.</summary>
    public int ImportLabels(ModLabels labels) =>
        labels.Import(
            Mods.SelectMany(m => m.Files.Select(f => (f.Hash, m.Name))),
            Mods.Where(m => m.Url is not null).Select(m => (m.Name, m.Url!))
        );

    /// <summary>The mod is already installed: the current inventory has a mod with the same link or the same name.</summary>
    public static bool Installed(ModListEntry entry, InventoryReport? current) =>
        current is not null
        && current.Groups.Any(g =>
            (
                entry.Url is not null
                && g.Url is not null
                && AuthorThanks
                    .ModPage(g.Url)
                    .Equals(AuthorThanks.ModPage(entry.Url), StringComparison.OrdinalIgnoreCase)
            ) || g.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)
        );
}
