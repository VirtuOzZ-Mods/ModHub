using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Rpf;

public abstract class RpfEntry
{
    public required string Name { get; init; }
    public string Path { get; internal set; } = "";
    public RpfDirectory? Parent { get; internal set; }
}

public sealed class RpfDirectory : RpfEntry
{
    public List<RpfEntry> Children { get; } = [];

    public IEnumerable<RpfEntry> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            if (c is RpfDirectory d)
                foreach (var x in d.Descendants())
                    yield return x;
        }
    }
}

public abstract class RpfFile : RpfEntry
{
    /// <summary>Data offset in 512-byte blocks.</summary>
    public uint OffsetBlocks { get; init; }

    /// <summary>On-disk data size in bytes.</summary>
    public abstract long StoredSize { get; }
}

public sealed class RpfBinaryFile : RpfFile
{
    /// <summary>0 means stored uncompressed.</summary>
    public uint CompressedSize { get; init; }
    public uint UncompressedSize { get; init; }
    public uint IsEncrypted { get; init; }
    public override long StoredSize => CompressedSize != 0 ? CompressedSize : UncompressedSize;
}

public sealed class RpfResourceFile : RpfFile
{
    /// <summary>Value of the 3-byte size field (0xFFFFFF means the size comes from the data header).</summary>
    public uint SizeField { get; init; }
    public uint ActualSize { get; init; }
    public uint SystemFlags { get; init; }
    public uint GraphicsFlags { get; init; }
    public override long StoredSize => ActualSize;
}

/// <summary>
/// Reads RPF7 with an unencrypted table of contents (None / OPEN). Read-only; the stream is never modified.
/// </summary>
public sealed class Rpf7Archive : IDisposable
{
    private readonly Stream _stream;
    private readonly long _baseOffset;
    private readonly bool _ownsStream;

    public RpfHeader Header { get; }
    public RpfDirectory Root { get; }
    public long Length { get; }

    private readonly Crypto.Gta5Keys? _keys;

    /// <summary>Archive name: used to select the NG key for the table of contents.</summary>
    public string Name { get; }

    private Rpf7Archive(
        Stream stream,
        long baseOffset,
        long length,
        bool ownsStream,
        Crypto.Gta5Keys? keys,
        string name
    )
    {
        _stream = stream;
        _baseOffset = baseOffset;
        _ownsStream = ownsStream;
        _keys = keys;
        Name = name;
        Length = length;

        _stream.Position = baseOffset;
        Header = RpfHeader.Read(_stream);
        if (!Header.IsRpf7)
            throw new RpfException(L.T("core.rpf.notRpf7Magic0x", Header.Magic));
        if (!Header.TocIsPlain && keys is null)
            throw new RpfEncryptedException(Header.Encryption);
        if (Header.EntryCount == 0)
            throw new RpfException(L.T("core.rpf.emptyTableContents"));

        long tocSize = (long)Header.EntryCount * Rpf7Format.EntrySize + Header.NamesLength;
        if (Rpf7Format.HeaderSize + tocSize > length)
            throw new RpfException(L.T("core.rpf.tableContentsExtendsBeyondArchive"));

        var entries = new byte[Header.EntryCount * Rpf7Format.EntrySize];
        _stream.ReadExactly(entries);
        var names = new byte[Header.NamesLength];
        _stream.ReadExactly(names);

        if (!Header.TocIsPlain)
        {
            if (Header.Encryption == RpfEncryption.Aes)
            {
                keys!.DecryptAes(entries);
                keys.DecryptAes(names);
            }
            else
            {
                keys!.DecryptNg(entries, name, (uint)length);
                keys.DecryptNg(names, name, (uint)length);
            }
        }

        try
        {
            Root = (RpfDirectory)ParseTree(entries, names);
        }
        catch (RpfException) when (!Header.TocIsPlain)
        {
            throw new RpfException(L.T("core.rpf.couldNotDecryptTableContents", name, Header.Encryption));
        }
    }

