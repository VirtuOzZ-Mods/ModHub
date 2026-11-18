using System.Diagnostics;
using System.Text.Json;
using GTAModManager.Core.Game;

namespace GTAModManager.Core.Discover;

public enum CatalogInstallStatus
{
    NotInstalled,
    Installed,
    UpdateAvailable,
}

/// <summary>A mod's anchor file: game path and fingerprint (size + modification time, no hash: packs can be 4 GB).</summary>
public sealed record SignatureFile(string Rel, long Size, long WriteTicks);

/// <summary>What is known about an installed catalog mod in a specific game.</summary>
public sealed record InstalledCatalogMod
{
    public required string CatalogId { get; init; }
    public required string GameRoot { get; init; }

    /// <summary>Version from the mod page at install (or detection) time; null if unknown.</summary>
    public string? Version { get; init; }

    /// <summary>modhub: installed through Recommendations; detected: found in the game by reconciliation.</summary>
    public string Source { get; init; } = "modhub";
    public DateTimeOffset Since { get; init; }
    public List<SignatureFile> Files { get; init; } = [];
}

/// <summary>
/// Status of mods from Recommendations: Installed and Update available.
/// 1) Installing through Recommendations records the mod, the page version and the anchor files from the transaction.
/// 2) A mod installed some other way (manually, by another manager) is found by the catalog marker files;
///    its version is taken as the site version at detection time (no false Update prompts).
/// 3) An update means the site version differs from the recorded one while the anchor files are unchanged. If the files changed,
///    the user updated the mod themselves, so the new version is recorded. If the anchor files are gone, the mod was removed.
/// </summary>
public sealed class InstalledMods(string? path = null)
{
    private readonly string _path = path ?? AppPaths.LibraryFile("installed-mods.json");
    private static readonly object Gate = new();

    private static string Key(string gameRoot, string id) =>
        Path.TrimEndingDirectorySeparator(gameRoot).ToLowerInvariant() + "|" + id;

