using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Peds;

/// <summary>An installed character appearance/clothing replacement (one operation in History).</summary>
public sealed record InstalledAccessory(
    string RecordId,
    string Title,
    DateTimeOffset Created,
    IReadOnlyList<string> Characters,
    IReadOnlyList<string> Slots,
    IReadOnlyList<string> Paths
);

/// <summary>
/// Character Accessories: clothing and appearance replacements (.ydd/.ytd/.yft files inside character archives) installed
/// through Mod Hub. The list is built from History records (backups store the paths of replaced files inside archives).
/// </summary>
public static partial class PedAccessories
{
    [GeneratedRegex(
        @"\\(?<char>mp_[mf]_freemode_01(?:_p)?|player_(?:zero|one|two)(?:_p)?|player_two_trevor_heist|[a-z0-9_]+?_p)\\(?<file>[^\\]+\.(?:ydd|ytd|yft))$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex CharacterFileRx();

    [GeneratedRegex(
        @"^(?<slot>p_head|p_eyes|p_ears|p_lwrist|p_rwrist|head|berd|hair|uppr|lowr|hand|feet|teef|accs|task|decl|jbib)_(?:diff_)?(?<num>\d{3})",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SlotRx();

    public static string CharacterName(string folder) =>
        folder.ToLowerInvariant() switch
        {
            "mp_m_freemode_01" or "mp_m_freemode_01_p" => L.T("core.peds.mpCharacterMale"),
            "mp_f_freemode_01" or "mp_f_freemode_01_p" => L.T("core.peds.mpCharacterFemale"),
            "player_zero" or "player_zero_p" => L.T("core.peds.michael"),
            "player_one" or "player_one_p" => L.T("core.peds.franklin"),
            "player_two" or "player_two_p" or "player_two_trevor_heist" => L.T("core.peds.trevor"),
            _ => folder,
        };

    /// <summary>Character and slot of a file inside character archives (or null if it is not a character file).</summary>
    public static (string Character, string? Slot)? Describe(string path)
    {
        var m = CharacterFileRx().Match(path);
        if (!m.Success)
            return null;
        var s = SlotRx().Match(m.Groups["file"].Value);
        var slot = s.Success
            ? $"{PedAssets.SlotName(s.Groups["slot"].Value)} №{int.Parse(s.Groups["num"].Value)}"
            : null;
        return (CharacterName(m.Groups["char"].Value), slot);
    }

    public static IReadOnlyList<InstalledAccessory> Installed(BackupLibrary library, GameEdition edition)
    {
        var result = new List<InstalledAccessory>();
        foreach (var r in library.Records(edition.ToString()).Where(r => !r.Undone).OrderByDescending(r => r.Created))
        {
            var paths = r
                .Archives.SelectMany(a =>
                    a.Entries.Where(e => e.Kind != InnerKind.Deleted).Select(e => a.Rel + "\\" + e.Path)
                )
                .Where(p => Describe(p) is not null)
                .ToList();
            if (paths.Count == 0)
                continue;
            var described = paths.Select(p => Describe(p)!.Value).ToList();
            result.Add(
                new InstalledAccessory(
                    r.Id,
                    Mods.ModNames.Pretty(r.Title),
                    r.Created,
                    described.Select(d => d.Character).Distinct().ToList(),
                    described.Where(d => d.Slot is not null).Select(d => d.Slot!).Distinct().ToList(),
                    paths
                )
            );
        }
        return result;
    }

    /// <summary>Which target paths are already taken by other installed mods: path → mod name.</summary>
    public static IReadOnlyDictionary<string, string> Occupied(
        BackupLibrary library,
        GameEdition edition,
        IEnumerable<string> targets
    )
    {
        var wanted = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Installed(library, edition))
        foreach (var p in a.Paths)
            if (wanted.Contains(p))
                result.TryAdd(p, a.Title);
        return result;
    }
}