    public static Rpf7Archive OpenRead(string path, Crypto.Gta5Keys? keys = null)
    {
        var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1 << 16,
            FileOptions.RandomAccess
        );
        try
        {
            return new Rpf7Archive(fs, 0, fs.Length, true, keys, Path.GetFileName(path));
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public static Rpf7Archive Open(
        Stream stream,
        bool leaveOpen = true,
        Crypto.Gta5Keys? keys = null,
        string name = ""
    ) => new(stream, 0, stream.Length, !leaveOpen, keys, name);

    public bool CanDecrypt => _keys is not null;

    /// <summary>A nested RPF stored uncompressed as a binary file.</summary>
    public Rpf7Archive OpenNested(RpfBinaryFile file)
    {
        if (file.CompressedSize != 0 || file.IsEncrypted != 0)
            throw new RpfException(L.T("core.rpf.nestedArchiveCompressedEncrypted", file.Path));
        return new Rpf7Archive(_stream, AbsOffset(file), file.UncompressedSize, false, _keys, file.Name);
    }

    public IEnumerable<RpfFile> Files => Root.Descendants().OfType<RpfFile>();

    public RpfEntry? Find(string path)
    {
        RpfEntry cur = Root;
        foreach (var part in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur is not RpfDirectory d)
                return null;
            var next = d.Children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next is null)
                return null;
            cur = next;
        }
        return cur;
    }

    /// <summary>The file bytes exactly as they are stored in the archive.</summary>
    public byte[] ReadRaw(RpfFile file)
    {
        long size = file.StoredSize;
        long off = AbsOffset(file);
        if (off - _baseOffset + size > Length)
            throw new RpfException(L.T("core.rpf.dataExtendsBeyondArchive", file.Path));
        var buf = new byte[size];
        _stream.Position = off;
        _stream.ReadExactly(buf);
        return buf;
    }

    /// <summary>
    /// The entry as a standalone file on disk: binary files decrypted and decompressed, resources (.ytd/.ydr…) with the
    /// RSC7 header rebuilt from the entry flags (for resources ≥ 16 MB the archive stores the size there).
    /// Writing such a file back into an archive yields identical bytes.
    /// </summary>
    public byte[] ReadAsFile(RpfFile file)
    {
        if (file is RpfBinaryFile binary)
            return ReadContent(binary);
        var resource = (RpfResourceFile)file;
        var data = ReadRaw(resource);
        if (data.Length < 16)
            throw new RpfException(L.T("core.rpf.dataExtendsBeyondArchive", file.Path));
        uint version = ((resource.SystemFlags >> 28) & 0xF) << 4 | ((resource.GraphicsFlags >> 28) & 0xF);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data, Rpf7Format.ResourceMagic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), version);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), resource.SystemFlags);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), resource.GraphicsFlags);
        return data;
    }

    /// <summary>Writes the entry as a standalone file; plain stored entries (nested archives) are streamed, not buffered.</summary>
    public void CopyAsFile(RpfFile file, Stream output)
    {
        if (file is not RpfBinaryFile { IsEncrypted: 0, CompressedSize: 0 })
        {
            output.Write(ReadAsFile(file));
            return;
        }
        long size = file.StoredSize;
        long off = AbsOffset(file);
        if (off - _baseOffset + size > Length)
            throw new RpfException(L.T("core.rpf.dataExtendsBeyondArchive", file.Path));
        var buf = new byte[1 << 20];
        _stream.Position = off;
        while (size > 0)
        {
            int n = (int)Math.Min(buf.Length, size);
            _stream.ReadExactly(buf, 0, n);
            output.Write(buf, 0, n);
            size -= n;
        }
    }

    /// <summary>SHA-256 of the file bytes in the archive, streamed (the file may exceed 2 GB).</summary>
    public string HashRaw(RpfFile file)
    {
        long size = file.StoredSize;
        long off = AbsOffset(file);
        if (off - _baseOffset + size > Length)
            throw new RpfException(L.T("core.rpf.dataExtendsBeyondArchive", file.Path));
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256
        );
        var buf = new byte[1 << 20];
        _stream.Position = off;
        while (size > 0)
        {
            int n = (int)Math.Min(buf.Length, size);
            _stream.ReadExactly(buf, 0, n);
            hash.AppendData(buf, 0, n);
            size -= n;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Decompressed content of a binary file.</summary>
    public byte[] ReadContent(RpfBinaryFile file)
    {
        var raw = ReadRaw(file);
        if (file.IsEncrypted != 0)
        {
            if (_keys is null)
                throw new RpfException(L.T("core.rpf.fileEncrypted", file.Path));
            // In OPEN archives (OpenIV) encrypted files use NG; in AES archives, AES.
            if (Header.Encryption == RpfEncryption.Aes)
                _keys.DecryptAes(raw);
            else
                _keys.DecryptNg(raw, file.Name, file.UncompressedSize);
        }
        if (file.CompressedSize == 0)
            return raw;
        using var inflater = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
        var result = new byte[file.UncompressedSize];
        inflater.ReadExactly(result);
        return result;
    }

    private long AbsOffset(RpfFile f) => _baseOffset + (long)f.OffsetBlocks * Rpf7Format.BlockSize;

    private RpfEntry ParseTree(byte[] entries, byte[] names)
    {
        int count = (int)Header.EntryCount;
        var parsed = new RpfEntry[count];
        var dirRanges = new (uint index, uint count)[count];

        for (int i = 0; i < count; i++)
        {
            var e = entries.AsSpan(i * Rpf7Format.EntrySize, Rpf7Format.EntrySize);
            ulong buf = BinaryPrimitives.ReadUInt64LittleEndian(e);
            uint x = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            uint h2 = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            uint h3 = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);

            if (x == Rpf7Format.DirectoryIdent)
            {
                uint nameOff = BinaryPrimitives.ReadUInt32LittleEndian(e);
                parsed[i] = new RpfDirectory { Name = ReadName(names, nameOff) };
                dirRanges[i] = (h2, h3);
            }
            else if ((x & 0x80000000) == 0)
            {
                parsed[i] = new RpfBinaryFile
                {
                    Name = ReadName(names, (uint)(buf & 0xFFFF)),
                    CompressedSize = (uint)((buf >> 16) & 0xFFFFFF),
                    OffsetBlocks = (uint)((buf >> 40) & 0xFFFFFF),
                    UncompressedSize = h2,
                    IsEncrypted = h3,
                };
            }
            else
            {
                uint sizeField = (uint)((buf >> 16) & 0xFFFFFF);
                uint offBlocks = (uint)((buf >> 40) & 0x7FFFFF);
                uint actual = sizeField == Rpf7Format.MaxThreeByte ? ReadLargeResourceSize(offBlocks) : sizeField;
                parsed[i] = new RpfResourceFile
                {
                    Name = ReadName(names, (uint)(buf & 0xFFFF)),
                    SizeField = sizeField,
                    ActualSize = actual,
                    OffsetBlocks = offBlocks,
                    SystemFlags = h2,
                    GraphicsFlags = h3,
                };
            }
        }

        if (parsed[0] is not RpfDirectory root)
            throw new RpfException(L.T("core.rpf.rootEntryNotDirectory"));

        var visited = new bool[count];
        var stack = new Stack<int>();
        stack.Push(0);
        visited[0] = true;
        root.Path = "";
        while (stack.Count > 0)
        {
            int di = stack.Pop();
            var dir = (RpfDirectory)parsed[di];
            var (start, n) = dirRanges[di];
            if (start + (ulong)n > (ulong)count)
                throw new RpfException(L.T("core.rpf.directoryPointsBeyondTableContents", dir.Path));
            for (uint k = start; k < start + n; k++)
            {
                if (visited[k])
                    throw new RpfException(L.T("core.rpf.cycleDuplicateReferenceTableContents"));
                visited[k] = true;
                var child = parsed[k];
                child.Parent = dir;
                child.Path = dir.Path.Length == 0 ? child.Name : dir.Path + "\\" + child.Name;
                dir.Children.Add(child);
                if (child is RpfDirectory)
                    stack.Push((int)k);
            }
        }
        return root;
    }

    // For resources > 16 MB the real size is encoded in the data header bytes (per CodeWalker).
    private uint ReadLargeResourceSize(uint offBlocks)
    {
        Span<byte> h = stackalloc byte[16];
        _stream.Position = _baseOffset + (long)offBlocks * Rpf7Format.BlockSize;
        _stream.ReadExactly(h);
        return h[7] | ((uint)h[14] << 8) | ((uint)h[5] << 16) | ((uint)h[2] << 24);
    }

    private static string ReadName(byte[] names, uint offset)
    {
        if (offset >= names.Length)
            throw new RpfException(L.T("core.rpf.nameOffsetOutsideNameTable", offset));
        int end = Array.IndexOf(names, (byte)0, (int)offset);
        if (end < 0)
            end = names.Length;
        return Encoding.ASCII.GetString(names, (int)offset, end - (int)offset);
    }

    public void Dispose()
    {
        if (_ownsStream)
            _stream.Dispose();
    }
}
