using System.Text.Json.Nodes;

namespace GTAModManager.Core;

/// <summary>
/// One-time move of data written by earlier versions into the program folder (<see cref="AppPaths"/>):
/// %LOCALAPPDATA%\GTAModManager (regular builds), %LOCALAPPDATA%\ModHub (preview cache),
/// ModHub-Data next to old portable builds, and leftovers in %TEMP%\ModHub.
/// Entries already present at the new place win; anything that cannot be moved now (locked by a running
/// copy) stays where it is and is retried on the next start.
/// </summary>
public static class AppDataMigration
{
    /// <summary>Old flat layout entry → new place relative to the program folder (null: obsolete, deleted).</summary>
    private static readonly Dictionary<string, string?> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["settings.json"] = "Settings.json",
        ["languages"] = "Languages",
        ["keys"] = @"Data\Game keys\Cache",
        ["magic.dat"] = @"Data\Game keys\magic.dat",
        ["webview"] = @"Data\Browser",
        ["managed-downloads"] = @"Data\Downloads",
        ["discover"] = @"Data\Cache\Mod pages",
        ["NaturalVisionPreviews"] = @"Data\Cache\NaturalVision previews",
        ["temp"] = null,
        ["GMMPedSelector.log"] = null,
        ["inventory-hashes.json"] = null,
        ["Backups"] = @"Data\Backups",
    };

    public const string NoMigrationVariable = "MODHUB_NO_MIGRATION";

    public sealed record Result(int Moved, int Failed, IReadOnlyList<string> Sources);

    /// <summary>Migrates from the default old locations. <paramref name="deleteSources"/> = false keeps the old copy (debug builds).</summary>
    public static Result Run(bool deleteSources = true)
    {
        // Set by scripts/check-portable-writes.ps1 so a test copy never takes over the user's real data.
        if (Environment.GetEnvironmentVariable(NoMigrationVariable) == "1")
            return new(0, 0, []);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var result = Run(
            AppPaths.Root,
            [
                Path.Combine(local, "GTAModManager"),
                Path.Combine(local, "ModHub"),
                Path.Combine(AppPaths.Root, "ModHub-Data"),
            ],
            deleteSources
        );
        if (deleteSources && LegacyTempLeftovers(Path.GetTempPath(), AppPaths.Root) is { } leftovers)
            TryDeleteTree(leftovers);
        return result;
    }

    /// <summary>
    /// %TEMP%\ModHub of older versions, or null when TEMP is already ModHub's own Data\Temp. gmm.exe started by the
    /// window inherits TEMP=Data\Temp, and Data\Temp\ModHub holds the job files the window just wrote for it.
    /// </summary>
    public static string? LegacyTempLeftovers(string tempPath, string root)
    {
        string temp = Path.GetFullPath(tempPath).TrimEnd('\\');
        string own = Path.GetFullPath(root).TrimEnd('\\');
        if (temp.Equals(own, StringComparison.OrdinalIgnoreCase) || temp.StartsWith(own + "\\", StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.Combine(temp, "ModHub");
    }

    public static Result Run(string root, IEnumerable<string> sources, bool deleteSources)
    {
        // A copying run (debug builds) leaves the sources in place; do it once, not on every start.
        var copied = Path.Combine(root, "Data", "copied-from-old-version.txt");
        if (!deleteSources && File.Exists(copied))
            return new(0, 0, []);
        int moved = 0,
            failed = 0;
        var used = new List<string>();
        foreach (var source in sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(source) || SamePath(source, root))
                continue;
            used.Add(source);
            (string From, string To)? backups = null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(source).ToList())
            {
                string name = Path.GetFileName(entry);
                string? target = Map.TryGetValue(name, out var mapped)
                    ? mapped
                    : Path.Combine("Data", "Library", name); // mod records: installed-mods.json, naturalvision, ...
                try
                {
                    if (target is null)
                    {
                        if (deleteSources)
                            DeletePath(entry);
                        continue;
                    }
                    var destination = Path.Combine(root, target);
                    if (Transfer(entry, destination, deleteSources))
                        moved++;
                    if (name.Equals("Backups", StringComparison.OrdinalIgnoreCase))
                        backups = (entry, destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                }
            }
            // After the loop: settings.json may be moved after the Backups folder.
            if (backups is { } backupMove)
                RepointBackupDir(Path.Combine(root, "Settings.json"), backupMove.From, backupMove.To);
            if (deleteSources)
                TryDeleteEmpty(source);
        }
        if (!deleteSources && used.Count > 0 && failed == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
            File.WriteAllText(copied, "Data was copied from an older ModHub version; the old folders were left in place.\n");
        }
        return new(moved, failed, used);
    }

    /// <summary>Moves (or copies) a file or folder, merging into an existing folder. Returns true when something was transferred.</summary>
    private static bool Transfer(string source, string destination, bool deleteSource)
    {
        if (File.Exists(source))
        {
            if (File.Exists(destination))
            {
                if (deleteSource)
                    File.Delete(source);
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (deleteSource)
                File.Move(source, destination);
            else
                File.Copy(source, destination);
            return true;
        }
        if (!Directory.Exists(source))
            return false;
        if (!Directory.Exists(destination) && deleteSource && SameVolume(source, destination))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
            return true;
        }
        bool any = false;
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source).ToList())
            any |= Transfer(entry, Path.Combine(destination, Path.GetFileName(entry)), deleteSource);
        if (deleteSource)
            TryDeleteEmpty(source);
        return any;
    }

    /// <summary>Old portable builds kept backups in ModHub-Data\Backups; the setting stores an absolute path.</summary>
    private static void RepointBackupDir(string settingsFile, string oldDir, string newDir)
    {
        if (!File.Exists(settingsFile) || JsonNode.Parse(File.ReadAllText(settingsFile)) is not JsonObject json)
            return;
        if (json["BackupDir"]?.GetValue<string>() is not { } current)
            return;
        if (!SamePath(current, oldDir))
            return;
        json["BackupDir"] = newDir;
        File.WriteAllText(settingsFile, json.ToJsonString(new() { WriteIndented = true }));
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        Path.GetFullPath(a).TrimEnd('\\').Equals(Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            DeletePath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
