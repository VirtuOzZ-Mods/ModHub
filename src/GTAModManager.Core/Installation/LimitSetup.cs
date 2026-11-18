using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Installation;

/// <summary>
/// How the game's limits are raised. Two setups exist and must not be mixed:
/// the classic set (HeapAdjuster, Packfile/Modkit/StaticBounds adjusters plus a raised gameconfig.xml; on Enhanced with
/// NaturalVision its own onigiri gameconfig already carries raised pools) or Resource Extensions [Enhanced] by Koukol,
/// a single ASI whose author asks to delete every other adjuster and any custom gameconfig.xml.
/// Mod Hub never redistributes Resource Extensions (its licence forbids it); it only recognises it.
/// </summary>
public static class LimitSetup
{
    public const string ResourceExtensionsAsi = "ResourceExtensions.asi";
    public static readonly Uri ResourceExtensionsPage = new("https://www.gta5-mods.com/scripts/resource-extensions");

    // Adjusters the Resource Extensions page lists as covered, besides the ones in AdjusterComponents.Catalog.
    private static readonly string[] OtherAdjusters =
    [
        "SirenSettingLimitAdjuster.asi",
        "SirenSettingLimitAdjusterEnhanced.asi",
        "WeaponLimitsAdjuster.asi",
        "WeaponLimitAdjuster.asi",
        "PedPropLimitAdjuster.asi",
        "ResourceAdjuster.asi",
        "fwBoxStreamerPatch.asi",
    ];

    public static bool HasResourceExtensions(GameInstall game) =>
        game.Edition == GameEdition.Enhanced && File.Exists(Path.Combine(game.Root, ResourceExtensionsAsi));

    /// <summary>A gameconfig.xml of its own is not needed: Resource Extensions forbids one, NVE (onigiri) ships its own.</summary>
    public static bool GameConfigNeeded(GameInstall game) => !HasResourceExtensions(game) && !game.UsesOnigiri;

    /// <summary>Limit plugins in the game root that Resource Extensions replaces.</summary>
    public static IReadOnlyList<string> InstalledAdjusters(GameInstall game) =>
        AdjusterComponents
            .Catalog.SelectMany(c => c.InstalledNames)
            .Concat(OtherAdjusters)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => File.Exists(Path.Combine(game.Root, n)))
            .ToArray();

    /// <summary>Mixed or doubled limit setups that make the game crash or ignore limits; empty when the setup is consistent.</summary>
    public static IReadOnlyList<string> Conflicts(GameInstall game)
    {
        var result = new List<string>();
        var heaps = AdjusterComponents
            .Catalog.Single(c => c.Id == "heap")
            .InstalledNames.Where(n => File.Exists(Path.Combine(game.Root, n)))
            .ToArray();
        if (!HasResourceExtensions(game))
        {
            if (heaps.Length > 1)
                result.Add(L.T("core.limits.doubleHeapAdjuster", string.Join(", ", heaps)));
            return result;
        }
        var adjusters = InstalledAdjusters(game);
        if (adjusters.Count > 0)
            result.Add(L.T("core.limits.resourceExtensionsWithAdjusters", string.Join(", ", adjusters)));
        if (File.Exists(Path.Combine(game.Root, Oiv.ModCompatibility.OverlayConfig)))
            result.Add(L.T("core.limits.resourceExtensionsWithNveGameconfig", Oiv.ModCompatibility.OverlayConfig));
        else if (!game.UsesOnigiri && Base.StarterPackage.GameConfigInstalled(game))
            result.Add(L.T("core.limits.resourceExtensionsWithGameconfig"));
        return result;
    }
}
