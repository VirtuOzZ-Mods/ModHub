using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Rpf;

/// <summary>Data source of a file to be written into an RPF.</summary>
public abstract record RpfSource;

/// <summary>A new binary file; compressed with raw deflate when Compress=true.</summary>
public sealed record RpfBinarySource(byte[] Content, bool Compress = false) : RpfSource;

/// <summary>
/// A binary file copied byte for byte from another archive (no recompression or decryption).
/// An encrypted (NG) file may only be moved under the same name and with the same size:
/// the NG key is selected by file name and size.
/// </summary>
public sealed record RpfRawBinarySource(
    byte[] Stored,
    uint CompressedSize,
    uint UncompressedSize,
    uint IsEncrypted = 0,
    string? OriginalName = null
) : RpfSource;

/// <summary>
/// A file copied byte for byte from an open archive during writing (without loading the whole archive into memory).
/// The archive must stay open until Write completes.
/// </summary>
public sealed record RpfArchiveCopySource(Rpf7Archive Archive, RpfFile File) : RpfSource;

/// <summary>
/// A resource restored byte for byte from a backup (it may be encrypted, so RSC7 is not checked).
/// The name must match the original.
/// </summary>
public sealed record RpfRawResourceSource(
    byte[] Stored,
    uint SizeField,
    uint SystemFlags,
    uint GraphicsFlags,
    string OriginalName
) : RpfSource;

/// <summary>A resource (RSC7) copied byte for byte. SizeField is kept as is.</summary>
public sealed record RpfResourceSource(byte[] Stored, uint SizeField, uint SystemFlags, uint GraphicsFlags) : RpfSource
{
    public static bool IsRsc7(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Rpf7Format.ResourceMagic;

    /// <summary>
    /// A resource from a file on disk (.ytd/.yft/.ydr…, starting with RSC7).
    /// For resources ≥ 16 MB the size is encoded in header bytes 2, 5, 7 and 14, as in CodeWalker.
    /// </summary>
    public static RpfResourceSource FromFile(byte[] data)
    {
        if (!IsRsc7(data))
            throw new RpfException(L.T("core.rpf.fileNotRsc7Resource"));
        uint sys = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        uint gfx = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        if (data.Length < Rpf7Format.MaxThreeByte)
            return new(data, (uint)data.Length, sys, gfx);
        var copy = (byte[])data.Clone();
        uint len = (uint)copy.Length;
        copy[7] = (byte)len;
        copy[14] = (byte)(len >> 8);
        copy[5] = (byte)(len >> 16);
        copy[2] = (byte)(len >> 24);
        return new(copy, Rpf7Format.MaxThreeByte, sys, gfx);
    }
}

/// <summary>
/// Builds an RPF7 with an unencrypted table of contents (OPEN by default, the mods folder format).
/// Writes only to the given stream; knows nothing about game paths.
/// </summary>
public sealed class Rpf7Builder
{
    private sealed class Node(string name, RpfSource? source)
    {
        public string Name { get; } = name;
        public RpfSource? Source { get; } = source;
        public bool IsDir => Source is null;
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Node _root = new("", null);

    public RpfEncryption Encryption { get; init; } = RpfEncryption.Open;

    public Rpf7Builder AddDirectory(string path)
    {
        _ = GetDir(Split(path), create: true);
        return this;
    }

    public Rpf7Builder AddFile(string path, RpfSource source)
    {
        var parts = Split(path);
        if (parts.Length == 0)
            throw new ArgumentException(L.T("core.rpf.emptyPath"));
        var dir = GetDir(parts[..^1], create: true)!;
        var name = parts[^1];
        if (dir.Children.TryGetValue(name, out var existing) && existing.IsDir)
            throw new InvalidOperationException(L.T("core.rpf.alreadyDirectory", path));
        dir.Children[name] = new Node(name, source);
        return this;
    }

    /// <summary>Deletes a file or directory; directories left empty by the deletion are removed too.</summary>
    public bool Remove(string path)
    {
        var parts = Split(path);
        if (parts.Length == 0)
            return false;
        var dir = GetDir(parts[..^1], create: false);
        if (dir is null || !dir.Children.Remove(parts[^1]))
            return false;
        for (int depth = parts.Length - 1; depth > 0; depth--)
        {
            var d = GetDir(parts[..depth], create: false)!;
            if (d.Children.Count > 0)
                break;
            GetDir(parts[..(depth - 1)], create: false)!.Children.Remove(parts[depth - 1]);
        }
        return true;
    }

