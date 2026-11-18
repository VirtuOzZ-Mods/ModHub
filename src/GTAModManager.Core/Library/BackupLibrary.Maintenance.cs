using System.Text.Json;
using GTAModManager.Core.Localization;
using GTAModManager.Core.NaturalVision;

namespace GTAModManager.Core.Library;

public sealed partial class BackupLibrary
{
    // Pre-pack layout: one file per blob and one JSON per install.
    static readonly string[] LegacyDirectories = ["records", "mods", "vanilla"];
    static readonly string[] ManagedDirectories =
    [
        .. LegacyDirectories,
        NveStore.LegacyCacheRootName,
        NveStore.CacheRootName,
    ];
    static readonly string[] ManagedFiles =
    [
        VanillaPackName,
        ModsPackName,
        JournalName,
        VanillaPackName + ".tmp",
        ModsPackName + ".tmp",
        JournalName + ".tmp",
        ContentsName,
        ContentsName + ".tmp",
    ];

    public long PruneUnused()
    {
        using var gate = Game.GameOperationGate.Acquire(Root);
        ValidateStorage();
        return CollectGarbage();
    }

    public void Reset()
    {
        using var gate = Game.GameOperationGate.Acquire(Root);
        ValidateStorage();
        if (Records().Any(r => !r.Undone) || NveStore.UsesLibrary(Root))
            throw new InvalidOperationException(L.T("backup.manage.active"));
        if (
            Directory
                .EnumerateFileSystemEntries(Root)
                .Any(p =>
                {
                    string name = Path.GetFileName(p);
                    // Manual mod copies ("Make backup") and the settings copy are the user's own: allowed here,
                    // never deleted by Reset.
                    return !name.Equals("library.json", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals(Inventory.ModFileBackup.FolderName, StringComparison.OrdinalIgnoreCase)
                        && !name.Equals(SettingsMirror.FolderName, StringComparison.OrdinalIgnoreCase)
                        && !ManagedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)
                        && !ManagedFiles.Contains(name, StringComparer.OrdinalIgnoreCase);
                })
        )
            throw new IOException(L.T("backup.manage.foreign"));
        // Only known, resolved children are deleted. The selected folder itself is retained.
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)) + Path.DirectorySeparatorChar;
        foreach (string name in ManagedDirectories.Concat(ManagedFiles))
        {
            string target = Path.GetFullPath(Path.Combine(Root, name));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException(L.T("backup.manage.foreign"));
            if (Directory.Exists(target))
                Directory.Delete(target, true);
            else if (File.Exists(target))
                File.Delete(target);
        }
        Pack(BackupOrigin.Vanilla).Delete();
        Pack(BackupOrigin.Mods).Delete();
        SaveConfig();
    }

    void ValidateStorage()
    {
        if (TryOpen(Root) is null)
            throw new IOException(L.T("backup.manage.unavailable"));
        for (string? parent = Path.GetFullPath(Root); parent is not null; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException(L.T("backup.manage.links"));
        foreach (string name in ManagedFiles.Append("library.json"))
        {
            string file = Path.Combine(Root, name);
            if (File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException(L.T("backup.manage.links"));
        }
        var pending = new Stack<string>();
        foreach (string name in ManagedDirectories)
            if (Directory.Exists(Path.Combine(Root, name)))
                pending.Push(Path.Combine(Root, name));
        while (pending.TryPop(out string? directory))
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException(L.T("backup.manage.links"));
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(child);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException(L.T("backup.manage.links"));
                if (attributes.HasFlag(FileAttributes.Directory))
                    pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Converts the old layout (records\*\*.json, vanilla\xx\hash, mods\xx\hash) into journal.json and two packs.
    /// Idempotent and crash-safe: packs skip content they already hold, the journal merges by id,
    /// and the old folders are deleted only after everything is written and flushed.
    /// </summary>
    void MigrateLegacyLayout()
    {
        bool LegacyBlobs() => LegacyDirectories.Any(d => Directory.Exists(Path.Combine(Root, d)));
        bool LegacyNaturalVision() => Directory.Exists(Path.Combine(Root, NveStore.LegacyCacheRootName));
        if (!LegacyBlobs() && !LegacyNaturalVision())
        {
            PruneEmptyNaturalVisionFolders();
            return;
        }
        using var gate = Game.GameOperationGate.Acquire(Root);
        if (LegacyNaturalVision())
            NveStore.MigrateCacheFolders(Root);
        PruneEmptyNaturalVisionFolders();
        if (!LegacyBlobs())
            return;

        // 1. Records. Any unreadable record stops the migration: the old layout stays as it is.
        var legacyRecords = new List<BackupRecord>();
        string recordsDir = Path.Combine(Root, "records");
        if (Directory.Exists(recordsDir))
            foreach (string file in Directory.EnumerateFiles(recordsDir, "*.json", SearchOption.AllDirectories))
            {
                BackupRecord? record;
                try
                {
                    record = Transactions.JsonSnapshot.Read<BackupRecord>(file, Json);
                }
                catch (JsonException ex)
                {
                    throw new IOException(L.T("core.nve.corruptNveRecord") + " " + file, ex);
                }
                legacyRecords.Add(record ?? throw new IOException(L.T("core.nve.corruptNveRecord") + " " + file));
            }

        // 2. Blobs. Content whose name does not match its hash was already unusable and is dropped.
        foreach (var origin in new[] { BackupOrigin.Vanilla, BackupOrigin.Mods })
        {
            string dir = Path.Combine(Root, origin == BackupOrigin.Vanilla ? "vanilla" : "mods");
            if (!Directory.Exists(dir))
                continue;
            var known = Pack(BackupOrigin.Vanilla)
                .Hashes()
                .Concat(Pack(BackupOrigin.Mods).Hashes())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = Directory
                .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => (Hash: Path.GetFileName(f).ToUpperInvariant(), Path: f))
                .Where(f => f.Hash.Length == 64 && f.Hash.All(Uri.IsHexDigit) && !known.Contains(f.Hash))
                .Where(f => Transactions.FileTransactionEngine.Hash(f.Path).Equals(f.Hash, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Pack(origin).AppendFiles(files);
        }

        // 3. Journal (merge: a record already in the journal wins over its legacy copy).
        var journal = ReadJournal();
        foreach (var r in legacyRecords)
            if (!journal.Any(x => Same(x, r.Edition, r.Id)))
                journal.Add(r);
        WriteJournal(journal);

        // 4. Only now remove the old folders.
        foreach (string name in LegacyDirectories)
        {
            string dir = Path.Combine(Root, name);
            if (Directory.Exists(dir) && !File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(dir, true);
        }
    }

    /// <summary>Removes NaturalVision cache folders left empty by removed or migrated installs.</summary>
    void PruneEmptyNaturalVisionFolders()
    {
        foreach (string name in new[] { NveStore.LegacyCacheRootName, NveStore.CacheRootName })
        {
            string root = Path.Combine(Root, name);
            if (!Directory.Exists(root) || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
                continue;
            try
            {
                // Deepest first: naturalvision-sources\<scope>\<install>\, NaturalVision\<install>\.
                foreach (
                    string dir in Directory
                        .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                        .OrderByDescending(d => d.Length)
                        .Append(root)
                        .ToArray()
                )
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
