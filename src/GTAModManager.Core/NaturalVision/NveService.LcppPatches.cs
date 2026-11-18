using System.IO.Compression;
using GTAModManager.Core.Game;
using GTAModManager.Core.Oiv;

namespace GTAModManager.Core.NaturalVision;

public sealed partial class NveService
{
    const string WorldTravelPatches = "WorldTravelPatches.asi";

    // WorldTravelPatches (LCPP, December 2024) makes PopZone indices unsigned at three movsx sites. On Legacy 1.0.3889
    // the second site is followed by "EB 63" instead of "EB 5F", the scan returns no match and the ASI dereferences it
    // at startup (0xC0000005 in WorldTravelPatches.asi). A wildcard of the same length matches both builds; the
    // instruction it patches is unchanged.
    static readonly byte[] PopZoneSignature = "0F BE 54 88 ? EB 5F 41 B8 ? ? ? ? E8"u8.ToArray();
    static readonly byte[] PopZoneSignatureAnyJump = "0F BE 54 88 ? EB ?  41 B8 ? ? ? ? E8"u8.ToArray();

    /// <summary>Rewrites the bundled WorldTravelPatches.asi in a cached LCPP package. Returns whether it changed.</summary>
    internal static bool PatchWorldTravelPatches(string oivPath)
    {
        string? source;
        byte[] bytes;
        using (var package = OivPackage.Open(oivPath))
        {
            source = package
                .Ops.OfType<OivAdd>()
                .Where(add =>
                    GameInstall.Normalize(add.Target).Equals(WorldTravelPatches, StringComparison.OrdinalIgnoreCase)
                )
                .Select(add => add.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SingleOrDefault();
            if (source is null || package.SourceSize(source) is < 2 or > 32L << 20)
                return false;
            bytes = package.ReadSource(source);
        }
        if (bytes[0] != 'M' || bytes[1] != 'Z' || !ReplaceOnce(bytes, PopZoneSignature, PopZoneSignatureAnyJump))
            return false;
        using var zip = ZipFile.Open(oivPath, ZipArchiveMode.Update);
        string name = "content/" + source.Replace('\\', '/').TrimStart('/');
        var entry = zip.Entries.Single(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
        string fullName = entry.FullName;
        entry.Delete();
        using var output = zip.CreateEntry(fullName, CompressionLevel.Optimal).Open();
        output.Write(bytes);
        return true;
    }

    static bool ReplaceOnce(byte[] data, byte[] from, byte[] to)
    {
        int index = data.AsSpan().IndexOf(from);
        if (index < 0 || data.AsSpan(index + 1).IndexOf(from) >= 0)
            return false;
        to.CopyTo(data, index);
        return true;
    }

    // Runtime logs and empty folders are not part of any backup record, so a complete removal clears them explicitly.
    static readonly string[] LcppRuntimeLogs = ["WorldTravel.log", "WorldTravelPatches.log"];

    // Structural folders the game or the mods mode relies on stay even when empty.
    static readonly HashSet<string> GameFolders = new(
        [
            "mods",
            "update",
            "x64",
            @"mods\update",
            @"mods\x64",
            @"update\x64",
            @"mods\update\x64",
            @"update\x64\dlcpacks",
            @"mods\update\x64\dlcpacks",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    void CleanLcppLeftovers(NveState state)
    {
        foreach (string log in LcppRuntimeLogs)
        {
            string asi = Path.Combine(game.Root, Path.ChangeExtension(log, ".asi"));
            string file = Path.Combine(game.Root, log);
            if (!File.Exists(asi) && File.Exists(file))
                TryDelete(() => File.Delete(file));
        }
        var folders = state
            .Components.Where(c => c.TransactionId is not null)
            .Select(c => library.LoadRecord(game.Edition.ToString(), c.TransactionId!))
            .Where(r => r is not null)
            .SelectMany(r => r!.Files.Select(f => f.Rel))
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d!.Length);
        foreach (string? relative in folders)
        {
            // Walk up to the game root; only folders that are now empty are removed.
            for (string? dir = relative; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                string full = Path.Combine(game.Root, dir);
                if (
                    GameFolders.Contains(dir)
                    || !Directory.Exists(full)
                    || Directory.EnumerateFileSystemEntries(full).Any()
                )
                    break;
                TryDelete(() => Directory.Delete(full));
            }
        }
    }

    static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
