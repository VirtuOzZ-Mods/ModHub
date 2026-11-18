using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace GTAModManager.Core.Library;

/// <summary>
/// Append-only pack of content-addressed blobs (one file instead of thousands).
/// Layout: header [magic "GMMPACK1"][generation guid] followed by entries
/// [magic "BLOB"][length int64][sha-256 32 bytes][data][trailer "DONE"].
/// A torn tail (crash mid-append) fails the trailer check and is truncated before the next append.
/// Compaction rewrites the pack under a new generation so other readers rebuild their index.
/// </summary>
internal sealed class BlobPack
{
    static readonly byte[] PackMagic = "GMMPACK1"u8.ToArray();
    const uint EntryMagic = 0x424F4C42; // "BLOB"
    const uint TrailerMagic = 0x454E4F44; // "DONE"
    const int HeaderSize = 8 + 16;
    const int EntryHeaderSize = 4 + 8 + 32;
    const int TrailerSize = 4;

    sealed class Index
    {
        public Guid Generation;
        public long ScannedEnd;
        public Dictionary<string, (long Offset, long Length)> Entries = new(StringComparer.OrdinalIgnoreCase);
    }

    // Shared per pack path: several BackupLibrary instances may point at the same folder.
    static readonly ConcurrentDictionary<string, Index> Indexes = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    public string PathName { get; }
    readonly object sync;

    public BlobPack(string path)
    {
        PathName = Path.GetFullPath(path);
        sync = Locks.GetOrAdd(PathName, _ => new object());
    }

    public bool Exists => File.Exists(PathName);

    public long FileLength => Exists ? new FileInfo(PathName).Length : 0;

    static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    public bool Contains(string hash) => TryLocate(hash, out _, out _);

    public long Size(string hash) => TryLocate(hash, out _, out long length) ? length : 0;

    public IReadOnlyCollection<string> Hashes()
    {
        lock (sync)
        {
            if (!Exists)
                return [];
            using var fs = OpenRead(PathName);
            return Refresh(fs).Entries.Keys.ToArray();
        }
    }

    bool TryLocate(string hash, out long offset, out long length)
    {
        offset = length = 0;
        lock (sync)
        {
            if (!Exists)
                return false;
            using var fs = OpenRead(PathName);
            if (!Refresh(fs).Entries.TryGetValue(hash, out var e))
                return false;
            (offset, length) = e;
            return true;
        }
    }

    public byte[] Read(string hash)
    {
        lock (sync)
        {
            using var fs = OpenRead(PathName);
            if (!Refresh(fs).Entries.TryGetValue(hash, out var e))
                throw new FileNotFoundException(hash);
            if (e.Length > int.MaxValue)
                throw new InvalidDataException(hash);
            var data = new byte[e.Length];
            fs.Position = e.Offset;
            fs.ReadExactly(data);
            return data;
        }
    }

    public void CopyTo(string hash, Stream target)
    {
        lock (sync)
        {
            using var fs = OpenRead(PathName);
            if (!Refresh(fs).Entries.TryGetValue(hash, out var e))
                throw new FileNotFoundException(hash);
            fs.Position = e.Offset;
            CopyExactly(fs, target, e.Length);
        }
    }

    /// <summary>Appends content unless the pack already has it. The caller holds the library gate.</summary>
    public void Append(string hash, Stream source, long length)
    {
        lock (sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            using var fs = new FileStream(
                PathName,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                1 << 16
            );
            if (fs.Length < HeaderSize)
                WriteHeader(fs, Guid.NewGuid());
            var index = Refresh(fs);
            if (index.Entries.ContainsKey(hash))
                return;
            // Drop a torn tail left by an interrupted append.
            if (fs.Length != index.ScannedEnd)
                fs.SetLength(index.ScannedEnd);
            fs.Position = index.ScannedEnd;
            WriteEntry(fs, hash, source, length);
            fs.Flush(true);
            long offset = index.ScannedEnd + EntryHeaderSize;
            index.Entries[hash] = (offset, length);
            index.ScannedEnd = offset + length + TrailerSize;
        }
    }

