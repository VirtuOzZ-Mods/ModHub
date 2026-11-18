using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.NaturalVision;

public sealed class NveInstalledComponent
{
    public required NveComponent Component { get; set; }
    public string? TransactionId { get; set; }
    public bool Active { get; set; }
    public string? CacheFile { get; set; }
    public string? CacheHash { get; set; }
    public Dictionary<string, string> LogicalRpf { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DlcBefore { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DlcAfter { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class NveState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Product { get; set; } = "nve";
    public bool IsLcpp => Product == "lcpp";
    public bool IsLcppHd =>
        IsLcpp && Components.Any(c => c.Component.Core && NveBundle.IsLcppHdPart(c.Component.Source));
    public string ProductTitle =>
        IsLcpp
            ? IsLcppHd
                ? NveBundle.LcppHdTitle
                : NveBundle.LcppTitle
            : Edition == "Enhanced"
                ? "NaturalVision Enhanced"
                : "NaturalVision Evolved";
    public Dictionary<string, string?> FinalFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PreservedAdjusters { get; set; } = [];
    public string? PreservedAdjusterHash { get; set; }
    public string GameRoot { get; set; } = "";
    public string Edition { get; set; } = "";
    public string Source { get; set; } = "";
    public string BackupRoot { get; set; } = "";

    /// <summary>Readable cache folder under &lt;backups&gt;\NaturalVision\ ("Legacy - NaturalVision Evolved").
    /// Null for installs made before readable names: those keep the old hashed path until migrated.</summary>
    public string? CacheFolder { get; set; }
    public string Phase { get; set; } = "Installing";
    public string Message { get; set; } = "";
    public string? PendingComponent { get; set; }
    public DateTime Updated { get; set; } = DateTime.UtcNow;
    public List<NveInstalledComponent> Components { get; set; } = [];
}

public sealed class NveStore
{
    readonly GameInstall game;
    public string Root { get; }

    public static string Scope(string root) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant()))
        );

    public NveStore(GameInstall game, string? directory = null)
    {
        this.game = game;
        Root = Path.Combine(directory ?? AppPaths.LibraryFile("naturalvision"), Scope(game.Root));
    }

    static string Id(string id) =>
        Guid.TryParseExact(id, "N", out _) ? id : throw new ArgumentException(L.T("core.nve.invalidNveEntry"));

    string FileFor(string id) => Path.Combine(Root, Id(id) + ".json");

