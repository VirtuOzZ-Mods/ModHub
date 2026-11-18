using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Inventory;

public enum ModOrigin
{
    ModHub,
    External,
}

public sealed record ModFileItem(string Rel, long Size, string Hash, ModOrigin Origin);

/// <summary>A mod in the inventory: file set, name, origin (via Mod Hub or added manually) and page link.</summary>
public sealed class ModGroup
{
    public required string Name { get; set; }
    public bool GeneratedName { get; init; }

    // Canonical names remain stable for grouping, labels and exported mod lists.
    public string DisplayName =>
        GeneratedName ? Localization.L.Owned(Name)
        : Origin == ModOrigin.ModHub ? Mods.ModNames.DisplayOperationTitle(Name)
        : Name;
    public required string Kind { get; init; }
    public ModOrigin Origin { get; init; }

    /// <summary>The name was found automatically or labeled by the user (as opposed to Unrecognized).</summary>
    public bool Recognized { get; set; }

    /// <summary>A component shipped with Mod Hub itself (modding essentials, Ped Selector): no mod page or link.</summary>
    public bool Builtin { get; init; }
    public string? Url { get; set; }
    public string? Detail { get; set; }
    public List<ModFileItem> Files { get; } = [];

    /// <summary>Mod Hub installations (journal ids) that make up this mod; "Remove" undoes all of them.</summary>
    public List<string> Operations { get; } = [];

    /// <summary>Title of each installation in <see cref="Operations"/> (journal id → title), for removing one component.</summary>
    public Dictionary<string, string> OperationTitles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long Size => Files.Sum(f => f.Size);

    /// <summary>My Mods section, from what the mod put into the game.</summary>
    public ModCategory Category { get; set; } = ModCategory.Other;

    /// <summary>The mod's add-on packs and their recognized contents (vehicles, engine sounds…).</summary>
    public List<Dlc.InstalledPack> Packs { get; } = [];

    /// <summary>Engine sound changes made for this mod's cars, oldest first (each is one installation).</summary>
    public List<Dlc.SoundAssignment> SoundAssignments { get; } = [];
}

public sealed record InventoryReport(
    IReadOnlyList<ModGroup> Groups,
    IReadOnlyCollection<string> FrozenJournals,
    IReadOnlyList<string> Warnings
)
{
    public int ExternalFiles => Groups.Where(g => g.Origin == ModOrigin.External).Sum(g => g.Files.Count);
    public int UnrecognizedFiles => Groups.Where(g => !g.Recognized).Sum(g => g.Files.Count);
}

/// <summary>
/// My Mods: an inventory of everything in the game folder beyond Rockstar's files. A file counts as installed through Mod Hub
/// if its content matches the result of the last Mod Hub operation on it (journal, PostHash); otherwise it was added
/// manually (for example from a personal backup). Manual files are grouped into mods: by user labels (remembered by file
/// hash), by script and ASI metadata, by DLC folders. History entries whose files were changed outside Mod Hub are frozen.
/// </summary>
public static class ModInventory
{
    private static readonly string[] BaseFiles =
    [
        "ScriptHookV.dll",
        "dinput8.dll",
        "ScriptHookVDotNet.asi",
        "ScriptHookVDotNet-Loader.asi",
        "ScriptHookVDotNet.dll",
        "ScriptHookVDotNet.ini",
        "ScriptHookVDotNet2.dll",
        "ScriptHookVDotNet3.dll",
        "MinHook.x64.dll",
        "RageOpenV.asi",
        "HeapAdjuster.asi",
        "ZHeapAdjuster.asi",
        "HeapAdjuster.ini",
        "PackfileLimitAdjusterEnhanced.asi",
        "PackfileLimitAdjusterEnhanced.ini",
        "ModkitLimitAdjusterEnhanced.asi",
        "ModkitLimitAdjusterEnhanced.ini",
        "StaticBoundsLimitAdjuster.asi",
        "StaticBoundsLimitAdjuster.ini",
        "OpenIV.asi",
        "OpenRPF.asi",
        "GMMPedSelector.asi",
        "GMMPedSelector.ini",
        "GMMPedSelector.models.txt",
    ];
    private static readonly string[] LogExtensions = [".log", ".old"];
    private static readonly string[] RuntimeExtensions =
    [
        ".ini",
        ".log",
        ".xml",
        ".json",
        ".txt",
        ".cfg",
        ".bak",
        ".old",
        ".lang",
    ];

