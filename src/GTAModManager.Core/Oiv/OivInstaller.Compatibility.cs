using System.Text.Json;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Oiv;

public sealed partial class OivInstaller
{
    private Dictionary<string, string?> ReadCompatibilityDocuments()
    {
        var docs = ModCompatibility.Documents.ToDictionary(
            k => k,
            _ => (string?)null,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var key in ModCompatibility.Documents.Where(k => !k.Contains('|')))
            if (File.Exists(Path.Combine(game.Root, key)))
                docs[key] = ModCompatibility.Decode(File.ReadAllBytes(Path.Combine(game.Root, key)));
        string rel = OnigiriOverlay.UpdateRpf;
        string full = Path.Combine(game.Root, rel);
        if (!File.Exists(full))
            full = Path.Combine(game.Root, @"update\update.rpf");
        if (File.Exists(full))
        {
            using var archive = ArchiveSession.OpenFile(full, rel, game.TryLoadKeys());
            foreach (var key in ModCompatibility.Documents.Where(k => k.Contains('|')))
            {
                var bytes = archive.ReadFile(key.Split('|')[1]);
                if (bytes is not null)
                    docs[key] = ModCompatibility.Decode(bytes);
            }
        }
        return docs;
    }

    private void PrepareCompatibility(Context ctx)
    {
        // Mod Hub does not ship a mods loader of its own any more, so installs never replace the installed one.
        PrepareHeapOrder(ctx);
        PrepareLoaderHandOff(ctx);
        bool overlay =
            ctx.Files.ContainsKey(ModCompatibility.OverlayConfig) || ctx.Files.ContainsKey(ModCompatibility.OverlayDlc);
        bool relevant =
            ctx.DlcTouched
            || ctx.GameConfigEdits.Count > 0
            || overlay
            || ctx.GameConfigReplaced
            || game.UsesOnigiri
                && ctx.Files.Keys.Concat(ctx.Archives.Keys).Any(p => game.AddonPackRemainder(p) is not null);
        if (!relevant)
            return;
        var existing = ModCompatibility.Load(game);
        if (
            !ctx.NaturalVision
            && !overlay
            && ctx.Before.GetValueOrDefault(ModCompatibility.OverlayConfig) is null
            && existing.Count == 0
            && !ctx.Files.Keys.Concat(ctx.Archives.Keys).Any(p => game.AddonPackRemainder(p) is not null)
        )
            return;
        var bases = existing.FirstOrDefault()?.Bases ?? ctx.Before;
        var receipt = new CompatibilityReceipt { Bases = new(bases, StringComparer.OrdinalIgnoreCase) };
        var previous = ModCompatibility.Compose(existing, bases);
        foreach (var key in ModCompatibility.Documents)
            if (!ModCompatibility.Same(ctx.Before[key], previous.GetValueOrDefault(key)))
                throw new IOException(L.T("core.compat.sharedFileChangedInstall", key));

        string? Staged(string key)
        {
            if (key.Contains('|'))
            {
                var bytes = ctx.Update?.ReadFile(key.Split('|')[1]);
                return bytes is null ? ctx.Before[key] : ModCompatibility.Decode(bytes);
            }
            return ctx.Files.TryGetValue(key, out var path) && path is not null && path != InstallPlan.PreviewSource
                ? ModCompatibility.Decode(File.ReadAllBytes(path))
                : ctx.Before[key];
        }
        if (ctx.Preview)
            return; // Compatibility changes are fully validated during actual preparation.
        receipt.BaselinePacks = existing.FirstOrDefault()?.BaselinePacks.ToList()
            ?? (
                ctx.Before[ModCompatibility.Dlc] is { } oldList
                    ? Dlc.DlcList.Packs(oldList).Where(p => IsAddonPack(ctx, p)).ToList()
                    : []
            );
        if (ctx.DlcTouched)
        {
            var before = ctx.Before[ModCompatibility.Dlc];
            var after = Staged(ModCompatibility.Dlc);
            if (after is not null)
            {
                var old = before is null ? [] : Dlc.DlcList.Packs(before);
                var now = Dlc.DlcList.Packs(after);
                receipt.AddedPacks = now.Except(old, StringComparer.OrdinalIgnoreCase).ToList();
                receipt.RemovedPacks = old.Except(now, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
        if (ctx.NaturalVision)
        {
            if (ctx.GameConfigReplaced && Staged(ModCompatibility.Config) is { } config)
                receipt.Authored[ModCompatibility.Config] = config;
        }
        else if (
            ctx.GameConfigReplaced
            && (previous[ModCompatibility.OverlayConfig] is not null || existing.Any(r => r.Authored.Count > 0))
        )
            throw new IOException(L.T("core.compat.gameconfigReplacedNve"));
        foreach (var key in new[] { ModCompatibility.OverlayConfig, ModCompatibility.OverlayDlc })
            if (ctx.Files.ContainsKey(key) && Staged(key) is { } text)
            {
                if (!ctx.NaturalVision && existing.Any(r => r.Authored.ContainsKey(key)))
                    throw new IOException(L.T("core.compat.replacesNveConfig"));
                receipt.Authored[key] = text;
            }
        if (
            ctx.GameConfigEdits.Count > 0
            && ctx.Before[ModCompatibility.Config] is { } original
            && Staged(ModCompatibility.Config) is { } edited
        )
        {
            var requirements = ModCompatibility.Requirements(original, edited, ctx.GameConfigEdits);
            receipt.Minimums = requirements.Minimums;
            receipt.UnsupportedConfig = requirements.Unsupported;
            if (receipt.UnsupportedConfig)
                throw new IOException(L.T("core.compat.unsupportedGameconfigEdits"));
        }
        // NOS 1.9.x explicitly requires PtFxAssetStore >= 800 in its package README,
        // but 1.9.5 intentionally contains no gameconfig operations. This rule is
        // identified by its script and both DLC targets, not by arbitrary README prose.
        if (
            ctx.Files.ContainsKey(@"scripts\N.O.S - Nitro Overdrive System.dll")
            && new[] { "nos_overdrive", "nos_lsc_entrance" }.All(p =>
                ctx.Files.TryGetValue(@"mods\update\x64\dlcpacks\" + p + @"\dlc.rpf", out var source)
                && source is not null
            )
        )
        {
            var config = Staged(ModCompatibility.Config)
                ?? throw new IOException(L.T("core.compat.nosNeedsGameconfig"));
            receipt.Minimums.AddRange(ModCompatibility.KnownPoolRequirement(config, "PtFxAssetStore", 800));
            ctx.Plan.Items.Add(new("compatibility", "PtFxAssetStore", L.T("core.compat.nosPtfxMinimum")));
        }
        // An unprofiled full replacement is a baseline choice, never an inferred addon requirement.
        if (!ctx.NaturalVision && ctx.GameConfigReplaced)
        {
            throw new IOException(L.T("core.compat.gameconfigReplaced"));
        }
        var all = existing.Append(receipt).ToArray();
        var next = ModCompatibility.Compose(all, receipt.Bases);
        foreach (var key in ModCompatibility.Documents)
        {
            var text = next.GetValueOrDefault(key);
            if (ModCompatibility.Same(Staged(key), text))
                continue;
            if (key.Contains('|'))
            {
                var archive = ctx.Update ?? OpenArchive(ctx, @"update\update.rpf", false);
                if (text is null)
                    archive.Delete(key.Split('|')[1]);
                else
                    archive.Put(key.Split('|')[1], ModCompatibility.Encode(text));
            }
            else if (text is not null)
                StageLoose(
                    ctx,
                    key,
                    ModCompatibility.Encode(text),
                    "compatibility",
                    [L.T("core.compat.addonRequirements")]
                );
        }
        ctx.Receipt = receipt;
    }

    /// <summary>
    /// Installing onigiri (NaturalVision Enhanced): a mods\ loader next to it is useless and harmful (RageOpenV turns
    /// itself off, NVE warns about OpenRPF). It is removed in the same transaction, so undoing NVE brings it back.
    /// </summary>
    private void PrepareLoaderHandOff(Context ctx)
    {
        if (
            game.Edition != GameEdition.Enhanced
            || !ctx.Files.TryGetValue(GameInstall.OnigiriAsi, out var onigiri)
            || onigiri is null
        )
            return;
        foreach (var name in GameInstall.LoadersReplacedByOnigiri)
        {
            if (ctx.Files.ContainsKey(name) || !File.Exists(Path.Combine(game.Root, name)))
                continue;
            ctx.Files[name] = null;
            ctx.Plan.Items.Add(new("compatibility", name, L.T("core.compat.loaderReplacedByOnigiri")));
        }
    }

    private void PrepareHeapOrder(Context ctx)
    {
        bool nve =
            File.Exists(Path.Combine(game.Root, "NVE.asi"))
            || ctx.Files.TryGetValue("NVE.asi", out var nveFile) && nveFile is not null;
        if (!nve || Installation.LimitSetup.HasResourceExtensions(game))
            return;
        const string alias = "ZHeapAdjuster.asi";
        var names = Installation.AdjusterComponents.Catalog.Single(c => c.Id == "heap").InstalledNames;
        var candidates = names
            .Where(n => !n.Equals(alias, StringComparison.OrdinalIgnoreCase))
            .Where(n => ctx.Files.TryGetValue(n, out var s) ? s is not null : File.Exists(Path.Combine(game.Root, n)))
            .ToList();
        if (candidates.Count == 0)
            return;
        bool aliasExists = ctx.Files.TryGetValue(alias, out var stagedAlias)
            ? stagedAlias is not null
            : File.Exists(Path.Combine(game.Root, alias));
        if (candidates.Count > 1 || aliasExists)
            throw new IOException(L.T("core.compat.heapAdjusterConflict"));
        var from = candidates[0];
        if (ctx.Preview)
            ctx.Files[alias] = InstallPlan.PreviewSource;
        else
        {
            var source =
                ctx.Files.TryGetValue(from, out var staged) && staged is not null
                    ? staged
                    : Path.Combine(game.Root, from);
            var copy = NewPrepFile(ctx);
            File.Copy(source, copy);
            ctx.Files[alias] = copy;
        }
        ctx.Files[from] = null;
        ctx.Plan.Items.Add(new("compatibility", alias, L.T("core.compat.heapAdjusterAfterNve")));
    }

    private void FinishCompatibility(Context ctx)
    {
        if (ctx.Preview || ctx.Receipt is null)
            return;
        var receipts = ModCompatibility.Load(game);
        if (
            ctx.Plan.Changes.Count == 0
            && ctx.Receipt.Minimums.All(m =>
                receipts
                    .SelectMany(r => r.Minimums)
                    .Any(old => old.Section == m.Section && old.Name == m.Name && old.Value >= m.Value)
            )
            && ctx.Receipt.Authored.All(d =>
                ModCompatibility.Same(
                    receipts.Select(r => r.Authored.GetValueOrDefault(d.Key)).LastOrDefault(v => v is not null),
                    d.Value
                )
            )
            && ctx.Receipt.AddedPacks.Count == 0
            && ctx.Receipt.RemovedPacks.Count == 0
        )
            return;
        var all = receipts.Append(ctx.Receipt).ToArray();
        var next = ModCompatibility.Compose(all, ctx.Receipt.Bases);
        var previousTargets = ctx.Plan.Changes.Select(c => c.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ReconcileMirrors(ctx.Plan.Changes, ctx.Plan.PrepDir, receipts, next, all, ctx.Receipt.Mirrors);
        ctx.Plan.BackupEstimate += ctx
            .Plan.Changes.Where(c => !previousTargets.Contains(c.RelativePath) && c.ExpectedPreHash is not null)
            .Sum(c => new FileInfo(Path.Combine(game.Root, c.RelativePath)).Length);
        var file = NewPrepFile(ctx);
        File.WriteAllText(file, JsonSerializer.Serialize(ctx.Receipt));
        ctx.Plan.Changes.Add(new(ctx.Receipt.Path, null, file, true));
    }

    private void ReconcileMirrors(
        List<FileChange> changes,
        string prep,
        IReadOnlyList<CompatibilityReceipt> before,
        Dictionary<string, string?> documents,
        IReadOnlyList<CompatibilityReceipt> after,
        Dictionary<string, string>? captured = null
    )
    {
        bool active =
            documents.GetValueOrDefault(ModCompatibility.OverlayDlc) is not null
            && (
                after.Any(r => r.Authored.ContainsKey(ModCompatibility.OverlayDlc))
                || File.Exists(Path.Combine(game.Root, "onigiri.asi"))
            )
            && !changes.Any(c =>
                c.RelativePath.Equals("onigiri.asi", StringComparison.OrdinalIgnoreCase) && c.SourcePath is null
            );
        var packs = active ? Dlc.DlcList.Packs(documents[ModCompatibility.OverlayDlc]!) : [];
        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs)
        {
            if (Directory.Exists(Path.Combine(game.Root, "update", "x64", "dlcpacks", pack)))
                continue;
            string canonical = @"mods\update\x64\dlcpacks\" + pack + "\\";
            var dir = Path.Combine(game.Root, canonical);
            var files = Directory.Exists(dir)
                ? Directory
                    .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(game.Root, p))
                : [];
            foreach (
                var rel in files
                    .Concat(
                        changes
                            .Where(c => c.RelativePath.StartsWith(canonical, StringComparison.OrdinalIgnoreCase))
                            .Select(c => c.RelativePath)
                    )
                    .Distinct(StringComparer.OrdinalIgnoreCase)
            )
            {
                var change = changes.FirstOrDefault(c =>
                    c.RelativePath.Equals(rel, StringComparison.OrdinalIgnoreCase)
                );
                var source = change is null ? Path.Combine(game.Root, rel) : change.SourcePath;
                if (source is not null)
                    desired[@"onigiri\dlcpacks\" + rel[@"mods\update\x64\dlcpacks\".Length..]] = source;
            }
        }
        var known = KnownMirrors(before);
        foreach (var rel in known.Keys.Concat(desired.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var current = FileTransactionEngine.HashOrNull(Path.Combine(game.Root, rel));
            var wanted = desired.TryGetValue(rel, out var src) ? FileTransactionEngine.Hash(src) : null;
            if (
                current != wanted
                && current is not null
                && (!known.TryGetValue(rel, out var expected) || current != expected)
            )
                throw new IOException(L.T("core.compat.dlcMirrorChanged", rel));
            // Equal foreign files are usable but are not acquired as our property.
            if (wanted is not null && (current is null || known.ContainsKey(rel)))
                captured?.Add(rel, wanted);
            changes.RemoveAll(c => c.RelativePath.Equals(rel, StringComparison.OrdinalIgnoreCase));
            if (current == wanted)
                continue;
            string? staged = null;
            if (src is not null)
            {
                if (new DriveInfo(Path.GetPathRoot(prep)!).AvailableFreeSpace < new FileInfo(src).Length + (64L << 20))
                    throw new IOException(L.T("core.compat.noSpaceForDlcCopy"));
                staged = Path.Combine(prep, Guid.NewGuid().ToString("N"));
                File.Copy(src, staged);
            }
            changes.Add(new(rel, current, staged, staged is not null));
        }
    }

    private sealed record CompatibilityUndo(
        CompatibilityReceipt Receipt,
        List<CompatibilityReceipt> Before,
        List<CompatibilityReceipt> After,
        Dictionary<string, string?> Documents
    );

    private static Dictionary<string, string> KnownMirrors(IReadOnlyList<CompatibilityReceipt> receipts)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (receipts.Count == 0)
            return known;
        var docs = ModCompatibility.Compose(receipts, receipts[0].Bases);
        var packs = docs.GetValueOrDefault(ModCompatibility.OverlayDlc) is { } list ? Dlc.DlcList.Packs(list) : [];
        foreach (var r in receipts)
        foreach (var pair in r.Mirrors)
            if (
                packs.Contains(pair.Key[@"onigiri\dlcpacks\".Length..].Split('\\')[0], StringComparer.OrdinalIgnoreCase)
            )
                known[pair.Key] = pair.Value;
        return known;
    }

    internal void PreflightCompatibility()
    {
        using var gate = GameOperationGate.Acquire(game);
        try
        {
            var receipts = ModCompatibility.Load(game);
            if (receipts.Count == 0)
                return;
            var expected = ModCompatibility.Compose(receipts, receipts[0].Bases);
            var current = ReadCompatibilityDocuments();
            foreach (var key in ModCompatibility.Documents)
                if (!ModCompatibility.Same(current[key], expected.GetValueOrDefault(key)))
                    throw new IOException(L.T("core.compat.sharedFileChangedRollback", key));
            var mirrors = KnownMirrors(receipts);
            foreach (var pair in mirrors)
                if (
                    FileTransactionEngine.HashOrNull(Path.Combine(game.Root, pair.Key)) != pair.Value
                    || FileTransactionEngine.HashOrNull(
                        Path.Combine(game.Root, @"mods\update\x64\dlcpacks\" + pair.Key[@"onigiri\dlcpacks\".Length..])
                    ) != pair.Value
                )
                    throw new IOException(L.T("core.compat.dlcCopyChangedRollback", pair.Key));
        }
        catch (Exception ex)
            when (ex
                    is System.Xml.XmlException
                        or System.Xml.XPath.XPathException
                        or ArgumentException
                        or FormatException
            )
        {
            throw new IOException(L.T("core.compat.sharedConfigCorrupted"), ex);
        }
    }

    private CompatibilityUndo? PrepareCompatibilityUndo(BackupRecord record, bool force = false)
    {
        var file = record.Files.SingleOrDefault(f => ModCompatibility.IsReceipt(f.Rel));
        if (file is null)
            return null; // Older installations retain their original strict undo path.
        var receipts = ModCompatibility.Load(game);
        var receipt = receipts.SingleOrDefault(r => r.Path.Equals(file.Rel, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException(L.T("core.compat.receiptMissing"));
        var expected = ModCompatibility.Compose(receipts, receipt.Bases);
        var current = ReadCompatibilityDocuments();
        var after = receipts.Where(r => r.Id != receipt.Id).ToList();
        var documents = ModCompatibility.Compose(after, receipt.Bases);
        foreach (var key in ModCompatibility.Documents)
            if (!ModCompatibility.Same(current[key], expected.GetValueOrDefault(key)))
            {
                if (!force)
                    throw new IOException(L.T("core.compat.sharedFileChangedRollback", key));
                // Edited by hand or by a mod installed outside Mod Hub: keep the document as it is now and take out
                // only this installation's own pack lines. Raised pool sizes are harmless and stay.
                documents[key] =
                    current[key] is { } text && key.EndsWith("dlclist.xml", StringComparison.OrdinalIgnoreCase)
                        ? OnigiriOverlay
                            .SyncPacks(
                                text,
                                [],
                                receipt.AddedPacks.Except(
                                    after.SelectMany(r => r.AddedPacks),
                                    StringComparer.OrdinalIgnoreCase
                                )
                            )
                            .text
                        : current[key];
            }
        return new(receipt, receipts, after, documents);
    }

    private void FinishCompatibilityUndo(CompatibilityUndo compatibility, List<FileChange> changes, string prep)
    {
        var now = ReadCompatibilityDocuments();
        foreach (var key in ModCompatibility.Documents.Where(k => !k.Contains('|')))
        {
            var text = compatibility.Documents.GetValueOrDefault(key);
            if (ModCompatibility.Same(now[key], text))
                continue;
            string? source = null;
            if (text is not null)
            {
                source = Path.Combine(prep, Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(source, ModCompatibility.Encode(text));
            }
            changes.Add(
                new(key, FileTransactionEngine.HashOrNull(Path.Combine(game.Root, key)), source, source is not null)
            );
        }
        // A profile can be removed before its addon; those addon mirrors are derived files, not independent ownership.
        ReconcileMirrors(changes, prep, compatibility.Before, compatibility.Documents, compatibility.After);
    }
}
