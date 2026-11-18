using GTAModManager.Core.Library;

namespace GTAModManager.Core.Tests;

/// <summary>Test helpers that damage or drop a single blob inside the library packs.</summary>
static class BackupPackHelpers
{
    static IEnumerable<BlobPack> Packs(BackupLibrary library) =>
        new[] { BackupLibrary.VanillaPackName, BackupLibrary.ModsPackName }
            .Select(n => new BlobPack(Path.Combine(library.Root, n)))
            .Where(p => p.Exists);

    public static void DropBlob(BackupLibrary library, string hash)
    {
        var pack = Packs(library).Single(p => p.Contains(hash));
        pack.Compact(pack.Hashes().Where(h => !h.Equals(hash, StringComparison.OrdinalIgnoreCase)).ToHashSet());
    }

    public static void CorruptBlob(BackupLibrary library, string hash)
    {
        var pack = Packs(library).Single(p => p.Contains(hash));
        byte[] bytes = File.ReadAllBytes(pack.PathName);
        byte[] key = Convert.FromHexString(hash);
        int at = bytes.AsSpan().IndexOf(key);
        Assert.True(at >= 0);
        bytes[at + key.Length] ^= 0xFF;
        File.WriteAllBytes(pack.PathName, bytes);
    }
}
