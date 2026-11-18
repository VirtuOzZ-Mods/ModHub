using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Keybinds;

public sealed class KeybindScanResult
{
    public List<KeybindEntry> Entries { get; } = [];
    public List<ModRef> Mods { get; } = [];

    /// <summary>Mods' own master switches for all of their hotkeys (see <see cref="KeybindSwitches"/>).</summary>
    public List<KeybindSwitch> Switches { get; } = [];

    /// <summary>Gamepad buttons mods read from their INI files (GTA control indices).</summary>
    public List<GamepadEntry> Pads { get; } = [];
    public List<string> Warnings { get; } = [];
    public int FilesScanned { get; set; }
    public int FilesFromCache { get; set; }
}

/// <summary>
/// Scans mod hotkeys of the selected game. Allowed locations only: the game root (*.ini, modhub.json),
/// scripts\ and plugins\ (up to 4 levels deep). Never reads mods\, RPF archives, game files or anything outside the game folder.
/// The cache and confirmations are stored in ModHub's service folder for this game version (WorkDir\keybinds).
/// </summary>
public sealed partial class KeybindScanner(GameInstall game, BackupLibrary? library = null)
{
    public const long MaxFileSize = 512 * 1024;

    /// <summary>Recognition rules version: when the rules change, the cache is considered stale.</summary>
    public const int RulesVersion = 4;
    private const int CacheVersion = RulesVersion;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Storage for this installation: game version + folder fingerprint (two Enhanced copies on one drive never mix).</summary>
    public string StoreDir =>
        Path.Combine(
            game.WorkDir,
            "keybinds",
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(game.Root.ToUpperInvariant()))
            )[..12]
        );
    private string CachePath => Path.Combine(StoreDir, "scan-cache.json");
    private string ConfirmPath => Path.Combine(StoreDir, "confirmations.json");

    private sealed class CacheFile
    {
        public int Version { get; set; } = CacheVersion;
        public Dictionary<string, CachedFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CachedFile
    {
        public long Size { get; set; }
        public long Ticks { get; set; }
        public List<KeybindEntry> Entries { get; set; } = [];
    }

    public Task<KeybindScanResult> ScanAsync(CancellationToken ct) => Task.Run(() => Scan(ct), ct);

    public KeybindScanResult Scan(CancellationToken ct = default)
    {
        var result = new KeybindScanResult();
        var cache = LoadCache();
        var fresh = new CacheFile();
        var confirmations = LoadConfirmations();
        var owners = OwnersFromLibrary();
        var installed = InstalledConfigFiles();
        foreach (var (rel, title) in installed)
            owners.TryAdd(rel, title);

        // Usual places, plus settings files Mod Hub itself installed anywhere in the game folder (OIV packages
        // may put a mod's .ini next to its files outside scripts\ or deeper than the usual scan).
        var files = DiscoverFiles(ct)
            .Concat(installed.Keys.Where(rel => File.Exists(Path.Combine(game.Root, rel))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var rel in files)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.Combine(game.Root, rel);
            FileInfo fi;
            try
            {
                fi = new FileInfo(full);
            }
            catch (IOException)
            {
                continue;
            }

            List<KeybindEntry> entries;
            if (cache.Files.TryGetValue(rel, out var c) && c.Size == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks)
            {
                entries = c.Entries;
                result.FilesFromCache++;
            }
            else
            {
                try
                {
                    entries = ParseFile(rel, ReadText(full));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Warnings.Add(L.T("core.common.couldNotRead", rel, ex.Message));
                    continue;
                }
                result.FilesScanned++;
            }
            fresh.Files[rel] = new CachedFile
            {
                Size = fi.Length,
                Ticks = fi.LastWriteTimeUtc.Ticks,
                Entries = entries,
            };

            var mod = ModFor(rel, owners);
            foreach (var e in entries)
                result.Entries.Add(ApplyConfirmation(e with { ModId = mod.Id, ModName = mod.Name }, confirmations));
            if (rel.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                try
                {
                    var text = ReadText(full);
                    result.Pads.AddRange(GamepadParser.Parse(text, rel, mod));
                    foreach (var s in KeybindSwitches.Find(rel, text))
                        result.Switches.Add(
                            new(mod.Id, mod.Name, rel, s.Section, s.Key, s.Line, s.Raw, s.On, L.T(s.NoteKey))
                        );
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        ApplyManifests(result, files, owners, ct);
        SaveCache(fresh, result);

        foreach (
            var m in result
                .Entries.GroupBy(e => e.ModId)
                .Select(g => new ModRef(g.Key, g.First().ModName))
                .OrderBy(m => m.Name)
        )
            result.Mods.Add(m);
        return result;
    }

    // ---------- Files ----------

    private IEnumerable<string> DiscoverFiles(CancellationToken ct)
    {
        if (!Directory.Exists(game.Root))
            yield break;
        foreach (var f in SafeFiles(game.Root, 0, 0, ct))
        {
            var name = Path.GetFileName(f);
            if (
                name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                || name.Equals("modhub.json", StringComparison.OrdinalIgnoreCase)
            )
                yield return Path.GetRelativePath(game.Root, f);
        }
        foreach (var dir in new[] { "scripts", "plugins" })
        {
            var d = Path.Combine(game.Root, dir);
            if (!Directory.Exists(d))
                continue;
            foreach (var f in SafeFiles(d, 0, 4, ct))
                if (Path.GetExtension(f).ToLowerInvariant() is ".ini" or ".json" or ".xml")
                    yield return Path.GetRelativePath(game.Root, f);
        }
    }

    private IEnumerable<string> SafeFiles(string dir, int depth, int maxDepth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string[] files,
            dirs;
        try
        {
            files = Directory.GetFiles(dir);
            dirs = depth < maxDepth ? Directory.GetDirectories(dir) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var f in files)
        {
            FileInfo fi;
            try
            {
                fi = new FileInfo(f);
            }
            catch (IOException)
            {
                continue;
            }
            if (fi.Length > MaxFileSize || fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            if (game.WhyProtected(Path.GetRelativePath(game.Root, f)) is not null)
                continue;
            yield return f;
        }
        foreach (var d in dirs)
        {
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            foreach (var f in SafeFiles(d, depth + 1, maxDepth, ct))
                yield return f;
        }
    }

    public static List<KeybindEntry> ParseFile(string rel, string text)
    {
        var placeholder = new ModRef("", "");
        return Path.GetExtension(rel).ToLowerInvariant() switch
        {
            ".ini" => IniKeybindParser.Parse(text, rel, placeholder),
            ".xml" => StructuredKeybindParsers.ParseXml(text, rel, placeholder),
            ".json" when !Path.GetFileName(rel).Equals("modhub.json", StringComparison.OrdinalIgnoreCase) =>
                StructuredKeybindParsers.ParseJson(text, rel, placeholder),
            _ => [],
        };
    }

    /// <summary>File text with encoding detection: BOM, then strict UTF-8, otherwise the system ANSI code page.</summary>
    public static string ReadText(string path) => DecodeText(File.ReadAllBytes(path)).text;

    public static (string text, Encoding encoding) DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode);
        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var ansi = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return (ansi.GetString(bytes), ansi);
        }
    }

    // ---------- Mod ownership ----------

    private Dictionary<string, string> OwnersFromLibrary()
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (library is null)
            return owners;
        try
        {
            // Hotkey changes are not mods: the settings file keeps the mod that installed it.
            foreach (
                var r in library
                    .Records(game.Edition.ToString())
                    .Where(r => !r.Undone && !Mods.ModNames.IsHotkeyEdit(r.Title))
            )
            foreach (var f in r.Files)
                owners[f.Rel] = TitleOnly(r.Title);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return owners;
    }

    /// <summary>
    /// Settings files (.ini / .json / .xml) that completed Mod Hub installations wrote as loose files, with the
    /// installation title: from the transaction journals, so it works without a backup folder too.
    /// The mods\ folder (archive copies) and game files are never included.
    /// </summary>
    private Dictionary<string, string> InstalledConfigFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (
                var j in game.CreateEngine()
                    .History()
                    .Where(j =>
                        j.State == Transactions.TxState.Completed
                        && j.TargetRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase)
                        && !Mods.ModNames.IsHotkeyEdit(j.Title ?? "")
                    )
            )
            foreach (var op in j.Ops.Where(o => o.PostHash is not null))
            {
                var rel = op.Rel.Replace('/', '\\');
                if (
                    Path.GetExtension(rel).ToLowerInvariant() is ".ini" or ".json" or ".xml"
                    && !rel.StartsWith(@"mods\", StringComparison.OrdinalIgnoreCase)
                    && game.WhyProtected(rel) is null
                )
                    files[rel] = TitleOnly(j.Title ?? Path.GetFileNameWithoutExtension(rel));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return files;
    }

    [GeneratedRegex(@"\s+\d+(\.\d+)+.*$")]
    private static partial Regex VersionTail();

    private static string TitleOnly(string title) => VersionTail().Replace(title, "").Trim();

    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trainerv"] = "TrainerV",
        ["reshade"] = "ReShade",
        ["reshade2"] = "ReShade",
        ["scripthookvdotnet"] = "ScriptHookVDotNet",
        ["menyooconfig"] = "Menyoo",
        ["openiv"] = "OpenIV.asi",
        ["rageopenv"] = "RageOpenV",
    };

    /// <summary>The file's mod: ModHub install record → folder in scripts\ → known name → file name.</summary>
    public ModRef ModFor(string rel, IReadOnlyDictionary<string, string> owners)
    {
        var dir = Path.GetDirectoryName(rel) ?? "";
        var stem = Path.GetFileNameWithoutExtension(rel);
        if (owners.TryGetValue(rel, out var owner))
            return Ref(owner);
        // A neighbouring .asi/.dll with the same name was installed through ModHub.
        foreach (var ext in new[] { ".asi", ".dll" })
            if (owners.TryGetValue(Path.Combine(dir, stem + ext), out owner))
                return Ref(owner);

        var parts = rel.Split('\\');
        if (parts.Length >= 3 && parts[0] is "scripts" or "plugins")
            return Ref(parts[1]);
        if (KnownNames.TryGetValue(stem, out var known))
            return Ref(known);
        return Ref(stem);

        // The id stays derived from the stored name (confirmations refer to it); the label follows the UI language.
        static ModRef Ref(string name) =>
            new(
                Regex.Replace(name.ToLowerInvariant(), @"[^\p{L}\p{N}]+", ""),
                Mods.ModNames.DisplayOperationTitle(name)
            );
    }

    // ---------- modhub.json ----------

    private void ApplyManifests(
        KeybindScanResult result,
        List<string> files,
        Dictionary<string, string> owners,
        CancellationToken ct
    )
    {
        foreach (
            var rel in files.Where(f => Path.GetFileName(f).Equals("modhub.json", StringComparison.OrdinalIgnoreCase))
        )
        {
            ct.ThrowIfCancellationRequested();
            ModHubManifest? m;
            try
            {
                m = ModHubManifest.TryParse(ReadText(Path.Combine(game.Root, rel)));
            }
            catch (IOException)
            {
                continue;
            }
            if (m is null)
            {
                result.Warnings.Add(L.T("core.keybinds.couldNotReadManifest", rel));
                continue;
            }

            var mod = new ModRef(
                Regex.Replace(m.Id.ToLowerInvariant(), @"[^\p{L}\p{N}]+", ""),
                m.Name.Length > 0 ? m.Name : m.Id
            );
            foreach (var kb in m.Keybinds)
            {
                string iniRel;
                try
                {
                    iniRel = Path.GetRelativePath(
                        game.Root,
                        Path.GetFullPath(Path.Combine(game.Root, Path.GetDirectoryName(rel) ?? "", kb.File))
                    );
                }
                catch (ArgumentException)
                {
                    continue;
                }
                if (
                    iniRel.StartsWith("..")
                    || Path.IsPathRooted(iniRel)
                    || !files.Contains(iniRel, StringComparer.OrdinalIgnoreCase)
                )
                {
                    result.Warnings.Add(L.T("core.keybinds.fileOutsideAllowedFoldersWas", rel, kb.File));
                    continue;
                }
                var id = $"{iniRel}|{kb.Section}|{kb.Key}" + (kb.ModifierKey is { Length: > 0 } mk ? "+" + mk : "");
                var found = result.Entries.FindIndex(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                KeyChord? def = kb.Default is { Length: > 0 } d ? KeyNotation.ParseCombination(d)?.Chord : null;
                if (found >= 0)
                {
                    var e = result.Entries[found];
                    result.Entries[found] = e with
                    {
                        ModId = mod.Id,
                        ModName = mod.Name,
                        Action = kb.Action.Length > 0 ? kb.Action : e.Action,
                        Context =
                            ModHubManifest.ParseContext(kb.Context) is var ctx && ctx != KeybindContext.Unknown
                                ? ctx
                                : e.Context,
                        Default = def ?? e.Default,
                        DefaultSource = def is null ? e.DefaultSource : "modhub.json",
                        Status =
                            e.Status == KeybindStatus.NeedsConfirmation
                            && e.StatusNote?.Contains("несколько раз") != true
                                ? KeybindStatus.Confirmed
                                : e.Status,
                        Editable = e.Format == KeybindFormat.Ini && e.StatusNote?.Contains("несколько раз") != true,
                        EditBlockReason = null,
                        FromManifest = true,
                    };
                }
                else
                    result.Warnings.Add(L.T("core.keybinds.parameterMissingEmpty", rel, kb.Section, kb.Key, kb.File));
            }
        }
    }

    // ---------- Confirmations ----------

    private Dictionary<string, string> LoadConfirmations()
    {
        try
        {
            return File.Exists(ConfirmPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ConfirmPath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static KeybindEntry ApplyConfirmation(KeybindEntry e, Dictionary<string, string> confirmations)
    {
        if (
            e.Status != KeybindStatus.NeedsConfirmation
            || !confirmations.TryGetValue(e.Id, out var chord)
            || chord != e.Chord.ToString()
        )
            return e;
        bool duplicate = e.StatusNote?.Contains("несколько раз") == true;
        return e with
        {
            Status = KeybindStatus.Confirmed,
            Editable = e.Format == KeybindFormat.Ini && !duplicate,
            EditBlockReason = duplicate ? e.EditBlockReason : null,
        };
    }

    /// <summary>The user confirmed an interpretation (for example "114 is F3"). The mod file is not changed.</summary>
    public void Confirm(KeybindEntry e)
    {
        var all = LoadConfirmations();
        all[e.Id] = e.Chord.ToString();
        Directory.CreateDirectory(StoreDir);
        File.WriteAllText(
            ConfirmPath,
            JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true })
        );
    }

    // ---------- Cache ----------

    private CacheFile LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
                return new();
            var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath), Json);
            return c is { Version: CacheVersion } ? c : new();
        }
        catch (Exception ex)
            when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new();
        }
    }

    private void SaveCache(CacheFile cache, KeybindScanResult result)
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache, Json));
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Warnings.Add(L.T("core.keybinds.scanCacheWasNotSaved", ex.Message));
        }
    }
}
