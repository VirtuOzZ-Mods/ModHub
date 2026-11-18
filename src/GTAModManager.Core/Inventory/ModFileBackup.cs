using GTAModManager.Core.Game;

namespace GTAModManager.Core.Inventory;

/// <summary>
/// Manual "Make a backup" of one mod: copies the mod's own files from the game folder into
/// &lt;backup folder&gt;\Mods Backup\&lt;mod name&gt;\, keeping their paths relative to the game root,
/// so the backup can be put back by copying the folder contents into the game.
/// Shared game archives (mods\update\update.rpf and other copies of original archives) are not copied:
/// they belong to the whole game, not to one mod.
/// </summary>
public static class ModFileBackup
{
    public const string FolderName = "Mods Backup";

    public sealed record Result(string Folder, int Copied, long Bytes, IReadOnlyList<string> Skipped);

    public static Result Save(GameInstall game, string backupRoot, string modName, IEnumerable<string> files)
    {
        string gameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.Root)) + Path.DirectorySeparatorChar;
        string parent = Path.Combine(Path.GetFullPath(backupRoot), FolderName);
        string target = Path.Combine(parent, SafeName(modName));
        var copy = new List<string>();
        var skipped = new List<string>();
        foreach (string raw in files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            string rel;
            try
            {
                rel = GameInstall.Normalize(raw);
            }
            catch (ArgumentException)
            {
                continue;
            }
            string full = Path.GetFullPath(Path.Combine(gameRoot, rel));
            if (
                !full.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("ModHubState\\", StringComparison.OrdinalIgnoreCase)
            )
                continue;
            if (IsSharedGameArchive(gameRoot, rel) || !File.Exists(full))
            {
                skipped.Add(rel);
                continue;
            }
            copy.Add(rel);
        }

        // Build the new copy next to the old one, then swap: a failed copy never destroys the previous backup.
        Directory.CreateDirectory(parent);
        string staging = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        long bytes = 0;
        try
        {
            foreach (string rel in copy)
            {
                string destination = Path.Combine(staging, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(gameRoot, rel), destination, overwrite: true);
                bytes += new FileInfo(destination).Length;
            }
            if (copy.Count == 0)
                return new Result(target, 0, 0, skipped);
            if (Directory.Exists(target))
                Directory.Delete(target, true);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
        return new Result(target, copy.Count, bytes, skipped);
    }

    /// <summary>A file in mods\ that mirrors an original game archive (update.rpf, x64*.rpf…).</summary>
    static bool IsSharedGameArchive(string gameRoot, string rel) =>
        rel.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase)
        && File.Exists(Path.Combine(gameRoot, rel["mods\\".Length..]));

    static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "mod" : safe.Length > 120 ? safe[..120].TrimEnd() : safe;
    }
}
