using System.Text.Json;
using System.Text.Json.Serialization;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Library;

/// <summary>Previous state of one file inside an RPF (for targeted rollback).</summary>
public sealed class OldEntry
{
    public required string Type { get; init; } // "binary" | "resource"
    public required string Name { get; init; }
    public string Blob { get; set; } = "";

    /// <summary>Origin of the previous content: an original game file (vanilla) or a mod file (mods).</summary>
    public BackupOrigin Origin { get; set; } = BackupOrigin.Mods;
    public long Size { get; init; }
    public uint CompressedSize { get; init; }
    public uint UncompressedSize { get; init; }
    public uint IsEncrypted { get; init; }
    public uint SizeField { get; init; }
    public uint SystemFlags { get; init; }
    public uint GraphicsFlags { get; init; }

    /// <summary>Raw bytes before they are stored in the library (not serialized to JSON).</summary>
    [JsonIgnore]
    public byte[]? Raw { get; set; }
}

public enum InnerKind
{
    Added,
    Replaced,
    Deleted,
}

/// <summary>Vanilla game files are stored separately from mod files (vanilla\ and mods\ folders).</summary>
public enum BackupOrigin
{
    Vanilla,
    Mods,
}

/// <summary>A change to a file inside an archive. Path may go through nested RPFs.</summary>
public sealed class InnerChange
{
    public required string Path { get; init; }
    public InnerKind Kind { get; set; }

    /// <summary>Fingerprint of the new content (null if the file was deleted).</summary>
    public string? NewFingerprint { get; set; }
    public OldEntry? Old { get; init; }
}

public sealed class ArchiveBackup
{
    public required string Rel { get; init; }

    /// <summary>The archive in mods\ did not exist before installation (created empty or as a copy of the original).</summary>
    public bool Created { get; init; }
    public string? PostFileHash { get; set; }
    public List<InnerChange> Entries { get; init; } = [];
}

public sealed class FileBackup
{
    public required string Rel { get; init; }

    /// <summary>Previous file in the library (null if there was no file).</summary>
    public string? OldBlob { get; set; }
    public BackupOrigin Origin { get; set; } = BackupOrigin.Mods;

    /// <summary>File hash after installation (null if the file was deleted).</summary>
    public string? NewHash { get; set; }
    public long OldSize { get; init; }

    /// <summary>
    /// A copy derived from mods\ content (onigiri layer): undo recomputes it from what is left in mods\ instead of
    /// restoring the old bytes, so later installations keep their part.
    /// </summary>
    public bool Derived { get; set; }
}

public sealed class BackupRecord
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Edition { get; init; }
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
    public bool Undone { get; set; }
    public List<FileBackup> Files { get; init; } = [];
    public List<ArchiveBackup> Archives { get; init; } = [];

    public long BackupBytes(BackupLibrary lib) =>
        Files.Where(f => f.OldBlob is not null).Sum(f => lib.BlobSize(f.OldBlob!))
        + Archives.SelectMany(a => a.Entries).Where(e => e.Old is not null).Sum(e => lib.BlobSize(e.Old!.Blob));
}

