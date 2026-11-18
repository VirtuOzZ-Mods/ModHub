using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Game;

public enum ModModeState
{
    Modded,
    Disabled,
    Incomplete,
}

public sealed record ModModeReport(
    ModModeState State,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> Warnings,
    string StoragePath,
    bool Pending,
    bool WritesBlocked
);

/// <summary>Renames loading binaries in place. Supports restoring old moved-file snapshots.</summary>
public sealed class ModModeService
{
    public const string DisabledSuffix = ".gmm-off";
    private const string RenameMode = "LoaderRename";
    private const string LegacyMoveMode = "MoveFiles";
    private static readonly HashSet<string> LoadingFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "ScriptHookV.dll",
        "ScriptHookVDotNet.dll",
        "ScriptHookVDotNet2.dll",
        "ScriptHookVDotNet3.dll",
    };
    private static readonly HashSet<string> ModDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "mods",
        "scripts",
        "plugins",
        "menyooStuff",
        "reshade-shaders",
        "nve_car_volumetrics",
        "VStancer",
    };
    private static readonly HashSet<string> ModFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "ScriptHookV.dll",
        "ScriptHookVDotNet.dll",
        "ScriptHookVDotNet2.dll",
        "ScriptHookVDotNet3.dll",
        "MinHook.x64.dll",
        "OpenIV.asi.gmm-disabled",
        "OpenRPF.asi.gmm-disabled",
        "NVE.bin",
        "NVE.ini",
        "ReShade.ini",
        "ReShade.log",
        "ReShadePreset.ini",
        "ReshadeEffectShaderToggler.addon64",
        "ReshadeEffectShaderToggler.ini",
        "ScriptHookV.log",
        "ScriptHookVDotNet.log",
        "ScriptHookVDotNet.ini",
        "asiloader.log",
        "TrainerV.ini",
        "menyooConfig.ini",
    };
    private static readonly HashSet<string> Proxies = new(StringComparer.OrdinalIgnoreCase)
    {
        "dinput8.dll",
        "dsound.dll",
        "version.dll",
        "winmm.dll",
        "d3d11.dll",
        "d3d12.dll",
        "dxgi.dll",
        "xinput1_3.dll",
        "xinput1_4.dll",
        "xinput9_1_0.dll",
    };
    private readonly GameInstall _game;
    private readonly string _root,
        _storage,
        _journalPath;
    public Action<string> FaultPoint { get; init; } = _ => { };

    public ModModeService(GameInstall game, string? stateRoot = null)
    {
        _game = game;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.Root));
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_root.ToUpperInvariant())));
        var storageBase = Path.GetFullPath(stateRoot ?? Path.Combine(game.WorkDir, "disabled-mods"));
        _storage = Path.Combine(storageBase, id);
        if (
            Under(_storage, _root)
            || Under(_root, _storage)
            || !string.Equals(Path.GetPathRoot(_root), Path.GetPathRoot(_storage), StringComparison.OrdinalIgnoreCase)
        )
            throw new ArgumentException(L.T("core.game.disabledModStorageMustOutside"));
        _journalPath = Path.Combine(_storage, "journal.json");
    }

    public ModModeReport PrepareDisable() => Inspect();

    public ModModeReport Inspect()
    {
        using var gate = Acquire();
        return InspectCore();
    }

    private ModModeReport InspectCore()
    {
        ValidateRoots();
        var warnings = new List<string> { L.T("core.game.modLoadersScripthookDisabledMods") };
        var candidates = Discover(warnings);
        var journal = Load();
        bool missingStored =
            journal is { Target: ModModeState.Disabled, Completed: true }
            && journal.Entries.Any(e => Exists(Source(e.RelativePath)) || !Exists(Stored(e.RelativePath, journal)));
        var state =
            journal is null || journal.Target == ModModeState.Modded && journal.Completed ? ModModeState.Modded
            : journal.Completed && candidates.Count == 0 && !missingStored ? ModModeState.Disabled
            : ModModeState.Incomplete;
        if (journal is { Target: ModModeState.Disabled } && candidates.Count > 0)
            warnings.Add(L.T("core.game.newModsWereFoundGame"));
        var tracked = journal is { StorageMode: RenameMode, Target: ModModeState.Disabled }
            ? journal.Entries.Select(e => Stored(e.RelativePath, journal)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        foreach (var file in Directory.EnumerateFiles(_root, "*" + DisabledSuffix))
            if (!tracked.Contains(file))
                warnings.Add(L.T("core.game.fileDisabledOutsideCurrentRecord", Path.GetFileName(file)));
        return new(
            state,
            candidates,
            warnings,
            _storage,
            journal is { Completed: false },
            state != ModModeState.Modded
        );
    }

    public ModModeReport Disable() => Change(ModModeState.Disabled);

    public ModModeReport Enable() => Change(ModModeState.Modded);

    private ModModeReport Change(ModModeState target)
    {
        using var gate = Acquire();
        if (_game.IsRunning())
            throw new InvalidOperationException(L.T("core.game.closeGtaVBeforeSwitching"));
        ValidateRoots();
        var journal = Load();
        bool fresh = journal is null || journal.Completed && journal.Target == ModModeState.Modded;
        if (journal is null || journal.Completed && journal.Target == ModModeState.Modded)
        {
            if (target == ModModeState.Modded)
                return InspectCore();
            journal = new Journal
            {
                Root = _root,
                Target = target,
                StorageMode = RenameMode,
                Entries = Discover([])
                    .Select(rel => new Entry { RelativePath = rel, Directory = Directory.Exists(Source(rel)) })
                    .ToList(),
            };
        }
        else if (target == ModModeState.Disabled && journal.StorageMode == LegacyMoveMode)
            throw new InvalidOperationException(L.T("core.game.firstEnableModsFromOld"));
        // Validate every operation before creating directories, writing a journal, or moving anything.
        var positions = new List<(Entry Entry, string From, string To, bool Move)>();
        foreach (var entry in journal.Entries)
        {
            var source = Source(entry.RelativePath);
            var stored = Stored(entry.RelativePath, journal);
            ValidateTreeIfExists(source);
            ValidateTreeIfExists(stored);
            bool atSource = Exists(source),
                atStorage = Exists(stored);
            if (atSource == atStorage)
                throw new IOException(L.T("core.game.missingModConflictingPaths", entry.RelativePath));
            var current = atSource ? source : stored;
            var hash = Fingerprint(current);
            if (fresh)
                entry.Hash = hash;
            if (Directory.Exists(current) != entry.Directory || hash != entry.Hash)
                throw new IOException(L.T("core.game.modContentsChangedFilesWere", entry.RelativePath));
            var from = target == ModModeState.Disabled ? source : stored;
            var to = target == ModModeState.Disabled ? stored : source;
            positions.Add((entry, from, to, Exists(from)));
        }
        if (target == ModModeState.Disabled && journal.Completed && journal.Target == target && Discover([]).Count > 0)
            throw new IOException(L.T("core.game.newFilesAppearedWhileMods"));
        Directory.CreateDirectory(_storage);
        journal.Target = target;
        journal.Completed = false;
        Save(journal);
        FaultPoint("journaled");
        for (int i = 0; i < positions.Count; i++)
        {
            var (entry, from, to, move) = positions[i];
            if (!move)
                continue; // A previous process renamed this entry before its final journal write.
            if (_game.IsRunning())
                throw new InvalidOperationException(L.T("core.game.gtaVStartedDuringOperation"));
            ValidateRoots();
            ValidateTreeIfExists(from);
            ValidateAncestors(to);
            if (Exists(to) || Fingerprint(from) != entry.Hash)
                throw new IOException(L.T("core.game.fileModifiedByAnotherProgram", entry.RelativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (entry.Directory)
                Directory.Move(from, to);
            else
                File.Move(from, to);
            FaultPoint($"after-move:{i}");
        }
        journal.Completed = true;
        Save(journal);
        return InspectCore();
    }

    private List<string> Discover(List<string> warnings)
    {
        var result = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_root))
        {
            var name = Path.GetFileName(path);
            bool proxyMod = Proxies.Contains(name) && RecognizedProxy(path);
            bool known =
                LoadingFiles.Contains(name)
                || Path.GetExtension(name).Equals(".asi", StringComparison.OrdinalIgnoreCase)
                || proxyMod;
            if (known && (_game.WhyProtected(name) is null || proxyMod))
                result.Add(name);
            else if (Proxies.Contains(name))
                warnings.Add(L.T("core.game.unrecognizedDllLeftUnchangedCheck", name));
            else if (
                Path.GetExtension(name).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                && _game.WhyProtected(name) is null
                && !ModFiles.Contains(name)
            )
                warnings.Add(L.T("core.game.unknownDllLeftUnchanged", name));
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool RecognizedProxy(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        var text = string.Join(" ", info.FileDescription, info.ProductName, info.CompanyName);
        return text.Contains("ReShade", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ASI", StringComparison.OrdinalIgnoreCase)
                && text.Contains("loader", StringComparison.OrdinalIgnoreCase)
                && (
                    text.Contains("Alexander Blade", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Ultimate ASI", StringComparison.OrdinalIgnoreCase)
                );
    }

    private sealed class Journal
    {
        public string Root { get; set; } = "";
        public ModModeState Target { get; set; }
        public bool Completed { get; set; }
        public string StorageMode { get; set; } = LegacyMoveMode;
        public List<Entry> Entries { get; set; } = [];
    }

    private sealed class Entry
    {
        public string RelativePath { get; set; } = "";
        public string Hash { get; set; } = "";
        public bool Directory { get; set; }
    }

    private Journal? Load()
    {
        if (!File.Exists(_journalPath))
            return null;
        ValidateAncestors(_journalPath);
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(_journalPath))
            ?? throw new IOException(L.T("core.game.modSwitchingLogCorrupted"));
        if (
            !string.Equals(journal.Root, _root, StringComparison.OrdinalIgnoreCase)
            || journal.Target is not (ModModeState.Modded or ModModeState.Disabled)
            || journal.StorageMode is not (RenameMode or LegacyMoveMode)
        )
            throw new IOException(L.T("core.game.logBelongsAnotherInstallationContains"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Entries)
        {
            // Current format only stores whole top-level entries. Never trust paths from disk.
            if (
                string.IsNullOrWhiteSpace(entry.RelativePath)
                || entry.RelativePath is "." or ".."
                || entry.RelativePath.IndexOfAny(['/', '\\', ':']) >= 0
                || !seen.Add(entry.RelativePath)
                || entry.Hash.Length != 64
                || !entry.Hash.All(Uri.IsHexDigit)
            )
                throw new IOException(L.T("core.game.invalidPathHashLog"));
            if (_game.WhyProtected(entry.RelativePath) is not null && !Proxies.Contains(entry.RelativePath))
                throw new IOException(L.T("core.game.logReferencesProtectedGameFile"));
        }
        var asiNames = journal
            .Entries.Where(e => e.RelativePath.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))
            .Select(e => Path.GetFileNameWithoutExtension(e.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Entries)
        {
            var name = entry.RelativePath;
            bool proxy = Proxies.Contains(name);
            if (proxy && !(journal.Completed && journal.Target == ModModeState.Modded))
            {
                var disabled = Stored(name, journal);
                var path = Exists(disabled) ? disabled : Source(name);
                ValidateAncestors(path);
                if (!File.Exists(path) || !RecognizedProxy(path))
                    throw new IOException(L.T("core.game.logContainsUnrecognizedDllLoader"));
            }
            bool allowed =
                journal.StorageMode == RenameMode
                    ? !entry.Directory
                        && (
                            LoadingFiles.Contains(name)
                            || proxy
                            || Path.GetExtension(name).Equals(".asi", StringComparison.OrdinalIgnoreCase)
                        )
                : entry.Directory ? ModDirectories.Contains(name)
                : ModFiles.Contains(name)
                    || proxy
                    || Path.GetExtension(name).Equals(".asi", StringComparison.OrdinalIgnoreCase)
                    || (
                        new[] { ".ini", ".log", ".xml", ".json" }.Contains(
                            Path.GetExtension(name),
                            StringComparer.OrdinalIgnoreCase
                        ) && asiNames.Contains(Path.GetFileNameWithoutExtension(name))
                    );
            if (!allowed)
                throw new IOException(L.T("core.game.logContainsUnknownModPath"));
        }
        return journal;
    }

    private void Save(Journal journal)
    {
        var temporary = _journalPath + ".tmp";
        ValidateAncestors(temporary);
        using (
            var file = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough
            )
        )
        {
            JsonSerializer.Serialize(file, journal);
            file.Flush(true);
        }
        File.Move(temporary, _journalPath, true);
    }

    private string Source(string rel) => Path.Combine(_root, rel);

    private string Stored(string rel, Journal journal) =>
        journal.StorageMode == RenameMode ? Source(rel) + DisabledSuffix : Path.Combine(_storage, "files", rel);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool Under(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void ValidateRoots()
    {
        ValidateAncestors(_root);
        ValidateAncestors(_storage);
        ValidateAncestors(_journalPath);
    }

    private static void ValidateAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("core.game.linksReparsePointsNotSupported", current));
    }

    private static void ValidateTreeIfExists(string path)
    {
        ValidateAncestors(path);
        if (!Directory.Exists(path))
            return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("core.game.linkFoundModDirectory", entry));
            if (Directory.Exists(entry))
                ValidateTreeIfExists(entry);
        }
    }

    private static string Fingerprint(string path)
    {
        ValidateTreeIfExists(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Text(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text + "\0"));
        void Visit(string current, string relative)
        {
            bool directory = Directory.Exists(current);
            Text((directory ? "D:" : "F:") + relative);
            if (directory)
                foreach (var child in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
                    Visit(child, Path.GetRelativePath(path, child));
            else
            {
                using var file = new FileStream(
                    current,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.SequentialScan
                );
                Text(file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = file.Read(buffer)) > 0)
                    hash.AppendData(buffer.AsSpan(0, count));
            }
        }
        Visit(path, "");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private IDisposable Acquire() => GameOperationGate.Acquire(_game);
}