    /// <summary>All mod files (paths relative to the game): everything in the root except Rockstar files, plus mod archives in update\ and x64\.</summary>
    public static IEnumerable<string> ModFiles(GameInstall game)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(game.Root))
        {
            var name = Path.GetFileName(entry);
            bool dir = Directory.Exists(entry);
            if (VanillaRestore.IsGameRootEntry(game, name, dir))
                continue;
            if (!dir)
            {
                yield return name;
                continue;
            }
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                continue;
            foreach (var f in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                yield return Path.GetRelativePath(game.Root, f);
        }
        foreach (var top in new[] { "update", "x64" }.Select(d => Path.Combine(game.Root, d)).Where(Directory.Exists))
        foreach (var rpf in Directory.EnumerateFiles(top, "*.rpf", SearchOption.AllDirectories))
            if (GameInstall.IsPlainRpf(rpf))
                yield return Path.GetRelativePath(game.Root, rpf);
    }

    public static InventoryReport Scan(GameInstall game, ModLabels labels, HashCache cache)
    {
        // The last completed Mod Hub operation on each file: its title and expected hash.
        var owner = new Dictionary<string, (string Title, string Id, string? Hash)>(StringComparer.OrdinalIgnoreCase);
        var journals = game.CreateEngine()
            .History()
            .Where(j =>
                j.State == TxState.Completed
                && j.Title is not null
                && !j.Title.StartsWith("Отмена: ")
                && Path.GetFullPath(j.TargetRoot).TrimEnd('\\').Equals(game.Root, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        foreach (var j in journals.OrderBy(j => j.Id))
        foreach (var op in j.Ops)
            owner[GameInstall.Normalize(op.Rel)] = (j.Title!, j.Id, op.PostHash);

        var files = new List<ModFileItem>();
        var frozen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in ModFiles(game))
        {
            var full = Path.Combine(game.Root, rel);
            string hash;
            try
            {
                hash = cache.Hash(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var origin =
                owner.TryGetValue(rel, out var o) && string.Equals(o.Hash, hash, StringComparison.OrdinalIgnoreCase)
                    ? ModOrigin.ModHub
                    : ModOrigin.External;
            files.Add(new ModFileItem(rel, new FileInfo(full).Length, hash, origin));
        }
        // An operation is frozen if any of its files now differs from what the last Mod Hub operation left.
        foreach (var (rel, o) in owner)
        {
            // Mods rewrite their own settings and logs during play; that is not "changed outside Mod Hub".
            if (RuntimeExtensions.Contains(Path.GetExtension(rel), StringComparer.OrdinalIgnoreCase))
                continue;
            var full = Path.Combine(game.Root, rel);
            string? now;
            try
            {
                now = File.Exists(full) ? cache.Hash(full) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (!string.Equals(now, o.Hash, StringComparison.OrdinalIgnoreCase))
                foreach (
                    var j in journals.Where(j =>
                        j.Ops.Any(op => GameInstall.Normalize(op.Rel).Equals(rel, StringComparison.OrdinalIgnoreCase))
                    )
                )
                    frozen.Add(j.Id);
        }
        cache.Save();

        var groups = new List<ModGroup>();
        // Via Mod Hub: by operation title.
        var hubByRel = new Dictionary<string, ModGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var g in files
                .Where(f => f.Origin == ModOrigin.ModHub)
                .GroupBy(f =>
                    owner[f.Rel].Title.StartsWith("NaturalVision Evolved", StringComparison.Ordinal)
                        ? "NaturalVision Evolved"
                    : owner[f.Rel].Title.StartsWith("NaturalVision Enhanced", StringComparison.Ordinal)
                        ? "NaturalVision Enhanced"
                    : owner[f.Rel].Title.StartsWith(NaturalVision.NveBundle.LcppHdTitle, StringComparison.Ordinal)
                        ? NaturalVision.NveBundle.LcppHdTitle
                    : owner[f.Rel].Title.StartsWith(NaturalVision.NveBundle.LcppTitle, StringComparison.Ordinal)
                        ? NaturalVision.NveBundle.LcppTitle
                    : Mods.ModNames.Pretty(owner[f.Rel].Title)
                )
        )
        {
            var group = new ModGroup
            {
                Name = g.Key,
                Kind = "Установлено через Mod Hub",
                Origin = ModOrigin.ModHub,
                Recognized = true,
                Url = labels.UrlFor(g.Key),
            };
            group.Files.AddRange(g);
            foreach (var f in g)
                hubByRel[f.Rel] = group;
            groups.Add(group);
        }
        groups.AddRange(GroupExternal(game, files, hubByRel, labels));

        var warnings = new List<string>();
        if (StaleUpdateRpf(game) is { } stale)
            warnings.Add(stale);
        return new InventoryReport(
            groups
                .OrderBy(g => g.Recognized)
                .ThenBy(g => g.Origin)
                .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            frozen,
            warnings
        );
    }

    /// <summary>Manual files → mods: user labels, base components, scripts, ASI, DLC, archive edits, everything else.</summary>
    public static List<ModGroup> GroupExternal(
        GameInstall game,
        List<ModFileItem> all,
        IReadOnlyDictionary<string, ModGroup> hubByRel,
        ModLabels labels
    )
    {
        var files = all.Where(f => f.Origin == ModOrigin.External).ToList();
        var groups = new Dictionary<string, ModGroup>(StringComparer.OrdinalIgnoreCase);
        ModGroup Get(string name, string kind, bool recognized, bool generatedName = false)
        {
            if (!groups.TryGetValue(name, out var g))
                groups[name] = g = new ModGroup
                {
                    Name = name,
                    Kind = kind,
                    GeneratedName = generatedName,
                    Origin = ModOrigin.External,
                    Recognized = recognized,
                    Url = labels.UrlFor(name),
                };
            return g;
        }
        var rest = new List<ModFileItem>();
        foreach (var f in files)
        {
            if (labels.NameFor(f.Hash) is { } named)
                Get(named, "Подписано вами", true).Files.Add(f);
            else if (!f.Rel.Contains('\\') && BaseFiles.Contains(f.Rel, StringComparer.OrdinalIgnoreCase))
                Get("Базовые компоненты (ScriptHook, загрузчики, адаптеры)", "База для моддинга", true, true)
                    .Files.Add(f);
            else
                rest.Add(f);
        }

        // Scripts: each .dll in scripts\ is a mod; files with the same name prefix and a folder with that name belong to it.
        var scriptDlls = all.Where(f =>
                IsDirectChild(f.Rel, "scripts") && f.Rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        var scriptKeys = scriptDlls
            .Select(f => Path.GetFileNameWithoutExtension(f.Rel))
            .OrderBy(k => k.Length)
            .ToList();
        string? ScriptKey(string rel)
        {
            if (!rel.StartsWith("scripts\\", StringComparison.OrdinalIgnoreCase))
                return null;
            // "Quick Dialogue System\…" and "QuickDialogueSystem.dll" are one mod: compare without spaces and punctuation.
            var tail = Squash(rel["scripts\\".Length..]);
            return scriptKeys
                .Where(k => Squash(k).Length > 0 && tail.StartsWith(Squash(k), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(k => k.Length)
                .FirstOrDefault();
        }
        static string Squash(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
        var asis = all.Where(f => !f.Rel.Contains('\\') && f.Rel.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f.Rel))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var f in rest)
        {
            var parts = f.Rel.Split('\\');
            // A file created by the mod itself (settings, logs) next to its .dll/.asi from Mod Hub is part of that mod.
            var ownerFile =
                ScriptKey(f.Rel) is { } k1
                    ? scriptDlls
                        .FirstOrDefault(d =>
                            Path.GetFileNameWithoutExtension(d.Rel).Equals(k1, StringComparison.OrdinalIgnoreCase)
                        )
                        ?.Rel
                : parts.Length == 1 && asis.Contains(Path.GetFileNameWithoutExtension(f.Rel))
                    ? Path.GetFileNameWithoutExtension(f.Rel) + ".asi"
                : null;
            if (ownerFile is not null && hubByRel.TryGetValue(ownerFile, out var hub))
            {
                hub.Files.Add(f);
                continue;
            }
            if (ScriptKey(f.Rel) is { } key)
            {
                var dll = scriptDlls.FirstOrDefault(d =>
                    Path.GetFileNameWithoutExtension(d.Rel).Equals(key, StringComparison.OrdinalIgnoreCase)
                );
                var title = dll is null ? key : AssemblyTitle(Path.Combine(game.Root, dll.Rel)) ?? key;
                Get(title, "Скрипт (ScriptHookVDotNet)", true).Files.Add(f);
            }
            else if (parts.Length == 1 && asis.Contains(Path.GetFileNameWithoutExtension(f.Rel)))
            {
                var asi = Path.GetFileNameWithoutExtension(f.Rel);
                Get(AsiTitle(Path.Combine(game.Root, asi + ".asi")) ?? asi, "ASI-плагин", true).Files.Add(f);
            }
            else if (DlcName(parts) is { } dlc)
                Get("DLC: " + dlc, "Аддон-DLC", true).Files.Add(f);
            else if (parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase))
                Get(
                    global::System.String.Format("Правки архивов игры: {0}", string.Join('\\', parts.Skip(1))),
                    "Замены в архивах (mods)",
                    false,
                    true
                )
                    .Files.Add(f);
            else if (parts[0].Equals("scripts", StringComparison.OrdinalIgnoreCase))
                Get(
                    parts.Length > 2 ? global::System.String.Format("Скрипты: {0}", parts[1]) : "Скрипты: прочие файлы",
                    "Скрипты",
                    false,
                    true
                )
                    .Files.Add(f);
            else if (LogExtensions.Contains(Path.GetExtension(f.Rel), StringComparer.OrdinalIgnoreCase))
                Get("Логи модов", "Служебные файлы", true, true).Files.Add(f);
            else
                Get(parts.Length > 1 ? parts[0] : Path.GetFileNameWithoutExtension(f.Rel), "Нераспознанное", false)
                    .Files.Add(f);
        }
        return groups.Values.ToList();
    }

    private static bool IsDirectChild(string rel, string dir) =>
        rel.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase) && rel.Count(c => c == '\\') == 1;

    /// <summary>DLC pack name: mods\update\x64\dlcpacks\name\… or update\x64\dlcpacks\name\dlc.rpf.</summary>
    private static string? DlcName(string[] parts)
    {
        var i = Array.FindIndex(parts, p => p.Equals("dlcpacks", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 2 < parts.Length ? parts[i + 1] : null;
    }

    /// <summary>Title of the script's .NET assembly (AssemblyTitle / AssemblyProduct).</summary>
    public static string? AssemblyTitle(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata)
                return null;
            var md = pe.GetMetadataReader();
            string? title = null,
                product = null;
            foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attr = md.GetCustomAttribute(h);
                string? type = attr.Constructor.Kind switch
                {
                    HandleKind.MemberReference
                        when md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent.Kind
                            == HandleKind.TypeReference => md.GetString(
                        md.GetTypeReference(
                            (TypeReferenceHandle)md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent
                        ).Name
                    ),
                    _ => null,
                };
                if (type is not ("AssemblyTitleAttribute" or "AssemblyProductAttribute"))
                    continue;
                var blob = md.GetBlobReader(attr.Value);
                if (blob.Length < 3 || blob.ReadUInt16() != 1)
                    continue;
                var value = blob.ReadSerializedString();
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (type == "AssemblyTitleAttribute")
                    title = value.Trim();
                else
                    product = value.Trim();
            }
            return title ?? product;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or BadImageFormatException
                        or UnauthorizedAccessException
                        or InvalidOperationException
            )
        {
            return null;
        }
    }

    /// <summary>ASI name from the file properties (product or description), if meaningful.</summary>
    public static string? AsiTitle(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var v = FileVersionInfo.GetVersionInfo(path);
            var name = new[] { v.ProductName, v.FileDescription }
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s.Trim().Length > 2)
                ?.Trim();
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// mods\update\update.rpf is older than the original update\update.rpf, meaning the game was updated after the copy
    /// was made (typical for a restored backup): the game may crash on load with such a copy.
    /// </summary>
    public static string? StaleUpdateRpf(GameInstall game)
    {
        var mods = Path.Combine(game.Root, @"mods\update\update.rpf");
        var orig = Path.Combine(game.Root, @"update\update.rpf");
        if (!File.Exists(mods) || !File.Exists(orig))
            return null;
        var m = File.GetLastWriteTimeUtc(mods);
        var o = File.GetLastWriteTimeUtc(orig);
        return m < o.AddMinutes(-5)
            ? L.T("core.inventory.modsUpdateUpdateRpfOlder", m.ToLocalTime(), o.ToLocalTime())
            : null;
    }
}

/// <summary>SHA-256 cache keyed by size and modification time: the full hash is computed once, then only for changed files.</summary>
public sealed class HashCache(string path)
{
    private sealed record Entry(long Size, long Ticks, string Hash);

    private readonly Dictionary<string, Entry> _map = Load(path);
    private bool _dirty;

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public string Hash(string full)
    {
        var info = new FileInfo(full);
        var key = full.ToUpperInvariant();
        if (_map.TryGetValue(key, out var e) && e.Size == info.Length && e.Ticks == info.LastWriteTimeUtc.Ticks)
            return e.Hash;
        var hash = FileTransactionEngine.Hash(full);
        _map[key] = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, hash);
        _dirty = true;
        return hash;
    }

    public void Save()
    {
        if (!_dirty)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_map));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Label memory: file hash → mod name, mod name → link. If the same backup is dropped into the game again,
