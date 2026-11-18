using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.Peds;

/// <summary>
/// Brings characters back from a saved Ped Selector pack (pedselector\dlc.rpf, for example from an old backup):
/// its peds.rpf is unpacked into a folder with the usual add-on ped layout, which then goes through the ordinary
/// character queue. The pack's registrations are not taken over: what is known about each character is gone,
/// so the gender is chosen again for every one; normal or streamed is still recognised from the files.
/// </summary>
public static class PedSelectorImport
{
    public const string PedsArchive = "peds.rpf";

    /// <summary>The pack in a chosen folder: dlc.rpf itself, a pack folder with dlc.rpf, or a folder holding pedselector\dlc.rpf.</summary>
    public static string FindPack(string path)
    {
        if (File.Exists(path))
            return path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)
                ? path
                : throw new ArgumentException(L.T("core.peds.selectorImportNoPack", path));
        foreach (
            var candidate in new[]
            {
                Path.Combine(path, "dlc.rpf"),
                Path.Combine(path, PedInstaller.PackName, "dlc.rpf"),
                Path.Combine(path, "dlcpacks", PedInstaller.PackName, "dlc.rpf"),
            }
        )
            if (File.Exists(candidate))
                return candidate;
        throw new ArgumentException(L.T("core.peds.selectorImportNoPack", path));
    }

    /// <summary>
    /// Unpacks the characters of a Ped Selector pack into a new folder under <paramref name="targetRoot"/> and
    /// returns that folder (its name is "pedselector"), ready for <see cref="PedArchiveAnalyzer"/>.
    /// </summary>
    public static string Extract(GameInstall? game, string path, string targetRoot)
    {
        var pack = FindPack(path);
        using var archive = Rpf7Archive.OpenRead(pack, game?.TryLoadKeys());
        if (archive.Find(PedsArchive) is not RpfBinaryFile pedsFile)
            throw new ArgumentException(L.T("core.peds.selectorImportNoPeds", pack));
        if (pedsFile.IsEncrypted != 0 || pedsFile.CompressedSize != 0)
            throw new ArgumentException(L.T("core.peds.selectorImportEncrypted", pack));
        using var peds = archive.OpenNested(pedsFile);
        var files = peds.Files.ToList();
        if (files.Count == 0)
            throw new ArgumentException(L.T("core.peds.selectorImportNoPeds", pack));
        long size = files.Sum(f => f.StoredSize);
        Directory.CreateDirectory(targetRoot);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(targetRoot))!);
        if (drive.AvailableFreeSpace < size * 2 + (256L << 20))
            throw new IOException(L.T("core.peds.selectorImportNoSpace"));
        var folder = Path.Combine(
            targetRoot,
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            PedInstaller.PackName
        );
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        try
        {
            foreach (var file in files)
            {
                var target = Path.GetFullPath(Path.Combine(folder, GameInstall.Normalize(file.Path)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(L.T("core.common.invalidPath", file.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = File.Create(target);
                peds.CopyAsFile(file, output);
            }
        }
        catch
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(folder)!, true);
            }
            catch (IOException) { }
            throw;
        }
        return folder;
    }
}
