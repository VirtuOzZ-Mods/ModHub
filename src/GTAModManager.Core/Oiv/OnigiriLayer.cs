using System.Text.Json;
using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Oiv;

/// <summary>One file onigiri reads in place of a piece of a mods\ archive.</summary>
/// <param name="Archive">The mods\ archive, e.g. mods\update\update.rpf.</param>
/// <param name="Unit">Path inside it: a single file or the first nested archive on the way (copied whole); "" for the whole archive.</param>
/// <param name="Target">Where onigiri reads it, e.g. onigiri\platform\data\carcols.ymt.</param>
internal sealed record LayerUnit(string Archive, string Unit, string Target);

internal sealed record OnigiriLayerFile(string Archive, string Unit, string Hash);

internal sealed class OnigiriLayerState
{
    public Dictionary<string, OnigiriLayerFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// NaturalVision Enhanced's onigiri loader never reads mods\ (RageOpenV turns itself off next to it, NVE warns about
/// OpenRPF). It mounts its own folder over the game devices instead: onigiri\common = common:/, onigiri\platform =
/// platform:/ (update.rpf\x64 and the x64*.rpf archives), onigiri\dlcpacks = dlcpacks:/, and reads OpenIV-style
/// archives with a plain table of contents. This layer keeps loose copies there of everything mods change in the game
/// archives, the way ModDrop V and the community guides do it by hand. mods\ stays the canonical copy (it is what the
/// game reads once NVE is removed); the copies are derived from it on every installation and undo and are listed in
/// <see cref="StatePath"/>. Files that onigiri already has from someone else (NVE's own weather, carcols…) are never
/// overwritten: NVE keeps its version and the installation says so.
/// Add-on packs, dlclist.xml and gameconfig.xml are handled by <see cref="ModCompatibility"/>.
/// </summary>
internal static partial class OnigiriLayer
{
    internal const string StatePath = @"ModHubState\onigiri-layer.json";
    private const string UpdatePrefix = @"mods\update\";

    [GeneratedRegex(@"^mods\\x64[a-z]\.rpf$", RegexOptions.IgnoreCase)]
    private static partial Regex PlatformArchive();

    [GeneratedRegex(@"^mods\\update\\x64\\dlcpacks\\([^\\]+)\\dlc\.rpf$", RegexOptions.IgnoreCase)]
    private static partial Regex PackArchive();