/// <summary>
/// Backup folder: only changed files (whole files and individual files inside RPFs),
/// stored by SHA-256 (identical content stored once) in two pack files, plus one journal of installs.
/// Old backups are never deleted automatically: only content no active install references is freed.
/// </summary>
public sealed partial class BackupLibrary
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class Config
    {
        public long QuotaBytes { get; set; }
        public int Version { get; set; } = 1;
    }

    public string Root { get; }
    public long QuotaBytes { get; private set; }

    internal const string VanillaPackName = "vanilla.pack";
    internal const string ModsPackName = "mods.pack";
    internal const string JournalName = "journal.json";

    private BackupLibrary(string root, long quota)
    {
        Root = root;
        QuotaBytes = quota;
    }

    public static BackupLibrary Create(string root, long quotaBytes)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var lib = new BackupLibrary(root, quotaBytes);
        lib.SaveConfig();
        lib.Prepare();
        return lib;
    }

    /// <summary>Brings an older folder layout up to date and makes sure README.txt describes the folder.</summary>
    private void Prepare()
    {
        MigrateLegacyLayout();
        if (!File.Exists(Path.Combine(Root, ContentsName)) && File.Exists(JournalPath))
            WriteContents(ReadJournal());
    }

    public static BackupLibrary? TryOpen(string root)
    {
        var cfg = Path.Combine(root, "library.json");
        if (!File.Exists(cfg))
            return null;
        BackupLibrary lib;
        try
        {
            var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(cfg))!;
            lib = new BackupLibrary(Path.GetFullPath(root), c.QuotaBytes);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
        // Outside the catch: a failed migration must surface, not look like "no backups configured".
        lib.Prepare();
        return lib;
    }

    public void SetQuota(long bytes)
    {
        QuotaBytes = bytes;
        SaveConfig();
    }

    private void SaveConfig() =>
        AtomicWrite(
            Path.Combine(Root, "library.json"),
            JsonSerializer.SerializeToUtf8Bytes(new Config { QuotaBytes = QuotaBytes }, Json)
        );

    // ---------- Blobs: vanilla.pack (game originals) and mods.pack (modded states) ----------

    private BlobPack Pack(BackupOrigin o) =>
        new(Path.Combine(Root, o == BackupOrigin.Vanilla ? VanillaPackName : ModsPackName));

    private BlobPack? PackOf(string hash)
    {
        var v = Pack(BackupOrigin.Vanilla);
        if (v.Contains(hash))
            return v;
        var m = Pack(BackupOrigin.Mods);
        return m.Contains(hash) ? m : null;
    }

    public long BlobSize(string hash) => PackOf(hash)?.Size(hash) ?? 0;

    public bool HasBlob(string hash) => PackOf(hash) is not null;

    public void VerifyBlob(string hash)
    {
        if (PackOf(hash) is not { } pack || BlobPack.HashOf(pack.Read(hash)) != hash)
            throw new InvalidDataException(L.T("core.library.backupCorrupted", hash[..12]));
    }

    public string PutBlob(byte[] data, BackupOrigin origin)
    {
        var hash = BlobPack.HashOf(data);
        using var gate = Game.GameOperationGate.Acquire(Root);
        if (!HasBlob(hash))
            Pack(origin).Append(hash, new MemoryStream(data, writable: false), data.Length);
        return hash;
    }

    public string PutBlobFile(string source, BackupOrigin origin)
    {
        var hash = Transactions.FileTransactionEngine.Hash(source);
        using var gate = Game.GameOperationGate.Acquire(Root);
        if (!HasBlob(hash))
        {
            using var fs = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            Pack(origin).Append(hash, fs, fs.Length);
        }
        return hash;
    }

    public byte[] ReadBlob(string hash)
    {
        var data = PackOf(hash)?.Read(hash)
            ?? throw new InvalidDataException(L.T("core.library.backupCorrupted", hash[..12]));
        if (BlobPack.HashOf(data) != hash)
            throw new InvalidDataException(L.T("core.library.backupCorrupted", hash[..12]));
        return data;
    }

    public void CopyBlobTo(string hash, string target)
    {
        var pack = PackOf(hash) ?? throw new InvalidDataException(L.T("core.library.backupCorrupted", hash[..12]));
        using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            pack.CopyTo(hash, fs);
        if (Transactions.FileTransactionEngine.Hash(target) != hash)
            throw new InvalidDataException(L.T("core.library.backupCorrupted", hash[..12]));
    }

    public long UsedBytes() => UsedBytes(BackupOrigin.Vanilla) + UsedBytes(BackupOrigin.Mods);

    public long UsedBytes(BackupOrigin origin) => Pack(origin).FileLength;

    // ---------- Records: journal.json ----------

    private sealed class JournalFile
    {
        public int Version { get; set; } = 1;
        public List<BackupRecord> Records { get; set; } = [];
    }

    private string JournalPath => Path.Combine(Root, JournalName);

    /// <summary>Reads the whole journal. A damaged journal throws: it must never be silently overwritten.</summary>
    private List<BackupRecord> ReadJournal()
    {
        try
        {
            if (!File.Exists(JournalPath))
                return [];
            var journal = Transactions.JsonSnapshot.Read<JournalFile>(JournalPath, Json);
            if (journal?.Records is null || journal.Records.Any(r => r is null))
                throw new IOException(L.T("core.nve.corruptNveRecord"));
            return journal.Records;
        }
        catch (JsonException ex)
        {
            throw new IOException(L.T("core.nve.corruptNveRecord"), ex);
        }
    }

    private void WriteJournal(List<BackupRecord> records)
    {
        AtomicWrite(
            JournalPath,
            JsonSerializer.SerializeToUtf8Bytes(
                new JournalFile { Records = records.OrderBy(r => r.Edition).ThenBy(r => r.Id).ToList() },
                Json
            )
        );
        WriteContents(records);
    }

    private static bool Same(BackupRecord a, string edition, string id) =>
        a.Id == id && a.Edition.Equals(edition, StringComparison.OrdinalIgnoreCase);

    public void SaveRecord(BackupRecord r)
    {
        using var gate = Game.GameOperationGate.Acquire(Root);
        var all = ReadJournal();
        all.RemoveAll(x => Same(x, r.Edition, r.Id));
        all.Add(r);
        WriteJournal(all);
    }

    public BackupRecord? LoadRecord(string edition, string id) =>
        ReadJournal().FirstOrDefault(r => Same(r, edition, id));

    public IReadOnlyList<BackupRecord> Records(string? edition = null) =>
        ReadJournal()
            .Where(r => edition is null || r.Edition.Equals(edition, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Id)
            .ToList();

    /// <summary>Deletes all install records for one game version (for Clear History). CollectGarbage frees the blobs.</summary>
    public int DeleteRecords(string edition, IReadOnlySet<string>? preserve = null)
    {
        using var gate = Game.GameOperationGate.Acquire(Root);
        var all = ReadJournal();
        int n = all.RemoveAll(r =>
            r.Edition.Equals(edition, StringComparison.OrdinalIgnoreCase) && preserve?.Contains(r.Id) != true
        );
        if (n > 0)
            WriteJournal(all);
        return n;
    }

    /// <summary>Drops blobs not referenced by any active install (rewrites the packs). Returns bytes freed.</summary>
    public long CollectGarbage()
    {
        using var gate = Game.GameOperationGate.Acquire(Root);
        var live = ReadJournal()
            .Where(r => !r.Undone)
            .SelectMany(r =>
                r.Files.Select(f => f.OldBlob).Concat(r.Archives.SelectMany(a => a.Entries).Select(e => e.Old?.Blob))
            )
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        long freed = Pack(BackupOrigin.Vanilla).Compact(live) + Pack(BackupOrigin.Mods).Compact(live);
        WriteContents(ReadJournal());
        return freed;
    }

    private static void AtomicWrite(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static string FormatSize(long bytes) =>
        bytes switch
        {
            >= 1L << 30 => L.T("core.library.sizeGigabytes", bytes / (double)(1L << 30)),
            >= 1L << 20 => L.T("core.library.sizeMegabytes", bytes / (double)(1L << 20)),
            >= 1L << 10 => L.T("core.library.sizeKilobytes", bytes / 1024.0),
            _ => L.T("core.library.sizeBytes", bytes),
        };

    /// <summary>"50 GB", "100MB", "25 gb" (Russian unit names too) → bytes.</summary>
    public static long? ParseSize(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            text.Trim().ToLowerInvariant().Replace(',', '.'),
            "^(\\d+(?:\\.\\d+)?)\\s*(gb|гб|g|г|mb|мб|m|м|tb|тб)?$"
        );
        if (!m.Success)
            return null;
        var v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = m.Groups[2].Value;
        double mult =
            unit is "tb" or "тб" ? 1L << 40
            : unit is "mb" or "мб" or "m" or "м" ? 1L << 20
            : 1L << 30;
        return (long)(v * mult);
    }
}
