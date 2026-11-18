using System.IO.Compression;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.NaturalVision;

public sealed partial class NveService
{
    // Remove precisely the supplied replacement; archive containers and sibling instructions remain intact.
    static IReadOnlyList<OivOp> WithoutLcppGameconfig(IReadOnlyList<OivOp> ops, string parent = "") =>
        ops.Where(op =>
                op is not OivAdd add
                || !(parent + "\\" + GameInstall.Normalize(add.Target)).Equals(
                    @"update\update.rpf\common\data\gameconfig.xml",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Select(op =>
                op is OivArchive archive
                    ? archive with
                    {
                        Ops = WithoutLcppGameconfig(
                            archive.Ops,
                            parent.Length == 0
                                ? GameInstall.Normalize(archive.Path)
                                : parent + "\\" + GameInstall.Normalize(archive.Path)
                        ),
                    }
                    : op
            )
            .ToArray();

    // Sum requirements per physical volume, including when cache, game and library share a disk.
    static void RequireLcppSpace(params (string Path, long Bytes)[] requirements)
    {
        foreach (
            var group in requirements.GroupBy(
                r => Path.GetPathRoot(Path.GetFullPath(r.Path))!,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            long needed = checked(group.Sum(r => r.Bytes) + (128L << 20));
            if (new DriveInfo(group.Key).AvailableFreeSpace < needed)
                throw new IOException(L.T("lcpp.space", group.Key, BackupLibrary.FormatSize(needed)));
        }
    }

    void PrepareLcpp(NveBundle bundle, NveState state, Action<string>? progress, Action<NveProgress>? componentProgress)
    {
        string cache = NveStore.CacheDirectory(state);
        long staged = state.Components.Sum(c => c.Component.Size);
        RequireLcppSpace((cache, staged));
        try
        {
            // Read each ZIP central directory before any game write: compressed OIV size alone is insufficient.
            var current = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long backups = 0,
                peak = 0,
                payload = 0;
            foreach (var item in state.Components)
            {
                componentProgress?.Invoke(
                    new(
                        NveProgressPhase.Preparing,
                        item.Component.Title,
                        state.Components.IndexOf(item) + 1,
                        state.Components.Count,
                        0
                    )
                );
                progress?.Invoke(L.T("core.nve.preparing", item.Component.Title));
                item.CacheFile = NveStore.CacheFileName(state, item, ".oiv");
                string path = NveStore.Cache(state, item.CacheFile);
                bundle.Copy(item.Component.Source, path);
                if (PatchOnInstall)
                    PatchWorldTravelPatches(path);
                item.CacheHash = FileTransactionEngine.Hash(path);
                using var package = OivPackage.Open(path);
                if (item.Component.Id == "core-1")
                    CachePreservedAdjusters(state, package);
                using var zip = ZipFile.OpenRead(path);
                long unpacked = checked(zip.Entries.Sum(e => e.Length));
                if (unpacked > ModArchive.MaxTotalUnpacked || zip.Entries.Any(e => e.Length > ModArchive.MaxEntry))
                    throw new InvalidDataException(L.T("lcpp.packageTooLarge"));
                payload = checked(payload + unpacked);
                var ops = item.Component.Id == "core-1" ? WithoutLcppGameconfig(package.Ops) : package.Ops;
                long Payload(OivOp op) =>
                    op switch
                    {
                        OivAdd add => package.SourceSize(add.Source),
                        OivArchive archive => archive.Ops.Sum(Payload),
                        OivXml => 1L << 20,
                        OivText => 1L << 20,
                        _ => 0,
                    };
                var growth = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var op in ops)
                {
                    string? target = op switch
                    {
                        OivArchive a => a.Path,
                        OivAdd a => a.Target,
                        OivDelete d => d.Target,
                        OivXml x => x.Path,
                        OivText t => t.Path,
                        _ => null,
                    };
                    if (target is null)
                        continue;
                    string relative = game.MapWriteTarget(target);
                    growth[relative] = checked(growth.GetValueOrDefault(relative) + Payload(op));
                }
                foreach (var change in growth)
                {
                    string relative = change.Key;
                    if (!current.TryGetValue(relative, out long previous))
                    {
                        string file = Path.Combine(game.Root, relative);
                        if (!File.Exists(file) && relative.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase))
                            file = Path.Combine(game.Root, relative[5..]);
                        previous = File.Exists(file) ? new FileInfo(file).Length : 0;
                    }
                    backups = checked(backups + previous);
                    // Conservative upper bound also covers alignment and the live transaction copy.
                    current[relative] = checked(previous + change.Value + (1L << 20));
                    peak = Math.Max(peak, current[relative]);
                }
            }
            if (checked(library.UsedBytes() + backups) > library.QuotaBytes)
                throw new IOException(L.T("lcpp.backupSpace", BackupLibrary.FormatSize(backups)));
            long gameSpace = checked(current.Values.Sum() + peak * 2 + payload);
            RequireLcppSpace((game.Root, gameSpace), (game.WorkDir, peak * 2), (library.Root, backups));
        }
        catch
        {
            CleanCache(state);
            throw;
        }
    }

    void CaptureLcppFiles(NveState state, BackupRecord record)
    {
        foreach (var file in record.Files)
            state.FinalFiles[file.Rel] = file.NewHash;
        foreach (var archive in record.Archives)
            state.FinalFiles[archive.Rel] = archive.PostFileHash;
    }

    void CaptureLcppUndo(NveState state, BackupRecord record)
    {
        // A completed undo journal records the rebuilt RPF hash. Do not trust a current file that another mod may have edited.
        var journal = game.CreateEngine()
            .History()
            .Where(j =>
                j.TargetRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase)
                && j.Title == "Отмена: " + record.Title
                && j.State == TxState.Completed
            )
            .OrderByDescending(j => j.Updated)
            .FirstOrDefault()
            ?? throw new IOException(L.T("core.nve.naturalvisionComponentBackupUnavailable", record.Title));
        foreach (var file in journal.Ops)
            state.FinalFiles[file.Rel] = file.PostHash;
    }