    private static bool IsUpdateArchive(string archive) =>
        archive.Equals(UpdatePrefix + "update.rpf", StringComparison.OrdinalIgnoreCase)
        || archive.Equals(UpdatePrefix + "update2.rpf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where onigiri reads this piece of a mods\ archive. Null with <c>supported</c> = true: someone else handles it
    /// (shared documents, add-on packs) or it is not a mods\ archive; null with false: onigiri cannot show it.
    /// </summary>
    internal static LayerUnit? Classify(GameInstall game, string archive, string inner, out bool supported)
    {
        supported = true;
        if (!GameInstall.IsInMods(archive))
            return null;
        if (PackArchive().Match(archive) is { Success: true } pack)
        {
            // Add-on packs are mirrored with their dlclist registration (ModCompatibility); a game pack is replaced whole.
            var name = pack.Groups[1].Value;
            return Directory.Exists(Path.Combine(game.Root, "update", "x64", "dlcpacks", name))
                ? new(archive, "", @"onigiri\dlcpacks\" + name + @"\dlc.rpf")
                : null;
        }
        if (inner.Length == 0)
        {
            supported = false;
            return null;
        }
        var parts = GameInstall.Normalize(inner).Split('\\');
        string device = "";
        int start = 0;
        if (IsUpdateArchive(archive))
        {
            var key = archive + "|" + string.Join('\\', parts);
            if (ModCompatibility.Documents.Contains(key, StringComparer.OrdinalIgnoreCase))
                return null;
            if (parts.Length < 2)
            {
                supported = false;
                return null;
            }
            device =
                parts[0].Equals("common", StringComparison.OrdinalIgnoreCase) ? "common"
                : parts[0].Equals("x64", StringComparison.OrdinalIgnoreCase) ? "platform"
                : "";
            start = 1;
        }
        else if (archive.Equals(@"mods\common.rpf", StringComparison.OrdinalIgnoreCase))
            (device, start) = ("common", 0);
        else if (PlatformArchive().IsMatch(archive))
            (device, start) = ("platform", 0);
        if (device.Length == 0)
        {
            supported = false;
            return null;
        }
        int end = parts.Length - 1;
        for (int i = start; i < parts.Length - 1; i++)
            if (parts[i].EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            {
                end = i;
                break;
            }
        return new(
            archive,
            string.Join('\\', parts[..(end + 1)]),
            @"onigiri\" + device + "\\" + string.Join('\\', parts[start..(end + 1)])
        );
    }

    /// <summary>mods\ archives the layer can take files from (for the full scan when onigiri appears).</summary>
    private static IEnumerable<string> LayerArchives(GameInstall game, IReadOnlyDictionary<string, string?> staged)
    {
        var mods = Path.Combine(game.Root, "mods");
        var found = new List<string>();
        if (Directory.Exists(mods))
        {
            found.AddRange(
                Directory
                    .EnumerateFiles(mods, "*.rpf", SearchOption.TopDirectoryOnly)
                    .Select(p => Path.GetRelativePath(game.Root, p))
            );
            foreach (var name in new[] { "update.rpf", "update2.rpf" })
                if (File.Exists(Path.Combine(mods, "update", name)))
                    found.Add(UpdatePrefix + name);
            var packs = Path.Combine(mods, "update", "x64", "dlcpacks");
            if (Directory.Exists(packs))
                found.AddRange(
                    Directory
                        .EnumerateDirectories(packs)
                        .Select(d => Path.Combine(d, "dlc.rpf"))
                        .Where(File.Exists)
                        .Select(p => Path.GetRelativePath(game.Root, p))
                );
        }
        return found
            .Concat(staged.Keys)
            .Where(a => a.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(a => staged.TryGetValue(a, out var s) ? s is not null : File.Exists(Path.Combine(game.Root, a)));
    }

    internal static OnigiriLayerState? Load(GameInstall game)
    {
        var path = Path.Combine(game.Root, StatePath);
        if (!File.Exists(path))
            return null;
        try
        {
            if (new FileInfo(path).Length > 16L << 20)
                throw new IOException(L.T("core.onigiri.layerStateCorrupted", StatePath));
            var state = JsonSerializer.Deserialize<OnigiriLayerState>(File.ReadAllText(path))
                ?? throw new IOException(L.T("core.onigiri.layerStateCorrupted", StatePath));
            state.Files = new(state.Files ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var (target, file) in state.Files)
                if (
                    file is null
                    || GameInstall.Normalize(target) != target
                    || !target.StartsWith(@"onigiri\", StringComparison.OrdinalIgnoreCase)
                    || !GameInstall.IsInMods(GameInstall.Normalize(file.Archive))
                    || file.Unit is null
                    || file.Hash is not { Length: 64 } hash
                    || hash.Any(c => !Uri.IsHexDigit(c))
                )
                    throw new IOException(L.T("core.onigiri.layerStateCorrupted", StatePath));
            return state;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new IOException(L.T("core.onigiri.layerStateCorrupted", StatePath), ex);
        }
    }

    /// <summary>
    /// Brings the onigiri copies in line with mods\ as it will be after <paramref name="changes"/>.
    /// </summary>
    /// <param name="active">onigiri stays installed after these changes; false removes every copy and the state.</param>
    /// <param name="touched">Archive entries these changes write (archive rel, inner path).</param>
    /// <param name="derived">Receives the paths this method adds to <paramref name="changes"/>.</param>
    /// <param name="notes">(target, message) for the plan: copies made and files onigiri cannot show.</param>
    internal static void Reconcile(
        GameInstall game,
        bool active,
        List<FileChange> changes,
        IEnumerable<(string Archive, string Inner)> touched,
        string prep,
        HashSet<string> derived,
        List<(string Target, string Message)> notes
    )
    {
        var staged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes.Where(c => GameInstall.IsInMods(c.RelativePath)))
            if (c.RelativePath.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                staged[c.RelativePath] = c.SourcePath;
        var state = Load(game);
        var units = new Dictionary<string, LayerUnit>(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, file) in state?.Files ?? [])
            units[target] = new(file.Archive, file.Unit, target);
        using var sources = new ArchiveCache(game, staged);
        if (active)
        {
            var entries = touched.ToList();
            // A whole archive replaced (not edited entry by entry) or onigiri appearing: compare it with the original.
            var scan = staged
                .Keys.Where(a => !entries.Any(e => e.Archive.Equals(a, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (state is null)
                scan.AddRange(LayerArchives(game, staged));
            foreach (var (archive, inner) in entries)
                Add(archive, inner);
            foreach (var archive in scan.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (PackArchive().IsMatch(archive))
                {
                    Add(archive, "");
                    continue;
                }
                if (sources.Open(archive) is not { } mod)
                    continue;
                var original = sources.Original(archive);
                if (original is null && sources.HasOriginal(archive))
                {
                    // No keys to read the game's original: copying everything would hide nothing but cost gigabytes.
                    notes.Add((archive, L.T("core.onigiri.layerCannotCompare")));
                    continue;
                }
                foreach (var file in mod.Files)
                    if (original is null || !Same(mod, file, original))
                        Add(archive, file.Path);
            }
        }

        void Add(string archive, string inner)
        {
            var unit = Classify(game, archive, inner, out bool supported);
            if (unit is not null)
                units[unit.Target] = unit;
            else if (!supported)
            {
                var label = inner.Length == 0 ? archive : archive + "\\" + inner;
                if (!notes.Any(n => n.Target == label))
                    notes.Add((label, L.T("core.onigiri.layerUnsupported")));
            }
        }

        var next = new OnigiriLayerState();
        foreach (var unit in units.Values.OrderBy(u => u.Target, StringComparer.OrdinalIgnoreCase))
        {
            // Its archive is not part of these changes: the copy is as current as it was.
            if (
                active
                && !staged.ContainsKey(unit.Archive)
                && state is not null
                && state.Files.TryGetValue(unit.Target, out var unchanged)
            )
            {
                next.Files[unit.Target] = unchanged;
                continue;
            }
            string? wanted = null;
            if (active && sources.Open(unit.Archive) is { } archive)
            {
                if (unit.Unit.Length == 0)
                {
                    wanted = Path.Combine(prep, Guid.NewGuid().ToString("N"));
                    File.Copy(sources.PathOf(unit.Archive)!, wanted);
                }
                else if (
                    archive.Find(unit.Unit) is RpfFile file
                    && (sources.Original(unit.Archive) is not { } original || !Same(archive, file, original))
                )
                {
                    wanted = Path.Combine(prep, Guid.NewGuid().ToString("N"));
                    try
                    {
                        using var output = File.Create(wanted);
                        archive.CopyAsFile(file, output);
                    }
                    catch (RpfException)
                    {
                        // An encrypted entry without the game keys: leave whatever onigiri has now.
                        File.Delete(wanted);
                        notes.Add((unit.Target, L.T("core.onigiri.layerCannotCompare")));
                        if (state?.Files.TryGetValue(unit.Target, out var kept) == true)
                            next.Files[unit.Target] = kept;
                        continue;
                    }
                }
            }
            var wantedHash = wanted is null ? null : FileTransactionEngine.Hash(wanted);
            var current = FileTransactionEngine.HashOrNull(Path.Combine(game.Root, unit.Target));
            bool ours =
                current is null
                || state is not null
                    && state.Files.TryGetValue(unit.Target, out var known)
                    && known.Hash.Equals(current, StringComparison.OrdinalIgnoreCase);
            if (!ours)
            {
                // NaturalVision's own file or one put there by hand: onigiri shows that one, never ours.
                if (wantedHash is not null && wantedHash != current)
                    notes.Add((unit.Target, L.T("core.onigiri.layerForeign")));
                if (wanted is not null)
                    File.Delete(wanted);
                continue;
            }
            if (wantedHash is not null)
                next.Files[unit.Target] = new(unit.Archive, unit.Unit, wantedHash);
            if (current == wantedHash)
            {
                if (wanted is not null)
                    File.Delete(wanted);
                continue;
            }
            changes.RemoveAll(c => c.RelativePath.Equals(unit.Target, StringComparison.OrdinalIgnoreCase));
            changes.Add(new(unit.Target, current, wanted, MoveSource: wanted is not null));
            derived.Add(unit.Target);
            notes.Add((unit.Target, L.T(wanted is null ? "core.onigiri.layerRemoved" : "core.onigiri.layerCopy")));
        }

        // The state exists exactly while onigiri is installed: its absence means "scan everything" next time.
        var statePath = Path.Combine(game.Root, StatePath);
        string? stateSource = null;
        if (active)
        {
            var json = JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true });
            if (File.Exists(statePath) && File.ReadAllText(statePath) == json)
                return;
            stateSource = Path.Combine(prep, Guid.NewGuid().ToString("N"));
            File.WriteAllText(stateSource, json);
        }
        else if (!File.Exists(statePath))
            return;
        changes.RemoveAll(c => c.RelativePath.Equals(StatePath, StringComparison.OrdinalIgnoreCase));
        changes.Add(new(StatePath, FileTransactionEngine.HashOrNull(statePath), stateSource, stateSource is not null));
        derived.Add(StatePath);
    }

    /// <summary>Same stored bytes as the original game archive: untouched entries are copied raw into mods\.</summary>
    private static bool Same(Rpf7Archive mod, RpfFile file, Rpf7Archive original)
    {
        if (
            original.Find(file.Path) is not RpfFile o
            || o.StoredSize != file.StoredSize
            || o.GetType() != file.GetType()
        )
            return false;
        return mod.HashRaw(file) == original.HashRaw(o);
    }

    /// <summary>mods\ archives as they will be after the transaction, and the game originals they were copied from.</summary>
    private sealed class ArchiveCache(GameInstall game, IReadOnlyDictionary<string, string?> staged) : IDisposable
    {
        private readonly Dictionary<string, Rpf7Archive?> _mods = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Rpf7Archive?> _originals = new(StringComparer.OrdinalIgnoreCase);
        private Crypto.Gta5Keys? _keys;
        private bool _keysLoaded;

        private Crypto.Gta5Keys? Keys
        {
            get
            {
                if (!_keysLoaded)
                    (_keys, _keysLoaded) = (game.TryLoadKeys(), true);
                return _keys;
            }
        }

        public string? PathOf(string archive) =>
            staged.TryGetValue(archive, out var source) ? source
            : File.Exists(Path.Combine(game.Root, archive)) ? Path.Combine(game.Root, archive)
            : null;

        public Rpf7Archive? Open(string archive) => Get(_mods, archive, PathOf(archive));

        public bool HasOriginal(string archive) => File.Exists(OriginalPath(archive));

        public Rpf7Archive? Original(string archive) =>
            Get(_originals, archive, HasOriginal(archive) ? OriginalPath(archive) : null);

        private string OriginalPath(string archive) => Path.Combine(game.Root, archive["mods\\".Length..]);

        private Rpf7Archive? Get(Dictionary<string, Rpf7Archive?> cache, string archive, string? path)
        {
            if (cache.TryGetValue(archive, out var opened))
                return opened;
            opened = null;
            if (path is not null)
                try
                {
                    // Keys also for plain archives: their entries may still be encrypted (copied raw from the game).
                    opened = Rpf7Archive.OpenRead(path, Keys);
                }
                catch (Exception ex) when (ex is RpfException or IOException)
                {
                    opened = null;
                }
            cache[archive] = opened;
            return opened;
        }

        public void Dispose()
        {
            foreach (var a in _mods.Values.Concat(_originals.Values))
                a?.Dispose();
        }
    }
}
