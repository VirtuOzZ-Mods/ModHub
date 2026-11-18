using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Crypto;

/// <summary>
/// RPF decryption keys found in the user's game exe by SHA-1 (as CodeWalker does).
/// Keys are not shipped with the program and never exported; they are cached locally by exe hash.
/// Only decryption is needed: archive copies in mods\ are written with an unencrypted table of contents (as OpenIV does).
/// </summary>
public sealed class Gta5Keys
{
    public required byte[] AesKey { get; init; } // 32
    public required byte[][] NgKeys { get; init; } // 101 × 272
    public required uint[][][] NgTables { get; init; } // 17 × 16 × 256
    public required byte[] HashLut { get; init; } // 256

    /// <summary>Audio bank (.awc) key (Rockstar's modified XXTEA), 4 words; empty if an older cache does not contain it.</summary>
    public uint[] AwcKey { get; init; } = [];

    private const int AesLen = 0x20,
        NgKeyLen = 0x110,
        TableLen = 0x400,
        LutLen = 0x100;
    private const int CacheVersion = 1;

    /// <summary>What the last search found (for diagnostics).</summary>
    public static string LastSearchSummary { get; private set; } = "";

    /// <summary>
    /// Keys for an exe: from the local cache; otherwise the AES key and LUT are located in the exe, and the NG keys and tables
    /// come from magic.dat (CodeWalker format) decrypted with that AES key.
    /// </summary>
    public static Gta5Keys Load(string exePath, string cacheDir, string magicPath)
    {
        // Fast path: the exe has not changed (same size and write time), so keys come from the cache without reading the 47+ MB exe.
        // This also helps when the exe is briefly locked by another process (Rockstar launcher, antivirus).
        var info = new FileInfo(exePath);
        if (!info.Exists)
            throw new FileNotFoundException(L.T("core.crypto.gameExeNotFound"), exePath);
        var index = Path.Combine(cacheDir, $"exe-{info.Length}-{info.LastWriteTimeUtc.Ticks}.txt");
        if (
            File.Exists(index)
            && File.ReadAllText(index).Trim() is { Length: 16 } known
            && File.Exists(Path.Combine(cacheDir, $"keys-{known}.bin"))
        )
        {
            try
            {
                return Deserialize(File.ReadAllBytes(Path.Combine(cacheDir, $"keys-{known}.bin")));
            }
            catch (InvalidDataException)
            {
                File.Delete(index);
            }
        }

        var exe = ReadShared(exePath);
        var exeHash = Convert.ToHexString(SHA256.HashData(exe))[..16];
        var cache = Path.Combine(cacheDir, $"keys-{exeHash}.bin");
        if (File.Exists(cache))
        {
            try
            {
                var cached = Deserialize(File.ReadAllBytes(cache));
                TryWriteIndex(index, exeHash);
                return cached;
            }
            catch (InvalidDataException)
            {
                File.Delete(cache);
            }
        }
        var (aes, lut) = SearchAesAndLut(exe);
        MagicDat.Ensure(magicPath); // file missing: download it from the official CodeWalker repository
        var keys = FromMagic(aes, File.ReadAllBytes(magicPath));
        if (!keys.HashLut.AsSpan().SequenceEqual(lut))
            throw new KeysNotFoundException(L.T("core.crypto.matchingKeySetLutMismatch"));
        Directory.CreateDirectory(cacheDir);
        File.WriteAllBytes(cache, keys.Serialize());
        TryWriteIndex(index, exeHash);
        return keys;
    }

