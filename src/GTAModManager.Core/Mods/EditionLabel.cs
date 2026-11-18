using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Mods;

/// <summary>
/// An explicit game version tag in a mod or package name: "[Legacy]", "(Enhanced)", "Legacy Version", "Enhanced Edition",
/// "for Legacy", "GTA V Enhanced", "Gen9", "E&amp;E", and the version as a separate part of a file name
/// ("NOS_1.9.5_Enhanced_Install.oiv", "Legacy.oiv"). The ordinary English word "Enhanced" in a title
/// ("Enhanced Native Trainer" is a Legacy mod) does not count as a tag.
/// </summary>
public static partial class EditionLabel
{
    [GeneratedRegex(
        @"[\[(][^\])]*\blegacy\b[^\])]*[\])]|\blegacy\s+(version|edition|only|build)\b|\b(for|для|под)\s+(gta\s*(v|5)\s*)?legacy\b|\bgta\s*(v|5)\s*legacy\b|(^|[_\-.])\s*legacy\s*($|[_\-.])|\bgen\s*8\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex LegacyRx();

    [GeneratedRegex(
        @"[\[(][^\])]*\b(enhanced|e&e)\b[^\])]*[\])]|\benhanced\s+(version|edition|only|build)\b|\b(for|для|под)\s+(gta\s*(v|5)\s*)?enhanced\b|\bgta\s*(v|5)\s*enhanced\b|(^|[_\-.])\s*enhanced\s*($|[_\-.])|\bgen\s*9\b|\be&e\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex EnhancedRx();

    [GeneratedRegex(@"\b(legacy|enhanced)\s*(and|&|\+|/|и|или|or)\s*(legacy|enhanced)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BothRx();

    /// <summary>Game version the text is explicitly tagged for; null if there is no tag or both are listed.</summary>
    public static GameEdition? Of(string text)
    {
        if (BothRx().IsMatch(text))
            return null; // «Legacy and Enhanced», «Enhanced & Legacy», «Legacy/Enhanced»
        bool legacy = LegacyRx().IsMatch(text),
            enhanced = EnhancedRx().IsMatch(text);
        return legacy == enhanced ? null
            : legacy ? GameEdition.Legacy
            : GameEdition.Enhanced;
    }

    /// <summary>A "mod for another game version" error, or null. where describes what is tagged (archive name, package).</summary>
    public static string? Mismatch(string text, GameEdition game, string where)
    {
        if (Of(text) is not { } edition || edition == game)
            return null;
        return L.T("core.mods.modMarkedGtaVBut", where, edition, game);
    }
}
