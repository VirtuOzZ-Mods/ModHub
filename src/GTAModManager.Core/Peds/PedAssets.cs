using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;

namespace GTAModManager.Core.Peds;

/// <summary>One character file to replace: from where, to where (outer archive | nested archive | inner path) and what it is.</summary>
public sealed record PedAssetFile(string Source, string Outer, string Nested, string Inner, string What);

public sealed class PedAssetPlan
{
    /// <summary>"Replacement per author instructions", "MP character clothing (male)", etc.</summary>
    public string Kind { get; set; } = "";
    public List<PedAssetFile> Files { get; } = [];
    public List<string> Notes { get; } = [];
    public List<string> Errors { get; } = [];

    /// <summary>Clothing for an MP character where the archive does not reveal whether it is male or female: the user must choose.</summary>
    public bool NeedsGender { get; set; }

    /// <summary>Install operations: files are grouped by archive (as in an OIV package).</summary>
    public List<OivOp> Ops() =>
        Files
            .GroupBy(f => (f.Outer, f.Nested))
            .Select(g =>
                (OivOp)
                    new OivArchive(
                        g.Key.Outer,
                        false,
                        "RPF7",
                        g.Key.Nested.Length == 0
                            ? g.Select(f => (OivOp)new OivAdd(f.Source, f.Inner)).ToList()
                            :
                            [
                                new OivArchive(
                                    g.Key.Nested,
                                    false,
                                    "RPF7",
                                    g.Select(f => (OivOp)new OivAdd(f.Source, f.Inner)).ToList()
                                ),
                            ]
                    )
            )
            .ToList();
}

/// <summary>
/// Character model and clothing replacements (not add-on DLC): .ydd/.ytd/.yft files go inside game archives (copies in mods).
/// Two sources of truth, with no path guessing:
/// 1) the author's instruction "Place &lt;folder/file&gt; into: &lt;path&gt;" (multi-part mods such as a Trevor replacement);
/// 2) MP character clothing by standard slot names (jbib_005_u.ydd, feet_diff_014_a_uni.ytd, mp_m_freemode_01^accs_100_u.ydd):
///    gender from the name, "MP Male/Female" folders, the archive name or a readme path; otherwise we ask.
/// </summary>
public static partial class PedAssets
{
    public const string MpPeds = @"x64v.rpf\models\cdimages\streamedpeds_mp.rpf";
    public const string MpProps = @"x64v.rpf\models\cdimages\streamedpedprops.rpf";
    private static readonly string[] AssetExtensions = [".ydd", ".ytd", ".yft", ".ymt", ".ydr"];

