using System.Buffers.Binary;
using System.Text;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Audio;

/// <summary>
/// GTA V audio bank (.awc, "ADAT"): streams (sounds) with data/format/peak/markers chunks, etc.
/// Can replace uncompressed PCM sounds (this is how OpenIV sound mods change vehicles.awc from .wav)
/// and write the bank back in its original layout: without changes the output matches the input byte for byte.
/// Format and encryption (Rockstar's modified XXTEA) follow the open CodeWalker documentation.
/// </summary>
public sealed class AwcBank
{
    public const uint MagicLe = 0x54414441; // "ADAT"
    public const byte DataChunk = 0x55,
        FormatChunk = 0xFA,
        PeakChunk = 0x36,
        MidChunk = 0x68,
        MarkersChunk = 0xBD,
        GrainsChunk = 0x5A,
        LoopsChunk = 0xD9;
    private const int PeakBlock = 4096;

    public sealed class Chunk(byte type, byte[] data, double order)
    {
        public byte Type { get; } = type;
        public byte[] Data { get; set; } = data;

        /// <summary>Position in the file: the original offset (new chunks go next to their stream).</summary>
        public double Order { get; set; } = order;
    }

    public sealed class Stream(uint id)
    {
        public uint Id { get; } = id;
        public List<Chunk> Chunks { get; } = [];

        public Chunk? Find(byte type) => Chunks.FirstOrDefault(c => c.Type == type);
    }

    public ushort Version { get; private set; }
    public ushort Flags { get; private set; }
    public bool WasEncrypted { get; private set; }
    public List<Stream> Streams { get; } = [];
    private bool ChunkIndices => (Flags & 1) != 0;

    /// <summary>Stream id from a mod file name: "DUMP_VALVE" → Jenkins(lower) &amp; 0x1FFFFFFF; "0x0103B3B2" → the number.</summary>
    public static uint StreamId(string name)
    {
        var n = Path.GetFileNameWithoutExtension(name).Trim();
        if (
            n.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(n[2..], System.Globalization.NumberStyles.HexNumber, null, out var raw)
        )
            return raw & 0x1FFFFFFF;
        return Crypto.Gta5Keys.JenkinsOneAtATime(Encoding.ASCII.GetBytes(n.ToLowerInvariant())) & 0x1FFFFFFF;
    }

    public static bool LooksEncrypted(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) != MagicLe;

