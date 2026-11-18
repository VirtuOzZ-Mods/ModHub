using System.Collections.Concurrent;
using System.Text;

namespace GTAModManager.Core.Game;

/// <summary>
/// Renames the old hidden-looking service folder X:\.gmm\&lt;Edition&gt; to X:\ModHub\&lt;Edition&gt; on the game's volume.
/// The rename is instant (same volume). Journals store absolute paths of their staging copies, so the JSON files
/// in the moved folder are rewritten afterwards. A marker file makes the rewrite resumable: it is written before the
/// move, travels with the folder and is removed only when every file has been rewritten.
/// If the rename fails (files locked, no access), the old folder simply keeps being used and the move is retried later.
/// </summary>
public static class WorkDirMigration
{
    public const string FolderName = "ModHub";
    public const string LegacyFolderName = ".gmm";
    private const string PendingMarker = "rename-from-gmm-pending.txt";

    private static readonly ConcurrentDictionary<string, bool> Done = new(StringComparer.OrdinalIgnoreCase);

    public static string Current(string gameRoot, GameEdition edition) =>
        Path.Combine(Path.GetPathRoot(Path.GetFullPath(gameRoot))!, FolderName, edition.ToString());

    public static string Legacy(string gameRoot, GameEdition edition) =>
        Path.Combine(Path.GetPathRoot(Path.GetFullPath(gameRoot))!, LegacyFolderName, edition.ToString());

    /// <summary>The service folder to use now: the old one only while it exists and the new one does not.</summary>
    public static string Resolve(string gameRoot, GameEdition edition)
    {
        var current = Current(gameRoot, edition);
        var legacy = Legacy(gameRoot, edition);
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }

    /// <summary>Runs the migration once per process for this drive and edition.</summary>
    public static void EnsureMigrated(string gameRoot, GameEdition edition)
    {
        var current = Current(gameRoot, edition);
        if (Done.ContainsKey(current) || Environment.GetEnvironmentVariable(AppDataMigration.NoMigrationVariable) == "1")
            return;
        Migrate(gameRoot, edition);
        Done[current] = true;
    }

    /// <summary>Returns true when the service folder is at the new place with all paths updated.</summary>
    public static bool Migrate(string gameRoot, GameEdition edition) =>
        Migrate(Legacy(gameRoot, edition), Current(gameRoot, edition));

    /// <summary>Moves <paramref name="legacy"/> to <paramref name="current"/> (same volume) and updates stored paths.</summary>
    public static bool Migrate(string legacy, string current)
    {
        try
        {
            if (Directory.Exists(legacy) && !Directory.Exists(current))
            {
                File.WriteAllText(Path.Combine(legacy, PendingMarker), legacy);
                Directory.CreateDirectory(Path.GetDirectoryName(current)!);
                Directory.Move(legacy, current);
            }
            var marker = Path.Combine(current, PendingMarker);
            if (File.Exists(marker))
            {
                RewritePaths(current, legacy, current);
                File.Delete(marker);
            }
            var legacyParent = Path.GetDirectoryName(legacy)!;
            if (Directory.Exists(legacyParent) && !Directory.EnumerateFileSystemEntries(legacyParent).Any())
                Directory.Delete(legacyParent);
            return !Directory.Exists(legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// ModHub's own state files. Staging, prep, quarantine and disabled-mod folders also hold mod and game files
    /// (which may be JSON too); those are never edited.
    /// </summary>
    private static IEnumerable<string> StateFiles(string folder)
    {
        foreach (var dir in new[] { "journals", "keybinds" }.Select(d => Path.Combine(folder, d)).Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                yield return file;
        foreach (
            var (dir, name) in new[]
            {
                ("disabled-mods", "journal.json"),
                ("vanilla-restore", "restore.json"),
                ("online-mode", "online.json"),
            }
        )
        {
            var parent = Path.Combine(folder, dir);
            if (!Directory.Exists(parent))
                continue;
            foreach (var sub in Directory.EnumerateDirectories(parent))
                if (File.Exists(Path.Combine(sub, name)))
                    yield return Path.Combine(sub, name);
        }
    }

    /// <summary>Replaces the old folder path (as written in JSON, with escaped backslashes) in ModHub's state files.</summary>
    private static void RewritePaths(string folder, string oldPath, string newPath)
    {
        string oldJson = oldPath.Replace("\\", "\\\\") + "\\\\";
        string newJson = newPath.Replace("\\", "\\\\") + "\\\\";
        var utf8 = new UTF8Encoding(false);
        foreach (var file in StateFiles(folder).ToList())
        {
            string text = File.ReadAllText(file);
            if (text.IndexOf(oldJson, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            var temp = file + ".rename-tmp";
            File.WriteAllText(temp, text.Replace(oldJson, newJson, StringComparison.OrdinalIgnoreCase), utf8);
            File.Move(temp, file, overwrite: true);
        }
    }
}