    /// <summary>
    /// Bulk import of loose files already named by their hash (migration): one open, one flush.
    /// The caller verifies the hashes. Returns how many blobs were added.
    /// </summary>
    public int AppendFiles(IEnumerable<(string Hash, string Path)> files)
    {
        lock (sync)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            using var fs = new FileStream(
                PathName,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                1 << 20
            );
            if (fs.Length < HeaderSize)
                WriteHeader(fs, Guid.NewGuid());
            var index = Refresh(fs);
            if (fs.Length != index.ScannedEnd)
                fs.SetLength(index.ScannedEnd);
            fs.Position = index.ScannedEnd;
            int added = 0;
            foreach (var (hash, path) in files)
            {
                if (index.Entries.ContainsKey(hash))
                    continue;
                using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                long length = src.Length;
                long start = fs.Position;
                WriteEntry(fs, hash, src, length);
                index.Entries[hash] = (start + EntryHeaderSize, length);
                added++;
            }
            fs.Flush(true);
            index.ScannedEnd = fs.Position;
            return added;
        }
    }

    /// <summary>Rewrites the pack keeping only <paramref name="live"/> blobs. Returns the size of the dropped content.</summary>
    public long Compact(IReadOnlySet<string> live)
    {
        lock (sync)
        {
            if (!Exists)
                return 0;
            Index index;
            using (var fs = OpenRead(PathName))
                index = Refresh(fs);
            var keep = index.Entries.Where(e => live.Contains(e.Key)).OrderBy(e => e.Value.Offset).ToList();
            long freed = index.Entries.Where(e => !live.Contains(e.Key)).Sum(e => e.Value.Length);
            if (keep.Count == index.Entries.Count && index.ScannedEnd == FileLength)
                return 0;
            if (keep.Count == 0)
            {
                File.Delete(PathName);
                Indexes.TryRemove(PathName, out _);
                return freed;
            }
            string tmp = PathName + ".tmp";
            using (var src = OpenRead(PathName))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                WriteHeader(dst, Guid.NewGuid());
                foreach (var (hash, (offset, length)) in keep)
                {
                    src.Position = offset;
                    WriteEntry(dst, hash, src, length);
                }
                dst.Flush(true);
            }
            File.Move(tmp, PathName, overwrite: true);
            Indexes.TryRemove(PathName, out _);
            return freed;
        }
    }

    public void Delete()
    {
        lock (sync)
        {
            if (Exists)
                File.Delete(PathName);
            Indexes.TryRemove(PathName, out _);
        }
    }

    static void WriteHeader(FileStream fs, Guid generation)
    {
        fs.SetLength(0);
        fs.Position = 0;
        fs.Write(PackMagic);
        fs.Write(generation.ToByteArray());
    }

    static void WriteEntry(Stream fs, string hash, Stream source, long length)
    {
        Span<byte> head = stackalloc byte[EntryHeaderSize];
        BitConverter.TryWriteBytes(head[..4], EntryMagic);
        BitConverter.TryWriteBytes(head.Slice(4, 8), length);
        Convert.FromHexString(hash).CopyTo(head.Slice(12, 32));
        fs.Write(head);
        CopyExactly(source, fs, length);
        Span<byte> tail = stackalloc byte[TrailerSize];
        BitConverter.TryWriteBytes(tail, TrailerMagic);
        fs.Write(tail);
    }

    static void CopyExactly(Stream source, Stream target, long length)
    {
        var buffer = new byte[Math.Max(1, (int)Math.Min(length, 1 << 20))];
        while (length > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (read <= 0)
                throw new EndOfStreamException();
            target.Write(buffer, 0, read);
            length -= read;
        }
    }

    /// <summary>Brings the cached index up to date with the file (new appends, compaction by another process).</summary>
    Index Refresh(FileStream fs)
    {
        var header = new byte[HeaderSize];
        fs.Position = 0;
        if (fs.Length < HeaderSize || fs.Read(header, 0, HeaderSize) != HeaderSize)
            return Indexes[PathName] = new Index { ScannedEnd = HeaderSize };
        if (!header.AsSpan(0, 8).SequenceEqual(PackMagic))
            throw new InvalidDataException(PathName);
        var generation = new Guid(header.AsSpan(8, 16));
        if (
            !Indexes.TryGetValue(PathName, out var index)
            || index.Generation != generation
            || fs.Length < index.ScannedEnd
        )
            index = Indexes[PathName] = new Index { Generation = generation, ScannedEnd = HeaderSize };
        Scan(fs, index);
        return index;
    }

    static void Scan(FileStream fs, Index index)
    {
        var head = new byte[EntryHeaderSize];
        var tail = new byte[TrailerSize];
        long pos = index.ScannedEnd;
        while (pos + EntryHeaderSize + TrailerSize <= fs.Length)
        {
            fs.Position = pos;
            fs.ReadExactly(head);
            if (BitConverter.ToUInt32(head, 0) != EntryMagic)
                break;
            long length = BitConverter.ToInt64(head, 4);
            long end = pos + EntryHeaderSize + length + TrailerSize;
            if (length < 0 || end > fs.Length)
                break;
            fs.Position = end - TrailerSize;
            fs.ReadExactly(tail);
            if (BitConverter.ToUInt32(tail, 0) != TrailerMagic)
                break;
            string hash = Convert.ToHexString(head, 12, 32);
            index.Entries.TryAdd(hash, (pos + EntryHeaderSize, length));
            pos = end;
        }
        index.ScannedEnd = pos;
    }

    public static string HashOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