    /// <summary>
    /// Checks that removing these parts cannot overwrite anyone else's work, before any undo.
    /// Loose files must be exactly what LCPP left. A shared archive (update.rpf) may have been edited by later
    /// mods: then only LCPP's own entries inside it are compared, each against the last LCPP part that wrote it.
    /// The shared documents (dlclist.xml, gameconfig.xml) are composed from receipts when LCPP installed them
    /// through compatibility, or merged like NaturalVision does (only LCPP's own pack lines are taken out).
    /// </summary>
    void PreflightLcpp(NveState state, IReadOnlyList<NveInstalledComponent> affected)
    {
        var blobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inner = new Dictionary<string, (string? Fingerprint, NveInstalledComponent Item)>(
            StringComparer.OrdinalIgnoreCase
        );
        var changedArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool managed = affected.Any(c => Record(c).Files.Any(f => ModCompatibility.IsReceipt(f.Rel)));
        foreach (var item in affected)
        {
            var record = Record(item);
            foreach (string relative in record.Files.Select(f => f.Rel))
            {
                if (managed && (ModCompatibility.IsMirror(relative) || OnigiriData(relative)))
                    continue; // composed from receipts; OivInstaller.Undo validates the whole document
                if (
                    !state.FinalFiles.TryGetValue(relative, out var expected)
                    || FileTransactionEngine.HashOrNull(Path.Combine(game.Root, relative)) != expected
                )
                    throw new IOException(L.T("lcpp.conflict", relative));
            }
            foreach (var archive in record.Archives)
            {
                string full = Path.Combine(game.Root, archive.Rel);
                string? current = FileTransactionEngine.HashOrNull(full);
                if (current is null)
                    throw new IOException(L.T("lcpp.conflict", archive.Rel));
                if (!state.FinalFiles.TryGetValue(archive.Rel, out var expected) || current != expected)
                    changedArchives.Add(archive.Rel);
                foreach (var entry in archive.Entries)
                    inner[archive.Rel + "|" + entry.Path] = (entry.NewFingerprint, item);
            }
            foreach (var file in record.Files.Where(f => f.OldBlob is not null))
                blobs.Add(file.OldBlob!);
            foreach (var entry in record.Archives.SelectMany(a => a.Entries).Where(e => e.Old is not null))
                blobs.Add(entry.Old!.Blob);
        }
        foreach (string rel in changedArchives)
        {
            using var archive = ArchiveSession.OpenFile(Path.Combine(game.Root, rel), rel, game.TryLoadKeys());
            foreach (var entry in inner.Where(p => p.Key.StartsWith(rel + "|", StringComparison.OrdinalIgnoreCase)))
            {
                if (managed && ModCompatibility.Documents.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
                    continue;
                // A nested archive is checked through its own entries.
                if (inner.Keys.Any(k => k.StartsWith(entry.Key + "\\", StringComparison.OrdinalIgnoreCase)))
                    continue;
                string path = entry.Key[(rel.Length + 1)..];
                if (
                    archive.Fingerprint(path) != entry.Value.Fingerprint
                    && !CanMergeDlc(entry.Key, entry.Value.Item, archive.ReadFile(path))
                )
                    throw new IOException(L.T("lcpp.conflict", entry.Key.Replace("|", "\\")));
            }
        }
        foreach (string blob in blobs)
            library.VerifyBlob(blob);
    }
}
