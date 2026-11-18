using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Oiv;

public sealed record PlanItem(string Action, string Target, string Detail);

public sealed class InstallPlan : IDisposable
{
    public required string Title { get; init; }
    public required string PrepDir { get; init; }
    public List<PlanItem> Items { get; } = [];
    public List<string> Log { get; } = [];
    public List<string> Errors { get; } = [];
    public List<FileChange> Changes { get; } = [];

    /// <summary>Preview: files are not extracted and archives are not built; such a plan cannot be installed.</summary>
    public bool Preview { get; init; }
    public bool CanInstall => Errors.Count == 0 && Changes.Count > 0 && !Preview;
    public const string PreviewSource = "<preview>";

    /// <summary>Targeted changes inside archives (for the backup).</summary>
    public Dictionary<string, Library.ArchiveBackup> ArchiveBackups { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How much space the backup will take (before deduplication).</summary>
    public long BackupEstimate { get; set; }

    /// <summary>Files recomputed from mods\ on every change rather than owned by this installation (onigiri layer).</summary>
    public HashSet<string> DerivedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(PrepDir))
                Directory.Delete(PrepDir, true);
        }
        catch (IOException) { }
    }
}

/// <summary>
/// OIV interpreter: translates package commands into a plan of file changes.
/// Writes nothing to the game until Install is called; prepared archives live in WorkDir on the same volume.
/// </summary>
public sealed partial class OivInstaller(GameInstall game)
{
    private sealed class Context(InstallPlan plan, Mods.IModSource pkg)
    {
        public InstallPlan Plan { get; } = plan;
        public bool Preview => Plan.Preview;
        public Mods.IModSource Package { get; } = pkg;
        public Dictionary<string, ArchiveSession> Archives { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>rel → prepared file (null = delete).</summary>
        public Dictionary<string, string?> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Counter;
        public Dictionary<string, string?> Before { get; set; } = [];
        public CompatibilityReceipt? Receipt;
        public bool NaturalVision;

        /// <summary>Large graphics suites (NaturalVision, LCPP) may replace loose game files in the root.</summary>
        public bool AllowGameRootFiles;

        /// <summary>This plan's mods\update\update.rpf and its dlclist state before edits (for onigiri).</summary>
        public ArchiveSession? Update;
        public byte[]? DlcBefore;
        public bool DlcTouched,
            GameConfigReplaced;
        public List<OivXmlOp> GameConfigEdits { get; } = [];
    }

    public InstallPlan Prepare(OivPackage pkg, bool preview = false)
    {
        var plan = Prepare(
            pkg.Info.Name + (pkg.Info.Version.Length > 0 ? " " + pkg.Info.Version : ""),
            pkg.Ops,
            pkg,
            preview
        );
        // The package is explicitly tagged for another game version (file name or title in the package): installation is refused.
        var label = Mods.EditionLabel.Mismatch(
            Path.GetFileNameWithoutExtension(pkg.FilePath),
            game.Edition,
            Path.GetFileName(pkg.FilePath)
        ) ?? Mods.EditionLabel.Mismatch(pkg.Info.Name, game.Edition, pkg.Info.Name);
        if (label is not null)
            plan.Errors.Insert(0, label);
        return plan;
    }

