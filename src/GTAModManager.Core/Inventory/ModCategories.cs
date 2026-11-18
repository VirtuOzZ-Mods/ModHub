using GTAModManager.Core.Dlc;
using GTAModManager.Core.NaturalVision;

namespace GTAModManager.Core.Inventory;

/// <summary>Sections of My Mods.</summary>
public enum ModCategory
{
    Scripts,
    Graphics,
    Maps,
    Characters,
    Accessories,
    Sounds,
    Vehicles,
    Other,
}

/// <summary>
/// Sorts an installed mod into a My Mods section by what it put into the game: the contents of its add-on packs
/// (vehicles, engine sounds, characters, map), script and ASI files, character clothing and sound banks.
/// A recognized add-on pack decides first, since a car or sound mod may also carry a helper script.
/// </summary>
public static class ModCategories
{
    public static IReadOnlyList<ModCategory> All { get; } = Enum.GetValues<ModCategory>();

    public static string Label(ModCategory c) =>
        Localization.L.T(
            c switch
            {
                ModCategory.Scripts => "ui.inventory.sectionScripts",
                ModCategory.Graphics => "ui.inventory.sectionGraphics",
                ModCategory.Maps => "ui.inventory.sectionMaps",
                ModCategory.Characters => "ui.inventory.sectionCharacters",
                ModCategory.Accessories => "ui.inventory.sectionAccessories",
                ModCategory.Sounds => "ui.inventory.sectionSounds",
                ModCategory.Vehicles => "ui.inventory.sectionVehicles",
                _ => "ui.inventory.sectionOther",
            }
        );

    /// <summary>NaturalVision is graphics; LCPP adds Liberty City, a map.</summary>
    public static ModCategory ForSuite(string productTitle) =>
        productTitle.StartsWith(NveBundle.LcppTitle, StringComparison.OrdinalIgnoreCase)
            ? ModCategory.Maps
            : ModCategory.Graphics;

    public static ModCategory Classify(string name, IEnumerable<string> files, IEnumerable<DlcContent> packs)
    {
        if (name == InstalledModList.PedSelectorName)
            return ModCategory.Characters;
        if (name == InstalledModList.BaseName)
            return ModCategory.Other;
        if (
            name.StartsWith("NaturalVision", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(NveBundle.LcppTitle, StringComparison.OrdinalIgnoreCase)
        )
            return ForSuite(name);
        var kinds = packs.Select(p => p.Kind).ToHashSet();
        var rels = files.ToList();
        if (kinds.Contains(DlcPackKind.Vehicles))
            return ModCategory.Vehicles;
        if (kinds.Contains(DlcPackKind.EngineSounds))
            return ModCategory.Sounds;
        bool scripts = rels.Any(r =>
            r.StartsWith(@"scripts\", StringComparison.OrdinalIgnoreCase)
            || r.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)
        );
        if (scripts)
            return ModCategory.Scripts;
        if (
            kinds.Contains(DlcPackKind.Peds)
            || rels.Any(r => r.StartsWith("GMMAddonPeds\\", StringComparison.OrdinalIgnoreCase))
        )
            return ModCategory.Characters;
        // Clothing of the main characters and the MP character is an accessory; other character files are a character.
        var pedFiles = rels.Where(r => Peds.PedAccessories.Describe(r) is not null).ToList();
        if (pedFiles.Count > 0)
            return pedFiles.Any(r => Peds.PedAccessories.Describe(r)?.Slot is not null)
                ? ModCategory.Accessories
                : ModCategory.Characters;
        if (kinds.Contains(DlcPackKind.Map) || rels.Any(r => r.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)))
            return ModCategory.Maps;
        if (rels.Any(IsGraphics))
            return ModCategory.Graphics;
        if (
            rels.Any(r =>
                r.EndsWith(".awc", StringComparison.OrdinalIgnoreCase)
                || r.Contains(@"\audio\", StringComparison.OrdinalIgnoreCase)
            )
        )
            return ModCategory.Sounds;
        return ModCategory.Other;
    }

    private static bool IsGraphics(string rel)
    {
        var name = Path.GetFileName(rel).ToLowerInvariant();
        return name
                is "visualsettings.dat"
                    or "dxgi.dll"
                    or "d3d11.dll"
                    or "enblocal.ini"
                    or "enbseries.ini"
                    or "reshade.ini"
            || name.StartsWith("timecycle")
            || rel.Contains("reshade-shaders", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(@"enbseries\", StringComparison.OrdinalIgnoreCase);
    }
}