    /// <summary>
    /// A copy of the archive's entire content without repacking the data.
    /// Data is read from archive during Write, so the archive must stay open.
    /// </summary>
    public static Rpf7Builder FromArchive(Rpf7Archive archive)
    {
        var b = new Rpf7Builder
        {
            Encryption = archive.Header.Encryption == RpfEncryption.None ? RpfEncryption.None : RpfEncryption.Open,
        };
        foreach (var e in archive.Root.Descendants())
        {
            if (e is RpfDirectory)
                b.AddDirectory(e.Path);
            else
                b.AddFile(e.Path, new RpfArchiveCopySource(archive, (RpfFile)e));
        }
        return b;
    }

    public RpfSource? GetSource(string path)
    {
        var parts = Split(path);
        if (parts.Length == 0)
            return null;
        var dir = GetDir(parts[..^1], create: false);
        return dir is not null && dir.Children.TryGetValue(parts[^1], out var n) ? n.Source : null;
    }

    /// <summary>Paths of all files with this name (case-insensitive), to hint where the archive actually is.</summary>
    public List<string> FindByName(string name)
    {
        var result = new List<string>();
        void Walk(Node node, string prefix)
        {
            foreach (var child in node.Children.Values)
            {
                var path = prefix.Length == 0 ? child.Name : prefix + "\\" + child.Name;
                if (child.IsDir)
                    Walk(child, path);
                else if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    result.Add(path);
            }
        }
        Walk(_root, "");
        return result;
    }

    public bool Exists(string path)
    {
        var parts = Split(path);
        if (parts.Length == 0)
            return true;
        var dir = GetDir(parts[..^1], create: false);
        return dir is not null && dir.Children.ContainsKey(parts[^1]);
    }

    /// <summary>Decompressed file content, if available without keys.</summary>
    public byte[]? TryReadContent(string path)
    {
        var parts = Split(path);
        var dir = GetDir(parts[..^1], create: false);
        if (dir is null || !dir.Children.TryGetValue(parts[^1], out var n) || n.IsDir)
            return null;
        return n.Source switch
        {
            RpfBinarySource bs => bs.Content,
            RpfArchiveCopySource { File: RpfBinaryFile bf } cs when bf.IsEncrypted == 0 || cs.Archive.CanDecrypt =>
                cs.Archive.ReadContent(bf),
            _ => null,
        };
    }

    public void Write(Stream output)
    {
        // 1. Entry order matches OpenIV (verified on real mods archives):
        //    a directory's children are contiguous, sorted ordinally by lower-case name;
        //    child ranges are assigned by a depth-first walk.
        var order = new List<Node> { _root };
        var ranges = new Dictionary<Node, (int start, int count)>();
        void Place(Node dir)
        {
            var kids = Sorted(dir);
            ranges[dir] = (order.Count, kids.Count);
            order.AddRange(kids);
            foreach (var k in kids.Where(k => k.IsDir))
                Place(k);
        }
        Place(_root);

        // 2. Name table with deduplication; order: subdirectories recursively, then the directory's files.
        var names = new MemoryStream();
        var nameIndex = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint AddName(string name)
        {
            if (nameIndex.TryGetValue(name, out var off))
                return off;
            var bytes = Encoding.ASCII.GetBytes(name);
            if (name.Any(c => c >= 0x80))
                throw new RpfException(L.T("core.rpf.nameNotAscii", name));
            off = (uint)names.Length;
            names.Write(bytes);
            names.WriteByte(0);
            nameIndex[name] = off;
            return off;
        }
        AddName("");
        void VisitNames(Node dir)
        {
            var kids = Sorted(dir);
            foreach (var d in kids.Where(k => k.IsDir))
            {
                AddName(d.Name);
                VisitNames(d);
            }
            foreach (var f in kids.Where(k => !k.IsDir))
                AddName(f.Name);
        }
        VisitNames(_root);
        while (names.Length % 16 != 0)
            names.WriteByte(0);

        var nameOffsets = new uint[order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            nameOffsets[i] = nameIndex[order[i].Name];
            if (!order[i].IsDir && nameOffsets[i] > 0xFFFF)
                throw new RpfException(L.T("core.rpf.nameTableExceeds64Kb"));
        }

        // 3. Prepare data and offsets.
        long tocEnd = Rpf7Format.HeaderSize + (long)order.Count * Rpf7Format.EntrySize + names.Length;
        long cursorBlocks = Align(tocEnd) / Rpf7Format.BlockSize;
        var payloads = new (long size, Func<byte[]>? get)[order.Count];
        var entries = new byte[order.Count * Rpf7Format.EntrySize];

        for (int i = 0; i < order.Count; i++)
        {
            var node = order[i];
            var e = entries.AsSpan(i * Rpf7Format.EntrySize, Rpf7Format.EntrySize);
            if (node.IsDir)
            {
                var (start, count) = ranges[node];
                BinaryPrimitives.WriteUInt32LittleEndian(e, nameOffsets[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(e[4..], Rpf7Format.DirectoryIdent);
                BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)start);
                BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)count);
                continue;
            }

