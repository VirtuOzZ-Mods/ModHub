using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.NaturalVision;

public sealed class NveInstallRequest
{
    public string Source { get; set; } = "";
    public string? DownloadId { get; set; }
    public string Stamp { get; set; } = "";
    public List<string> Components { get; set; } = [];
}

public sealed class NveRemoveRequest
{
    public string InstallationId { get; set; } = "";
    public List<string>? Components { get; set; }
}

public sealed record NveResult(bool Success, string InstallationId, string Message);

public sealed partial class NveService(GameInstall game, BackupLibrary library, string? stateDirectory = null)
{
    readonly NveStore store = new(game, stateDirectory);

    string Title(NveState state, NveInstalledComponent item) =>
        state.ProductTitle + " · " + item.Component.Title + " [NVE:" + state.Id + ":" + item.Component.Id + "]";

    void Guard()
    {
        if (game.IsRunning())
            throw new InvalidOperationException(L.T("core.nve.closeGtaVBeforeChanging"));
        if (new ModModeService(game).Inspect().WritesBlocked)
            throw new InvalidOperationException(L.T("core.nve.enableModsFirst"));
    }

    public NveResult Install(
        NveInstallRequest request,
        Action<string>? progress = null,
        Action<NveProgress>? componentProgress = null
    )
    {
        using var gate = GameOperationGate.Acquire(game);
        using var backupGate = GameOperationGate.Acquire(library.Root);
        Guard();
        if (request.DownloadId is not null)
        {
            var receipt = new ManagedDownloads().Load(request.DownloadId);
            if (
                !receipt.Completed
                || !receipt.GameRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase)
                || !receipt.File.Equals(Path.GetFullPath(request.Source), StringComparison.OrdinalIgnoreCase)
                || FileTransactionEngine.Hash(request.Source) != receipt.Hash
            )
                throw new IOException(L.T("core.nve.naturalvisionDownloadChangedBelongsAnother"));
        }
        using var bundle = new NveBundle(request.Source);
        var catalog = bundle.Analyze();
        if (store.Active().Any(s => s.Product == catalog.Product))
            throw new InvalidOperationException(
                catalog.IsLcpp
                    ? L.T("lcpp.alreadyInstalled")
                    : L.T("core.nve.naturalvisionAlreadyInstalledItsInstallation")
            );
        if (catalog.Edition != game.Edition)
            throw new InvalidOperationException(L.T("core.nve.archiveIntendedGtaV", catalog.Title, catalog.Edition));
        // Enhanced: NVE needs neither Script Hook V nor a loader installed beforehand. The package's own ASI loader
        // (xinput1_4.dll) is put in first if the game has none (InstallEnhancedLoader), so NVE can go on a clean game.
        if (request.Stamp != catalog.Stamp)
            throw new IOException(L.T("core.nve.archiveChangedAfterComponentsWere"));
        var errors = catalog.Validate(request.Components);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("\n", errors));
        var state = new NveState
        {
            Product = catalog.Product,
            GameRoot = game.Root,
            Edition = game.Edition.ToString(),
            Source = Path.GetFullPath(request.Source),
            BackupRoot = library.Root,
            Components = catalog
                .Components.Where(c => request.Components.Contains(c.Id, StringComparer.Ordinal))
                .Select(c => new NveInstalledComponent { Component = c })
                .ToList(),
        };
        state.CacheFolder = NveStore.AllocateCacheFolder(state);
        if (state.IsLcpp)
            PrepareLcpp(bundle, state, progress, componentProgress);
        else if (catalog.Edition == GameEdition.Enhanced)
            InstallEnhancedLoader(bundle, catalog, state);
        store.Save(state);
        int completed = 0;
        try
        {
            foreach (var item in state.Components)
            {
                int index = state.Components.IndexOf(item) + 1;
                componentProgress?.Invoke(
                    new(NveProgressPhase.Preparing, item.Component.Title, index, state.Components.Count, completed)
                );
                progress?.Invoke(L.T("core.nve.preparing", item.Component.Title));
                if (!state.IsLcpp)
                {
                    string cache = NveStore.CacheFileName(
                        state,
                        item,
                        item.Component.Id is "reshade" or "enhanced-main" ? ".zip" : ".oiv"
                    );
                    item.CacheFile = cache;
                    string path = NveStore.Cache(state, cache);
                    if (item.Component.Id == "enhanced-main")
                        CacheEnhanced(bundle, item.Component.Source, path, false);
                    else if (item.Component.Id == "reshade" && catalog.Edition == GameEdition.Enhanced)
                        CacheEnhanced(bundle, item.Component.Source, path, true);
                    else if (item.Component.Id == "reshade")
                        CacheReShade(bundle, path);
                    else
                        bundle.Copy(item.Component.Source, path);
                    item.CacheHash = FileTransactionEngine.Hash(path);
                }
                store.Save(state);
                componentProgress?.Invoke(
                    new(NveProgressPhase.Installing, item.Component.Title, index, state.Components.Count, completed)
                );
                Apply(state, item, progress, () =>
                    componentProgress?.Invoke(
                        new(NveProgressPhase.Verifying, item.Component.Title, index, state.Components.Count, completed)
                    )
                );
                completed++;
                componentProgress?.Invoke(
                    new(NveProgressPhase.Installed, item.Component.Title, index, state.Components.Count, completed)
                );
                if (item.Component.Core)
                {
                    File.Delete(NveStore.Cache(state, item.CacheFile!));
                    item.CacheFile = null;
                    item.CacheHash = null;
                    store.Save(state);
                }
            }
            state.Phase = "Installed";
            state.PendingComponent = null;
            store.Save(state);
            return new(
                true,
                state.Id,
                state.IsLcpp
                    ? L.T("lcpp.installed")
                    : L.T(
                        "core.nve.installedOptionsSelected",
                        state.ProductTitle,
                        state.Components.Count(c => !c.Component.Core)
                    )
            );
        }
        catch (Exception ex)
        {
            RecoverPending(state);
            state.Phase = "RollingBack";
            state.Message = ex.Message;
            store.Save(state);
            try
            {
                var active = state.Components.Where(c => c.Active).ToArray();
                if (state.IsLcpp)
                    PreflightLcpp(state, active);
                else
                    new OivInstaller(game).PreflightCompatibility();
                RemoveSteps(state, active, progress, componentProgress, rollback: true);
                state.Phase = "Removed";
                state.PendingComponent = null;
                store.Save(state);
                CleanCache(state);
                if (state.IsLcpp)
                    CleanLcppLeftovers(state);
                return new(
                    false,
                    state.Id,
                    state.IsLcpp
                        ? L.T("lcpp.rolledBack", ex.Message)
                        : L.T("core.nve.nveWasNotInstalledChanges", ex.Message)
                );
            }
            catch (Exception rollback)
            {
                state.Phase = "NeedsAttention";
                state.Message = L.T("core.nve.rollbackIncomplete", ex.Message, rollback.Message);
                store.Save(state);
                return new(false, state.Id, state.Message);
            }
        }
    }

    public NveResult Remove(
        NveRemoveRequest request,
        Action<string>? progress = null,
        Action<NveProgress>? componentProgress = null
    )
    {
        using var gate = GameOperationGate.Acquire(game);
        var state = store.Load(request.InstallationId);
        if (state.Phase == "Removed" || VanillaModCleanup.TryComplete(game, library, Path.GetDirectoryName(store.Root)))
            return new(true, state.Id, L.T("suite.alreadyRemoved"));
        Guard();
        using var backupGate = GameOperationGate.Acquire(library.Root);
        if (!Path.GetFullPath(state.BackupRoot).Equals(library.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("core.nve.originalNveBackupLibraryRequired"));
        RecoverPending(state);
        var active = state.Components.Where(c => c.Active).ToArray();
        bool all = request.Components is null;
        var wanted = all
            ? active
            : active.Where(c => request.Components!.Contains(c.Component.Id, StringComparer.Ordinal)).ToArray();
        if (wanted.Length == 0)
            throw new InvalidOperationException(L.T("core.nve.noInstalledComponentsSelected"));
        if (
            !all
            && (
                wanted.Any(c => c.Component.Core)
                || request.Components!.Any(id => !active.Any(c => c.Component.Id == id))
            )
        )
            throw new InvalidOperationException(L.T("core.nve.threeMainPartsCanOnly"));
        int first = Array.FindIndex(active, c => wanted.Contains(c));
        var tail = active.Skip(first).ToArray();
        var replay = all ? [] : tail.Where(c => !wanted.Contains(c)).ToArray();
        foreach (var item in replay)
            CheckCache(state, item);
        // Removal takes out exactly what these parts installed, whatever happened to those files afterwards
        // (hand edits, other mods): each file and archive entry goes back to its pre-install state, everything
        // else stays. Only the backups of the originals must be intact, or the originals could not come back.
        VerifyBackups(tail);
        state.Phase = "Removing";
        store.Save(state);
        try
        {
            RemoveSteps(state, tail, progress, componentProgress, force: true);
            foreach (var item in replay)
            {
                int index = state.Components.IndexOf(item) + 1;
                int completed = state.Components.Count(c => c.Active);
                componentProgress?.Invoke(
                    new(NveProgressPhase.Installing, item.Component.Title, index, state.Components.Count, completed)
                );
                Apply(state, item, progress, () =>
                    componentProgress?.Invoke(
                        new(NveProgressPhase.Verifying, item.Component.Title, index, state.Components.Count, completed)
                    )
                );
                componentProgress?.Invoke(
                    new(
                        NveProgressPhase.Installed,
                        item.Component.Title,
                        index,
                        state.Components.Count,
                        state.Components.Count(c => c.Active)
                    )
                );
            }

            state.PendingComponent = null;
            state.Phase = state.Components.Any(c => c.Active) ? "Installed" : "Removed";
            state.Message = "";
            store.Save(state);
            if (state.Phase == "Removed")
            {
                CleanCache(state);
                if (state.IsLcpp)
                    CleanLcppLeftovers(state);
            }
            return new(
                true,
                state.Id,
                all
                    ? L.T("core.nve.removedPreviousFileStateRestored", state.ProductTitle)
                    : L.T("core.nve.selectedOptionsRemovedOtherNaturalvision")
            );
        }
        catch (Exception ex)
        {
            RecoverPending(state);
            state.Phase = "NeedsAttention";
            state.Message = L.T("core.nve.operationIncomplete", ex.Message);
            store.Save(state);
            return new(false, state.Id, state.Message);
        }
    }

    void Apply(NveState state, NveInstalledComponent item, Action<string>? progress, Action? verifying = null)
    {
        CheckCache(state, item);
        state.PendingComponent = item.Component.Id;
        store.Save(state);
        progress?.Invoke(L.T("core.nve.installing", item.Component.Title));
        string file = NveStore.Cache(state, item.CacheFile!);
        InstallPlan plan;
        if (item.Component.Id is "reshade" or "enhanced-main")
        {
            using var archive = ModArchive.Open(file);
            var ops = archive
                .Files.Where(f =>
                    item.Component.Id == "enhanced-main"
                    || AllowedReShade(f)
                    || game.Edition == GameEdition.Enhanced && NveBundle.EnhancedShader(f)
                )
                .Select(f => (OivOp)new OivAdd(f, f))
                .ToArray();
            plan = new OivInstaller(game).Prepare(
                Title(state, item),
                ops,
                archive,
                naturalVision: !state.IsLcpp,
                allowGameRootFiles: true
            );
        }
        else
        {
            using var package = OivPackage.Open(file);
            var ops = state.IsLcpp && item.Component.Id == "core-1" ? WithoutLcppGameconfig(package.Ops) : package.Ops;
            if (state.IsLcpp && item.Component.Id != LcppAdjusterComponent)
                ops = ops.Where(op =>
                        op is not OivAdd add
                        || !state.PreservedAdjusters.Contains(
                            GameInstall.Normalize(add.Target),
                            StringComparer.OrdinalIgnoreCase
                        )
                    )
                    .ToArray();
            plan = new OivInstaller(game).Prepare(
                Title(state, item),
                item.Component.Id == LcppAdjusterComponent ? ops : Filter(ops),
                package,
                naturalVision: !state.IsLcpp,
                // Graphics suites may replace loose game files (upscaler DLLs); undo restores the originals.
                allowGameRootFiles: true
            );
        }
        using (plan)
        {
            if (!plan.CanInstall)
                throw new InvalidOperationException(
                    string.Join("\n", plan.Errors.DefaultIfEmpty(L.T("core.nve.packageContainsNoChanges")))
                );
            item.DlcBefore.Clear();
            item.DlcAfter.Clear();
            item.LogicalRpf.Clear();
            foreach (var a in plan.ArchiveBackups.Values)
            foreach (
                var e in a.Entries.Where(e =>
                    e.Path.EndsWith("dlclist.xml", StringComparison.OrdinalIgnoreCase) && e.Old is not null
                )
            )
            {
                if (e.Old!.Type == "binary" && e.Old.IsEncrypted == 0 && e.Old.Raw is { } raw)
                    item.DlcBefore[a.Rel + "|" + e.Path] = Decode(e.Old, raw);
            }
            var result = new OivInstaller(game).Install(plan, library);
            if (result.State != TxState.Completed)
                throw new InvalidOperationException(result.Message);
            item.TransactionId = result.Id;
            item.Active = true;
            state.PendingComponent = null;
            var record = Record(item);
            if (state.IsLcpp)
                CaptureLcppFiles(state, record);
            store.Save(state);
            verifying?.Invoke();
            foreach (var key in item.DlcBefore.Keys)
                try
                {
                    var parts = key.Split('|');
                    using var archive = ArchiveSession.OpenFile(
                        Path.Combine(game.Root, parts[0]),
                        parts[0],
                        game.TryLoadKeys()
                    );
                    item.DlcAfter[key] = Encoding.UTF8.GetString(archive.ReadFile(parts[1])!);
                }
                catch (Exception ex) when (ex is IOException or OivFormatException)
                {
                    throw new IOException(L.T("core.nve.couldNotVerifyNveDlc"), ex);
                }
            var owned = state
                .Components.Where(c => c.Active && c.TransactionId is not null)
                .SelectMany(c =>
                    Record(c)
                        .Files.Concat(
                            state.IsLcpp
                                ? Record(c)
                                    .Archives.Where(a => a.Created)
                                    .Select(a => new FileBackup { Rel = a.Rel, NewHash = a.PostFileHash })
                                : []
                        )
                )
                .Where(f => f.NewHash is not null && f.Rel.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Rel)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (
                var rel in record
                    .Files.Select(f => f.Rel)
                    .Concat(record.Archives.Select(a => a.Rel))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(rel => game.Edition == GameEdition.Legacy && owned.Contains(rel))
            )
            {
                progress?.Invoke(L.T("core.nve.resourceVerification", rel));
                item.LogicalRpf[rel] = NveRpfFingerprint.FileHash(Path.Combine(game.Root, rel), game.TryLoadKeys());
            }
            if (state.IsLcpp)
                CaptureLcppFiles(state, record);
            store.Save(state);
        }
    }

    IReadOnlyList<OivOp> Filter(IReadOnlyList<OivOp> input)
    {
        var output = new List<OivOp>();
        var status = ModdingBase.Inspect(game);
        foreach (var op in input)
        {
            if (op is OivAdd add)
            {
                string target = GameInstall.Normalize(add.Target);
                if (
                    new[] { ".bat", ".cmd", ".ps1", ".exe", ".lnk" }.Contains(
                        Path.GetExtension(target),
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                    continue;
                if (
                    !target.Contains('\\')
                    && (
                        target.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase) && status.AsiLoader
                        || target.Equals("GTAV.HeapAdjuster.asi", StringComparison.OrdinalIgnoreCase)
                            && (AdjusterComponents.Present(game, "heap") || LimitSetup.HasResourceExtensions(game))
                    )
                )
                    continue;
            }
            output.Add(op);
        }
        return output;
    }

    BackupRecord Record(NveInstalledComponent item) =>
        library.LoadRecord(game.Edition.ToString(), item.TransactionId!)
        ?? throw new IOException(L.T("core.nve.naturalvisionComponentBackupUnavailable", item.Component.Title));

    void CheckCache(NveState state, NveInstalledComponent item)
    {
        if (item.CacheFile is null || item.CacheHash is null)
            throw new IOException(L.T("core.nve.sourceOptionPackageUnavailable", item.Component.Title));
        string path = NveStore.Cache(state, item.CacheFile);
        if (!File.Exists(path) || FileTransactionEngine.Hash(path) != item.CacheHash)
            throw new IOException(L.T("core.nve.optionPackageBackupDamaged", item.Component.Title));
    }

    void RecoverPending(NveState state)
    {
        foreach (var completed in state.Components.Where(c => c.Active && c.TransactionId is not null))
            if (library.LoadRecord(game.Edition.ToString(), completed.TransactionId!) is { Undone: true } undone)
            {
                if (state.IsLcpp)
                    CaptureLcppUndo(state, undone);
                completed.Active = false;
            }
        if (state.PendingComponent is null)
        {
            store.Save(state);
            return;
        }
        var item = state.Components.Single(c => c.Component.Id == state.PendingComponent);
        var journal = game.CreateEngine()
            .History()
            .FirstOrDefault(j =>
                j.TargetRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase)
                && j.Title == Title(state, item)
                && j.State == TxState.Completed
                && library.LoadRecord(game.Edition.ToString(), j.Id)?.Undone == false
            );
        if (journal is not null)
        {
            item.TransactionId = journal.Id;
            item.Active = true;
            if (state.IsLcpp)
                CaptureLcppFiles(state, Record(item));
        }
        state.PendingComponent = null;
        store.Save(state);
    }

    void RemoveSteps(
        NveState state,
        IReadOnlyList<NveInstalledComponent> components,
        Action<string>? progress,
        Action<NveProgress>? componentProgress = null,
        bool rollback = false,
        bool force = false
    )
    {
        foreach (var item in components.Reverse())
        {
            var phase = rollback ? NveProgressPhase.RollingBack : NveProgressPhase.Removing;
            int index = state.Components.IndexOf(item) + 1;
            componentProgress?.Invoke(
                new(phase, item.Component.Title, index, state.Components.Count, state.Components.Count(c => c.Active))
            );
            progress?.Invoke(L.T("core.nve.recovery", item.Component.Title));
            var record = Record(item);
            if (record.Undone)
            {
                if (state.IsLcpp)
                    CaptureLcppUndo(state, record);
                item.Active = false;
                store.Save(state);
                continue;
            }
            foreach (
                var f in record.Files.Where(f =>
                    f.NewHash is not null && f.Rel.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                string full = Path.Combine(game.Root, f.Rel);
                string? current = FileTransactionEngine.HashOrNull(full);
                if (
                    current != f.NewHash
                    && item.LogicalRpf.TryGetValue(f.Rel, out var expected)
                    && current is not null
                    && NveRpfFingerprint.FileHash(full, game.TryLoadKeys()) == expected
                )
                    f.NewHash = current;
            }
            // onigiri\common\data\dlclist.xml / gameconfig.xml get extended by mod installs (pack registration, pools) and are removed together with NVE.
            foreach (var f in record.Files.Where(f => f.NewHash is not null && OnigiriData(f.Rel)))
            {
                string? current = FileTransactionEngine.HashOrNull(Path.Combine(game.Root, f.Rel));
                if (current is not null)
                    f.NewHash = current;
            }
            if (state.IsLcpp)
            {
                foreach (var archive in record.Archives.Where(a => a.Created))
                {
                    string full = Path.Combine(game.Root, archive.Rel);
                    if (
                        item.LogicalRpf.TryGetValue(archive.Rel, out var expected)
                        && NveRpfFingerprint.FileHash(full, game.TryLoadKeys()) == expected
                    )
                        archive.PostFileHash = FileTransactionEngine.Hash(full);
                }
            }
            // Both products keep a later mod's dlclist lines: only their own pack lines are taken out.
            if (!record.Files.Any(f => ModCompatibility.IsReceipt(f.Rel)))
                try
                {
                    RebaseDlc(record, item);
                }
                catch (Exception ex) when (force && ex is IOException or System.Xml.XmlException)
                {
                    // The list was rewritten beyond a merge: forced undo restores the pre-install list.
                }
            library.SaveRecord(record);
            var result = new OivInstaller(game).Undo(item.TransactionId!, library, force);
            if (result.State != TxState.Undone)
                throw new IOException(result.Message);
            if (state.IsLcpp)
                CaptureLcppUndo(state, record);
            item.Active = false;
            store.Save(state);
            componentProgress?.Invoke(
                new(phase, item.Component.Title, index, state.Components.Count, state.Components.Count(c => c.Active))
            );
        }
    }

    /// <summary>The originals these parts replaced must still be in the backup library.</summary>
    void VerifyBackups(IReadOnlyList<NveInstalledComponent> affected)
    {
        var blobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in affected)
        {
            var record = Record(item);
            if (record.Undone)
                continue;
            foreach (var file in record.Files.Where(f => f.OldBlob is not null))
                blobs.Add(file.OldBlob!);
            foreach (var entry in record.Archives.SelectMany(a => a.Entries).Where(e => e.Old is not null))
                blobs.Add(entry.Old!.Blob);
        }
        foreach (string blob in blobs)
            library.VerifyBlob(blob);
    }

    static bool OnigiriData(string rel) =>
        rel.Equals(@"onigiri\common\data\dlclist.xml", StringComparison.OrdinalIgnoreCase)
        || rel.Equals(@"onigiri\common\data\gameconfig.xml", StringComparison.OrdinalIgnoreCase);

    static bool CanMergeDlc(string key, NveInstalledComponent item, byte[]? current)
    {
        if (
            !key.EndsWith("dlclist.xml", StringComparison.OrdinalIgnoreCase)
            || !item.DlcBefore.ContainsKey(key)
            || !item.DlcAfter.ContainsKey(key)
            || current is null
        )
            return false;
        return InstallDocuments.Kind(Encoding.UTF8.GetString(current)) == XmlDocumentKind.DlcList;
    }

    void RebaseDlc(BackupRecord record, NveInstalledComponent item)
    {
        foreach (var archive in record.Archives)
        {
            using var current = ArchiveSession.OpenFile(
                Path.Combine(game.Root, archive.Rel),
                archive.Rel,
                game.TryLoadKeys()
            );
            for (int i = 0; i < archive.Entries.Count; i++)
            {
                var e = archive.Entries[i];
                string key = archive.Rel + "|" + e.Path;
                var now = e.Path.EndsWith("dlclist.xml", StringComparison.OrdinalIgnoreCase)
                    ? current.ReadFile(e.Path)
                    : null;
                if (now is null || current.Fingerprint(e.Path) == e.NewFingerprint || !CanMergeDlc(key, item, now))
                    continue;
                string before = item.DlcBefore[key],
                    after = item.DlcAfter[key],
                    text = Encoding.UTF8.GetString(now);
                var diff = InstallDocuments.CompareDlc(before, after);
                if (diff.Removed.Count > 0)
                    throw new IOException(L.T("core.nve.packageReplacedEntireDlcList"));
                var doc = InstallDocuments.Parse(text);
                var added = diff.Added.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var node in doc.Root!.Element("Paths")!.Elements("Item").ToArray())
                {
                    string name = node.Value.Trim().Replace('\\', '/');
                    if (name.StartsWith("dlcpacks:/", StringComparison.OrdinalIgnoreCase))
                        name = name[10..].Trim('/');
                    if (added.Contains(name))
                        node.Remove();
                }
                byte[] bytes = Encoding.UTF8.GetBytes(doc.ToString());
                var old = new OldEntry
                {
                    Type = "binary",
                    Name = Path.GetFileName(e.Path),
                    Size = bytes.Length,
                    UncompressedSize = (uint)bytes.Length,
                    CompressedSize = 0,
                    IsEncrypted = 0,
                    Blob = library.PutBlob(bytes, BackupOrigin.Mods),
                };
                archive.Entries[i] = new InnerChange
                {
                    Path = e.Path,
                    Kind = InnerKind.Replaced,
                    NewFingerprint = current.Fingerprint(e.Path),
                    Old = old,
                };
            }
        }
    }

    static string Decode(OldEntry entry, byte[] raw)
    {
        if (entry.UncompressedSize > 8L << 20)
            throw new InvalidDataException(L.T("core.nve.dlcListTooLarge"));
        if (entry.CompressedSize == 0)
            return Encoding.UTF8.GetString(raw);
        using var input = new DeflateStream(new MemoryStream(raw, false), CompressionMode.Decompress);
        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }

    public static bool AllowedReShade(string path) =>
        !Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
        && (
            path.Equals("dxgi.asi", StringComparison.OrdinalIgnoreCase)
            || path.Equals("NVE.ini", StringComparison.OrdinalIgnoreCase)
            || path.Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase)
            || path.Equals("LICENSE.md", StringComparison.OrdinalIgnoreCase)
            || path.Equals("ReshadeEffectShaderToggler.addon64", StringComparison.OrdinalIgnoreCase)
            || path.Equals("ReshadeEffectShaderToggler.ini", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"reshade-shaders\", StringComparison.OrdinalIgnoreCase)
                && new[] { ".fx", ".fxh", ".pfx", ".png", ".dds", ".jpg", ".jpeg", ".bmp", ".txt" }.Contains(
                    Path.GetExtension(path),
                    StringComparer.OrdinalIgnoreCase
                )
        );

    /// <summary>
    /// GTA5_Enhanced.exe never loads dinput8.dll: without the package's xinput1_4.dll loader neither NVE nor any other
    /// .asi runs. Installed as its own transaction before NVE (kept when NVE is removed); skipped if a loader works already.
    /// </summary>
    void InstallEnhancedLoader(NveBundle bundle, NveCatalog catalog, NveState state)
    {
        if (ModdingBase.Inspect(game).AsiLoader)
            return;
        var main = catalog.Components.FirstOrDefault(c => c.Id == "enhanced-main");
        var file = main is null
            ? null
            : bundle.Files.FirstOrDefault(f =>
                f.Equals(main.Source + ModdingBase.EnhancedLoaderName, StringComparison.OrdinalIgnoreCase)
            );
        if (file is null)
            throw new InvalidOperationException(L.T("core.nve.enhancedLoaderMissing"));
        var copy = NveStore.Cache(state, "asi-loader-" + ModdingBase.EnhancedLoaderName);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        using (var source = bundle.Open(file))
        using (var output = File.Create(copy))
            source.CopyTo(output);
        using var plan = ModdingBase.PrepareEnhancedLoader(game, copy);
        if (!plan.CanInstall)
            throw new InvalidOperationException(string.Join("\n", plan.Errors));
        var result = new OivInstaller(game).Install(plan, library);
        if (result.State != TxState.Completed)
            throw new InvalidOperationException(result.Message);
    }

    static void CacheEnhanced(NveBundle bundle, string prefix, string target, bool shaders)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        long total = bundle
            .Files.Where(f =>
                f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && NveBundle.EnhancedShader(f[prefix.Length..]) == shaders
            )
            .Sum(bundle.Size);
        if (new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace < total + (256L << 20))
            throw new IOException(L.T("core.nve.notEnoughSpacePrepareNaturalvision"));
        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var file in bundle.Files.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            string relative = file[prefix.Length..];
            if (NveBundle.EnhancedShader(relative) != shaders)
                continue;
            // The package's xinput1_4.dll (the Enhanced ASI loader) is installed on its own, before NVE
            // (InstallEnhancedLoader), so that removing NVE keeps it. Launchers and scripts are never taken.
            if (
                relative.Equals(ModdingBase.EnhancedLoaderName, StringComparison.OrdinalIgnoreCase)
                || new[] { ".exe", ".bat", ".cmd", ".ps1", ".lnk" }.Contains(
                    Path.GetExtension(relative),
                    StringComparer.OrdinalIgnoreCase
                )
            )
                continue;
            if (relative.Equals("LICENSE", StringComparison.OrdinalIgnoreCase))
                relative = @"ModHubCredits\NaturalVisionEnhanced\LICENSE";
            using var source = bundle.Open(file);
            using var output = zip.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.NoCompression).Open();
            source.CopyTo(output);
        }
    }

    static void CacheReShade(NveBundle bundle, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (
            var path in bundle.Files.Where(f => f.StartsWith(@"ReShade Shaders\", StringComparison.OrdinalIgnoreCase))
        )
        {
            string relative = path[@"ReShade Shaders\".Length..];
            if (!AllowedReShade(relative))
                continue;
            if (bundle.Size(path) > 256L << 20)
                throw new InvalidDataException(L.T("core.nve.individualReshadeFileTooLarge"));
            using var source = bundle.Open(path);
            using var output = zip.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Fastest).Open();
            source.CopyTo(output);
        }
    }

    static void CleanCache(NveState state)
    {
        if (state.PreservedAdjusterHash is not null)
            File.Delete(NveStore.Cache(state, LcppAdjusterCache));
        foreach (var item in state.Components.Where(c => c.CacheFile is not null))
        {
            string file = NveStore.Cache(state, item.CacheFile!);
            if (File.Exists(file))
                File.Delete(file);
        }
        // Do not leave an empty per-install folder behind in the backup directory.
        string directory = NveStore.CacheDirectory(state);
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }
}