/// the files are grouped into mods right away, the way the user labeled them last time.
/// </summary>
public sealed class ModLabels(string path)
{
    private sealed class Data
    {
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Urls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Data _data = Load(path);

    public static ModLabels Default() => new(AppPaths.LibraryFile("mod-labels.json"));

    private static Data Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new();
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) ?? new();
            return new Data
            {
                Files = new Dictionary<string, string>(d.Files, StringComparer.OrdinalIgnoreCase),
                Urls = new Dictionary<string, string>(d.Urls, StringComparer.OrdinalIgnoreCase),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public string? NameFor(string hash) => _data.Files.GetValueOrDefault(hash);

    public string? UrlFor(string name) => _data.Urls.GetValueOrDefault(name);

    /// <summary>Labels files with a mod name (and a link, if available).</summary>
    public void Assign(IEnumerable<ModFileItem> files, string name, string? url = null)
    {
        name = name.Trim();
        if (name.Length == 0)
            return;
        foreach (var f in files)
            _data.Files[f.Hash] = name;
        if (!string.IsNullOrWhiteSpace(url))
            _data.Urls[name] = url.Trim();
        Save();
    }

    /// <summary>Renames a mod: all its labeled files and its link.</summary>
    public void Rename(IEnumerable<ModFileItem> files, string oldName, string newName, string? url)
    {
        Assign(files, newName, url ?? UrlFor(oldName));
        if (!oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
        {
            _data.Urls.Remove(oldName);
            Save();
        }
    }

    public void SetUrl(string name, string url)
    {
        _data.Urls[name] = url.Trim();
        Save();
    }

    /// <summary>Imports labels from a mod list (from another PC or an earlier setup). Existing labels are not overwritten.</summary>
    public int Import(IEnumerable<(string Hash, string Name)> files, IEnumerable<(string Name, string Url)> urls)
    {
        int n = 0;
        foreach (var (hash, name) in files)
            if (
                !string.IsNullOrWhiteSpace(hash)
                && !string.IsNullOrWhiteSpace(name)
                && _data.Files.TryAdd(hash, name.Trim())
            )
                n++;
        foreach (var (name, url) in urls)
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                _data.Urls.TryAdd(name.Trim(), url.Trim());
        Save();
        return n;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    _data,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }
                )
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Mod name from a readme: the first meaningful line or a "Name:/Title:" line (including the Russian label).</summary>
public static class ReadmeTitle
{
    public static string? From(string text)
    {
        var lines = text.Replace("\r", "")
            .Split('\n')
            .Select(l => l.Trim().Trim('=', '-', '*', '#', ' ', '\t'))
            .Where(l => l.Length > 0)
            .Take(40)
            .ToList();
        foreach (var l in lines)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                l,
                "^(?:mod\\s*name|name|title|название|мод)\\s*[:\\-–]\\s*(?<v>.{2,80})$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            if (m.Success)
                return m.Groups["v"].Value.Trim();
        }
        return lines.FirstOrDefault(l =>
            l.Length is >= 3 and <= 80
            && !l.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && l.Any(char.IsLetter)
        );
    }

    public static string? FromFile(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 1 << 20)
                return null;
            return From(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