            uint offBlocks = (uint)cursorBlocks;
            switch (node.Source)
            {
                case RpfBinarySource bs:
                {
                    byte[] stored = bs.Content;
                    uint csize = 0;
                    if (bs.Compress)
                    {
                        var c = Deflate(bs.Content);
                        if (c.Length < bs.Content.Length && c.Length < Rpf7Format.MaxThreeByte)
                        {
                            stored = c;
                            csize = (uint)c.Length;
                        }
                    }
                    WriteBinaryEntry(e, nameOffsets[i], csize, offBlocks, (uint)bs.Content.Length);
                    payloads[i] = (stored.Length, () => stored);
                    break;
                }
                case RpfRawBinarySource rb:
                    CheckEncryptedName(rb.IsEncrypted, rb.OriginalName, node.Name);
                    WriteBinaryEntry(
                        e,
                        nameOffsets[i],
                        rb.CompressedSize,
                        offBlocks,
                        rb.UncompressedSize,
                        rb.IsEncrypted
                    );
                    payloads[i] = (rb.Stored.Length, () => rb.Stored);
                    break;
                case RpfResourceSource rs:
                {
                    if (rs.SizeField != Rpf7Format.MaxThreeByte && !RpfResourceSource.IsRsc7(rs.Stored))
                        throw new RpfException(L.T("core.rpf.resourceDoesNotStartRsc7", node.Name));
                    if (rs.SizeField != Rpf7Format.MaxThreeByte && rs.SizeField != rs.Stored.Length)
                        throw new RpfException(L.T("core.rpf.resourceSizeFieldDoesNot", node.Name));
                    WriteResourceEntry(e, nameOffsets[i], rs.SizeField, offBlocks, rs.SystemFlags, rs.GraphicsFlags);
                    payloads[i] = (rs.Stored.Length, () => rs.Stored);
                    break;
                }
                case RpfRawResourceSource rr:
                    if (!string.Equals(rr.OriginalName, node.Name, StringComparison.Ordinal))
                        throw new RpfException(
                            L.T("core.rpf.resourceBeingRestoredCannotRenamed", rr.OriginalName, node.Name)
                        );
                    WriteResourceEntry(e, nameOffsets[i], rr.SizeField, offBlocks, rr.SystemFlags, rr.GraphicsFlags);
                    payloads[i] = (rr.Stored.Length, () => rr.Stored);
                    break;
                case RpfArchiveCopySource { File: RpfBinaryFile bf } cs:
                    CheckEncryptedName(bf.IsEncrypted, bf.Name, node.Name);
                    WriteBinaryEntry(
                        e,
                        nameOffsets[i],
                        bf.CompressedSize,
                        offBlocks,
                        bf.UncompressedSize,
                        bf.IsEncrypted
                    );
                    payloads[i] = (bf.StoredSize, () => cs.Archive.ReadRaw(bf));
                    break;
                case RpfArchiveCopySource { File: RpfResourceFile rf } cs:
                    // The resource may be encrypted (copied from an NG archive): carry it over as-is.
                    if (!string.Equals(rf.Name, node.Name, StringComparison.Ordinal))
                        throw new RpfException(L.T("core.rpf.copiedResourceCannotRenamed", rf.Name, node.Name));
                    WriteResourceEntry(e, nameOffsets[i], rf.SizeField, offBlocks, rf.SystemFlags, rf.GraphicsFlags);
                    payloads[i] = (rf.StoredSize, () => cs.Archive.ReadRaw(rf));
                    break;
                default:
                    throw new InvalidOperationException();
            }
            cursorBlocks += Align(payloads[i].size) / Rpf7Format.BlockSize;
        }

