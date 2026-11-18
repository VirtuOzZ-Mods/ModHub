using System.IO.Compression;
using System.Security.Cryptography;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.Oiv;

/// <summary>
/// Edits one RPF (and the RPFs nested in it) in memory: original data is copied from the archive on write,
/// changed files are kept in the builder. The source file is never modified.
/// Each change is recorded together with the file's previous content, for targeted rollback.
/// </summary>
public sealed class ArchiveSession : IDisposable
{
    private readonly Rpf7Archive? _source;
    private readonly IDisposable? _owned;
    private readonly Dictionary<string, ArchiveSession> _nested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InnerChange> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _changeOrder = [];
    private readonly HashSet<string> _createdNested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _createdNestedFingerprints = new(StringComparer.OrdinalIgnoreCase);

    public string Label { get; }
    public Rpf7Builder Builder { get; }
    public bool Changed { get; private set; }

    private ArchiveSession(string label, Rpf7Archive? source, IDisposable? owned)
    {
        Label = label;
        _source = source;
        _owned = owned;
        Builder = source is null ? new Rpf7Builder() : Rpf7Builder.FromArchive(source);
    }

    public static ArchiveSession OpenFile(string path, string label, Crypto.Gta5Keys? keys = null)
    {
        try
        {
            return FromArchive(Rpf7Archive.OpenRead(path, keys), label, owns: true);
        }
        catch (RpfEncryptedException)
        {
            throw new OivFormatException(L.T("core.oiv.tableContentsEncryptedGameKeys", label));
        }
    }

    public void MarkChanged() => Changed = true;

    public static ArchiveSession CreateEmpty(string label)
    {
        var s = new ArchiveSession(label, null, null);
        s.Changed = true;
        return s;
    }

    private static ArchiveSession FromArchive(Rpf7Archive a, string label, bool owns) => new(label, a, owns ? a : null);