    [GeneratedRegex(
        @"^(?:(?<prefix>[^\\^]+)\^)?(?<slot>p_head|p_eyes|p_ears|p_lwrist|p_rwrist|p_mouth|p_lhand|p_rhand|p_lhip|p_rhip|head|berd|hair|uppr|lowr|hand|feet|teef|accs|task|decl|jbib)_(?:diff_)?(?<num>\d{3})(?:_|\.)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SlotRx();

    // "Place \"player_two folder\" & \"player_two.yft\" into:" / "Put … in" / "Copy … to" and the Russian equivalent.
    // "Place \"player_two folder\" & \"player_two.yft\" into:", "…paste all from \"mppatchseng\" folder in:" and the Russian equivalent.
    [GeneratedRegex(
        @"\b(?:place|put|paste|copy|drag|move|помест\w*|вставь\w*|скопир\w*|перемест\w*|закин\w*)\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex PlaceRx();

    [GeneratedRegex(
        @"\ball\s+(?:the\s+)?(?:files\s+)?from\b|\bcontents?\s+of\b|содержим\w*|\bвсё\s+из\b|\bвсе\s+файлы\s+из\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ContentsRx();

    [GeneratedRegex(@"[A-Za-z0-9_\-\\/ .()]*\.rpf[A-Za-z0-9_\-\\/ .()]*", RegexOptions.IgnoreCase)]
    private static partial Regex RpfPathRx();

    [GeneratedRegex(@"^\s*(?:part|часть)\s*(?<n>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PartRx();

    public static bool IsAsset(string path) =>
        AssetExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Game path ("Grand Theft Auto V\mods\x64v.rpf\…\streamedpeds_players.rpf") → outer archive | nested archive | folder.</summary>
    public static (string Outer, string Nested, string Dir)? SplitGamePath(string raw)
    {
        var p = raw.Trim().Trim('"', '\'', '.', ' ').Replace('/', '\\');
        var gta = p.IndexOf("Grand Theft Auto V\\", StringComparison.OrdinalIgnoreCase);
        if (gta >= 0)
            p = p[(gta + "Grand Theft Auto V\\".Length)..];
        p = Regex.Replace(p.Trim('\\', ' '), @"^(?:mods\\)+", "", RegexOptions.IgnoreCase).Trim('\\');
        var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var rpfs = parts
            .Select((s, i) => (s, i))
            .Where(x => x.s.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.i)
            .ToList();
        if (rpfs.Count == 0 || rpfs.Count > 2 || parts.Any(s => s is "." or ".."))
            return null;
        var outer = string.Join('\\', parts[..(rpfs[0] + 1)]);
        if (rpfs.Count == 1)
            return (outer, "", string.Join('\\', parts[(rpfs[0] + 1)..]));
        return (
            outer,
            string.Join('\\', parts[(rpfs[0] + 1)..(rpfs[1] + 1)]),
            string.Join('\\', parts[(rpfs[1] + 1)..])
        );
    }

    public static PedAssetPlan? Plan(ModArchive archive, IReadOnlyList<string> files, string docs, PedGender? gender)
    {
        var assets = files.Where(IsAsset).ToList();
        if (assets.Count == 0)
            return null;
        if (
            files.Any(f =>
                Path.GetFileName(f).Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(f).Equals("__resource.lua", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            var fivem = new PedAssetPlan { Kind = "Набор для FiveM" };
            fivem.Errors.Add(L.T("core.peds.fivemServerPackFxmanifestLua"));
            return fivem;
        }
        return ByInstruction(files, docs) ?? Clothing(archive, assets, files, docs, gender);
    }

    /// <summary>Author instructions: "what → where" pairs (taking PART 1/2/… sections that match archive folders into account).</summary>
    private static PedAssetPlan? ByInstruction(IReadOnlyList<string> files, string docs)
    {
        var lines = docs.Replace("\r", "").Split('\n');
        var plan = new PedAssetPlan { Kind = "Замена по инструкции автора" };
        string? section = null;
        int steps = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (PartRx().Match(lines[i]) is { Success: true } part)
            {
                section = "part " + part.Groups["n"].Value;
                continue;
            }
            var line = lines[i];
            if (!PlaceRx().IsMatch(line))
                continue;
            var quoted = Regex.Matches(line, "\"([^\"]+)\"|«([^»]+)»");
            if (quoted.Count == 0)
                continue;
            var names = quoted
                .Select(x => (x.Groups[1].Success ? x.Groups[1].Value : x.Groups[2].Value).Trim())
                .ToList();
            bool contents = ContentsRx().IsMatch(line); // "all from \"X\" folder" means the folder contents, not the folder itself
            // The path follows the names on the same line or on the next few lines.
            string? target = null;
            var tail = line[(quoted[^1].Index + quoted[^1].Length)..];
            for (int j = i; j < Math.Min(lines.Length, i + 4) && target is null; j++)
            {
                var candidate = j == i ? tail : lines[j];
                if (RpfPathRx().Match(candidate) is { Success: true } rp && SplitGamePath(rp.Value) is not null)
                    target = rp.Value;
            }
            if (target is null || SplitGamePath(target) is not { } dest)
                continue;
            steps++;
            foreach (var name in names)
            {
                bool folder =
                    contents
                    || name.EndsWith(" folder", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(" папк", StringComparison.OrdinalIgnoreCase)
                    || !Path.HasExtension(name);
                var clean = Regex.Replace(name, @"\s+(folder|папк\w*)$", "", RegexOptions.IgnoreCase).Trim();
                var found = Match(files, clean, folder, section);
                if (contents)
                    found = found
                        .Select(f => (f.Source, f.Rel.Contains('\\') ? f.Rel[(f.Rel.IndexOf('\\') + 1)..] : f.Rel))
                        .ToList();
                if (found.Count == 0)
                {
                    plan.Notes.Add(
                        L.T("core.peds.archiveHasNoAlthoughInstructions", Cap(section), clean, target.Trim())
                    );
                    continue;
                }
                foreach (var (source, rel) in found)
                {
                    var inner = dest.Dir.Length == 0 ? rel : dest.Dir + "\\" + rel;
                    plan.Files.Add(
                        new PedAssetFile(source, dest.Outer, dest.Nested, inner, Cap(section) + ": " + clean)
                    );
                }
            }
        }
        return steps > 0 && plan.Files.Count > 0 ? plan : null;
    }

    private static string Cap(string? section) =>
        section is null ? L.T("core.peds.instructions") : section.ToUpperInvariant();

    /// <summary>Files/folder with this name; on multiple matches, those inside the current section folder (PART N).</summary>
    private static List<(string Source, string Rel)> Match(
        IReadOnlyList<string> files,
        string name,
        bool folder,
        string? section
    )
    {
        var result = new List<(string, string)>();
        foreach (var f in files)
        {
            var parts = f.Split('\\');
            if (folder)
            {
                var i = Array.FindIndex(parts, p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && i < parts.Length - 1)
                    result.Add((f, string.Join('\\', parts[i..])));
            }
            else if (parts[^1].Equals(name, StringComparison.OrdinalIgnoreCase))
                result.Add((f, parts[^1]));
        }
        if (section is not null)
        {
            var inSection = result
                .Where(r =>
                    r.Item1.Split('\\')
                        .Any(p =>
                            Regex.Replace(p, @"\s+", " ").Trim().Equals(section, StringComparison.OrdinalIgnoreCase)
                        )
                )
                .ToList();
            if (inSection.Count > 0 || result.Any(r => Regex.IsMatch(r.Item1, @"(?i)(^|\\)part\s*\d+\\")))
                return inSection;
        }
        return result;
    }

    /// <summary>MP character clothing (mp_m/mp_f_freemode_01) by standard slot names.</summary>
    private static PedAssetPlan? Clothing(
        ModArchive archive,
        List<string> assets,
        IReadOnlyList<string> files,
        string docs,
        PedGender? chosen
    )
    {
        var items = assets.Select(f => (File: f, Match: SlotRx().Match(Path.GetFileName(f)))).ToList();
        if (items.Any(x => !x.Match.Success))
            return null; // not only clothing: not our case
        var plan = new PedAssetPlan();
        PedGender? archiveGender = GenderOf(Path.GetFileName(archive.FilePath)) ?? DocsGender(docs);
        foreach (var (file, m) in items)
        {
            var gender = GenderOf(m.Groups["prefix"].Value)
                ?? GenderOf(Path.GetDirectoryName(file) ?? "")
                ?? archiveGender
                ?? chosen;
            if (gender is null)
            {
                plan.NeedsGender = true;
                continue;
            }
            var g = gender == PedGender.Male ? "mp_m_freemode_01" : "mp_f_freemode_01";
            bool prop = m.Groups["slot"].Value.StartsWith("p_", StringComparison.OrdinalIgnoreCase);
            var name = Path.GetFileName(file);
            var caret = name.LastIndexOf('^');
            if (caret >= 0)
                name = name[(caret + 1)..]; // «mp_f_freemode_01_mp_f_noangel^berd_000_u.ydd» → «berd_000_u.ydd»
            var root = prop ? MpProps : MpPeds;
            var split = SplitGamePath(root)!.Value;
            plan.Files.Add(
                new PedAssetFile(
                    file,
                    split.Outer,
                    split.Nested,
                    (prop ? g + "_p" : g) + "\\" + name,
                    L.T(
                        "core.peds.character",
                        SlotName(m.Groups["slot"].Value),
                        int.Parse(m.Groups["num"].Value),
                        (gender == PedGender.Male ? L.T("core.peds.male") : L.T("core.peds.female"))
                    )
                )
            );
        }
        if (plan.NeedsGender)
        {
            plan.Kind = "Одежда MP-персонажа";
            plan.Errors.Add(L.T("core.peds.archiveDoesNotIndicateWhich"));
            return plan;
        }
        var genders = plan
            .Files.Select(f => f.Inner.StartsWith("mp_m", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .Count();
        plan.Kind =
            genders > 1 ? "Одежда MP-персонажей (мужской и женский)"
            : plan.Files[0].Inner.StartsWith("mp_m", StringComparison.OrdinalIgnoreCase)
                ? "Одежда MP-персонажа (мужской)"
            : "Одежда MP-персонажа (женский)";
        if (items.Any(x => x.Match.Groups["prefix"].Value.Contains("_mp_", StringComparison.OrdinalIgnoreCase)))
            plan.Notes.Add(L.T("core.peds.filesFromCollectionPackFivem"));
        plan.Notes.Add(L.T("core.peds.replacementDefaultItemsSameSlot"));
        return plan;
    }

    public static string SlotName(string slot) =>
        slot.ToLowerInvariant() switch
        {
            "head" => L.T("core.peds.head"),
            "berd" => L.T("core.peds.mask"),
            "hair" => L.T("core.peds.hairstyle"),
            "uppr" => L.T("core.peds.torso"),
            "lowr" => L.T("core.peds.legs"),
            "hand" => L.T("core.peds.bagParachute"),
            "feet" => L.T("core.peds.shoes"),
            "teef" => L.T("core.peds.accessory"),
            "accs" => L.T("core.peds.accessoryNeck"),
            "task" => L.T("core.peds.bodyArmor"),
            "decl" => L.T("core.peds.decal"),
            "jbib" => L.T("core.peds.top"),
            "p_head" => L.T("core.peds.headwear"),
            "p_eyes" => L.T("core.peds.glasses"),
            "p_ears" => L.T("core.peds.earrings"),
            "p_lwrist" => L.T("core.peds.watch"),
            "p_rwrist" => L.T("core.peds.bracelet"),
            _ => slot,
        };

    private static PedGender? GenderOf(string text)
    {
        var t = text.ToLowerInvariant();
        bool male = Regex.IsMatch(t, @"mp_m_freemode|\bmp[\s_\-]?male\b|\bmale\b|мужск");
        bool female = Regex.IsMatch(t, @"mp_f_freemode|\bmp[\s_\-]?fem(a|e)le\b|\bfem(a|e)le\b|женск");
        return male == female ? null
            : male ? PedGender.Male
            : PedGender.Female;
    }

    private static PedGender? DocsGender(string docs)
    {
        bool male = docs.Contains("mp_m_freemode_01", StringComparison.OrdinalIgnoreCase);
        bool female = docs.Contains("mp_f_freemode_01", StringComparison.OrdinalIgnoreCase);
        return male == female ? null
            : male ? PedGender.Male
            : PedGender.Female;
    }

    /// <summary>Text of all instructions in the archive (up to 16 files of 1 MB each).</summary>
    public static string Docs(ModArchive archive, IEnumerable<string> files)
    {
        var sb = new StringBuilder();
        foreach (
            var f in files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".txt" or ".md" or ".nfo").Take(16)
        )
        {
            try
            {
                if (archive.SourceSize(f) > 1 << 20)
                    continue;
                var bytes = archive.Read(f);
                var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
                sb.AppendLine(text);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        }
        return sb.ToString();
    }
}

/// <summary>Gender choice for untagged clothing, remembered by archive content (gmm reads it during installation too).</summary>
public static class PedAssetChoices
{
    private static string FilePath => AppPaths.LibraryFile("ped-asset-choices.json");

    public static string Key(string archive)
    {
        using var fs = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static Dictionary<string, PedGender> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, PedGender>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static PedGender? Get(string archive)
    {
        try
        {
            return Load().TryGetValue(Key(archive), out var g) ? g : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Set(string archive, PedGender gender)
    {
        var map = Load();
        map[Key(archive)] = gender;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(map.TakeLast(500).ToDictionary()));
    }
}