    private Dictionary<string, InstalledCatalogMod> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, InstalledCatalogMod>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, InstalledCatalogMod> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void ForgetGame(string gameRoot)
    {
        lock (Gate)
        {
            var all = Load();
            var keys = all.Where(p => p.Value.GameRoot.Equals(gameRoot, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Key)
                .ToArray();
            foreach (string key in keys)
                all.Remove(key);
            if (keys.Length > 0)
                Save(all);
        }
    }

    public InstalledCatalogMod? Get(string gameRoot, string catalogId)
    {
        lock (Gate)
            return Load().GetValueOrDefault(Key(gameRoot, catalogId));
    }

    /// <summary>All catalog mods recorded for one game.</summary>
    public IReadOnlyList<InstalledCatalogMod> ForGame(string gameRoot)
    {
        lock (Gate)
            return Load().Values.Where(m => m.GameRoot.Equals(gameRoot, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Catalog id of the recorded mod that owns any of these game files (the anchor files of a Recommendations
    /// install are the files of its transactions), or null.
    /// </summary>
    public static string? CatalogIdFor(IEnumerable<InstalledCatalogMod> installed, IEnumerable<string> files)
    {
        var set = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = installed
            .Where(m => m.Files.Any(f => set.Contains(f.Rel)))
            .Select(m => m.CatalogId)
            .Distinct()
            .ToList();
        return ids.Count == 1 ? ids[0] : null;
    }

    /// <summary>Install through Recommendations: anchor files come from the transactions of that install.</summary>
    public void Record(GameInstall game, string catalogId, string? version, IEnumerable<string> installedFiles)
    {
        var files = Signature(game, installedFiles);
        if (files.Count == 0)
            return;
        lock (Gate)
        {
            var all = Load();
            all[Key(game.Root, catalogId)] = new InstalledCatalogMod
            {
                CatalogId = catalogId,
                GameRoot = game.Root,
                Version = Clean(version),
                Source = "modhub",
                Since = DateTimeOffset.UtcNow,
                Files = files,
            };
            Save(all);
        }
    }

    /// <summary>Card status in this game. latestVersion is the site version (from the page cache) and may be null.</summary>
    public CatalogInstallStatus Resolve(GameInstall game, CatalogEntry entry, string? latestVersion)
    {
        lock (Gate)
        {
            var all = Load();
            var key = Key(game.Root, entry.Id);
            var latest = Clean(latestVersion);
            if (all.TryGetValue(key, out var known))
            {
                var present = known.Files.Where(f => File.Exists(Path.Combine(game.Root, f.Rel))).ToList();
                if (present.Count == 0)
                {
                    // No anchor files: the mod was removed or the install undone; reconciliation may still find it again.
                    all.Remove(key);
                    Save(all);
                    return Detect(game, entry, latest, all, key);
                }
                bool unchanged = known.Files.All(f => Fingerprint(game, f.Rel) is not { } now || now == f);
                if (known.Version is null && latest is not null)
                {
                    // The site version is only known now: it becomes the baseline for future updates.
                    all[key] = known with { Version = latest };
                    Save(all);
                    return CatalogInstallStatus.Installed;
                }
                if (latest is null || known.Version is null || Same(latest, known.Version))
                    return CatalogInstallStatus.Installed;
                if (unchanged)
                    return CatalogInstallStatus.UpdateAvailable;
                // The mod files changed after recording: the user updated it themselves, so the version now matches the site.
                all[key] = known with
                {
                    Version = latest,
                    Files = known.Files.Select(f => Fingerprint(game, f.Rel) ?? f).ToList(),
                };
                Save(all);
                return CatalogInstallStatus.Installed;
            }
            return Detect(game, entry, latest, all, key);
        }
    }

    private CatalogInstallStatus Detect(
        GameInstall game,
        CatalogEntry entry,
        string? latest,
        Dictionary<string, InstalledCatalogMod> all,
        string key
    )
    {
        var found = entry.Detect.Select(rule => Match(game, rule)).Where(r => r is not null).Cast<string>().ToList();
        if (found.Count == 0)
            return CatalogInstallStatus.NotInstalled;
        all[key] = new InstalledCatalogMod
        {
            CatalogId = entry.Id,
            GameRoot = game.Root,
            Version = latest,
            Source = "detected",
            Since = DateTimeOffset.UtcNow,
            Files = found.Select(f => Fingerprint(game, f)).Where(f => f is not null).Cast<SignatureFile>().ToList(),
        };
        Save(all);
        return CatalogInstallStatus.Installed;
    }

    /// <summary>
    /// Catalog rule: "Menyoo.asi", "scripts\X.dll", or with a condition on the file description:
    /// "ScriptHookVDotNet.asi|product~Enhanced" (ProductName contains it) or "…|product!~Enhanced" (does not). On a match, returns the file path.
    /// </summary>
    public static string? Match(GameInstall game, string rule)
    {
        var parts = rule.Split('|', 2);
        var rel = parts[0].Trim();
        var full = Path.Combine(game.Root, rel);
        if (!File.Exists(full))
            return null;
        if (parts.Length == 1)
            return rel;
        var condition = parts[1].Trim();
        bool negate = condition.StartsWith("product!~", StringComparison.OrdinalIgnoreCase);
        if (!negate && !condition.StartsWith("product~", StringComparison.OrdinalIgnoreCase))
            return null;
        var needle = condition[(negate ? 9 : 8)..];
        string product;
        try
        {
            product = FileVersionInfo.GetVersionInfo(full).ProductName ?? "";
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        return product.Contains(needle, StringComparison.OrdinalIgnoreCase) != negate ? rel : null;
    }

    /// <summary>Install anchor files: the mod's own files, not shared game archives (update.rpf, x64*.rpf copies, onigiri files).</summary>
    public static List<SignatureFile> Signature(GameInstall game, IEnumerable<string> installedFiles)
    {
        var result = new List<SignatureFile>();
        foreach (var rel in installedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            if (ext is not (".asi" or ".dll" or ".rpf" or ".cs" or ".vb" or ".lua" or ".exe"))
                continue;
            if (rel.StartsWith(@"onigiri\common\", StringComparison.OrdinalIgnoreCase))
                continue;
            // A copy of a game archive in mods\ (update.rpf, x64v.rpf, a game pack's dlc.rpf) is shared by all mods.
            if (GameInstall.IsInMods(rel) && File.Exists(Path.Combine(game.Root, rel[5..])))
                continue;
            if (Fingerprint(game, rel) is { } f)
                result.Add(f);
        }
        return result;
    }

    private static SignatureFile? Fingerprint(GameInstall game, string rel)
    {
        try
        {
            var info = new FileInfo(Path.Combine(game.Root, rel));
            return info.Exists ? new SignatureFile(rel, info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? Clean(string? version) => string.IsNullOrWhiteSpace(version) ? null : version.Trim();

    /// <summary>"v1.9.5" and "1.9.5" are the same version.</summary>
    public static bool Same(string a, string b) =>
        string.Equals(
            a.Trim().TrimStart('v', 'V').Trim(),
            b.Trim().TrimStart('v', 'V').Trim(),
            StringComparison.OrdinalIgnoreCase
        );
}