    /// <summary>Splits a path like "x64\vehicles.rpf\car.yft" into nested sessions.</summary>
    public (ArchiveSession session, string inner) Resolve(string path, bool createArchives)
    {
        var parts = Game.GameInstall.Normalize(path).Split('\\');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!parts[i].EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                continue;
            var nested = Nested(string.Join('\\', parts[..(i + 1)]), createArchives);
            return nested.Resolve(string.Join('\\', parts[(i + 1)..]), createArchives);
        }
        return (this, string.Join('\\', parts));
    }

    public ArchiveSession Nested(string innerPath, bool create)
    {
        var (owner, inner) = Resolve(innerPath, create);
        if (owner != this)
            return owner.Nested(inner, create);
        if (_nested.TryGetValue(inner, out var s))
            return s;

        var label = Label + "\\" + inner;
        switch (Builder.GetSource(inner))
        {
            case RpfArchiveCopySource { File: RpfBinaryFile bf } cs:
                if (bf.IsEncrypted != 0 || bf.CompressedSize != 0)
                    throw new OivFormatException(L.T("core.oiv.nestedArchiveEncryptedCompressedGame", label));
                try
                {
                    s = FromArchive(cs.Archive.OpenNested(bf), label, owns: true);
                }
                catch (RpfEncryptedException)
                {
                    throw new OivFormatException(L.T("core.oiv.tableContentsEncryptedGameKeys", label));
                }
                break;
            case RpfBinarySource bs:
                s = FromArchive(Rpf7Archive.Open(new MemoryStream(bs.Content), leaveOpen: false), label, owns: true);
                break;
            case null when create:
                s = CreateEmpty(label);
                _createdNested.Add(inner);
                Changed = true;
                break;
            case null:
                throw new MissingGamePathException(
                    label,
                    Builder
                        .FindByName(Path.GetFileName(inner))
                        .Where(p => !p.Equals(inner, StringComparison.OrdinalIgnoreCase))
                        .Select(p => Label + "\\" + p)
                        .ToList(),
                    L.T("core.oiv.missingNestedArchive", label)
                );
            default:
                throw new OivFormatException(L.T("core.oiv.notArchive", label));
        }
        _nested[inner] = s;
        return s;
    }

    public byte[]? ReadFile(string path)
    {
        var (s, inner) = Resolve(path, false);
        if (s != this)
            return s.ReadFile(inner);
        var src = Builder.GetSource(inner);
        if (src is RpfArchiveCopySource { File: RpfBinaryFile { IsEncrypted: not 0 } } enc && !enc.Archive.CanDecrypt)
            throw new OivFormatException(L.T("core.oiv.encryptedGameKeysRequired", Label, inner));
        return Builder.TryReadContent(inner);
    }

    public void Put(string path, byte[] data)
    {
        var (s, inner) = Resolve(path, true);
        if (s != this)
        {
            s.Put(inner, data);
            Changed = true;
            return;
        }
        RpfSource src = RpfResourceSource.IsRsc7(data)
            ? RpfResourceSource.FromFile(data)
            // Nested archives and audio banks (the game streams them) are stored uncompressed, as in the original.
            : new RpfBinarySource(
                data,
                Compress: !inner.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)
                    && !inner.EndsWith(".awc", StringComparison.OrdinalIgnoreCase)
            );
        PutSource(inner, src);
    }

    /// <summary>Puts a ready-made source (including an entry restored from a backup).</summary>
    public void PutSource(string path, RpfSource src)
    {
        var (s, inner) = Resolve(path, true);
        if (s != this)
        {
            s.PutSource(inner, src);
            Changed = true;
            return;
        }
        Track(inner);
        DropNested(inner);
        Builder.AddFile(inner, src);
        _changes[inner].NewFingerprint = Fingerprint(src);
        if (_changes[inner].Old is not null)
            _changes[inner].Kind = InnerKind.Replaced;
        Changed = true;
    }

    public bool Delete(string path)
    {
        var (s, inner) = Resolve(path, false);
        if (s != this)
        {
            bool r = s.Delete(inner);
            Changed |= r;
            return r;
        }
        if (!Builder.Exists(inner))
            return false;
        Track(inner);
        DropNested(inner);
        Builder.Remove(inner);
        var c = _changes[inner];
        if (c.Old is null)
        {
            _changes.Remove(inner);
            _changeOrder.Remove(inner);
        } // added and immediately removed
        else
        {
            c.Kind = InnerKind.Deleted;
            c.NewFingerprint = null;
        }
        Changed = true;
        return true;
    }

    /// <summary>Fingerprint of the file's current content (null if the file does not exist). Compared on undo.</summary>
    public string? Fingerprint(string path)
    {
        var (s, inner) = Resolve(path, false);
        if (s != this)
            return s.Fingerprint(inner);
        if (_nested.TryGetValue(inner, out var child) && child.Changed)
            return null; // a nested archive is being edited, so it is not compared
        var src = Builder.GetSource(inner);
        return src is null ? null : Fingerprint(src);
    }

    /// <summary>
    /// All changes (including nested archives) with paths from the root of this session.
    /// Call after WriteTo: newly created nested archives need a fingerprint of their final bytes.
    /// </summary>
    public List<InnerChange> CollectChanges()
    {
        var result = new List<InnerChange>();
        foreach (var key in _changeOrder)
            result.Add(_changes[key]);
        foreach (var (inner, child) in _nested)
        {
            if (_createdNested.Contains(inner))
            {
                if (child.Changed)
                    result.Add(
                        new InnerChange
                        {
                            Path = inner,
                            Kind = InnerKind.Added,
                            NewFingerprint = _createdNestedFingerprints.GetValueOrDefault(inner),
                        }
                    );
                continue;
            }
            foreach (var c in child.CollectChanges())
                result.Add(
                    new InnerChange
                    {
                        Path = inner + "\\" + c.Path,
                        Kind = c.Kind,
                        NewFingerprint = c.NewFingerprint,
                        Old = c.Old,
                    }
                );
        }
        return result;
    }

    /// <summary>Writes the resulting archive (nested ones are rebuilt and stored uncompressed).</summary>
    public void WriteTo(Stream output)
    {
        foreach (var (inner, child) in _nested)
        {
            if (!child.Changed)
                continue;
            var ms = new MemoryStream();
            child.WriteTo(ms);
            var bytes = ms.ToArray();
            Builder.AddFile(inner, new RpfBinarySource(bytes, Compress: false));
            if (_createdNested.Contains(inner))
                _createdNestedFingerprints[inner] = ContentHash(bytes);
        }
        Builder.Write(output);
    }

    // ---------- Change tracking ----------

    private void Track(string inner)
    {
        if (_changes.ContainsKey(inner))
            return;
        var old = Builder.GetSource(inner);
        _changes[inner] = new InnerChange
        {
            Path = inner,
            Kind = old is null ? InnerKind.Added : InnerKind.Replaced,
            Old = old is null ? null : Snapshot(old, inner.Split('\\')[^1]),
        };
        _changeOrder.Add(inner);
    }

    private void DropNested(string inner)
    {
        if (_nested.Remove(inner, out var old))
            old.Dispose();
        _createdNested.Remove(inner);
    }

    private static OldEntry Snapshot(RpfSource src, string name) =>
        src switch
        {
            RpfArchiveCopySource { File: RpfBinaryFile bf } cs => new OldEntry
            {
                Type = "binary",
                Name = bf.Name,
                Raw = cs.Archive.ReadRaw(bf),
                Size = bf.StoredSize,
                CompressedSize = bf.CompressedSize,
                UncompressedSize = bf.UncompressedSize,
                IsEncrypted = bf.IsEncrypted,
            },
            RpfArchiveCopySource { File: RpfResourceFile rf } cs => new OldEntry
            {
                Type = "resource",
                Name = rf.Name,
                Raw = cs.Archive.ReadRaw(rf),
                Size = rf.StoredSize,
                SizeField = rf.SizeField,
                SystemFlags = rf.SystemFlags,
                GraphicsFlags = rf.GraphicsFlags,
            },
            RpfBinarySource bs => new OldEntry
            {
                Type = "binary",
                Name = name,
                Raw = bs.Content,
                Size = bs.Content.Length,
                UncompressedSize = (uint)bs.Content.Length,
            },
            RpfRawBinarySource rb => new OldEntry
            {
                Type = "binary",
                Name = name,
                Raw = rb.Stored,
                Size = rb.Stored.Length,
                CompressedSize = rb.CompressedSize,
                UncompressedSize = rb.UncompressedSize,
                IsEncrypted = rb.IsEncrypted,
            },
            RpfResourceSource rs => new OldEntry
            {
                Type = "resource",
                Name = name,
                Raw = rs.Stored,
                Size = rs.Stored.Length,
                SizeField = rs.SizeField,
                SystemFlags = rs.SystemFlags,
                GraphicsFlags = rs.GraphicsFlags,
            },
            RpfRawResourceSource rr => new OldEntry
            {
                Type = "resource",
                Name = name,
                Raw = rr.Stored,
                Size = rr.Stored.Length,
                SizeField = rr.SizeField,
                SystemFlags = rr.SystemFlags,
                GraphicsFlags = rr.GraphicsFlags,
            },
            _ => throw new InvalidOperationException(),
        };

    /// <summary>An entry restored from a backup as-is (byte for byte, with its original flags).</summary>
    public static RpfSource Restore(OldEntry e, byte[] raw) =>
        e.Type == "resource"
            ? new RpfRawResourceSource(raw, e.SizeField, e.SystemFlags, e.GraphicsFlags, e.Name)
            : new RpfRawBinarySource(raw, e.CompressedSize, e.UncompressedSize, e.IsEncrypted, e.Name);

    // Fingerprint: content for regular files, raw bytes for resources and files that cannot be decrypted.
    private static string Fingerprint(RpfSource src) =>
        src switch
        {
            RpfArchiveCopySource { File: RpfBinaryFile bf } cs when bf.IsEncrypted == 0 || cs.Archive.CanDecrypt =>
                ContentHash(cs.Archive.ReadContent(bf)),
            RpfArchiveCopySource cs => RawHash(cs.Archive.ReadRaw(cs.File)),
            RpfBinarySource bs => ContentHash(bs.Content),
            RpfRawBinarySource { IsEncrypted: not 0 } rb => RawHash(rb.Stored),
            RpfRawBinarySource { CompressedSize: not 0 } rb => ContentHash(Inflate(rb.Stored, rb.UncompressedSize)),
            RpfRawBinarySource rb => ContentHash(rb.Stored),
            RpfResourceSource rs => RawHash(rs.Stored),
            RpfRawResourceSource rr => RawHash(rr.Stored),
            _ => throw new InvalidOperationException(),
        };

    private static string ContentHash(byte[] b) => "c:" + Convert.ToHexString(SHA256.HashData(b));

    private static string RawHash(byte[] b) => "r:" + Convert.ToHexString(SHA256.HashData(b));

    private static byte[] Inflate(byte[] data, uint size)
    {
        using var d = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        var r = new byte[size];
        d.ReadExactly(r);
        return r;
    }

    public void Dispose()
    {
        foreach (var n in _nested.Values)
            n.Dispose();
        _owned?.Dispose();
    }
}