    public InstallPlan Prepare(
        string title,
        IReadOnlyList<OivOp> ops,
        Mods.IModSource? pkg = null,
        bool preview = false,
        bool naturalVision = false,
        bool allowGameRootFiles = false
    )
    {
        using var operation = GameOperationGate.Acquire(game);
        var plan = new InstallPlan
        {
            Title = title,
            Preview = preview,
            PrepDir = Path.Combine(
                game.WorkDir,
                "prep",
                DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]
            ),
        };
        if (new ModModeService(game).Inspect().WritesBlocked)
        {
            plan.Errors.Add(L.T("core.oiv.modsDisabledSwitchingIncompleteEnable"));
            return plan;
        }
        Directory.CreateDirectory(plan.PrepDir);
        var ctx = new Context(plan, pkg ?? Mods.LocalFileSource.Instance)
        {
            NaturalVision = naturalVision,
            AllowGameRootFiles = allowGameRootFiles,
        };
        try
        {
            ctx.Before = ReadCompatibilityDocuments();
            foreach (var op in ops)
            {
                try
                {
                    ApplyTop(ctx, op);
                }
                catch (MissingGamePathException missing)
                {
                    // Do not guess a new path: outdated instructions can break the game. Stop the whole installation.
                    var message = L.T("core.oiv.pathFromAuthorSInstructions", game.Edition, missing.Path);
                    if (missing.Hints.Count > 0)
                        message = L.Join(
                            " ",
                            message,
                            L.T("core.oiv.gameHasArchiveSameName", string.Join(", ", missing.Hints.Take(3)))
                        );
                    if (!plan.Errors.Contains(message))
                        plan.Errors.Add(message);
                }
                catch (Exception ex)
                    when (ex
                            is OivFormatException
                                or RpfException
                                or ArgumentException
                                or System.Xml.XmlException
                                or IOException
                    )
                {
                    plan.Errors.Add(ex.Message);
                }
            }
            // Without a loader the game will not see files in mods\, so such an installation is not performed.
            var loader = game.CheckModLoader();
            var writes = ctx.Archives.Keys.Concat(ctx.Files.Where(f => f.Value is not null).Select(f => f.Key));
            if (!loader.Installed && writes.Any(GameInstall.IsInMods))
                plan.Errors.Add(loader.Message);
            if (plan.Errors.Count == 0)
            {
                try
                {
                    PrepareCompatibility(ctx);
                    Finish(ctx);
                    FinishCompatibility(ctx);
                    FinishOnigiriLayer(ctx);
                }
                catch (Exception ex)
                    when (ex
                            is OivFormatException
                                or ArgumentException
                                or FormatException
                                or System.Xml.XmlException
                                or System.Xml.XPath.XPathException
                                or InvalidDataException
                                or IOException
                    )
                {
                    plan.Errors.Add(ex.Message);
                    plan.Changes.Clear();
                }
            }
        }
        finally
        {
            foreach (var s in ctx.Archives.Values)
                s.Dispose();
        }
        return plan;
    }