    /// <summary>Reads the exe even if another process holds it open; retries on a temporary lock.</summary>
    private static byte[] ReadShared(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                var buffer = new byte[fs.Length];
                fs.ReadExactly(buffer);
                return buffer;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(400 * attempt);
            }
        }
    }

    private static void TryWriteIndex(string index, string exeHash)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(index)!);
            File.WriteAllText(index, exeHash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static (byte[] aes, byte[] lut) SearchAesAndLut(byte[] exe)
    {
        var found = new ConcurrentDictionary<int, byte[]>();
        var targets = new[]
        {
            (AesLen, Convert.ToHexString(Gta5KeyHashes.AesKey)),
            (LutLen, Convert.ToHexString(Gta5KeyHashes.HashLookupTable)),
        };
        Parallel.For(0, (exe.Length + 0xFFFFF) / 0x100000, (block, state) =>
        {
            Span<byte> h = stackalloc byte[20];
            long start = block * 0x100000L,
                end = Math.Min(start + 0x100000, exe.Length);
            for (long pos = start; pos < end && found.Count < 2; pos += 8)
                foreach (var (len, hash) in targets)
                {
                    if (pos + len > exe.Length || found.ContainsKey(len))
                        continue;
                    SHA1.HashData(exe.AsSpan((int)pos, len), h);
                    if (Convert.ToHexString(h) == hash)
                        found.TryAdd(len, exe.AsSpan((int)pos, len).ToArray());
                }
        });
        if (!found.TryGetValue(AesLen, out var aes))
            throw new KeysNotFoundException(L.T("core.crypto.aesKey"));
        if (!found.TryGetValue(LutLen, out var lut))
            throw new KeysNotFoundException(L.T("core.crypto.hashTable"));
        return (aes, lut);
    }

    /// <summary>
    /// Decodes magic.dat: subtracts 4 pseudo-random streams (System.Random seeded with Jenkins(AES key)),
    /// then AES-ECB and raw deflate; the result is 101 NG keys, 272 tables, the LUT and the AWC key (306,272 bytes).
    /// On .NET 6+ Random(seed) uses the same algorithm as .NET Framework.
    /// </summary>
    public static Gta5Keys FromMagic(byte[] aesKey, byte[] magic)
    {
        var plain = DecodeMagic(aesKey, magic);
        if (plain.Length < 306256)
            throw new KeysNotFoundException(L.T("core.crypto.completeDatasetMagicDat"));

        var ng = Enumerable.Range(0, 101).Select(i => plain.AsSpan(i * NgKeyLen, NgKeyLen).ToArray()).ToArray();
        var tables = new uint[17][][];
        int off = 101 * NgKeyLen;
        for (int i = 0; i < 17; i++)
        {
            tables[i] = new uint[16][];
            for (int j = 0; j < 16; j++)
            {
                tables[i][j] = new uint[256];
                for (int b = 0; b < 256; b++, off += 4)
                    tables[i][j][b] = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(off));
            }
        }
        var lut = plain.AsSpan(off, LutLen).ToArray();
        off += LutLen;
        return new Gta5Keys
        {
            AesKey = aesKey,
            NgKeys = ng,
            NgTables = tables,
            HashLut = lut,
            AwcKey = ReadAwcKey(plain, off),
        };
    }

    /// <summary>The .awc key from magic.dat (the last 16 bytes of the decrypted data), if present.</summary>
    public static uint[] AwcKeyFromMagic(byte[] aesKey, byte[] magic) => ReadAwcKey(DecodeMagic(aesKey, magic), 306256);

    private static uint[] ReadAwcKey(byte[] plain, int off) =>
        plain.Length >= off + 16
            ? Enumerable
                .Range(0, 4)
                .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(off + i * 4)))
                .ToArray()
            : [];

    private static byte[] DecodeMagic(byte[] aesKey, byte[] magic)
    {
        var rnd = new Random((int)JenkinsOneAtATime(aesKey));
        var streams = new byte[4][];
        for (int s = 0; s < 4; s++)
        {
            streams[s] = new byte[magic.Length];
            rnd.NextBytes(streams[s]);
        }
        var d = new byte[magic.Length];
        for (int i = 0; i < d.Length; i++)
            d[i] = (byte)((magic[i] - streams[0][i] - streams[1][i] - streams[2][i] - streams[3][i]) & 0xFF);

        var tmp = new Gta5Keys
        {
            AesKey = aesKey,
            NgKeys = [],
            NgTables = [],
            HashLut = [],
        };
        tmp.DecryptAes(d);
        byte[] plain;
        try
        {
            using var inflater = new System.IO.Compression.DeflateStream(
                new MemoryStream(d),
                System.IO.Compression.CompressionMode.Decompress
            );
            var ms = new MemoryStream();
            inflater.CopyTo(ms);
            plain = ms.ToArray();
        }
        catch (InvalidDataException)
        {
            throw new KeysNotFoundException(L.T("core.crypto.matchingMagicDatCouldNot"));
        }
        return plain;
    }

    public static uint JenkinsOneAtATime(ReadOnlySpan<byte> data)
    {
        uint h = 0;
        foreach (var b in data)
        {
            h += b;
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }

    public static Gta5Keys Search(byte[] exe)
    {
        var wanted = new Dictionary<int, Dictionary<string, int>>
        {
            [AesLen] = Index([Gta5KeyHashes.AesKey]),
            [NgKeyLen] = Index(Gta5KeyHashes.NgKeys),
            [TableLen] = Index(Gta5KeyHashes.NgDecryptTables),
            [LutLen] = Index([Gta5KeyHashes.HashLookupTable]),
        };
        var found = new ConcurrentDictionary<(int len, int idx), byte[]>();
        int lengths = wanted.Count;

        Parallel.For(0, (exe.Length + 0xFFFFF) / 0x100000, block =>
        {
            Span<byte> h = stackalloc byte[20];
            long start = block * 0x100000L,
                end = Math.Min(start + 0x100000, exe.Length);
            for (long pos = start; pos < end; pos += 8)
            {
                foreach (var (len, map) in wanted)
                {
                    if (pos + len > exe.Length)
                        continue;
                    SHA1.HashData(exe.AsSpan((int)pos, len), h);
                    if (map.TryGetValue(Convert.ToHexString(h), out int idx))
                        found.TryAdd((len, idx), exe.AsSpan((int)pos, len).ToArray());
                }
            }
        });

        LastSearchSummary = L.T(
            "core.crypto.aes1NgKeys101",
            found.Keys.Count(k => k.len == AesLen),
            found.Keys.Count(k => k.len == NgKeyLen),
            found.Keys.Count(k => k.len == TableLen),
            found.Keys.Count(k => k.len == LutLen)
        );

        byte[] Get(int len, int idx) =>
            found.TryGetValue((len, idx), out var v)
                ? v
                : throw new KeysNotFoundException(
                    len switch
                    {
                        AesLen => L.T("core.crypto.aesKey"),
                        NgKeyLen => L.T("core.crypto.ngKey", idx),
                        TableLen => L.T("core.crypto.ngTable", idx),
                        _ => L.T("core.crypto.hashTable"),
                    }
                );

        var tables = new uint[17][][];
        for (int i = 0; i < 17; i++)
        {
            tables[i] = new uint[16][];
            for (int j = 0; j < 16; j++)
            {
                var raw = Get(TableLen, j + 16 * i);
                var t = new uint[256];
                for (int b = 0; b < 256; b++)
                    t[b] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(b * 4));
                tables[i][j] = t;
            }
        }
        return new Gta5Keys
        {
            AesKey = Get(AesLen, 0),
            NgKeys = Enumerable.Range(0, 101).Select(i => Get(NgKeyLen, i)).ToArray(),
            NgTables = tables,
            HashLut = Get(LutLen, 0),
        };
    }

    private static Dictionary<string, int> Index(IList<byte[]> hashes)
    {
        var d = new Dictionary<string, int>();
        for (int i = 0; i < hashes.Count; i++)
            d[Convert.ToHexString(hashes[i])] = i;
        return d;
    }

    private byte[] Serialize()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(CacheVersion);
        w.Write(AesKey);
        foreach (var k in NgKeys)
            w.Write(k);
        foreach (var r in NgTables)
        foreach (var t in r)
        foreach (var v in t)
            w.Write(v);
        w.Write(HashLut);
        foreach (var v in AwcKey)
            w.Write(v); // optional tail: older caches without it can still be read
        return ms.ToArray();
    }

    private static Gta5Keys Deserialize(byte[] data)
    {
        var r = new BinaryReader(new MemoryStream(data));
        if (r.ReadInt32() != CacheVersion)
            throw new InvalidDataException();
        var aes = r.ReadBytes(AesLen);
        var ng = Enumerable.Range(0, 101).Select(_ => r.ReadBytes(NgKeyLen)).ToArray();
        var tables = new uint[17][][];
        for (int i = 0; i < 17; i++)
        {
            tables[i] = new uint[16][];
            for (int j = 0; j < 16; j++)
            {
                tables[i][j] = new uint[256];
                for (int b = 0; b < 256; b++)
                    tables[i][j][b] = r.ReadUInt32();
            }
        }
        var lut = r.ReadBytes(LutLen);
        if (lut.Length != LutLen)
            throw new InvalidDataException();
        uint[] awc =
            r.BaseStream.Length - r.BaseStream.Position >= 16
                ? Enumerable.Range(0, 4).Select(_ => r.ReadUInt32()).ToArray()
                : [];
        return new Gta5Keys
        {
            AesKey = aes,
            NgKeys = ng,
            NgTables = tables,
            HashLut = lut,
            AwcKey = awc,
        };
    }

    // ---------- Decryption ----------

    /// <summary>Name hash used to select the NG key (GTA5Hash in CodeWalker; case is preserved).</summary>
    public uint NameHash(string name)
    {
        uint result = 0;
        foreach (char c in name)
        {
            uint temp = 1025 * (HashLut[c & 0xFF] + result);
            result = (temp >> 6) ^ temp;
        }
        return 32769 * ((9 * result >> 11) ^ (9 * result));
    }

    public byte[] NgKeyFor(string name, uint length) => NgKeys[(int)((NameHash(name) + length + 61) % 101)];

    public void DecryptNg(Span<byte> data, string name, uint length) => DecryptNg(data, NgKeyFor(name, length));

    /// <summary>Independent 16-byte blocks; a tail shorter than a block is left unchanged.</summary>
    public void DecryptNg(Span<byte> data, byte[] key)
    {
        Span<uint> k = stackalloc uint[68];
        for (int i = 0; i < 68; i++)
            k[i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
        Span<byte> tmp = stackalloc byte[16];
        for (int off = 0; off + 16 <= data.Length; off += 16)
        {
            var block = data.Slice(off, 16);
            RoundA(block, tmp, NgTables[0], k[0..4]);
            RoundA(tmp, block, NgTables[1], k[4..8]);
            for (int r = 2; r <= 15; r++)
            {
                RoundB(block, tmp, NgTables[r], k[(4 * r)..(4 * r + 4)]);
                tmp.CopyTo(block);
            }
            RoundA(block, tmp, NgTables[16], k[64..68]);
            tmp.CopyTo(block);
        }
    }

    private static void RoundA(ReadOnlySpan<byte> d, Span<byte> o, uint[][] t, ReadOnlySpan<uint> k)
    {
        for (int w = 0; w < 4; w++)
        {
            uint x =
                t[4 * w][d[4 * w]]
                ^ t[4 * w + 1][d[4 * w + 1]]
                ^ t[4 * w + 2][d[4 * w + 2]]
                ^ t[4 * w + 3][d[4 * w + 3]]
                ^ k[w];
            BinaryPrimitives.WriteUInt32LittleEndian(o[(4 * w)..], x);
        }
    }

    private static void RoundB(ReadOnlySpan<byte> d, Span<byte> o, uint[][] t, ReadOnlySpan<uint> k)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(o, t[0][d[0]] ^ t[7][d[7]] ^ t[10][d[10]] ^ t[13][d[13]] ^ k[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(o[4..], t[1][d[1]] ^ t[4][d[4]] ^ t[11][d[11]] ^ t[14][d[14]] ^ k[1]);
        BinaryPrimitives.WriteUInt32LittleEndian(o[8..], t[2][d[2]] ^ t[5][d[5]] ^ t[8][d[8]] ^ t[15][d[15]] ^ k[2]);
        BinaryPrimitives.WriteUInt32LittleEndian(o[12..], t[3][d[3]] ^ t[6][d[6]] ^ t[9][d[9]] ^ t[12][d[12]] ^ k[3]);
    }

    /// <summary>AES-256-ECB, whole blocks only.</summary>
    public void DecryptAes(Span<byte> data)
    {
        int len = data.Length - data.Length % 16;
        if (len == 0)
            return;
        using var aes = Aes.Create();
        aes.Key = AesKey;
        var dec = aes.DecryptEcb(data[..len], PaddingMode.None);
        dec.CopyTo(data);
    }
}

public sealed class KeysNotFoundException : Exception
{
    public KeysNotFoundException(string what)
        : base(L.T("core.crypto.notFoundGameExecutableGame", what)) { }

    /// <summary>A ready-made message (for example about downloading magic.dat), without the exe template.</summary>
    public KeysNotFoundException(string message, bool exact)
        : base(message)
    {
        _ = exact;
    }
}
