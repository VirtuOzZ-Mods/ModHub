using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Game;

public sealed record VanillaItem(string Rel, bool Directory, long Bytes);

public sealed record VanillaResult(string Folder, int Moved, IReadOnlyList<string> Failed);

public sealed record VanillaPlan(IReadOnlyList<VanillaItem> Items, IReadOnlyList<string> Warnings)
{
    public long Bytes => Items.Sum(i => i.Bytes);
    public bool Empty => Items.Count == 0;
}

/// <summary>
/// Restore Vanilla: removes everything not shipped by Rockstar from the game folder: the mods folder, ASI/DLL/scripts and
/// other mod files in the root, mod (unencrypted) archives in update\ and x64\, and loaders disabled with Disable Mods.
/// Original game files are not touched: Mod Hub never overwrites them (replacements go to copies in mods\).
/// Everything removed is first moved to a service folder on the same drive (instant and reversible); it can be deleted right away.
/// </summary>
public static class VanillaRestore
{
    // Files and folders that Steam, Epic and Rockstar add on top of GameInstall.ProtectedRoot*.
    private static readonly HashSet<string> StoreFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam_api64.dll",
        "installscript.vdf",
        "EOSSDK-Win64-Shipping.dll",
        "GTAVLauncher.exe",
        "GTAVLanguageSelect.exe",
    };
    private static readonly HashSet<string> StoreDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Installers",
        "ReadMe",
        ".egstore",
    };
    private static readonly string[] GameDirs = ["update", "x64"];

    /// <summary>A game root item belongs to Rockstar, Steam or Epic (not a mod).</summary>
    public static bool IsGameRootEntry(GameInstall game, string name, bool directory) =>
        (directory ? StoreDirs.Contains(name) : StoreFiles.Contains(name)) || game.WhyProtected(name) is not null;

    public static VanillaPlan Inspect(GameInstall game)
    {
        var items = new List<VanillaItem>();
        var warnings = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(game.Root))
        {
            var name = Path.GetFileName(path);
            bool dir = Directory.Exists(path);
            if (dir ? StoreDirs.Contains(name) : StoreFiles.Contains(name))
                continue;
            if (game.WhyProtected(name) is not null)
                continue; // Rockstar files, archives and folders
            items.Add(new VanillaItem(name, dir, Size(path)));
        }
        // Inside game folders only an archive with an unencrypted table of contents counts as a mod (Rockstar originals are encrypted).
        foreach (var top in GameDirs.Select(d => Path.Combine(game.Root, d)).Where(Directory.Exists))
        foreach (var rpf in Directory.EnumerateFiles(top, "*.rpf", SearchOption.AllDirectories))
            if (GameInstall.IsPlainRpf(rpf))
                items.Add(new VanillaItem(Path.GetRelativePath(game.Root, rpf), false, new FileInfo(rpf).Length));
        var disabled = DisabledStorage(game);
        if (Directory.Exists(disabled))
            warnings.Add(L.T("core.game.recordsDisableModsButtonWill"));
        warnings.Add(L.T("core.game.ifModsEverEditedOriginal"));
        return new VanillaPlan(items.OrderBy(i => i.Rel, StringComparer.OrdinalIgnoreCase).ToList(), warnings);
    }

    public static string QuarantineRoot(GameInstall game) => Path.Combine(game.WorkDir, "vanilla-restore");

    /// <summary>Disable Mods storage for this installation (see ModModeService).</summary>
    internal static bool HasDisabledStorage(GameInstall game) => Directory.Exists(DisabledStorage(game));

    private static string DisabledStorage(GameInstall game)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.Root));
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        return Path.Combine(game.WorkDir, "disabled-mods", id);
    }

    private sealed class Journal
    {
        public string Root { get; set; } = "";
        public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
        public List<string> Moved { get; set; } = [];
    }

    /// <summary>
    /// Moves everything in the plan to a service folder on the same drive (D:\ModHub\Legacy\vanilla-restore\…). Returns its path.
    /// The list (restore.json) is updated after each move, so everything can be restored manually after a failure.
    /// An item locked by another program is skipped and reported in <see cref="VanillaResult.Failed"/>.
    /// </summary>
    public static VanillaResult Execute(
        GameInstall game,
        VanillaPlan plan,
        Library.BackupLibrary? library = null,
        string? stateDirectory = null
    )
    {
        using var gate = GameOperationGate.Acquire(game);
        if (game.IsRunning())
            throw new InvalidOperationException(L.T("core.game.closeGtaVBeforeRestoring"));
        if (plan.Empty && !HasDisabledStorage(game))
            return new VanillaResult(
                "",
                0,
                VanillaModCleanup.TryComplete(game, library, stateDirectory) ? [] : [L.T("vanilla.cleanup.incomplete")]
            );
        var target = Path.Combine(
            QuarantineRoot(game),
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "-" + Guid.NewGuid().ToString("N")[..8]
        );
        if (!string.Equals(Path.GetPathRoot(target), Path.GetPathRoot(game.Root), StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("core.game.workingFolderMustSameDrive"));
        var files = Path.Combine(target, "files");
        Directory.CreateDirectory(files);
        var journal = new Journal { Root = game.Root };
        var journalPath = Path.Combine(target, "restore.json");
        var failed = new List<string>();
        File.WriteAllText(journalPath, JsonSerializer.Serialize(journal));
        foreach (var item in plan.Items)
        {
            var rel = GameInstall.Normalize(item.Rel);
            if (game.WhyProtected(rel) is not null || StoreFiles.Contains(rel) || StoreDirs.Contains(rel))
                throw new InvalidOperationException(L.T("core.game.gameFileWillNotRemoved", rel));
            var from = Path.Combine(game.Root, rel);
            if (!File.Exists(from) && !Directory.Exists(from))
                continue;
            if (game.IsRunning())
                throw new InvalidOperationException(L.T("core.game.gtaVStartedDuringOperation2", target));
            var to = Path.Combine(files, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            try
            {
                if (Directory.Exists(from))
                    Directory.Move(from, to);
                else
                    File.Move(from, to);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(L.T("core.game.useByAnotherProgramClose", rel));
                continue;
            }
            journal.Moved.Add(rel);
            File.WriteAllText(journalPath, JsonSerializer.Serialize(journal));
            try
            {
                RemoveEmptyParents(Path.GetDirectoryName(from)!, game.Root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var disabled = DisabledStorage(game);
        if (Directory.Exists(disabled))
        {
            try
            {
                Directory.Move(disabled, Path.Combine(target, "disabled-mods"));
                journal.Moved.Add("(записи «Отключить моды»)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(L.T("core.game.disableModsRecords", ex.Message));
            }
        }
        File.WriteAllText(journalPath, JsonSerializer.Serialize(journal));
        if (failed.Count == 0 && !VanillaModCleanup.TryComplete(game, library, stateDirectory))
            failed.Add(L.T("vanilla.cleanup.incomplete"));
        if (failed.Count > 0)
            VanillaModCleanup.MarkIncomplete(game, string.Join("\n", failed), stateDirectory);
        return new VanillaResult(target, journal.Moved.Count, failed);
    }

    /// <summary>Permanently deletes what was moved (only the folder created by <see cref="Execute"/>).</summary>
    public static void DeleteQuarantine(GameInstall game, string folder)
    {
        var full = Path.GetFullPath(folder);
        var root = Path.GetFullPath(QuarantineRoot(game)) + Path.DirectorySeparatorChar;
        if (
            !full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(full, "restore.json"))
        )
            throw new InvalidOperationException(L.T("core.game.notVanillaRestorationFolder"));
        foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(full, true);
        try
        {
            if (!Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch (IOException) { }
    }

    private static void RemoveEmptyParents(string dir, string root)
    {
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (
            var d = Path.GetFullPath(dir);
            d.Length > stop.Length && d.StartsWith(stop, StringComparison.OrdinalIgnoreCase);
            d = Path.GetDirectoryName(d)!
        )
        {
            // An empty mod DLC pack folder (update\x64\dlcpacks\mycar) is removed; game folders never become empty.
            if (Directory.EnumerateFileSystemEntries(d).Any())
                break;
            if (Path.GetDirectoryName(d)!.Equals(stop, StringComparison.OrdinalIgnoreCase))
                break;
            Directory.Delete(d);
        }
    }

    private static long Size(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
