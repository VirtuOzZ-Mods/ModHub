using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GTAModManager.Core.Game;

namespace GTAModManager.Core.Instructions;

public sealed record VariantOption(string Key, string Label);

/// <summary>A "pick one" group: sibling folders that provide the same files (Version 1/2, Silent, ON/OFF…).</summary>
public sealed record VariantGroup(string Key, string Title, IReadOnlyList<VariantOption> Options);

/// <summary>
/// Variants inside a mod archive, detected by structure rather than names: if sibling folders contain files with the same
/// relative paths, they are mutually exclusive variants. Nested forks (Waste Gate (New)\Version 2 and
/// Waste Gate (Old)\Silent) collapse into one group. Step folders (PART 1, Step 2), game versions (Legacy/Enhanced)
/// and backups of the originals (Original files, Backup) are not variants.
/// </summary>
public static partial class Variants
{
    [GeneratedRegex(
        @"^\s*(original(\s+(files?|backups?|sounds?))?|originals|backups?|vanilla(\s+files?)?|default\s+files|uninstall\w*|оригинал\w*(\s+файл\w*)?|резервн\w*(\s+копи\w*)?)\s*$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex BackupRx();

    [GeneratedRegex(
        @"^\s*\d*\s*[-.)]?\s*(part|step|stage|часть|шаг|этап)\s*\d+|^\s*\d+\s*[-.)]\s*(install|установк)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SequentialRx();

    [GeneratedRegex(@"legacy|enhanced|gen\s*8|gen\s*9|\be&e\b", RegexOptions.IgnoreCase)]
    private static partial Regex EditionRx();

    [GeneratedRegex(@"manual|ручн|copy.?manually|without.?oiv", RegexOptions.IgnoreCase)]
    private static partial Regex ManualRx();

    // Clothing for the male and the female character is installed together; that is not a choice.
    [GeneratedRegex(@"\b(fe)?male\b|\bmp_[mf]_|мужск|женск", RegexOptions.IgnoreCase)]
    private static partial Regex GenderRx();

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".md",
        ".nfo",
        ".pdf",
        ".url",
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp",
        ".bmp",
        ".html",
        ".htm",
        ".rtf",
        ".docx",
        ".mp4",
        ".lnk",
    };

    /// <summary>A folder with a backup of the original files: its content is never installed.</summary>
    public static bool IsBackupFolder(string segment) => BackupRx().IsMatch(segment);

    public static bool InBackupFolder(string path) => path.Split('\\')[..^1].Any(IsBackupFolder);

    public static IReadOnlyList<VariantGroup> Find(IEnumerable<string> files)
    {
        var list = files.Where(f => !InBackupFolder(f) && !DocExtensions.Contains(Path.GetExtension(f))).ToList();
        // For each folder, the relative paths of the files inside it.
        var under = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in list)
        {
            var parts = f.Split('\\');
            for (int i = 1; i < parts.Length; i++)
            {
                var dir = string.Join('\\', parts[..i]);
                if (!under.TryGetValue(dir, out var set))
                    under[dir] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(string.Join('\\', parts[i..]));
            }
        }
        // Alternative folders: siblings with shared relative paths.
        var alternatives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<(string A, string B)>();
        foreach (
            var siblings in under.Keys.GroupBy(
                d => d.Contains('\\') ? d[..d.LastIndexOf('\\')] : "",
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            // A manual-install folder that duplicates the OIV package is not a variant (the OIV takes precedence).
            var children = siblings
                .Where(d =>
                    !SequentialRx().IsMatch(Leaf(d))
                    && !EditionRx().IsMatch(Leaf(d))
                    && !GenderRx().IsMatch(Leaf(d))
                    && !ManualRx().IsMatch(Leaf(d))
                )
                .ToList();
            for (int i = 0; i < children.Count; i++)
            for (int j = i + 1; j < children.Count; j++)
                // Variants are folders with mostly the same file set (at least half of the shared set).
                // Different DLC packs side by side (spbmr and spbmr_patch) share only the service dlc.rpf, and trainer data folders
                // (Menyoo: Outfit\GhostRider.xml and Vehicle\GhostRider.xml) share a name by accident: these are not variants.
                if (AreAlternatives(under[children[i]], under[children[j]]))
                {
                    alternatives.Add(children[i]);
                    alternatives.Add(children[j]);
                    pairs.Add((children[i], children[j]));
                }
        }
        if (alternatives.Count == 0)
            return [];

        // A file's variant is the deepest alternative folder above it; conflicting variants are merged into a group.
        var optionOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in list)
        {
            var parts = f.Split('\\');
            for (int i = parts.Length - 1; i >= 1; i--)
            {
                var dir = string.Join('\\', parts[..i]);
                if (alternatives.Contains(dir))
                {
                    optionOf[f] = dir;
                    break;
                }
            }
        }
        var options = optionOf.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parent = options.ToDictionary(o => o, o => o, StringComparer.OrdinalIgnoreCase);
        string Root(string o)
        {
            while (!parent[o].Equals(o, StringComparison.OrdinalIgnoreCase))
                o = parent[o] = parent[parent[o]];
            return o;
        }
        foreach (var byName in optionOf.GroupBy(p => p.Key[(p.Value.Length + 1)..], StringComparer.OrdinalIgnoreCase))
        {
            var first = byName.First().Value;
            foreach (var other in byName.Skip(1))
                parent[Root(other.Value)] = Root(first);
        }
        // Variants inside sibling alternative folders form a single fork, even with different nesting.
        foreach (var (x, y) in pairs)
        {
            var inX = options
                .Where(o =>
                    o.Equals(x, StringComparison.OrdinalIgnoreCase)
                    || o.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase)
                )
                .ToList();
            var inY = options
                .Where(o =>
                    o.Equals(y, StringComparison.OrdinalIgnoreCase)
                    || o.StartsWith(y + "\\", StringComparison.OrdinalIgnoreCase)
                )
                .ToList();
            foreach (var o in inX.Concat(inY).Skip(1))
                parent[Root(o)] = Root(inX.Concat(inY).First());
        }
        var groups = new List<VariantGroup>();
        foreach (var group in options.GroupBy(Root, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var members = group.OrderBy(o => o, NaturalComparer.Instance).ToList();
            var common = CommonFolder(members);
            string Label(string o) => (common.Length == 0 ? o : o[(common.Length + 1)..]).Replace("\\", " / ");
            // The title is the fork folder; if it is the archive root, the variant's shared files ("WASTE_GATE.wav").
            bool rootLevel =
                common.Length == 0 || list.All(f => f.StartsWith(common + "\\", StringComparison.OrdinalIgnoreCase));
            var shared = optionOf
                .Where(p => members.Contains(p.Value, StringComparer.OrdinalIgnoreCase))
                .Select(p => Path.GetFileName(p.Key))
                .Where(n => !n.Equals("dlc.rpf", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToList();
            // An empty title makes the UI show just "Mod variant".
            var title =
                !rootLevel ? Leaf(common)
                : shared.Count is > 0 and <= 2 ? string.Join(", ", shared)
                : "";
            groups.Add(
                new VariantGroup(
                    common.Length == 0 ? "\\" : common,
                    title,
                    members.Select(o => new VariantOption(o, Label(o))).ToList()
                )
            );
        }
        return groups;
    }

    /// <summary>Files after applying the choice: only the chosen variant from each group; a group without a choice is dropped entirely.</summary>
    public static List<string> Apply(
        IEnumerable<string> files,
        IReadOnlyList<VariantGroup> groups,
        IReadOnlyDictionary<string, string> chosen
    )
    {
        var drop = groups
            .SelectMany(g =>
                g.Options.Where(o =>
                    !(chosen.TryGetValue(g.Key, out var c) && c.Equals(o.Key, StringComparison.OrdinalIgnoreCase))
                )
            )
            .Select(o => o.Key + "\\")
            .ToList();
        return files.Where(f => !drop.Any(d => f.StartsWith(d, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static bool AreAlternatives(HashSet<string> a, HashSet<string> b)
    {
        var shared = a.Intersect(b, StringComparer.OrdinalIgnoreCase)
            .Where(p => !p.Equals("dlc.rpf", StringComparison.OrdinalIgnoreCase))
            .Count();
        if (shared == 0)
            return false;
        int union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
        return shared * 2 >= union;
    }

    private static string Leaf(string dir) => dir.Contains('\\') ? dir[(dir.LastIndexOf('\\') + 1)..] : dir;

    private static string CommonFolder(IReadOnlyList<string> dirs)
    {
        var first = dirs[0].Split('\\');
        int n = first.Length;
        foreach (var d in dirs.Skip(1))
        {
            var parts = d.Split('\\');
            int k = 0;
            while (k < Math.Min(n, parts.Length) && parts[k].Equals(first[k], StringComparison.OrdinalIgnoreCase))
                k++;
            n = k;
        }
        // The common ancestor cannot be the variant itself.
        n = Math.Min(n, dirs.Min(d => d.Split('\\').Length) - 1);
        return string.Join('\\', first[..Math.Max(0, n)]);
    }

    /// <summary>"Version 2" sorts before "Version 10".</summary>
    private sealed partial class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        [GeneratedRegex(@"\d+")]
        private static partial Regex Digits();

        public int Compare(string? a, string? b) =>
            string.Compare(
                Digits().Replace(a ?? "", m => m.Value.PadLeft(10, '0')),
                Digits().Replace(b ?? "", m => m.Value.PadLeft(10, '0')),
                StringComparison.OrdinalIgnoreCase
            );
    }
}

/// <summary>Variant choice remembered by archive content (gmm reads it during installation too).</summary>
public static class InstallChoices
{
    private static string FilePath => AppPaths.LibraryFile("install-choices.json");

    public static string Key(string archive)
    {
        using var fs = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                    File.ReadAllText(FilePath, Encoding.UTF8)
                ) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyDictionary<string, string> Get(string archive)
    {
        try
        {
            return Load().TryGetValue(Key(archive), out var c) ? c : new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static void Set(string archive, IReadOnlyDictionary<string, string> choices)
    {
        var all = Load();
        all[Key(archive)] = new Dictionary<string, string>(choices, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all.TakeLast(500).ToDictionary()), Encoding.UTF8);
    }
}