    public NveState Load(string id)
    {
        string file = FileFor(id);
        if (new FileInfo(file).Length > 16L << 20)
            throw new InvalidDataException(L.T("core.nve.nveRecordTooLarge"));
        var state = Transactions.JsonSnapshot.Read<NveState>(file)
            ?? throw new InvalidDataException(L.T("core.nve.corruptNveRecord"));
        if (
            !state.GameRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase)
            || state.Edition != game.Edition.ToString()
            || state.Id != id
        )
            throw new InvalidDataException(L.T("core.nve.nveRecordBelongsAnotherGame"));
        return state;
    }

    public IReadOnlyList<NveState> All()
    {
        if (!Directory.Exists(Root))
            return [];
        return Directory
            .EnumerateFiles(Root, "*.json")
            .Select(f => Load(Path.GetFileNameWithoutExtension(f)))
            .OrderByDescending(s => s.Updated)
            .ToArray();
    }

    public IReadOnlyList<NveState> Active() =>
        All()
            .Where(s => s.Phase != "Removed" && (s.Components.Any(c => c.Active) || s.PendingComponent is not null))
            .ToArray();

    public static bool UsesLibrary(string root)
    {
        string directory = AppPaths.LibraryFile("naturalvision");
        if (!Directory.Exists(directory))
            return false;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            var state = Transactions.JsonSnapshot.Read<NveState>(file);
            if (state is null)
                throw new IOException(L.T("core.nve.corruptNveRecord"));
            if (
                state.Phase != "Removed"
                && (state.Components.Any(c => c.Active) || state.PendingComponent is not null)
                && !string.IsNullOrWhiteSpace(state.BackupRoot)
                && Path.GetFullPath(state.BackupRoot).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
            )
                return true;
        }
        return false;
    }

    public void Save(NveState state)
    {
        if (!state.GameRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("core.nve.incorrectNveTargetGame"));
        Directory.CreateDirectory(Root);
        state.Updated = DateTime.UtcNow;
        string file = FileFor(state.Id),
            temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, file, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public const string CacheRootName = "NaturalVision";
    internal const string LegacyCacheRootName = "naturalvision-sources";

    public static string CacheDirectory(NveState state) =>
        state.CacheFolder is { } folder
            ? Path.Combine(Path.GetFullPath(state.BackupRoot), CacheRootName, SafeName(folder))
            : LegacyCacheDirectory(state);

    internal static string LegacyCacheDirectory(NveState state) =>
        Path.Combine(Path.GetFullPath(state.BackupRoot), LegacyCacheRootName, Scope(state.GameRoot), Id(state.Id));

    /// <summary>File name for a cached component: its title, unique within the install ("New Parallax Roads.oiv").</summary>
    public static string CacheFileName(NveState state, NveInstalledComponent item, string extension)
    {
        string stem = SafeName(item.Component.Title);
        if (stem.Length == 0)
            stem = SafeName(item.Component.Id);
        string name = stem + extension;
        for (
            int n = 2;
            state.Components.Any(c => c != item && name.Equals(c.CacheFile, StringComparison.OrdinalIgnoreCase));
            n++
        )
            name = $"{stem} ({n}){extension}";
        return name;
    }

    /// <summary>
    /// Picks a readable cache folder for an install: "&lt;Edition&gt; - &lt;product&gt;", with " (2)" when another
    /// recorded install in the same backup folder already uses that name. Deterministic for a given set of states,
    /// so an interrupted migration picks the same folder again.
    /// </summary>
    public static string AllocateCacheFolder(NveState state)
    {
        string stem = SafeName(state.Edition + " - " + state.ProductTitle);
        var taken = AllStates()
            .Where(s =>
                s.Id != state.Id
                && s.CacheFolder is not null
                // A removed install has no cached files left: its name is free again.
                && s.Phase != "Removed"
                && !string.IsNullOrWhiteSpace(s.BackupRoot)
                && Path.GetFullPath(s.BackupRoot)
                    .Equals(Path.GetFullPath(state.BackupRoot), StringComparison.OrdinalIgnoreCase)
            )
            .Select(s => s.CacheFolder!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string name = stem;
        for (int n = 2; taken.Contains(name); n++)
            name = $"{stem} ({n})";
        return name;
    }

    static IEnumerable<(string File, NveState State)> StateFiles()
    {
        string directory = Path.Combine(GameInstall.AppDataDir, "naturalvision");
        if (!Directory.Exists(directory))
            yield break;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).ToArray())
            if (Transactions.JsonSnapshot.Read<NveState>(file) is { } state)
                yield return (file, state);
    }

    static IEnumerable<NveState> AllStates() => StateFiles().Select(s => s.State);

    static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length > 100 ? safe[..100].TrimEnd() : safe;
    }

    /// <summary>
    /// Moves caches of installs made before readable names (naturalvision-sources\&lt;hash&gt;\&lt;id&gt;\)
    /// into NaturalVision\&lt;Edition - product&gt;\, renaming component files after their titles.
    /// Files are moved first and the state is saved last; a rerun after a crash finds the files already moved.
    /// The caller holds the backup library gate.
    /// </summary>
    public static void MigrateCacheFolders(string backupRoot)
    {
        foreach (var (file, state) in StateFiles().ToArray())
        {
            if (
                state.CacheFolder is not null
                || string.IsNullOrWhiteSpace(state.BackupRoot)
                || !Guid.TryParseExact(state.Id, "N", out _)
                || !Path.GetFullPath(state.BackupRoot)
                    .Equals(Path.GetFullPath(backupRoot), StringComparison.OrdinalIgnoreCase)
            )
                continue;
            string legacy = LegacyCacheDirectory(state);
            if (state.Phase == "Removed" && !(Directory.Exists(legacy) && Directory.EnumerateFiles(legacy).Any()))
                continue;
            state.CacheFolder = AllocateCacheFolder(state);
            string target = CacheDirectory(state);
            void Move(string oldName, string newName)
            {
                string from = Path.Combine(legacy, oldName),
                    to = Path.Combine(target, newName);
                if (!File.Exists(from) || File.Exists(to))
                    return;
                Directory.CreateDirectory(target);
                File.Move(from, to);
            }
            foreach (var item in state.Components.Where(c => c.CacheFile is not null))
            {
                string old = item.CacheFile!;
                // Fixed names the code looks up directly stay as they are; generated ids become titles.
                string renamed =
                    old.StartsWith("option-", StringComparison.OrdinalIgnoreCase)
                    || old.StartsWith("core-", StringComparison.OrdinalIgnoreCase)
                    || old is "reshade.zip" or "enhanced-main.zip"
                        ? CacheFileName(state, item, Path.GetExtension(old))
                        : old;
                Move(old, renamed);
                item.CacheFile = renamed;
            }
            // Anything else in the old folder (adjuster snapshot and similar) keeps its name.
            if (Directory.Exists(legacy))
                foreach (string rest in Directory.EnumerateFiles(legacy).ToArray())
                    Move(Path.GetFileName(rest), Path.GetFileName(rest));
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, file, true);
            if (Directory.Exists(legacy) && !Directory.EnumerateFileSystemEntries(legacy).Any())
                Directory.Delete(legacy);
        }
    }

    public static string Cache(NveState state, string file)
    {
        if (
            file is "." or ".."
            || Path.GetFileName(file) != file
            || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        )
            throw new InvalidDataException(L.T("core.nve.invalidNveComponentPath"));
        string root = CacheDirectory(state);
        for (string? p = root; p is not null; p = Path.GetDirectoryName(p))
            if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("core.nve.nveCacheContainsFileSystem"));
        return Path.Combine(root, file);
    }
}
