using System.Security.Cryptography;
using System.Text;
using GTAModManager.Core.Crypto;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.NaturalVision;

internal static class NveRpfFingerprint
{
    public static string FileHash(string path, Gta5Keys? keys)
    {
        using var archive = Rpf7Archive.OpenRead(path, keys);
        return Hash(archive, keys, 0);
    }

    static string Hash(Rpf7Archive archive, Gta5Keys? keys, int depth)
    {
        if (depth > 12)
            throw new InvalidDataException(L.T("core.nve.nveRpfStructureNestedToo"));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in archive.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            string value;
            if (file is RpfBinaryFile binary && file.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            {
                // NVE Legacy (NVE_3\x64\ss.rpf) contains an ".rpf" that is not RPF7: the game does not mind, so the fingerprint is taken over raw bytes.
                if (binary.CompressedSize == 0 && binary.IsEncrypted == 0)
                {
                    try
                    {
                        using var child = archive.OpenNested(binary);
                        value = "a:" + Hash(child, keys, depth + 1);
                    }
                    catch (RpfException)
                    {
                        value = "r:" + archive.HashRaw(binary);
                    }
                }
                else
                {
                    if (binary.UncompressedSize > 256L << 20)
                        throw new InvalidDataException(L.T("core.nve.compressedNestedNveArchiveToo"));
                    var content = archive.ReadContent(binary);
                    try
                    {
                        using var stream = new MemoryStream(content, false);
                        using var child = Rpf7Archive.Open(stream, keys: keys, name: file.Name);
                        value = "a:" + Hash(child, keys, depth + 1);
                    }
                    catch (RpfException)
                    {
                        value = "r:" + Convert.ToHexString(SHA256.HashData(content));
                    }
                }
            }
            else
            {
                if (file.StoredSize > 256L << 20 || file is RpfBinaryFile { UncompressedSize: > 268435456 })
                    throw new InvalidDataException(L.T("core.nve.individualNveFileTooLarge"));
                var data = file is RpfBinaryFile bf ? archive.ReadContent(bf) : archive.ReadRaw(file);
                value =
                    Convert.ToHexString(SHA256.HashData(data))
                    + (
                        file is RpfResourceFile resource
                            ? ":" + resource.SystemFlags + ":" + resource.GraphicsFlags
                            : ""
                    );
            }
            hash.AppendData(Encoding.UTF8.GetBytes(file.Path.ToUpperInvariant() + "\0" + value + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