        // 4. Write.
        Span<byte> header = stackalloc byte[Rpf7Format.HeaderSize];
        new RpfHeader(Rpf7Format.Magic, (uint)order.Count, (uint)names.Length, (uint)Encryption).Write(header);
        output.Write(header);
        output.Write(entries);
        names.Position = 0;
        names.CopyTo(output);
        Pad(output, tocEnd);
        long written = Align(tocEnd);
        foreach (var (size, get) in payloads)
        {
            if (get is null)
                continue;
            var data = get();
            if (data.Length != size)
                throw new RpfException(L.T("core.rpf.dataSizeChangedDuringWriting"));
            output.Write(data);
            written += data.Length;
            Pad(output, written);
            written = Align(written);
        }
        output.Flush();
    }

    private static void CheckEncryptedName(uint isEncrypted, string? original, string name)
    {
        if (isEncrypted != 0 && !string.Equals(original, name, StringComparison.Ordinal))
            throw new RpfException(L.T("core.rpf.encryptedFileCannotRenamed", original, name));
    }

    private static void WriteResourceEntry(
        Span<byte> e,
        uint nameOff,
        uint sizeField,
        uint offBlocks,
        uint sys,
        uint gfx
    )
    {
        if (offBlocks > 0x7FFFFF)
            throw new RpfException(L.T("core.rpf.archiveTooLargeResourceOffset"));
        ulong buf =
            (ulong)(nameOff & 0xFFFF)
            | ((ulong)(sizeField & 0xFFFFFF) << 16)
            | ((ulong)((offBlocks & 0x7FFFFF) | 0x800000) << 40);
        BinaryPrimitives.WriteUInt64LittleEndian(e, buf);
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], sys);
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], gfx);
    }

    private static void WriteBinaryEntry(
        Span<byte> e,
        uint nameOff,
        uint csize,
        uint offBlocks,
        uint usize,
        uint isEncrypted = 0
    )
    {
        // The high bit of the 3-byte offset distinguishes a resource from a binary file.
        if (offBlocks > 0x7FFFFF)
            throw new RpfException(L.T("core.rpf.archiveTooLargeFileOffset"));
        ulong buf =
            (ulong)(nameOff & 0xFFFF) | ((ulong)(csize & 0xFFFFFF) << 16) | ((ulong)(offBlocks & 0x7FFFFF) << 40);
        BinaryPrimitives.WriteUInt64LittleEndian(e, buf);
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], usize);
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], isEncrypted);
    }

    private static List<Node> Sorted(Node dir) =>
        dir.Children.Values.OrderBy(n => n.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            d.Write(data);
        return ms.ToArray();
    }

    private static long Align(long v) => (v + Rpf7Format.BlockSize - 1) / Rpf7Format.BlockSize * Rpf7Format.BlockSize;

    private static void Pad(Stream s, long current)
    {
        long pad = Align(current) - current;
        if (pad > 0)
            s.Write(new byte[pad]);
    }

    private Node? GetDir(string[] parts, bool create)
    {
        var cur = _root;
        foreach (var p in parts)
        {
            if (!cur.Children.TryGetValue(p, out var next))
            {
                if (!create)
                    return null;
                next = new Node(p, null);
                cur.Children[p] = next;
            }
            if (!next.IsDir)
                throw new InvalidOperationException(L.T("core.rpf.fileNotDirectory", p));
            cur = next;
        }
        return cur;
    }

    private static string[] Split(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
            if (p is "." or "..")
                throw new ArgumentException(L.T("core.rpf.invalidPathSegment", path));
        return parts;
    }
}
