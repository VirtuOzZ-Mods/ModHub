using System.Text.Json.Nodes;

namespace GTAModManager.Core.Library;

/// <summary>
/// A copy of ModHub's own settings inside the backup folder ("Mod Hub settings"): Settings.json, the mod records
/// (Data\Library: installed mods, labels, choices, hotkey confirmations) and user translations.
/// It is refreshed whenever settings are saved and on exit, so a reinstalled ModHub that is pointed at the same
/// backup folder can pick everything up again. Hotkeys changed in mod configs live in the game folder and in the
/// service folder next to the game, which survive a reinstall on their own.
/// </summary>
public static class SettingsMirror
{
    public const string FolderName = "Mod Hub settings";
    private const string SettingsName = "Settings.json";
    private static readonly string[] Folders = ["Library", "Languages"];

    public static string For(string backupDir) => Path.Combine(backupDir, FolderName);

    public static bool Exists(string backupDir) => File.Exists(Path.Combine(For(backupDir), SettingsName));

    /// <summary>Replaces the copy in <paramref name="backupDir"/> with the current settings. Failures are swallowed by callers.</summary>
    public static void Save(string backupDir, string? root = null)
    {
        root ??= AppPaths.Root;
        if (!Directory.Exists(backupDir) || !File.Exists(Path.Combine(root, SettingsName)))
            return;
        string target = For(backupDir);
        string staging = target + ".new";
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        File.Copy(Path.Combine(root, SettingsName), Path.Combine(staging, SettingsName));
        CopyTree(Path.Combine(root, "Data", "Library"), Path.Combine(staging, "Library"));
        CopyTree(Path.Combine(root, "Languages"), Path.Combine(staging, "Languages"));
        File.WriteAllText(
            Path.Combine(staging, "README.txt"),
            "Mod Hub settings copy. Choose this backup folder again after reinstalling Mod Hub to restore them.\n"
        );
        string old = target + ".old";
        if (Directory.Exists(old))
            Directory.Delete(old, true);
        if (Directory.Exists(target))
            Directory.Move(target, old);
        Directory.Move(staging, target);
        if (Directory.Exists(old))
            Directory.Delete(old, true);
    }

    /// <summary>
    /// Copies the saved settings into the program folder, replacing current ones. The backup folder setting is kept
    /// as <paramref name="backupDir"/> (the copy may come from a different drive letter or path).
    /// </summary>
    public static void Restore(string backupDir, string? root = null)
    {
        root ??= AppPaths.Root;
        string source = For(backupDir);
        if (!Exists(backupDir))
            throw new FileNotFoundException(null, Path.Combine(source, SettingsName));
        foreach (var folder in Folders)
        {
            string from = Path.Combine(source, folder);
            string to = folder == "Library" ? Path.Combine(root, "Data", "Library") : Path.Combine(root, folder);
            if (Directory.Exists(from))
                CopyTree(from, to);
        }
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(source, SettingsName))) as JsonObject ?? [];
        json["BackupDir"] = Path.GetFullPath(backupDir);
        json["BackupsDeclined"] = false;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, SettingsName), json.ToJsonString(new() { WriteIndented = true }));
    }

    private static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from))
            return;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
