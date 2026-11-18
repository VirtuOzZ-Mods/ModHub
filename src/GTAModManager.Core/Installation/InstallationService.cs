using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using GTAModManager.Core.Base;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Installation;

public sealed record InstallInspection(
    string Title,
    string Description,
    IReadOnlyList<string> Targets,
    IReadOnlyList<DependencyNeed> Dependencies,
    IReadOnlyList<string> Warnings
);

public sealed class InstallationRequest
{
    public string Source { get; set; } = "";
    public string? RecommendedId { get; set; }
    public string? ScriptLibraryId { get; set; }
    public string? JobId { get; set; }
    public string? SourceHash { get; set; }
    public Dictionary<string, string?> ExpectedTargets { get; set; } = [];
    public bool PrepareMods { get; set; }
    public bool ConfirmUnverified { get; set; }
    public bool ConfirmOverwrite { get; set; }
    public Dictionary<string, string> Mappings { get; set; } = [];
    public string? DownloadId { get; set; }
    public bool KeepArchive { get; set; }
    public string? ArchiveLibrary { get; set; }
    public string? SourceUrl { get; set; }
}

public static class InstallationService
{
    public static string Documentation(ModArchive archive)
    {
        var text = new StringBuilder();
        foreach (
            var file in archive
                .Files.Where(f =>
                    new[] { ".txt", ".md", ".nfo", ".html", ".htm" }.Contains(
                        Path.GetExtension(f),
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                .Take(32)
        )
        {
            if (archive.SourceSize(file) > 262144 || text.Length >= 524288)
                continue;
            using var reader = new StreamReader(archive.OpenSource(file), Encoding.UTF8, true);
            var buffer = new char[Math.Min(65536, 524288 - text.Length)];
            int n = reader.ReadBlock(buffer, 0, buffer.Length);
            text.AppendLine(file).AppendLine(DocumentRequirements.PlainDocumentation(new string(buffer, 0, n)));
        }
        return text.ToString();
    }

    public static InstallInspection Inspect(
        GameInstall game,
        string source,
        string? scriptLibraryId = null,
        string? recommendedId = null
    )
    {
        if (recommendedId is not null)
            return RecommendedMods.Inspect(game, recommendedId, source);
        if (scriptLibraryId is not null)
            return ScriptLibraries.Inspect(game, scriptLibraryId, source);
        string extension = Path.GetExtension(source).ToLowerInvariant();
        string docs = "";
        var targets = new List<string>();
        var warnings = new List<string>();
        if (extension == ".xml")
        {
            var kind = InstallDocuments.Kind(InstallDocuments.ReadText(source));
            if (kind != XmlDocumentKind.Other)
                targets.Add(@"update\update.rpf");
            if (kind == XmlDocumentKind.Other)
                warnings.Add(L.T("core.installation.xmlDestinationUnknownSelectExisting"));
        }
        else if (extension is ".asi" or ".ini" or ".json")
            targets.Add(Path.GetFileName(source));
        else if (extension == ".oiv")
        {
            using var package = OivPackage.Open(source);
            docs = package.Info.Description;
            Collect(package.Ops, targets);
        }
        else
        {
            using var archive = ModArchive.OpenExpanded(source);
            var analysis = ModAnalyzer.Analyze(archive, game);
            docs = Documentation(archive);
            targets.AddRange(analysis.Parts.Select(p => p.Target));
            warnings.AddRange(analysis.Errors.Concat(analysis.Notes));
            if (GameConfigVariants.Find(archive).Count > 0)
            {
                warnings.RemoveAll(w => w.StartsWith("Не найдено ничего, что можно установить автоматически"));
                targets.Add(@"update\update.rpf|common\data\gameconfig.xml");
            }
            if (analysis.NestedOiv.Count == 1)
            {
                string temp = Path.Combine(
                    Path.GetTempPath(),
                    "ModHub",
                    "inspect-" + Guid.NewGuid().ToString("N") + ".oiv"
                );
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                try
                {
                    using (var input = archive.OpenSource(analysis.NestedOiv[0]))
                    using (var output = File.Create(temp))
                        input.CopyTo(output);
                    using var oiv = OivPackage.Open(temp);
                    docs += "\n" + oiv.Info.Description;
                    Collect(oiv.Ops, targets);
                }
                finally
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
            }
        }
        if (
            !File.Exists(Path.Combine(game.ModsDir, "update", "update.rpf"))
            && targets.Any(p => p.Contains(".rpf", StringComparison.OrdinalIgnoreCase))
        )
            warnings.Add(L.T("core.installation.modsFolderNotReadyOnly"));
        return new(
            Path.GetFileName(source),
            docs,
            targets,
            DocumentRequirements.Inspect(game, docs, targets),
            warnings
        );
    }

    static void Collect(IEnumerable<OivOp> ops, List<string> targets)
    {
        foreach (var op in ops)
            switch (op)
            {
                case OivArchive a:
                    targets.Add(a.Path);
                    Collect(a.Ops, targets);
                    break;
                case OivAdd a:
                    targets.Add(a.Target);
                    break;
            }
    }

    public static InstallPlan Prepare(GameInstall game, InstallationRequest request, bool preview = false)
    {
        using var gate = GameOperationGate.Acquire(game);
        if (game.IsRunning())
            throw new InvalidOperationException(L.T("core.installation.closeGameBeforeInstalling"));
        if (request.SourceHash is not null && FileTransactionEngine.Hash(request.Source) != request.SourceHash)
            throw new IOException(L.T("core.installation.installerFileChangedAfterAnalysis"));
        foreach (var expected in request.ExpectedTargets)
        {
            string relative = GameInstall.Normalize(expected.Key);
            string full = Path.Combine(game.Root, relative);
            string? current = File.Exists(full) ? FileTransactionEngine.Hash(full) : null;
            if (current != expected.Value)
                throw new IOException(L.T("core.installation.fileChangedAfterPlanWas", relative));
        }
        if (request.RecommendedId is { } recommendation)
            return CheckPlan(
                game,
                RecommendedMods.Prepare(game, recommendation, request.Source, preview),
                request,
                preview
            );
        if (request.ScriptLibraryId is { } libraryId)
            return CheckPlan(game, ScriptLibraries.Prepare(game, libraryId, request.Source, preview), request, preview);
        var inspection = Inspect(game, request.Source);
        var provided = request.Mappings.Values.Concat(inspection.Targets).ToArray();
        bool Provides(DependencyNeed d) =>
            d.Id switch
            {
                "gameconfig" => provided.Any(p => p.EndsWith("gameconfig.xml", StringComparison.OrdinalIgnoreCase)),
                "shv" => provided.Any(p => p.EndsWith("ScriptHookV.dll", StringComparison.OrdinalIgnoreCase)),
                "shvdn" => provided.Any(p => p.EndsWith("ScriptHookVDotNet.asi", StringComparison.OrdinalIgnoreCase)),
                "heap" => provided.Any(p => p.EndsWith("HeapAdjuster.asi", StringComparison.OrdinalIgnoreCase)),
                "packfile" => provided.Any(p =>
                    p.EndsWith("PackfileLimitAdjuster.asi", StringComparison.OrdinalIgnoreCase)
                ),
                _ => false,
            };
        var needs = DocumentRequirements.Inspect(game, inspection.Description, provided);
        bool installsGameConfig =
            provided.Any(p => p.EndsWith("gameconfig.xml", StringComparison.OrdinalIgnoreCase))
            || Path.GetExtension(request.Source).Equals(".xml", StringComparison.OrdinalIgnoreCase)
                && InstallDocuments.Kind(InstallDocuments.ReadText(request.Source)) == XmlDocumentKind.GameConfig;
        var missing = needs
            .Where(d =>
                d.Required && !d.Present && !Provides(d) && !(installsGameConfig && d.Id is "heap" or "packfile")
            )
            .ToArray();
        if (!preview && missing.Length > 0)
            throw new InvalidOperationException(
                L.T(
                    "core.installation.requiredComponentsNotInstalledInstall",
                    string.Join(", ", missing.Select(d => d.Title))
                )
            );
        string ext = Path.GetExtension(request.Source).ToLowerInvariant();
        if (request.Mappings.Count > 0)
        {
            using var source = ext is ".zip" or ".rar" or ".7z" ? ModArchive.OpenExpanded(request.Source) : null;
            var ops = new List<OivOp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in request.Mappings)
            {
                string key = source is null ? request.Source : mapping.Key;
                if (mapping.Value.EndsWith("gameconfig.xml", StringComparison.OrdinalIgnoreCase))
                {
                    if (!request.ConfirmUnverified && !preview)
                        throw new InvalidOperationException(
                            L.T("core.installation.confirmSelectedGameconfigCompatibleCurrent")
                        );
                    string text = source is null
                        ? InstallDocuments.ReadText(request.Source)
                        : Encoding.UTF8.GetString(
                            source.SourceSize(key) <= 8L << 20
                                ? source.Read(key)
                                : throw new InvalidDataException(L.T("core.installation.gameconfigExceedsLimit"))
                        );
                    if (InstallDocuments.Kind(text) != XmlDocumentKind.GameConfig)
                        throw new InvalidDataException(L.T("core.installation.selectedFileNotGameconfig"));
                }
                if (mapping.Value.EndsWith("dlclist.xml", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(L.T("core.installation.dlcListUseSafeXml"));
                if (
                    new[]
                    {
                        ".exe",
                        ".bat",
                        ".cmd",
                        ".ps1",
                        ".vbs",
                        ".js",
                        ".msi",
                        ".reg",
                        ".lnk",
                        ".scr",
                        ".com",
                    }.Contains(Path.GetExtension(key), StringComparer.OrdinalIgnoreCase)
                )
                    throw new InvalidOperationException(
                        L.T("core.installation.executableProgramsCommandsFromPackages")
                    );
                var parts = mapping.Value.Split('|');
                string outer = GameInstall.Normalize(parts[0]);
                if (!seen.Add(mapping.Value))
                    throw new InvalidOperationException(L.T("core.installation.multipleFilesTargetSamePath"));
                OivOp op = new OivAdd(key, parts.Length == 1 ? outer : GameInstall.Normalize(parts[^1]));
                for (int i = parts.Length - 2; i >= 1; i--)
                    op = new OivArchive(GameInstall.Normalize(parts[i]), false, "RPF7", [op]);
                if (parts.Length > 1)
                    op = new OivArchive(outer, false, "RPF7", [op]);
                ops.Add(op);
            }
            return CheckPlan(
                game,
                new OivInstaller(game).Prepare(inspection.Title, ops, source, preview),
                request,
                preview
            );
        }
        if (ext == ".xml")
        {
            bool verified =
                InstallDocuments.Kind(InstallDocuments.ReadText(request.Source)) == XmlDocumentKind.GameConfig
                && GameConfigCompatibility.Verified(
                    request.Source,
                    game.Edition,
                    ModdingBase.GameVersion(game),
                    GameConfigCompatibility.Read(
                        Path.Combine(AppContext.BaseDirectory, "gameconfig-compatibility.json")
                    )
                );
            return CheckPlan(
                game,
                new RpfWorkspace(game).PrepareXmlImport(
                    request.Source,
                    request.PrepareMods || preview,
                    request.ConfirmUnverified || verified || preview,
                    preview: preview
                ),
                request,
                preview
            );
        }
        if (ext == ".asi")
        {
            using var stream = File.OpenRead(request.Source);
            using var pe = new PEReader(stream);
            if (
                pe.PEHeaders.PEHeader is null
                || (pe.PEHeaders.CoffHeader.Characteristics & System.Reflection.PortableExecutable.Characteristics.Dll)
                    == 0
            )
                throw new InvalidDataException(L.T("core.installation.asiNotPeLibrary"));
            return CheckPlan(
                game,
                new OivInstaller(game).Prepare(
                    inspection.Title,
                    [new OivAdd(Path.GetFullPath(request.Source), Path.GetFileName(request.Source))],
                    preview: preview
                ),
                request,
                preview
            );
        }
        if (ext is ".ini" or ".json")
            throw new InvalidOperationException(L.T("core.installation.selectExistingTargetFileManually"));
        if (ext == ".oiv")
        {
            using var package = OivPackage.Open(request.Source);
            return CheckPlan(game, new OivInstaller(game).Prepare(package, preview), request, preview);
        }
        using var archive = ModArchive.OpenExpanded(request.Source);
        var analysis = ModAnalyzer.Analyze(archive, game);
        if (analysis.Errors.Count > 0)
            throw new InvalidOperationException(string.Join("\n", analysis.Errors));
        if (analysis.NestedOiv.Count > 0)
        {
            if (analysis.NestedOiv.Count != 1)
                throw new InvalidOperationException(L.T("core.installation.manuallySelectOneOivFrom"));
            string temp = Path.Combine(
                Path.GetTempPath(),
                "ModHub",
                "package-" + Guid.NewGuid().ToString("N") + ".oiv"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            try
            {
                using (var input = archive.OpenSource(analysis.NestedOiv[0]))
                using (var output = File.Create(temp))
                    input.CopyTo(output);
                using var package = OivPackage.Open(temp);
                return CheckPlan(game, new OivInstaller(game).Prepare(package, preview), request, preview);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
        return CheckPlan(
            game,
            new OivInstaller(game).Prepare(analysis.Title, analysis.Ops, archive, preview),
            request,
            preview
        );
    }

    static InstallPlan CheckPlan(GameInstall game, InstallPlan plan, InstallationRequest request, bool preview)
    {
        if (!preview && !request.ConfirmOverwrite && plan.Changes.Any(c => c.ExpectedPreHash is not null))
            plan.Errors.Add(L.T("core.installation.planReplacesExistingFilesConfirm"));
        if (!preview && !request.PrepareMods && plan.Log.Any(l => l.Contains("копия оригинала будет создана")))
            plan.Errors.Add(L.T("core.installation.preparingCopyModsWasNot"));
        return plan;
    }

    static void PrepareDlcFolders(GameInstall game)
    {
        string relative = @"mods\update\x64\dlcpacks",
            path = game.Root;
        foreach (var part in relative.Split('\\'))
        {
            path = Path.Combine(path, part);
            if (
                (Directory.Exists(path) || File.Exists(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            )
                throw new IOException(L.T("core.installation.modsFolderContainsFileSystem"));
            Directory.CreateDirectory(path);
        }
    }

    public static TxResult Execute(GameInstall game, InstallationRequest request, BackupLibrary library)
    {
        using var gate = GameOperationGate.Acquire(game);
        ArgumentNullException.ThrowIfNull(library);
        if (request.DownloadId is not null)
        {
            var receipt = new ManagedDownloads().Load(request.DownloadId);
            if (
                !receipt.Completed
                || !receipt.File.Equals(Path.GetFullPath(request.Source), StringComparison.OrdinalIgnoreCase)
                || !receipt.GameRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase)
            )
                throw new InvalidOperationException(L.T("core.installation.downloadDoesNotBelongOperation"));
            request.SourceHash = receipt.Hash;
        }
        using var plan = Prepare(game, request);
        if (!plan.CanInstall)
            return new(
                "",
                TxState.Rejected,
                string.Join("\n", plan.Errors.DefaultIfEmpty(L.T("core.installation.noChanges")))
            );
        if (request.PrepareMods && plan.Changes.Any(c => GameInstall.IsInMods(c.RelativePath)))
            PrepareDlcFolders(game);
        var result = new OivInstaller(game).Install(plan, library);
        if (result.State != TxState.Completed)
            return result;
        if (request.JobId is not null)
        {
            var queue = new InstallQueueStore(game.Root);
            var record = queue.Load().FirstOrDefault(j => j.Id == request.JobId);
            if (record is not null)
            {
                record.Phase = InstallPhase.Verifying;
                record.TransactionId = result.Id;
                record.Message = L.T("core.installation.verifyingWrittenFiles");
                queue.Save(record);
            }
        }
        var journal = game.CreateEngine().LoadJournal(result.Id)
            ?? throw new IOException(L.T("core.installation.noCompletedOperationLog"));
        bool verified =
            journal.TargetRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase)
            && journal.Ops.All(op =>
                op.PostHash is null
                    ? !File.Exists(Path.Combine(game.Root, op.Rel))
                    : FileTransactionEngine.Hash(Path.Combine(game.Root, op.Rel)) == op.PostHash
            );
        if (!verified)
        {
            var undo = new OivInstaller(game).Undo(result.Id, library);
            return new(
                result.Id,
                undo.State,
                L.T("core.installation.resultVerificationFailedRecoveryPerformed", undo.Message)
            );
        }
        if (request.JobId is not null)
        {
            var store = new InstallQueueStore(game.Root);
            var job = store.Load().FirstOrDefault(j => j.Id == request.JobId);
            if (job is not null)
            {
                job.Phase = InstallPhase.Completed;
                job.TransactionId = result.Id;
                job.Message = result.Message;
                store.Save(job);
            }
        }
        if (request.DownloadId is not null)
        {
            try
            {
                new ManagedDownloads().Finish(request.DownloadId, request.KeepArchive, request.ArchiveLibrary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return result with { Message = L.T("core.installation.archiveSaved", result.Message, ex.Message) };
            }
        }
        return result;
    }
}