    public static AwcBank Parse(byte[] file, uint[]? key)
    {
        var data = file;
        bool encrypted = false;
        if (LooksEncrypted(data))
        {
            if (key is not { Length: 4 })
                throw new InvalidDataException(L.T("core.common.soundBankEncryptedItsKey"));
            if (data.Length % 4 != 0)
                throw new InvalidDataException(L.T("core.audio.awcSoundBankDamagedHas"));
            data = (byte[])file.Clone();
            Decrypt(data, key);
            encrypted = true;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data) != MagicLe)
                throw new InvalidDataException(L.T("core.audio.awcSoundBankDamagedHas"));
        }
        if (data.Length < 16)
            throw new InvalidDataException(L.T("core.audio.awcSoundBankDamagedHas"));
        var bank = new AwcBank
        {
            Version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)),
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)),
            WasEncrypted = encrypted,
        };
        // Multi-channel banks (music, radio) use a different data format and are not supported here.
        if ((bank.Flags & 0x4) != 0)
            throw new InvalidDataException(L.T("core.audio.multichannelSoundBanksMusicRadio"));
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        if (count < 0 || count > 1 << 20)
            throw new InvalidDataException(L.T("core.audio.awcSoundBankDamagedHas"));
        int pos = 16 + (bank.ChunkIndices ? count * 2 : 0);
        var infos = new uint[count];
        for (int i = 0; i < count; i++, pos += 4)
            infos[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        foreach (var info in infos)
        {
            var stream = new Stream(info & 0x1FFFFFFF);
            for (int c = 0; c < (int)(info >> 29); c++, pos += 8)
            {
                ulong raw = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos));
                int size = (int)((raw >> 28) & 0x0FFFFFFF),
                    offset = (int)(raw & 0x0FFFFFFF);
                if (offset < 0 || size < 0 || (long)offset + size > data.Length)
                    throw new InvalidDataException(L.T("core.audio.awcSoundBankDamagedHas"));
                stream.Chunks.Add(new Chunk((byte)(raw >> 56), data.AsSpan(offset, size).ToArray(), offset));
            }
            bank.Streams.Add(stream);
        }
        return bank;
    }

    public Stream? Find(uint id) => Streams.FirstOrDefault(s => s.Id == id);

    /// <summary>Replaces a sound with uncompressed 16-bit mono PCM: data, sample count, sample rate and peak levels.</summary>
    public void ReplacePcm(uint id, short[] samples, int sampleRate)
    {
        var stream = Find(id) ?? throw new InvalidDataException(L.T("core.audio.soundWasNotFoundBank", $"0x{id:X8}"));
        var format = stream.Find(FormatChunk);
        var dataChunk = stream.Find(DataChunk);
        if (format is null || dataChunk is null || format.Data.Length < 20)
            throw new InvalidDataException(L.T("core.audio.awcSoundBankDamagedHas"));
        if (format.Data[19] != 0)
            throw new InvalidDataException(L.T("core.audio.soundBankCompressedAdpcmMod", $"0x{id:X8}"));
        if (sampleRate is <= 0 or > ushort.MaxValue)
            throw new InvalidDataException(L.T("core.audio.unsupportedSampleRateHz", sampleRate));

        var pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
        dataChunk.Data = pcm;

        var fmt = (byte[])format.Data.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(fmt, (uint)samples.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(8), (ushort)sampleRate);
        var peaks = Peaks(samples);
        if (fmt.Length >= 24)
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(20), peaks[0]);
        format.Data = fmt;

        // Peaks of the remaining full 4096-sample blocks go to a separate chunk (as in the game's banks).
        var rest = peaks.Skip(1).ToArray();
        var peakChunk = stream.Find(PeakChunk);
        if (rest.Length == 0 || fmt.Length < 24)
        {
            if (peakChunk is not null)
                stream.Chunks.Remove(peakChunk);
            return;
        }
        var bytes = new byte[rest.Length * 2];
        for (int i = 0; i < rest.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), rest[i]);
        if (peakChunk is not null)
            peakChunk.Data = bytes;
        else
            stream.Chunks.Insert(0, new Chunk(PeakChunk, bytes, format.Order + 0.5));
    }

    /// <summary>Twice the maximum absolute sample value in each full 4096-sample block (a shorter sound counts as one block).</summary>
    public static ushort[] Peaks(short[] samples)
    {
        int blocks = Math.Max(1, samples.Length / PeakBlock);
        var result = new ushort[blocks];
        for (int b = 0; b < blocks; b++)
        {
            int start = b * PeakBlock,
                end = blocks == 1 && samples.Length < PeakBlock ? samples.Length : start + PeakBlock;
            int max = 0;
            for (int i = start; i < end; i++)
                max = Math.Max(max, Math.Abs((int)samples[i]));
            result[b] = (ushort)Math.Min(ushort.MaxValue, max * 2);
        }
        return result;
    }

    private static int Align(byte type) =>
        type switch
        {
            DataChunk or MidChunk => 16,
            MarkersChunk or GrainsChunk or LoopsChunk => 4,
            _ => 1,
        };

    /// <summary>Writes the bank: header, tables, then chunks in their original order and alignment.</summary>
    public byte[] Build(uint[]? key, bool? encrypt = null)
    {
        int chunkCount = Streams.Sum(s => s.Chunks.Count);
        if (Streams.Any(s => s.Chunks.Count > 7))
            throw new InvalidDataException(L.T("core.audio.awcSoundBankDamagedHas"));
        if (ChunkIndices && chunkCount > ushort.MaxValue)
            throw new InvalidDataException(L.T("core.audio.awcSoundBankDamagedHas"));
        int tables = 16 + (ChunkIndices ? Streams.Count * 2 : 0) + Streams.Count * 4 + chunkCount * 8;

        var offsets = new Dictionary<Chunk, int>();
        long pos = tables;
        foreach (var chunk in Streams.SelectMany(s => s.Chunks).OrderBy(c => c.Order))
        {
            int align = Align(chunk.Type);
            pos = (pos + align - 1) / align * align;
            offsets[chunk] = (int)pos;
            pos += chunk.Data.Length;
            if (pos > 0x0FFFFFFF)
                throw new InvalidDataException(L.T("core.audio.awcSoundBankDamagedHas"));
        }
        bool doEncrypt = encrypt ?? WasEncrypted;
        long length = doEncrypt ? (pos + 3) / 4 * 4 : pos;
        var output = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(output, MagicLe);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6), Flags);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), Streams.Count);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12), tables);
        int w = 16;
        if (ChunkIndices)
        {
            int first = 0;
            foreach (var s in Streams)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(w), (ushort)first);
                w += 2;
                first += s.Chunks.Count;
            }
        }
        foreach (var s in Streams)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan(w),
                ((uint)s.Chunks.Count << 29) | (s.Id & 0x1FFFFFFF)
            );
            w += 4;
        }
        foreach (var c in Streams.SelectMany(s => s.Chunks))
        {
            ulong raw = ((ulong)c.Type << 56) | ((ulong)(uint)c.Data.Length << 28) | (uint)offsets[c];
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(w), raw);
            w += 8;
        }
        foreach (var (c, off) in offsets)
            c.Data.CopyTo(output, off);
        if (doEncrypt)
        {
            if (key is not { Length: 4 })
                throw new InvalidDataException(L.T("core.common.soundBankEncryptedItsKey"));
            Encrypt(output, key);
        }
        return output;
    }

    // ---------- Rockstar's modified XXTEA: standard XXTEA with an extra ^ 0x7B3A207F in key mixing ----------

    private const uint Delta = 0x9E3779B9,
        KeyMix = 0x7B3A207F;

    public static void Decrypt(byte[] data, uint[] key)
    {
        int n = data.Length / 4;
        if (n < 2)
            return;
        var v = new uint[n];
        Buffer.BlockCopy(data, 0, v, 0, n * 4);
        uint rounds = (uint)(6 + 52 / n),
            sum = rounds * Delta,
            y = v[0];
        while (rounds-- > 0)
        {
            uint e = (sum >> 2) & 3;
            for (int p = n - 1; p >= 0; p--)
            {
                uint z = v[p > 0 ? p - 1 : n - 1];
                y = v[p] -=
                    (((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4))) ^ ((sum ^ y) + (key[(p & 3) ^ e] ^ z ^ KeyMix));
            }
            sum -= Delta;
        }
        Buffer.BlockCopy(v, 0, data, 0, n * 4);
    }

    public static void Encrypt(byte[] data, uint[] key)
    {
        int n = data.Length / 4;
        if (n < 2)
            return;
        var v = new uint[n];
        Buffer.BlockCopy(data, 0, v, 0, n * 4);
        uint rounds = (uint)(6 + 52 / n),
            sum = 0,
            z = v[n - 1];
        while (rounds-- > 0)
        {
            sum += Delta;
            uint e = (sum >> 2) & 3;
            for (int p = 0; p < n; p++)
            {
                uint y = v[(p + 1) % n];
                z = v[p] +=
                    (((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4))) ^ ((sum ^ y) + (key[(p & 3) ^ e] ^ z ^ KeyMix));
            }
        }
        Buffer.BlockCopy(v, 0, data, 0, n * 4);
    }
}