    /// <summary>
    /// Installation. With a library, a targeted backup (changed files only) is saved after writing and the temporary full
    /// copies are deleted. Without a library (the user opted out) the installation cannot be undone.
    /// </summary>
    public TxResult Install(InstallPlan plan, Library.BackupLibrary? library)
    {
        using var operation = GameOperationGate.Acquire(game);
        using var backupGate = library is null ? null : GameOperationGate.Acquire(library.Root);
        if (new ModModeService(game).Inspect().WritesBlocked)
            return new("", TxState.Rejected, L.T("core.oiv.enableModsFinishRestoringFirst"));
        if (!plan.CanInstall)
            return new(
                "",
                TxState.Rejected,
                plan.Errors.Count > 0 ? string.Join("; ", plan.Errors) : L.T("core.oiv.nothingInstall")
            );
        if (library is not null)
        {
            long used = library.UsedBytes();
            if (used + plan.BackupEstimate > library.QuotaBytes)
                return new(
                    "",
                    TxState.Rejected,
                    L.T(
                        "core.oiv.backupFolderLimitInsufficientAnother",
                        Library.BackupLibrary.FormatSize(plan.BackupEstimate),
                        Library.BackupLibrary.FormatSize(used),
                        Library.BackupLibrary.FormatSize(library.QuotaBytes)
                    )
                );
        }

        var engine = game.CreateEngine();
        var r = engine.Execute(plan.Changes, plan.Title);
        if (r.State != TxState.Completed)
            return r;

        if (library is null)
        {
            engine.DiscardBackups(r.Id);
            return r with { Message = L.T("core.oiv.changesAppliedNoBackupWas", plan.Changes.Count) };
        }
        try
        {
            var record = BuildRecord(r.Id, plan, engine.LoadJournal(r.Id)!, library);
            library.SaveRecord(record);
            engine.DiscardBackups(r.Id);
            return r with
            {
                Message = L.T(
                    "core.oiv.changesAppliedBackup",
                    plan.Changes.Count,
                    Library.BackupLibrary.FormatSize(record.BackupBytes(library))
                ),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The transaction's full copies are kept, so the legacy undo path still works.
            return r with { Message = L.T("core.oiv.warningTargetedBackupWasNot", r.Message, ex.Message) };
        }
    }

    private Library.BackupRecord BuildRecord(
        string id,
        InstallPlan plan,
        TxJournal journal,
        Library.BackupLibrary library
    )
    {
        var record = new Library.BackupRecord
        {
            Id = id,
            Title = plan.Title,
            Edition = game.Edition.ToString(),
        };
        using var oracle = new Library.VanillaOracle(game);
        foreach (var op in journal.Ops)
        {
            if (plan.ArchiveBackups.TryGetValue(op.Rel, out var ab))
            {
                ab.PostFileHash = op.PostHash;
                foreach (var e in ab.Entries.Where(e => e.Old?.Raw is not null))
                {
                    // Whether the previous file is vanilla: compare with the same file in the original game archive.
                    e.Old!.Origin = oracle.IsVanillaEntry(op.Rel, e.Path, e.Old.Raw!)
                        ? Library.BackupOrigin.Vanilla
                        : Library.BackupOrigin.Mods;
                    e.Old.Blob = library.PutBlob(e.Old.Raw!, e.Old.Origin);
                    e.Old.Raw = null;
                }
                record.Archives.Add(ab);
            }
            else
            {
                var origin = oracle.IsVanillaFile(op.Rel, op.PreHash)
                    ? Library.BackupOrigin.Vanilla
                    : Library.BackupOrigin.Mods;
                record.Files.Add(
                    new Library.FileBackup
                    {
                        Rel = op.Rel,
                        Origin = origin,
                        OldBlob = op.PreHash is null ? null : library.PutBlobFile(op.Backup, origin),
                        NewHash = op.PostHash,
                        OldSize = op.PreHash is null ? 0 : new FileInfo(op.Backup).Length,
                        Derived = plan.DerivedFiles.Contains(op.Rel),
                    }
                );
            }
        }
        return record;
    }

    /// <summary>
    /// Undoes an installation. With a targeted backup, only the files changed by this installation are restored
    /// (other mods in the same archives are unaffected; undo order does not matter as long as files were not changed again).
    /// Otherwise the legacy path through full transaction copies is used.
    /// <paramref name="force"/> removes this installation's own files and archive entries even when they were edited
    /// after installation (by hand or by another mod): each is put back to what it was before this installation.
    /// Entries and files that this installation never touched stay as they are.
    /// </summary>
    public TxResult Undo(string id, Library.BackupLibrary? library, bool force = false)
    {
        using var operation = GameOperationGate.Acquire(game);
        using var backupGate = library is null ? null : GameOperationGate.Acquire(library.Root);
        if (new ModModeService(game).Inspect().WritesBlocked)
            return new(id, TxState.Rejected, L.T("core.oiv.enableModsFinishRecoveryFirst"));
        var rec = library?.LoadRecord(game.Edition.ToString(), id);
        if (rec is null)
            return game.CreateEngine().Undo(id);
        if (rec.Undone)
            return new(id, TxState.Rejected, L.T("core.oiv.packageApplicationHasAlreadyBeen"));
        if (game.IsRunning())
            return new(id, TxState.Rejected, L.T("core.common.gameRunningChangesProhibited"));

        var prep = Path.Combine(game.WorkDir, "prep", "undo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(prep);
        try
        {
            var conflicts = new List<string>();
            var changes = new List<FileChange>();
            int n = 0;
            var compatibility = PrepareCompatibilityUndo(rec, force);

            foreach (var f in rec.Files)
            {
                if (f.Derived)
                    continue; // onigiri copies are recomputed from mods\ below
                if (
                    compatibility is not null
                    && (
                        ModCompatibility.Documents.Contains(f.Rel, StringComparer.OrdinalIgnoreCase)
                        || compatibility.Before.Any(r => r.Mirrors.ContainsKey(f.Rel))
                    )
                )
                    continue;
                var cur = FileTransactionEngine.HashOrNull(Path.Combine(game.Root, f.Rel));
                if (!force && !string.Equals(cur, f.NewHash, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(L.T("core.oiv.modifiedAfterInstallation", f.Rel));
                    continue;
                }
                if (f.OldBlob is not null)
                {
                    var staged = Path.Combine(prep, (n++).ToString("D5"));
                    library!.CopyBlobTo(f.OldBlob, staged);
                    changes.Add(new FileChange(f.Rel, cur, staged, MoveSource: true));
                }
                else if (cur is not null)
                    changes.Add(new FileChange(f.Rel, cur, null));
            }

            var undoArchives = rec.Archives.ToList();
            if (
                compatibility is not null
                && !undoArchives.Any(a => a.Rel.Equals(OnigiriOverlay.UpdateRpf, StringComparison.OrdinalIgnoreCase))
                && File.Exists(Path.Combine(game.Root, OnigiriOverlay.UpdateRpf))
            )
                undoArchives.Add(new Library.ArchiveBackup { Rel = OnigiriOverlay.UpdateRpf });
            foreach (var a in undoArchives)
            {
                var full = Path.Combine(game.Root, a.Rel);
                var cur = FileTransactionEngine.HashOrNull(full);
                if (cur is null)
                {
                    if (!force) // nothing of this installation is left inside a deleted archive
                        conflicts.Add(L.T("core.oiv.missing2", a.Rel));
                    continue;
                }
                if (
                    compatibility is null
                    && a.Created
                    && string.Equals(cur, a.PostFileHash, StringComparison.OrdinalIgnoreCase)
                )
                {
                    changes.Add(new FileChange(a.Rel, cur, null)); // the archive was created by this installation and has not changed since
                    continue;
                }

                using var session = OpenForUndo(full, a.Rel);
                var bad = a
                    .Entries.Where(e =>
                        !(
                            compatibility is not null
                            && ModCompatibility.Documents.Contains(
                                a.Rel + "|" + e.Path,
                                StringComparer.OrdinalIgnoreCase
                            )
                        )
                        && !string.Equals(
                            SafeFingerprint(session, e.Path),
                            e.NewFingerprint,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .Select(e => L.T("core.oiv.modifiedAfterInstallation2", a.Rel, e.Path))
                    .ToList();
                if (bad.Count > 0 && !force)
                {
                    conflicts.AddRange(bad);
                    continue;
                }

                foreach (var e in Enumerable.Reverse(a.Entries))
                {
                    if (
                        compatibility is not null
                        && ModCompatibility.Documents.Contains(a.Rel + "|" + e.Path, StringComparer.OrdinalIgnoreCase)
                    )
                        continue;
                    try
                    {
                        if (e.Kind == Library.InnerKind.Added)
                            session.Delete(e.Path);
                        else
                            session.PutSource(e.Path, ArchiveSession.Restore(e.Old!, library!.ReadBlob(e.Old!.Blob)));
                    }
                    catch (OivFormatException) when (force)
                    {
                        // The nested archive holding this entry was removed or replaced: nothing of ours is left there.
                    }
                }
                if (compatibility is not null)
                    foreach (
                        var key in ModCompatibility.Documents.Where(k =>
                            k.StartsWith(a.Rel + "|", StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    {
                        var text = compatibility.Documents.GetValueOrDefault(key);
                        if (text is null)
                            session.Delete(key.Split('|')[1]);
                        else
                            session.Put(key.Split('|')[1], ModCompatibility.Encode(text));
                    }
                var staged = Path.Combine(prep, (n++).ToString("D5"));
                using (var fs = File.Create(staged))
                    session.WriteTo(fs);
                changes.Add(new FileChange(a.Rel, cur, staged, MoveSource: true));
            }

            if (conflicts.Count > 0)
                return new(
                    id,
                    TxState.Rejected,
                    L.T(
                        "core.oiv.cannotUndoFilesChangedAfter",
                        string.Join("; ", conflicts.Take(5)),
                        (conflicts.Count > 5 ? L.T("core.oiv.more", conflicts.Count - 5) : "")
                    )
                );

            if (compatibility is not null)
                FinishCompatibilityUndo(compatibility, changes, prep);
            UndoOnigiriLayer(rec, changes, prep);
            var engine = game.CreateEngine();
            var r = engine.Execute(changes, "Отмена: " + rec.Title);
            if (r.State != TxState.Completed)
                return r;
            rec.Undone = true;
            library!.SaveRecord(rec);
            engine.DiscardBackups(r.Id);
            var freed = library.CollectGarbage();
            return new(
                id,
                TxState.Undone,
                L.T(
                    "core.oiv.packageApplicationUndoneFilesProcessed",
                    changes.Count,
                    Library.BackupLibrary.FormatSize(freed)
                )
            );
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or System.Xml.XmlException
                        or System.Xml.XPath.XPathException
                        or FormatException
                        or ArgumentException
            )
        {
            return new(id, TxState.Rejected, ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(prep, true);
            }
            catch (IOException) { }
        }
    }

    private ArchiveSession OpenForUndo(string full, string rel)
    {
        // Keys are needed for nested archives with an encrypted table of contents; without them only unencrypted archives work.
        return ArchiveSession.OpenFile(full, rel, game.TryLoadKeys());
    }

    private static string? SafeFingerprint(ArchiveSession s, string path)
    {
        try
        {
            return s.Fingerprint(path);
        }
        catch (OivFormatException)
        {
            return L.T("core.oiv.none");
        }
    }

    private void ApplyTop(Context ctx, OivOp op)
    {
        switch (op)
        {
            case OivAdd add:
            {
                var target = game.MapWriteTarget(add.Target);
                Guard(ctx, target);
                if (ctx.Archives.ContainsKey(target))
                    throw new OivFormatException(L.T("core.oiv.conflictBothReplacedEntirelyEdited", target));
                if (ctx.Preview)
                {
                    ctx.Package.SourceSize(add.Source); // existence check
                    ctx.Files[target] = InstallPlan.PreviewSource;
                    ctx.Plan.Items.Add(new("add", target, add.Source));
                    break;
                }
                var file = NewPrepFile(ctx);
                using (var src = ctx.Package.OpenSource(add.Source))
                using (var dst = File.Create(file))
                    src.CopyTo(dst);
                ctx.Files[target] = file;
                ctx.Plan.Items.Add(new("add", target, add.Source));
                break;
            }
            case OivDelete del:
                PlanDelete(ctx, del.Target);
                break;
            case OivArchive arc:
            {
                var session = OpenArchive(ctx, arc.Path, arc.CreateIfNotExist);
                ApplyInArchive(ctx, session, arc.Ops);
                break;
            }
            case OivXml xml:
            {
                var target = game.MapWriteTarget(xml.Path);
                Guard(ctx, target);
                var bytes = ReadLoose(ctx, target, xml.Path)
                    ?? throw new OivFormatException(L.T("core.common.fileMissing", xml.Path));
                var (result, log) = TextEdits.ApplyXml(bytes, xml.Ops);
                StageLoose(ctx, target, result, "xml", log);
                break;
            }
            case OivText text:
            {
                var target = game.MapWriteTarget(text.Path);
                Guard(ctx, target);
                var bytes = ReadLoose(ctx, target, text.Path)
                    ?? (
                        text.CreateIfNotExist
                            ? []
                            : throw new OivFormatException(L.T("core.common.fileMissing", text.Path))
                    );
                var (str, bom) = TextEdits.Decode(bytes);
                var (result, log) = TextEdits.ApplyText(str, text.Ops);
                StageLoose(ctx, target, TextEdits.Encode(result, bom), "text", log);
                break;
            }
        }
    }

    private void ApplyInArchive(Context ctx, ArchiveSession session, IReadOnlyList<OivOp> ops)
    {
        foreach (var op in ops)
        {
            if (ReferenceEquals(session, ctx.Update))
                Track(ctx, op);
            switch (op)
            {
                case OivAdd add:
                    if (ctx.Preview)
                    {
                        ctx.Package.SourceSize(add.Source);
                        session.Resolve(add.Target, createArchives: true).session.MarkChanged();
                        session.MarkChanged();
                    }
                    // Without a package, source is a local file (the "add files to a DLC pack" command).
                    else
                        session.Put(add.Target, ReadAll(ctx.Package, add.Source));
                    ctx.Plan.Items.Add(
                        new("add", session.Label + "\\" + GameInstall.Normalize(add.Target), add.Source)
                    );
                    break;
                case OivDelete del:
                    bool removed = session.Delete(del.Target);
                    ctx.Plan.Items.Add(
                        new(
                            "delete",
                            session.Label + "\\" + GameInstall.Normalize(del.Target),
                            removed ? "" : L.T("core.oiv.notArchiveSkipped")
                        )
                    );
                    break;
                case OivArchive nested:
                    ApplyInArchive(ctx, session.Nested(nested.Path, nested.CreateIfNotExist), nested.Ops);
                    break;
                case OivXml xml:
                {
                    var bytes = session.ReadFile(xml.Path)
                        ?? throw new OivFormatException(L.T("core.oiv.missing", session.Label, xml.Path));
                    var (result, log) = TextEdits.ApplyXml(bytes, xml.Ops);
                    if (!result.AsSpan().SequenceEqual(bytes) && !SameXml(result, bytes))
                        session.Put(xml.Path, result);
                    else
                        log.Add(L.T("core.oiv.contentUnchanged"));
                    ctx.Plan.Items.Add(
                        new("xml", session.Label + "\\" + GameInstall.Normalize(xml.Path), string.Join("; ", log))
                    );
                    break;
                }
                case OivRequire require:
                {
                    var (owner, inner) = session.Resolve(require.Path, false);
                    if (!owner.Builder.Exists(inner))
                        throw new MissingGamePathException(
                            owner.Label + "\\" + inner,
                            owner
                                .Builder.FindByName(require.HintFile)
                                .Select(p => owner.Label + "\\" + (Path.GetDirectoryName(p) ?? ""))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList(),
                            L.T("core.oiv.missingNestedArchive", owner.Label + "\\" + inner)
                        );
                    break;
                }
                case OivAwc awc:
                {
                    var awcPath = awc.Path;
                    if (session.ReadFile(awcPath) is null)
                    {
                        // The instructions name the bank without a folder inside the archive: take the only file with that name.
                        var byName = session.Builder.FindByName(Path.GetFileName(awcPath));
                        if (byName.Count == 1)
                            awcPath = byName[0];
                        else
                            throw new MissingGamePathException(
                                session.Label + "\\" + GameInstall.Normalize(awcPath),
                                byName.Select(p => session.Label + "\\" + p).ToList(),
                                L.T("core.oiv.missing", session.Label, awcPath)
                            );
                    }
                    var bytes = session.ReadFile(awcPath)!;
                    uint[]? key = Audio.AwcBank.LooksEncrypted(bytes) ? game.TryLoadAwcKey() : null;
                    if (Audio.AwcBank.LooksEncrypted(bytes) && key is null)
                        throw new OivFormatException(L.T("core.common.soundBankEncryptedItsKey"));
                    Audio.AwcBank bank;
                    try
                    {
                        bank = Audio.AwcBank.Parse(bytes, key);
                        foreach (var sound in awc.Sounds)
                        {
                            var wav = Audio.WavFile.Read(ReadAll(ctx.Package, sound.Source));
                            bank.ReplacePcm(Audio.AwcBank.StreamId(sound.Name), wav.Samples, wav.SampleRate);
                        }
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new OivFormatException(
                            session.Label + "\\" + GameInstall.Normalize(awcPath) + ": " + ex.Message
                        );
                    }
                    session.Put(awcPath, bank.Build(key));
                    ctx.Plan.Items.Add(
                        new(
                            "awc",
                            session.Label + "\\" + GameInstall.Normalize(awcPath),
                            L.T("core.oiv.soundsReplaced", awc.Sounds.Count)
                        )
                    );
                    break;
                }
                case OivText text:
                {
                    var bytes = session.ReadFile(text.Path)
                        ?? (
                            text.CreateIfNotExist
                                ? []
                                : throw new OivFormatException(L.T("core.oiv.missing", session.Label, text.Path))
                        );
                    var (str, bom) = TextEdits.Decode(bytes);
                    var (result, log) = TextEdits.ApplyText(str, text.Ops);
                    if (result != str)
                        session.Put(text.Path, TextEdits.Encode(result, bom));
                    else
                        log.Add(L.T("core.oiv.contentUnchanged"));
                    ctx.Plan.Items.Add(
                        new("text", session.Label + "\\" + GameInstall.Normalize(text.Path), string.Join("; ", log))
                    );
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Archive to edit: the copy in mods\ if present; otherwise an original with an unencrypted table of contents (a mod outside mods\);
    /// otherwise a new empty one (createIfNotExist). An encrypted game original requires keys.
    /// </summary>
    private ArchiveSession OpenArchive(Context ctx, string path, bool create)
    {
        var target = game.MapWriteTarget(path);
        Guard(ctx, target);
        if (ctx.Archives.TryGetValue(target, out var s))
            return s;
        if (ctx.Files.ContainsKey(target))
            throw new OivFormatException(L.T("core.oiv.conflictBothReplacedEntirelyEdited", target));

        var inMods = Path.Combine(game.Root, target);
        var original = Path.Combine(game.Root, GameInstall.Normalize(path));
        // Keys are needed for encrypted originals and nested NG archives; loaded lazily (~0.5 s from cache).
        var keys = NeedsKeys(inMods, original) ? game.TryLoadKeys() : null;
        if (File.Exists(inMods))
            s = ArchiveSession.OpenFile(inMods, target, keys ?? game.TryLoadKeys());
        else if (File.Exists(original))
        {
            if (!GameInstall.IsPlainRpf(original) && keys is null)
                throw new OivFormatException(
                    L.T(
                        "core.oiv.gameArchiveEncryptedDecryptionKeys",
                        GameInstall.Normalize(path),
                        (game.KeysError ?? L.T("core.oiv.unknownReason"))
                    )
                );
            // A copy of the original in mods\ with an unencrypted table of contents (as OpenIV does); the original itself is not changed.
            s = ArchiveSession.OpenFile(original, target, keys);
            s.MarkChanged();
            ctx.Plan.Log.Add($"{GameInstall.Normalize(path)}: копия оригинала будет создана в {target}");
        }
        else if (create)
            s = ArchiveSession.CreateEmpty(target);
        else
            throw new MissingGamePathException(GameInstall.Normalize(path), [], L.T("core.oiv.missingArchive", path));

        ctx.Archives[target] = s;
        if (target.Equals(OnigiriOverlay.UpdateRpf, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Update = s;
            try
            {
                ctx.DlcBefore = s.ReadFile(OnigiriOverlay.DlcListInner);
            }
            catch (Exception ex) when (ex is OivFormatException or RpfException or IOException)
            {
                ctx.DlcBefore = null;
            }
        }
        return s;
    }

    /// <summary>Which update.rpf edits affect files that onigiri replaces with its own copies.</summary>
    private static void Track(Context ctx, OivOp op)
    {
        string? Inner(string path)
        {
            try
            {
                return GameInstall.Normalize(path);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        switch (op)
        {
            case OivAdd add when Inner(add.Target) is { } p:
                if (p.Equals(OnigiriOverlay.DlcListInner, StringComparison.OrdinalIgnoreCase))
                    ctx.DlcTouched = true;
                if (p.Equals(OnigiriOverlay.GameConfigInner, StringComparison.OrdinalIgnoreCase))
                    ctx.GameConfigReplaced = true;
                break;
            case OivXml xml when Inner(xml.Path) is { } p:
                if (p.Equals(OnigiriOverlay.DlcListInner, StringComparison.OrdinalIgnoreCase))
                    ctx.DlcTouched = true;
                if (p.Equals(OnigiriOverlay.GameConfigInner, StringComparison.OrdinalIgnoreCase))
                    ctx.GameConfigEdits.AddRange(xml.Ops);
                break;
            case OivText text
                when Inner(text.Path) is { } p
                    && p.Equals(OnigiriOverlay.DlcListInner, StringComparison.OrdinalIgnoreCase):
                ctx.DlcTouched = true;
                break;
        }
    }

    /// <summary>Add-on pack: located (or to be located) in mods\update\x64\dlcpacks or onigiri\dlcpacks and not a game pack.</summary>
    private bool IsAddonPack(Context ctx, string pack)
    {
        if (IsVanillaPack(pack))
            return false;
        foreach (var rel in new[] { $@"mods\update\x64\dlcpacks\{pack}\dlc.rpf", $@"onigiri\dlcpacks\{pack}\dlc.rpf" })
        {
            if (ctx.Files.TryGetValue(rel, out var staged))
            {
                if (staged is not null)
                    return true;
                continue;
            }
            if (File.Exists(Path.Combine(game.Root, rel)))
                return true;
        }
        return false;
    }

    private bool IsVanillaPack(string pack) =>
        Directory.Exists(Path.Combine(game.Root, "update", "x64", "dlcpacks", pack));

    private byte[]? ReadFromModsUpdate(string inner)
    {
        var full = Path.Combine(game.Root, OnigiriOverlay.UpdateRpf);
        if (!File.Exists(full))
            return null;
        try
        {
            using var session = ArchiveSession.OpenFile(full, OnigiriOverlay.UpdateRpf, game.TryLoadKeys());
            return session.ReadFile(inner);
        }
        catch (Exception ex) when (ex is OivFormatException or RpfException or IOException)
        {
            return null;
        }
    }

    private static bool NeedsKeys(string inMods, string original) =>
        File.Exists(inMods) ? false : File.Exists(original) && !GameInstall.IsPlainRpf(original);

    private void PlanDelete(Context ctx, string path)
    {
        var norm = GameInstall.Normalize(path);
        var candidates = new List<string> { game.MapWriteTarget(norm) };
        if (!candidates.Contains(norm, StringComparer.OrdinalIgnoreCase))
            candidates.Add(norm);

        int found = 0;
        foreach (var rel in candidates)
        {
            var full = Path.Combine(game.Root, rel);
            IEnumerable<string> files =
                Directory.Exists(full)
                    ? Directory
                        .EnumerateFiles(full, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(game.Root, f))
                : File.Exists(full) ? [rel]
                : [];
            foreach (var f in files)
            {
                if (game.WhyProtected(f) is { } why)
                {
                    ctx.Plan.Log.Add(L.T("core.oiv.notDeleting", f, why));
                    continue;
                }
                ctx.Files[f] = null;
                ctx.Plan.Items.Add(new("delete", f, ""));
                found++;
            }
        }
        if (found == 0)
            ctx.Plan.Log.Add($"удаление: {norm} не найден (пропуск)");
    }

    private byte[]? ReadLoose(Context ctx, string target, string original)
    {
        if (ctx.Files.TryGetValue(target, out var staged) && staged is not null && staged != InstallPlan.PreviewSource)
            return File.ReadAllBytes(staged);
        var full = Path.Combine(game.Root, target);
        if (File.Exists(full))
            return File.ReadAllBytes(full);
        var orig = Path.Combine(game.Root, GameInstall.Normalize(original));
        return File.Exists(orig) ? File.ReadAllBytes(orig) : null;
    }

    private void StageLoose(Context ctx, string target, byte[] data, string action, List<string> log)
    {
        var file = NewPrepFile(ctx);
        File.WriteAllBytes(file, data);
        ctx.Files[target] = file;
        ctx.Plan.Items.Add(new(action, target, string.Join("; ", log)));
    }

    private void Finish(Context ctx)
    {
        if (ctx.Preview)
        {
            foreach (var (rel, session) in ctx.Archives)
                if (session.Changed)
                    ctx.Files[rel] = InstallPlan.PreviewSource;
                else
                    ctx.Plan.Log.Add(L.T("core.oiv.unchanged", rel));
            foreach (var (rel, staged) in ctx.Files)
            {
                bool exists = File.Exists(Path.Combine(game.Root, rel));
                if (staged is null && !exists)
                    continue;
                ctx.Plan.Changes.Add(new FileChange(rel, exists ? "exists" : null, staged));
            }
            return;
        }

        foreach (var (rel, session) in ctx.Archives)
        {
            if (!session.Changed)
            {
                ctx.Plan.Log.Add(L.T("core.oiv.unchanged", rel));
                continue;
            }
            var file = NewPrepFile(ctx);
            using (var fs = File.Create(file))
                session.WriteTo(fs);
            ctx.Files[rel] = file;
            // Targeted archive backup: only the inner files the installation changes.
            ctx.Plan.ArchiveBackups[rel] = new Library.ArchiveBackup
            {
                Rel = rel,
                Created = !File.Exists(Path.Combine(game.Root, rel)),
                Entries = session.CollectChanges(),
            };
        }

        foreach (var (rel, staged) in ctx.Files)
        {
            var full = Path.Combine(game.Root, rel);
            var current = FileTransactionEngine.HashOrNull(full);
            if (staged is null && current is null)
                continue;
            if (staged is not null && current is not null && current == FileTransactionEngine.Hash(staged))
            {
                ctx.Plan.Log.Add(L.T("core.oiv.versionAlreadyInstalled", rel));
                ctx.Plan.ArchiveBackups.Remove(rel);
                continue;
            }
            ctx.Plan.Changes.Add(new FileChange(rel, current, staged, MoveSource: staged is not null));
            ctx.Plan.BackupEstimate +=
                ctx.Plan.ArchiveBackups.TryGetValue(rel, out var ab) ? ab.Entries.Sum(e => e.Old?.Raw?.LongLength ?? 0)
                : current is null ? 0
                : new FileInfo(full).Length;
        }
    }

    // XmlDocument serialization may differ from the source only in the declaration formatting.
    private static bool SameXml(byte[] a, byte[] b)
    {
        static string N(byte[] x) =>
            System.Text.RegularExpressions.Regex.Replace(TextEdits.Decode(x).text, @"<\?xml[^>]*\?>|\s+", "");
        return N(a) == N(b);
    }

    private void Guard(Context ctx, string rel)
    {
        if (
            rel.Equals("ModHubState", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(@"ModHubState\", StringComparison.OrdinalIgnoreCase)
        )
            throw new OivFormatException(L.T("core.oiv.touchesModHubSettings"));
        if (ctx.AllowGameRootFiles && IsGameRootFile(rel))
            return; // replaced with a backup; undo puts the original back
        if (game.WhyProtected(rel) is { } why)
            throw new OivFormatException(L.T("core.oiv.modifyingProhibited", why));
    }

    /// <summary>A loose game file in the root (DLLs such as nvngx_dlss.dll), not an archive or a game folder.</summary>
    private bool IsGameRootFile(string rel) =>
        !rel.Contains('\\')
        && !rel.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)
        && !rel.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && game.WhyProtected(rel) is not null;

    private static byte[] ReadAll(Mods.IModSource src, string source)
    {
        if (src.SourceSize(source) > int.MaxValue)
            throw new OivFormatException(L.T("core.oiv.fileTooLargeEditMemory", source));
        using var s = src.OpenSource(source);
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string NewPrepFile(Context ctx) => Path.Combine(ctx.Plan.PrepDir, (ctx.Counter++).ToString("D5"));
}
