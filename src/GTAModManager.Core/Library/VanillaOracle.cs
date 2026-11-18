using GTAModManager.Core.Game;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Library;

/// <summary>
/// Determines whether a file's previous content is a game original by comparing its bytes with the same file
/// in the original archive (update\update.rpf, dlcpacks, etc.), which the manager never modifies.
/// </summary>
public sealed class VanillaOracle(GameInstall game) : IDisposable
{
    private readonly Dictionary<string, Rpf7Archive?> _archives = new(StringComparer.OrdinalIgnoreCase);
    private Crypto.Gta5Keys? _keys;
    private bool _keysTried;

    /// <summary>Original path for a file in mods\ (mods\update\update.rpf → update\update.rpf), otherwise null.</summary>
    public static string? OriginalRel(string rel) => GameInstall.IsInMods(rel) ? rel["mods\\".Length..] : null;

    /// <summary>Whole file: whether the previous content matches the game original.</summary>
    public bool IsVanillaFile(string rel, string? oldHash)
    {
        if (oldHash is null || OriginalRel(rel) is not { } orig)
            return false;
        var full = Path.Combine(game.Root, orig);
        return File.Exists(full)
            && string.Equals(FileTransactionEngine.Hash(full), oldHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>File inside an archive: whether its previous bytes match the same file in the original archive.</summary>
    public bool IsVanillaEntry(string archiveRel, string innerPath, byte[] oldRaw)
    {
        try
        {
            var archive = Original(archiveRel);
            if (archive is null)
                return false;
            var parts = GameInstall.Normalize(innerPath).Split('\\');
            var nestedOpened = new List<Rpf7Archive>();
            try
            {
                var cur = archive;
                int start = 0;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (!parts[i].EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (cur.Find(string.Join('\\', parts[start..(i + 1)])) is not RpfBinaryFile nf)
                        return false;
                    cur = cur.OpenNested(nf);
                    nestedOpened.Add(cur);
                    start = i + 1;
                }
                if (cur.Find(string.Join('\\', parts[start..])) is not RpfFile f || f.StoredSize != oldRaw.LongLength)
                    return false;
                return cur.ReadRaw(f).AsSpan().SequenceEqual(oldRaw);
            }
            finally
            {
                foreach (var n in nestedOpened)
                    n.Dispose();
            }
        }
        catch (Exception ex) when (ex is RpfException or IOException)
        {
            return false;
        }
    }

    private Rpf7Archive? Original(string archiveRel)
    {
        if (_archives.TryGetValue(archiveRel, out var a))
            return a;
        a = null;
        if (OriginalRel(archiveRel) is { } orig && File.Exists(Path.Combine(game.Root, orig)))
        {
            var full = Path.Combine(game.Root, orig);
            try
            {
                if (!GameInstall.IsPlainRpf(full))
                {
                    if (!_keysTried)
                    {
                        _keys = game.TryLoadKeys();
                        _keysTried = true;
                    }
                    if (_keys is not null)
                        a = Rpf7Archive.OpenRead(full, _keys);
                }
                else
                    a = Rpf7Archive.OpenRead(full);
            }
            catch (Exception ex) when (ex is RpfException or IOException)
            {
                a = null;
            }
        }
        _archives[archiveRel] = a;
        return a;
    }

    public void Dispose()
    {
        foreach (var a in _archives.Values)
            a?.Dispose();
    }
}
