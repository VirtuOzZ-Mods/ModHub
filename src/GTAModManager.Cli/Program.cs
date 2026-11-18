using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Peds;
using GTAModManager.Core.Transactions;

Console.OutputEncoding = System.Text.Encoding.UTF8;
#if DEBUG
GTAModManager.Core.AppDataMigration.Run(deleteSources: false);
#else
GTAModManager.Core.AppDataMigration.Run();
#endif
GTAModManager.Core.AppPaths.UseOwnTemp();

// Writing to the game folder usually requires administrator rights (Rockstar Launcher ACLs).
// Write commands relaunch gmm with UAC; output comes back through a log file.
if (args is ["--log", var logPath, .. var forwarded])
{
    var log = new StreamWriter(logPath, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
    Console.SetOut(log);
    Console.SetError(log);
    args = forwarded;
}
else if (
    args.Length > 0
    && args[0]
        is "oiv"
            or "mod"
            or "pack-add"
            or "keybind-set"
            or "keybind-switch"
            or "keybind-pad-set"
            or "undo"
            or "mod-remove"
            or "engine-sound"
            or "engine-sound-batch"
            or "mod-queue"
            or "recover"
            or "mods-disable"
            or "mods-enable"
            or "peds-install"
            or "peds-remove"
            or "peds-sync"
            or "peds-selector-install"
            or "peds-selector-remove"
            or "peds-install-batch"
            or "peds-remove-many"
            or "base-install"
            or "starter-install"
            or "vanilla-restore"
            or "history-clear"
            or "pipeline-install"
            or "nve-install"
            or "nve-remove"
            or "nve-runtime"
            or "nve-repair"
            or "adjusters-install"
            or "rpf-edit"
            or "rpf-copy"
            or "fix-permissions"
    && !args.Contains("--dry-run")
    && NeedsElevation(args)
)
{
    return RelaunchElevated(args);
}

try
{
    return args switch
    {
        ["info", var game] => Info(game),
        ["pipeline-install", var game, var request] => PipelineInstall(game, request),
        ["nve-install", var game, var request] => NaturalVisionCommand(game, request, false),
        ["nve-remove", var game, var request] => NaturalVisionCommand(game, request, true),
        ["nve-runtime", var game, var request] => NaturalVisionCommand(game, request, false, true),
        ["nve-repair", var game, var request] => NaturalVisionRepair(game, request),
        ["adjusters-install", var game, var request] => InstallAdjusters(game, request),
        ["rpf-edit", var game, var request] => PipelineRpf(game, request, false),
        ["rpf-copy", var game, var original] => PipelineRpf(game, original, true),
        ["fix-permissions", var game, var sid] => FixPermissions(game, sid),
        ["oiv", var file, var game, .. var rest] => Oiv(file, game, rest.Contains("--dry-run")),
        ["mod", var file, var game, .. var rest] => Mod(file, game, rest.Contains("--dry-run"), ExtrasArg(rest)),
        ["keybinds", var game] => Keybinds(game),
        ["keybind-set", var game, var id, var chord] => KeybindSet(game, id, chord),
        ["keybind-switch", var game, var file, var state] => KeybindSwitch(game, file, state),
        ["keybind-pad-set", var game, var id, var control] => KeybindPadSet(game, id, control),
        ["pack-add", var game, var archive, var inner, .. var files] when files.Length > 0 => PackAdd(
            game,
            archive,
            inner,
            files
        ),
        ["history", var game] => History(game),
        ["undo", var game, var id] => Undo(game, id),
        ["mod-remove", var game, var request] => ModRemove(game, request),
        ["engine-sound", var game, var request] => EngineSound(game, request),
        ["engine-sound-batch", var game, var request] => EngineSoundBatch(game, request),
        ["mod-queue", var game, var request] => ModQueue(game, request),
        ["recover", var game] => Recover(game),
        ["mods-status", var game] => ModsStatus(game),
        ["mods-disable", var game] => ChangeModMode(game, false),
        ["mods-enable", var game] => ChangeModMode(game, true),
        ["peds-install", var game, var archive, var selections] => PedsInstall(game, archive, selections),
        ["peds-install-batch", var game, var requests] => PedsBatch(game, requests),
        ["peds-remove-many", var game, var names] => PedsRemoveMany(game, names),
        ["peds-remove", var game, var spawn] => PedsAction(game, "remove", spawn),
        ["peds-sync", var game] => PedsAction(game, "sync", null),
        ["peds-selector-install", var game, var asi] => PedsAction(game, "selector-install", asi),
        ["peds-selector-remove", var game] => PedsAction(game, "selector-remove", null),
        ["peds-selector-remove", var game, "--with-peds"] => PedsRemoveEverything(game),
        ["base-install", var game, .. var rest] => BaseInstall(game, rest),
        ["starter-install", var game, var manifest] => StarterInstall(game, manifest),
        ["vanilla-restore", var game, .. var rest] => VanillaRestoreCommand(game, rest.Contains("--delete")),
        ["history-clear", var game, ..] => HistoryClear(game),
        ["base-status", var game] => BaseStatus(game),
        ["backup-setup", var dir, var quota] => BackupSetup(dir, quota),
        ["backup-off"] => BackupOff(),
        ["backup-info"] => BackupInfo(),
        _ => Usage(),
    };
}
catch (Exception ex)
    when (ex
            is OivFormatException
                or IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException
                or System.Text.Json.JsonException
    )
{
    Console.Error.WriteLine("Ошибка: " + ex.Message);
    return 1;
}

static T ReadPipelineRequest<T>(string path)
{
    if (new FileInfo(path).Length > 1L << 20)
        throw new ArgumentException(L.T("cli.requestExceeds1Mb"));
    return System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(path))
        ?? throw new ArgumentException(L.T("cli.emptyRequest"));
}
static BackupLibrary PipelineLibrary() =>
    LibraryForInstall() ?? throw new InvalidOperationException(L.T("cli.enableBackupsSettingsTheseOperations"));

// Clear History: journals and backups of all completed operations for this game. Game files are not touched.
static int HistoryClear(string root)
{
    var g = GameInstall.Open(root);
    using var gate = GameOperationGate.Acquire(g);
    var lib = LibraryForInstall();
    using var backupGate = lib is null ? null : GameOperationGate.Acquire(lib.Root);
    var keep = g.CreateEngine()
        .History()
        .Where(j =>
            j.Title?.Contains("[NVE:", StringComparison.Ordinal) == true
            && lib?.LoadRecord(g.Edition.ToString(), j.Id)?.Undone != true
        )
        .Select(j => j.Id)
        .ToHashSet(StringComparer.Ordinal);
    var ownIds = g.CreateEngine()
        .History()
        .Where(j => j.TargetRoot.Equals(g.Root, StringComparison.OrdinalIgnoreCase))
        .Select(j => j.Id)
        .ToHashSet(StringComparer.Ordinal);
    var preserveRecords = (lib?.Records(g.Edition.ToString()) ?? [])
        .Where(r => !ownIds.Contains(r.Id) || keep.Contains(r.Id))
        .Select(r => r.Id)
        .ToHashSet(StringComparer.Ordinal);
    int journals = g.CreateEngine().ClearHistory(keep);
    int records = lib?.DeleteRecords(g.Edition.ToString(), preserveRecords) ?? 0;
    long freed = lib?.CollectGarbage() ?? 0;
    Console.WriteLine(
        L.T(
            "cli.historyClearedOperationsRecordsFreed",
            journals,
            records,
            BackupLibrary.FormatSize(freed),
            (keep.Count > 0 ? L.T("cli.naturalvisionRecordsRetainedModRemoval", keep.Count) : "")
        )
    );
    return 0;
}

// Restore Vanilla: everything extra is moved to a service folder on the same drive; --delete removes it permanently right away.
static int VanillaRestoreCommand(string root, bool delete)
{
    var g = GameInstall.Open(root);
    var plan = GTAModManager.Core.Game.VanillaRestore.Inspect(g);
    var result = GTAModManager.Core.Game.VanillaRestore.Execute(g, plan, AppSettings.Load().OpenLibrary());
    Console.WriteLine(L.T("cli.removed", result.Moved, plan.Items.Count));
    foreach (var f in result.Failed)
        Console.WriteLine(L.T("cli.notRemoved", f));
    if (delete && result.Folder.Length > 0)
    {
        GTAModManager.Core.Game.VanillaRestore.DeleteQuarantine(g, result.Folder);
        Console.WriteLine(L.T("cli.removedItemsHaveBeenPermanently"));
    }
    else if (result.Folder.Length > 0)
        Console.WriteLine(L.T("cli.backupSaved", result.Folder));
    Console.WriteLine("GMM_VANILLA:" + (delete ? "" : result.Folder));
    return result.Failed.Count == 0 ? 0 : 1;
}

// Base package with a single elevation prompt: each component is its own transaction recorded in History.
static int StarterInstall(string root, string manifestPath)
{
    var g = GameInstall.Open(root);
    var m = ReadPipelineRequest<GTAModManager.Core.Base.StarterManifest>(manifestPath);
    var library = PipelineLibrary();
    int failed = 0;
    void Step(Func<InstallPlan> make)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var plan = make();
        Console.WriteLine($"== {plan.Title}");
        if (RunPlan(g, plan, false, watch, library) != 0)
            failed++;
    }
    if (m.RageOpenV is not null)
        Step(() => GTAModManager.Core.Base.ModdingBase.PrepareRageOpenV(g, m.RageOpenV));
    if (m.ScriptHookV is not null)
        Step(() => GTAModManager.Core.Base.ModdingBase.PrepareScriptHookV(g, m.ScriptHookV));
    if (m.ShvdnVariant is { } v && m.Shvdn is not null)
        Step(() => GTAModManager.Core.Base.ModdingBase.PrepareShvdn(g, v, m.Shvdn));
    if (m.Adjusters.Count > 0)
    {
        using var gate = GameOperationGate.Acquire(g);
        try
        {
            Step(() => GTAModManager.Core.Installation.AdjusterComponents.Prepare(g, m.Adjusters));
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            Console.WriteLine(L.T("cli.adapters", ex.Message));
            failed++;
        }
    }
    if (m.GameConfigArchive is not null && m.GameConfigSource is not null)
    {
        Console.WriteLine("== GameConfig");
        var request = new GTAModManager.Core.Installation.InstallationRequest
        {
            Source = m.GameConfigArchive,
            PrepareMods = true,
            ConfirmUnverified = true,
            ConfirmOverwrite = true,
        };
        request.Mappings[m.GameConfigSource] = @"update\update.rpf|common\data\gameconfig.xml";
        try
        {
            var result = GTAModManager.Core.Installation.InstallationService.Execute(g, request, library);
            Console.WriteLine($"{result.State}: {result.Message}");
            if (result.State != TxState.Completed)
                failed++;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            Console.WriteLine("GameConfig: " + ex.Message);
            failed++;
        }
    }
    return failed == 0 ? 0 : 1;
}
static int InstallAdjusters(string root, string request)
{
    var game = GameInstall.Open(root);
    using var gate = GameOperationGate.Acquire(game);
    using var plan = GTAModManager.Core.Installation.AdjusterComponents.Prepare(
        game,
        ReadPipelineRequest<Dictionary<string, string>>(request)
    );
    return RunPlan(game, plan, false, System.Diagnostics.Stopwatch.StartNew(), PipelineLibrary());
}
static void WriteComponentProgress(GTAModManager.Core.NaturalVision.NveProgress update)
{
    Console.WriteLine(update.ToLine());
    Console.Out.Flush();
}

static int NaturalVisionRepair(string root, string request)
{
    var game = GameInstall.Open(root);
    var operation = ReadPipelineRequest<GTAModManager.Core.NaturalVision.NveRemoveRequest>(request);
    var state = new GTAModManager.Core.NaturalVision.NveStore(game).Load(operation.InstallationId);
    var library = BackupLibrary.TryOpen(state.BackupRoot)
        ?? throw new IOException(L.T("cli.originalNaturalvisionBackupLibraryUnavailable"));
    var result = new GTAModManager.Core.NaturalVision.NveService(game, library).RepairWorldTravelPatches(operation, m =>
        Console.WriteLine("NVE_PROGRESS:" + m)
    );
    Console.WriteLine("NVE_RESULT:" + System.Text.Json.JsonSerializer.Serialize(result));
    return result.Success ? 0 : 1;
}

static int NaturalVisionCommand(string root, string request, bool remove, bool runtime = false)
{
    var game = GameInstall.Open(root);
    GTAModManager.Core.NaturalVision.NveResult result;
    if (runtime)
    {
        var operation = ReadPipelineRequest<GTAModManager.Core.NaturalVision.NveRemoveRequest>(request);
        var state = new GTAModManager.Core.NaturalVision.NveStore(game).Load(operation.InstallationId);
        var library = BackupLibrary.TryOpen(state.BackupRoot)
            ?? throw new IOException(L.T("cli.originalNaturalvisionBackupLibraryUnavailable"));
        result = new GTAModManager.Core.NaturalVision.NveService(game, library).InstallPreservedAdjusters(
            operation,
            m => Console.WriteLine("NVE_PROGRESS:" + m)
        );
    }
    else if (remove)
    {
        var operation = ReadPipelineRequest<GTAModManager.Core.NaturalVision.NveRemoveRequest>(request);
        var state = new GTAModManager.Core.NaturalVision.NveStore(game).Load(operation.InstallationId);
        if (state.Phase == "Removed" || VanillaModCleanup.TryComplete(game, AppSettings.Load().OpenLibrary()))
            result = new(true, state.Id, L.T("suite.alreadyRemoved"));
        else
        {
            var library = BackupLibrary.TryOpen(state.BackupRoot)
                ?? throw new IOException(L.T("cli.originalNaturalvisionBackupLibraryUnavailable"));
            result = new GTAModManager.Core.NaturalVision.NveService(game, library).Remove(
                operation,
                m => Console.WriteLine("NVE_PROGRESS:" + m),
                WriteComponentProgress
            );
        }
    }
    else
        result = new GTAModManager.Core.NaturalVision.NveService(game, PipelineLibrary()).Install(
            ReadPipelineRequest<GTAModManager.Core.NaturalVision.NveInstallRequest>(request),
            m => Console.WriteLine("NVE_PROGRESS:" + m),
            WriteComponentProgress
        );
    Console.WriteLine("NVE_RESULT:" + System.Text.Json.JsonSerializer.Serialize(result));
    return result.Success ? 0 : 1;
}
static int PipelineInstall(string root, string request)
{
    var game = GameInstall.Open(root);
    var result = GTAModManager.Core.Installation.InstallationService.Execute(
        game,
        ReadPipelineRequest<GTAModManager.Core.Installation.InstallationRequest>(request),
        PipelineLibrary()
    );
    Console.WriteLine("GMM_PIPELINE_RESULT:" + System.Text.Json.JsonSerializer.Serialize(result));
    return result.State == TxState.Completed ? 0 : 1;
}
static int PipelineRpf(string root, string request, bool copy)
{
    var game = GameInstall.Open(root);
    using var gate = GameOperationGate.Acquire(game);
    var workspace = new GTAModManager.Core.Installation.RpfWorkspace(game);
    using var plan = copy
        ? workspace.PrepareCopy(request)
        : workspace.PrepareEdit(ReadPipelineRequest<GTAModManager.Core.Installation.RpfEditRequest>(request));
    return RunPlan(game, plan, false, System.Diagnostics.Stopwatch.StartNew(), PipelineLibrary());
}

static int Usage()
{
    Console.WriteLine(L.T("cli.gmmGtaVMiniMod"));
    return 2;
}

static int BackupSetup(string dir, string quotaText)
{
    var quota = BackupLibrary.ParseSize(quotaText);
    if (quota is null or < 50L << 20)
    {
        Console.WriteLine(L.T("cli.limitNumberUnitSuchAs"));
        return 1;
    }
    var lib = BackupLibrary.TryOpen(dir) ?? BackupLibrary.Create(dir, quota.Value);
    lib.SetQuota(quota.Value);
    var s = AppSettings.Load();
    s.BackupDir = lib.Root;
    s.BackupsDeclined = false;
    s.Save();
    Console.WriteLine(L.T("cli.backupsLimit", lib.Root, BackupLibrary.FormatSize(lib.QuotaBytes)));
    return 0;
}

static int BackupOff()
{
    var s = AppSettings.Load();
    s.BackupsDeclined = true;
    s.BackupDir = null;
    s.Save();
    Console.WriteLine(L.T("cli.backupsDisabledNewInstallationsCannot"));
    return 0;
}

static int BackupInfo()
{
    var s = AppSettings.Load();
    var lib = s.OpenLibrary();
    if (lib is null)
    {
        Console.WriteLine(s.BackupsDeclined ? L.T("cli.backupsDisabled") : L.T("cli.noBackupFolderSelected"));
        return 0;
    }
    Console.WriteLine(L.T("cli.folder", lib.Root));
    Console.WriteLine(
        L.T("cli.used", BackupLibrary.FormatSize(lib.UsedBytes()), BackupLibrary.FormatSize(lib.QuotaBytes))
    );
    Console.WriteLine(
        L.T("cli.vanillaGameFilesVanilla", BackupLibrary.FormatSize(lib.UsedBytes(BackupOrigin.Vanilla)))
    );
    Console.WriteLine(L.T("cli.modFilesMods", BackupLibrary.FormatSize(lib.UsedBytes(BackupOrigin.Mods))));
    foreach (var r in lib.Records())
        Console.WriteLine(
            L.T(
                "cli.historyRow",
                r.Id,
                r.Edition,
                (r.Undone ? L.T("cli.undone") : L.T("cli.active")),
                BackupLibrary.FormatSize(r.BackupBytes(lib)),
                r.Title
            )
        );
    return 0;
}

/// <summary>Library used for installation: null means backups are disabled; throws if the user has not chosen yet.</summary>
static BackupLibrary? LibraryForInstall()
{
    var s = AppSettings.Load();
    if (s.BackupChoicePending)
        throw new OivFormatException(L.T("cli.firstDecideWhetherUseBackups"));
    if (s.BackupsDeclined)
        return null;
    return s.OpenLibrary() ?? throw new OivFormatException(L.T("cli.backupFolderUnavailableConnectDrive", s.BackupDir));
}

static int Info(string root)
{
    var g = GameInstall.Open(root);
    Console.WriteLine($"{g.Edition}: {g.Root}");
    Console.WriteLine(
        L.T(
            "cli.modsModsUpdateUpdateRpf",
            (Directory.Exists(g.ModsDir) ? L.T("cli.present") : L.T("cli.no")),
            (File.Exists(Path.Combine(g.ModsDir, "update", "update.rpf")) ? L.T("cli.present") : L.T("cli.no"))
        )
    );
    Console.WriteLine(L.T("cli.modLoader", g.CheckModLoader().Message));
    Console.WriteLine(L.T("cli.gameRunning", (g.IsRunning() ? L.T("cli.yes") : L.T("cli.no"))));
    Console.WriteLine(L.T("cli.serviceFolder", g.WorkDir));
    return 0;
}

static int Oiv(string file, string root, bool dryRun)
{
    var g = GameInstall.Open(root);
    using var pkg = OivPackage.Open(file);
    Console.WriteLine(L.T("cli.packageOiv", pkg.Info.Name, pkg.Info.Version, pkg.Info.Author, pkg.Info.FormatVersion));
    Console.WriteLine(L.T("cli.game", g.Edition, g.Root));
    var editionHint = pkg.Info.Name + " " + Path.GetFileName(file);
    if (
        g.Edition == GameEdition.Enhanced && editionHint.Contains("Legacy", StringComparison.OrdinalIgnoreCase)
        || g.Edition == GameEdition.Legacy && editionHint.Contains("Enhanced", StringComparison.OrdinalIgnoreCase)
    )
        Console.WriteLine(L.T("cli.warningPackageNameSuggestsDifferent"));

    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var plan = new OivInstaller(g).Prepare(pkg);
    return RunPlan(g, plan, dryRun, sw);
}

// --extras all | none | "name1.asi;name2.asi": optional components next to the OIV (installed after it).
static string? ExtrasArg(string[] rest)
{
    int i = Array.IndexOf(rest, "--extras");
    return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
}

static int Mod(string file, string root, bool dryRun, string? extras = null)
{
    var g = GameInstall.Open(root);
    using var archive = GTAModManager.Core.Mods.ModArchive.Open(file);
    var analysis = GTAModManager.Core.Mods.ModAnalyzer.Analyze(archive, g);
    Console.WriteLine(L.T("cli.mod", analysis.Title, analysis.Summary));
    Console.WriteLine(L.T("cli.game2", g.Edition, g.Root));
    foreach (var p in analysis.Parts)
        Console.WriteLine($"  {p.Kind, -14} {p.Source}  →  {p.Target}");
    foreach (var n in analysis.Notes)
        Console.WriteLine("  · " + n);

    if (analysis.NestedOiv.Count > 0)
    {
        if (analysis.NestedOiv.Count > 1)
        {
            Console.WriteLine(L.T("cli.archiveContainsMultipleOivPackages"));
            return 1;
        }
        // OIV inside an archive: extract it to a temp folder and install it as a regular package.
        var temp = Path.Combine(Path.GetTempPath(), "ModHub", "oiv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var oivPath = Path.Combine(temp, Path.GetFileName(analysis.NestedOiv[0]));
        int code;
        try
        {
            using (var src = archive.OpenSource(analysis.NestedOiv[0]))
            using (var dst = File.Create(oivPath))
                src.CopyTo(dst);
            code = Oiv(oivPath, root, dryRun);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, true);
            }
            catch (IOException) { }
        }
        // Optional components strictly after the OIV (NOS author: the OIV removes the old ASI on update).
        var chosen = analysis
            .Extras.Where(x =>
                extras is "all"
                || (
                    extras is not null and not "none"
                    && extras
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Contains(Path.GetFileName(x.Source), StringComparer.OrdinalIgnoreCase)
                )
            )
            .ToList();
        if (code != 0 || chosen.Count == 0)
            return code;
        return InstallExtras(g, archive, chosen, dryRun);
    }

    if (analysis.Errors.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine(L.T("cli.cannotInstall"));
        foreach (var e in analysis.Errors)
            Console.WriteLine("  ✗ " + e);
        return 1;
    }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var plan = new OivInstaller(g).Prepare(analysis.Title, analysis.Ops, archive);
    return RunPlan(g, plan, dryRun, sw);
}

/// <summary>Extra ASI files from the archive (and the matching .ini if not present yet) as a separate transaction, undoable in History.</summary>
static int InstallExtras(
    GameInstall g,
    GTAModManager.Core.Mods.ModArchive archive,
    List<GTAModManager.Core.Mods.ExtraComponent> chosen,
    bool dryRun
)
{
    var ops = new List<OivOp>();
    foreach (var x in chosen)
    {
        using (var s = archive.OpenSource(x.Source))
        {
            // Only a real PE library (MZ…PE), not a renamed file.
            var head = new byte[0x200];
            int n = s.Read(head, 0, head.Length);
            int pe = n > 0x40 ? BitConverter.ToInt32(head, 0x3C) : -1;
            if (
                n < 0x40
                || head[0] != 'M'
                || head[1] != 'Z'
                || pe <= 0
                || pe + 4 > n
                || head[pe] != 'P'
                || head[pe + 1] != 'E'
            )
                throw new InvalidDataException(L.T("cli.notAsiLibrary", Path.GetFileName(x.Source)));
        }
        ops.Add(new OivAdd(x.Source, x.Target));
        if (x.IniSource is { } ini && !File.Exists(Path.Combine(g.Root, Path.GetFileName(ini))))
            ops.Add(new OivAdd(ini, Path.GetFileName(ini)));
    }
    Console.WriteLine();
    Console.WriteLine(L.T("cli.additionalAfterOiv", string.Join(", ", chosen.Select(x => Path.GetFileName(x.Source)))));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var plan = new OivInstaller(g).Prepare(
        L.T("cli.additional", string.Join(", ", chosen.Select(x => Path.GetFileName(x.Source)))),
        ops,
        archive
    );
    return RunPlan(g, plan, dryRun, sw);
}

static int PackAdd(string root, string archive, string inner, string[] files)
{
    var g = GameInstall.Open(root);
    var ops = new List<OivOp>
    {
        new OivArchive(
            archive,
            CreateIfNotExist: false,
            "RPF7",
            files.Select(f => (OivOp)new OivAdd(Path.GetFullPath(f), Path.Combine(inner, Path.GetFileName(f)))).ToList()
        ),
    };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var plan = new OivInstaller(g).Prepare(
        L.T("cli.adding", archive, string.Join(", ", files.Select(Path.GetFileName))),
        ops
    );
    return RunPlan(g, plan, dryRun: false, sw);
}

static int RunPlan(
    GameInstall g,
    InstallPlan plan,
    bool dryRun,
    System.Diagnostics.Stopwatch sw,
    BackupLibrary? requiredLibrary = null
)
{
    Console.WriteLine();
    Console.WriteLine(L.T("cli.plan"));
    foreach (var group in plan.Items.GroupBy(i => i.Action))
    {
        Console.WriteLine($"  {group.Key}: {group.Count()}");
        foreach (var i in group.Take(8))
            Console.WriteLine($"    {i.Target}{(i.Detail.Length > 0 ? "  — " + Cut(i.Detail) : "")}");
        if (group.Count() > 8)
            Console.WriteLine(L.T("cli.more", group.Count() - 8));
    }
    var skipped = plan.Log.Count(l => l.EndsWith("не найден (пропуск)"));
    foreach (var l in plan.Log.Where(l => !l.EndsWith("не найден (пропуск)")))
        Console.WriteLine("  · " + l);
    if (skipped > 0)
        Console.WriteLine(L.T("cli.removalSkippedNoFiles", skipped));
    Console.WriteLine(L.T("cli.gameFilesModifyPreparationS", plan.Changes.Count, sw.Elapsed.TotalSeconds));
    foreach (var c in plan.Changes)
        Console.WriteLine(
            L.T(
                "cli.indentedPair",
                (
                    c.SourcePath is null ? L.T("cli.delete")
                    : c.ExpectedPreHash is null ? L.T("cli.create")
                    : L.T("cli.replace")
                ),
                c.RelativePath
            )
        );

    if (plan.Errors.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine(L.T("cli.cannotInstall"));
        foreach (var e in plan.Errors)
            Console.WriteLine("  ✗ " + e);
        return 1;
    }
    if (dryRun)
    {
        Console.WriteLine(L.T("cli.dryRunGameWasNot"));
        return 0;
    }
    // Every change is already in place (for example a pack installed earlier by hand): not an error.
    if (plan.Changes.Count == 0 && !plan.Preview)
    {
        Console.WriteLine();
        Console.WriteLine(L.T("cli.alreadyInstalledNothingChanged"));
        return 0;
    }

    sw.Restart();
    var r = new OivInstaller(g).Install(plan, requiredLibrary ?? LibraryForInstall());
    Console.WriteLine();
    Console.WriteLine(L.T("cli.elapsedResult", r.State, r.Message, sw.Elapsed.TotalSeconds));
    if (r.State == TxState.Completed)
        Console.WriteLine(L.T("cli.undoId", r.Id));
    return r.State == TxState.Completed ? 0 : 1;
}

static BackupLibrary PedLibrary() =>
    LibraryForInstall() ?? throw new OivFormatException(L.T("cli.backupsRequiredInstallRemovePeds"));

static int PedsInstall(string root, string archive, string selectionsPath)
{
    var game = GameInstall.Open(root);
    using var gate = GameOperationGate.Acquire(game);
    if (game.Edition != GameEdition.Legacy)
        throw new ArgumentException(L.T("cli.gmmAddonpedsSupportsGtaV"));
    var library = PedLibrary();
    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    if (new FileInfo(selectionsPath).Length > 1L << 20)
        throw new ArgumentException(L.T("cli.pedSelectionFileExceeds1"));
    var selections = System.Text.Json.JsonSerializer.Deserialize<List<PedSelection>>(
        File.ReadAllText(selectionsPath),
        options
    ) ?? throw new ArgumentException(L.T("cli.pedSelectionFileMustContain"));
    var analysis = PedArchiveAnalyzer.Analyze(archive);
    var discovery = new PedDiscoveryService(game).Scan();
    foreach (var warning in analysis.Warnings.Concat(discovery.Warnings))
        Console.WriteLine("  · " + warning);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    using var plan = new PedInstaller(game).PrepareInstall(analysis, selections, discovery.Entries);
    return RunPlan(game, plan, false, watch, library);
}

static int PedsAction(string root, string action, string? value)
{
    var game = GameInstall.Open(root);
    using var gate = GameOperationGate.Acquire(game);
    if (game.Edition != GameEdition.Legacy)
        throw new ArgumentException(L.T("cli.gmmAddonpedsSupportsGtaV"));
    var library = PedLibrary();
    var discovery = new PedDiscoveryService(game).Scan();
    foreach (var warning in discovery.Warnings)
        Console.WriteLine("  · " + warning);
    var component = new PedSelectorComponent(game);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    using var plan = action switch
    {
        "remove" => new PedInstaller(game).PrepareRemove(value!, discovery.Entries),
        "sync" => component.PrepareSync(discovery.Entries),
        "selector-install" => component.PrepareInstall(value!, discovery.Entries),
        "selector-remove" => component.PrepareRemove(),
        _ => throw new ArgumentException(L.T("cli.unknownPedCommand")),
    };
    return RunPlan(game, plan, false, watch, library);
}

static int History(string root)
{
    var g = GameInstall.Open(root);
    foreach (
        var j in g.CreateEngine().History().Where(j => j.TargetRoot.Equals(g.Root, StringComparison.OrdinalIgnoreCase))
    )
        Console.WriteLine(L.T("cli.files", j.Id, j.State, j.Ops.Count, j.Title));
    return 0;
}

static int Recover(string root)
{
    var g = GameInstall.Open(root);
    EnsureModsWritable(g);
    var results = g.CreateEngine().Recover();
    if (results.Count == 0)
        Console.WriteLine(L.T("cli.noIncompleteTransactions"));
    foreach (var r in results)
        Console.WriteLine($"{r.Id}: {r.State} — {r.Message}");
    return results.All(r => r.State == TxState.RolledBack) ? 0 : 1;
}

static int Undo(string root, string id)
{
    var g = GameInstall.Open(root);
    EnsureModsWritable(g);
    var r = new OivInstaller(g).Undo(id, AppSettings.Load().OpenLibrary());
    Console.WriteLine($"{r.State}: {r.Message}");
    return r.State == TxState.Undone ? 0 : 1;
}

// "Remove" on a My Mods card: undoes every Mod Hub installation of the mod, keeping other mods intact.
static int ModRemove(string root, string request)
{
    var g = GameInstall.Open(root);
    var operation = ReadPipelineRequest<GTAModManager.Core.Inventory.ModRemoveRequest>(request);
    var result = GTAModManager.Core.Inventory.ModRemoval.Remove(g, AppSettings.Load().OpenLibrary(), operation);
    Console.WriteLine("MOD_REMOVE_RESULT:" + System.Text.Json.JsonSerializer.Serialize(result));
    return result.Success ? 0 : 1;
}

// A queue of mods installed under one UAC prompt: the window sends "install N" / "undo N" / "stop" commands
// (see BatchChannel); each mod is installed exactly as "gmm mod" / "gmm oiv" would do it.
static int ModQueue(string root, string request)
{
    var queue = ReadPipelineRequest<GTAModManager.Core.Installation.BatchQueueRequest>(request);
    var g = GameInstall.Open(root);
    HashSet<string> Completed() =>
        g.CreateEngine()
            .History()
            .Where(j => j.State == TxState.Completed)
            .Select(j => j.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    return GTAModManager.Core.Installation.BatchChannel.Serve(
        queue,
        entry =>
        {
            var before = Completed();
            var output = new StringWriter();
            var console = Console.Out;
            Console.SetOut(output);
            int code;
            try
            {
                code = entry.Verb == "oiv" ? Oiv(entry.Path, root, false) : Mod(entry.Path, root, false);
            }
            catch (Exception ex)
            {
                code = 1;
                output.WriteLine(ex.Message);
            }
            finally
            {
                Console.SetOut(console);
            }
            console.Write(output);
            var lines = output
                .ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToList();
            string message =
                code == 0
                    ? lines.LastOrDefault(l => l.StartsWith("Completed")) ?? lines.LastOrDefault() ?? ""
                    : output.ToString().Trim();
            var made = Completed().Except(before).Order(StringComparer.Ordinal).ToList();
            return (code == 0, message, made);
        },
        made =>
        {
            var result = GTAModManager.Core.Inventory.ModRemoval.Remove(
                g,
                AppSettings.Load().OpenLibrary(),
                new() { Name = "queue", Operations = [.. made] }
            );
            return (result.Success, result.Message);
        }
    );
}

static int EngineSound(string root, string request)
{
    var g = GameInstall.Open(root);
    var operation = ReadPipelineRequest<GTAModManager.Core.Dlc.EngineSoundRequest>(request);
    var result = GTAModManager.Core.Dlc.EngineSounds.Assign(g, AppSettings.Load().OpenLibrary(), operation);
    Console.WriteLine("ENGINE_SOUND_RESULT:" + System.Text.Json.JsonSerializer.Serialize(result));
    return result.Success ? 0 : 1;
}

// Several cars at once under one UAC prompt (the engine sound table in My Mods).
static int EngineSoundBatch(string root, string request)
{
    var g = GameInstall.Open(root);
    var batch = ReadPipelineRequest<GTAModManager.Core.Dlc.EngineSoundBatchRequest>(request);
    var result = GTAModManager.Core.Dlc.EngineSounds.AssignMany(g, AppSettings.Load().OpenLibrary(), batch);
    Console.WriteLine("ENGINE_SOUND_BATCH_RESULT:" + System.Text.Json.JsonSerializer.Serialize(result));
    return result.Success ? 0 : 1;
}

static void EnsureModsWritable(GameInstall game)
{
    if (new ModModeService(game).Inspect().WritesBlocked)
        throw new InvalidOperationException(L.T("cli.modsDisabledSwitchingIncompleteEnable"));
}

static int ModsStatus(string root)
{
    var report = new ModModeService(GameInstall.Open(root)).Inspect();
    Console.WriteLine(L.T("cli.modeLogDirectory", report.State, report.StoragePath));
    Console.WriteLine(L.T("cli.activeLoadingComponents", report.Candidates.Count));
    foreach (var warning in report.Warnings)
        Console.WriteLine(L.T("cli.warning", warning));
    return report.State == ModModeState.Incomplete ? 1 : 0;
}

static int ChangeModMode(string root, bool enable)
{
    var service = new ModModeService(GameInstall.Open(root));
    var report = enable ? service.Enable() : service.Disable();
    Console.WriteLine(
        enable ? L.T("cli.originalNamesLocationsDisabledComponents") : L.T("cli.loadingComponentsHaveBeenRenamed")
    );
    foreach (var warning in report.Warnings)
        Console.WriteLine(L.T("cli.warning", warning));
    return report.State == ModModeState.Incomplete ? 1 : 0;
}

static bool NeedsElevation(string[] a)
{
    if (!OperatingSystem.IsWindows())
        return false;
    if (
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(
            System.Security.Principal.WindowsBuiltInRole.Administrator
        )
    )
        return false;
    // Permissions in the game folder are set on both folders and files (backups keep the originals' ACLs),
    // so guessing from folders is unreliable: write commands always run elevated.
    return true;
}

// Grants Modify on the game folder to the current user (the SID is passed by the non-elevated process). Runs under UAC.
static int FixPermissions(string root, string sid)
{
    var (code, output) = GamePermissions.Grant(root, sid);
    if (output.Length > 0)
        Console.WriteLine(output);
    return code;
}

static int RelaunchElevated(string[] a)
{
    Console.WriteLine(L.T("cli.administratorPrivilegesRequiredWriteGame"));
    var log = Path.Combine(Path.GetTempPath(), "gmm-" + Guid.NewGuid().ToString("N")[..8] + ".log");
    var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = true,
        Verb = "runas",
        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
    };
    psi.ArgumentList.Add("--log");
    psi.ArgumentList.Add(log);
    foreach (var x in a)
        psi.ArgumentList.Add(x);
    try
    {
        using var p = System.Diagnostics.Process.Start(psi)!;
        GTAModManager
            .Core.Processes.LiveLogRelay.RelayAsync(log, p.WaitForExitAsync(), Console.Out)
            .GetAwaiter()
            .GetResult();
        if (File.Exists(log))
            File.Delete(log);
        return p.ExitCode;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        Console.WriteLine(L.T("cli.uacRequestDeclinedNothingWas"));
        return 1223; // ERROR_CANCELLED: the UAC prompt was declined; not a failure, nothing changed
    }
}

static string Cut(string s) => s.Length > 140 ? s[..140] + "…" : s;

static int Keybinds(string root)
{
    var g = GameInstall.Open(root);
    var scanner = new GTAModManager.Core.Keybinds.KeybindScanner(g, AppSettings.Load().OpenLibrary());
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var r = scanner.Scan();
    Console.WriteLine(
        L.T(
            "cli.bindingsModsFilesReadCached",
            g.Edition,
            r.Entries.Count,
            r.Mods.Count,
            r.FilesScanned,
            r.FilesFromCache,
            sw.ElapsedMilliseconds
        )
    );
    foreach (var mod in r.Entries.GroupBy(e => e.ModName).OrderBy(x => x.Key))
    {
        Console.WriteLine($"== {mod.Key}");
        foreach (var e in mod)
            Console.WriteLine(
                L.T(
                    "cli.keybindRow",
                    e.Chord,
                    e.Action,
                    e.Status,
                    (e.Editable ? L.T("cli.modSuffix") : "    "),
                    e.File,
                    e.RawValue,
                    (e.Default is { } d ? L.T("cli.default", d) : "")
                )
            );
    }
    foreach (var w in r.Warnings)
        Console.WriteLine("  ! " + w);
    return 0;
}

static int KeybindSet(string root, string entryId, string chordText)
{
    var g = GameInstall.Open(root);
    var chord = GTAModManager.Core.Keybinds.KeyChord.Parse(chordText);
    if (chord is null)
    {
        Console.WriteLine(L.T("cli.couldNotParseShortcut", chordText));
        return 1;
    }
    var settings = AppSettings.Load();
    var r = GTAModManager.Core.Keybinds.KeybindEditor.Apply(
        g,
        settings.BackupsDeclined ? null : settings.OpenLibrary(),
        entryId,
        chord.Value
    );
    Console.WriteLine($"{r.State}: {r.Message}");
    return r.State == TxState.Completed ? 0 : 1;
}

static int KeybindSwitch(string root, string file, string state)
{
    if (state is not ("on" or "off"))
    {
        Console.WriteLine(L.T("cli.couldNotParseShortcut", state));
        return 1;
    }
    var settings = AppSettings.Load();
    var r = GTAModManager.Core.Keybinds.KeybindSwitches.Apply(
        GameInstall.Open(root),
        settings.BackupsDeclined ? null : settings.OpenLibrary(),
        file,
        state == "on"
    );
    Console.WriteLine($"{r.State}: {r.Message}");
    return r.State == TxState.Completed ? 0 : 1;
}

static int KeybindPadSet(string root, string entryId, string controlText)
{
    if (
        !int.TryParse(
            controlText,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var control
        )
    )
    {
        Console.WriteLine(L.T("cli.couldNotParseShortcut", controlText));
        return 1;
    }
    var settings = AppSettings.Load();
    var r = GTAModManager.Core.Keybinds.GamepadEditor.Apply(
        GameInstall.Open(root),
        settings.BackupsDeclined ? null : settings.OpenLibrary(),
        entryId,
        control
    );
    Console.WriteLine($"{r.State}: {r.Message}");
    return r.State == TxState.Completed ? 0 : 1;
}

static int BaseStatus(string root)
{
    var g = GameInstall.Open(root);
    var s = GTAModManager.Core.Base.ModdingBase.Inspect(g);
    Console.WriteLine(L.T("cli.game2", g.Edition, s.GameVersion));
    Console.WriteLine(L.T("cli.asiLoader", (s.AsiLoader ? s.AsiLoaderName : L.T("cli.no"))));
    Console.WriteLine(L.T("cli.scriptHookV", (s.ScriptHookV ? L.T("cli.present") : L.T("cli.no"))));
    Console.WriteLine(L.T("cli.modsLoader", (s.ModsLoader ? s.ModsLoaderName : L.T("cli.no"))));
    Console.WriteLine(L.T("cli.scripthookvdotnet", (s.Shvdn ? s.ShvdnName : L.T("cli.no"))));
    return 0;
}

/// <summary>Modding essentials: each component is a separate transaction with a backup (undoable in History).</summary>
static int BaseInstall(string root, string[] rest)
{
    var g = GameInstall.Open(root);
    string? shv = null,
        rage = null,
        shvdnZip = null;
    GTAModManager.Core.Base.ShvdnVariant? variant = null;
    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--shv" when i + 1 < rest.Length:
                shv = rest[++i];
                break;
            case "--rageopenv" when i + 1 < rest.Length:
                rage = rest[++i];
                break;
            case "--shvdn" when i + 2 < rest.Length:
                variant = rest[++i].ToLowerInvariant() switch
                {
                    "nightly" => GTAModManager.Core.Base.ShvdnVariant.Nightly,
                    "enhanced" => GTAModManager.Core.Base.ShvdnVariant.Enhanced,
                    _ => throw new ArgumentException(L.T("cli.shvdnVariantNightlyEnhanced")),
                };
                shvdnZip = rest[++i];
                break;
            case "--log" when i + 1 < rest.Length:
                i++;
                break;
            default:
                throw new ArgumentException(L.T("cli.unknownOption", rest[i]));
        }
    }
    var plans = new List<Func<InstallPlan>>();
    if (rage is not null)
        plans.Add(() => GTAModManager.Core.Base.ModdingBase.PrepareRageOpenV(g, rage));
    if (shv is not null)
        plans.Add(() => GTAModManager.Core.Base.ModdingBase.PrepareScriptHookV(g, shv));
    if (variant is { } v && shvdnZip is not null)
        plans.Add(() => GTAModManager.Core.Base.ModdingBase.PrepareShvdn(g, v, shvdnZip));
    if (plans.Count == 0)
    {
        Console.WriteLine(L.T("cli.nothingInstall"));
        return 1;
    }
    int failed = 0;
    foreach (var make in plans)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var plan = make();
        Console.WriteLine($"== {plan.Title}");
        if (RunPlan(g, plan, false, watch) != 0)
            failed++;
    }
    return failed == 0 ? 0 : 1;
}
static T ReadPedJson<T>(string path)
    where T : class
{
    if (new FileInfo(path).Length > 1L << 20)
        throw new ArgumentException(L.T("cli.pedRequestFileExceeds1"));
    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    return System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(path), options)
        ?? throw new ArgumentException(L.T("cli.pedRequestFileDoesNot"));
}

static int PedsBatch(string root, string requestsPath)
{
    var game = GameInstall.Open(root);
    using var gate = GameOperationGate.Acquire(game);
    if (game.Edition != GameEdition.Legacy)
        throw new ArgumentException(L.T("cli.gmmAddonpedsSupportsGtaV"));
    if (game.IsRunning())
        throw new ArgumentException(L.T("cli.gameRunningPedChangesProhibited"));
    EnsureModsWritable(game);
    var library = PedLibrary();
    var requests = ReadPedJson<List<PedInstallRequest>>(requestsPath);
    if (requests.Count is < 1 or > 100)
        throw new ArgumentException(L.T("cli.pedQueueRequires1100"));
    Console.WriteLine(L.T("cli.eachArchiveInstalledSeparateTransaction"));
    var outcomes = PedInstallBatch.Run(game, requests, library);
    foreach (var outcome in outcomes)
        Console.WriteLine(
            L.T("cli.text", (outcome.Succeeded ? L.T("cli.done") : "Ошибка"), outcome.ArchivePath, outcome.Message)
        );
    Console.WriteLine("GMM_PEDS_BATCH_RESULT:" + System.Text.Json.JsonSerializer.Serialize(outcomes));
    return outcomes.All(o => o.Succeeded) ? 0 : 1;
}

// Full removal: first every Mod Hub character (one transaction), then the menu with its saved looks.
// If removing the characters fails, the menu stays, so the user is never left with characters and no menu.
static int PedsRemoveEverything(string root)
{
    var game = GameInstall.Open(root);
    using var gate = GameOperationGate.Acquire(game);
    if (game.Edition != GameEdition.Legacy)
        throw new ArgumentException(L.T("cli.gmmAddonpedsSupportsGtaV"));
    var library = PedLibrary();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var names = new PedInstaller(game).ListManaged().Entries.Select(e => e.SpawnName).ToList();
    if (names.Count > 0)
    {
        var discovery = new PedDiscoveryService(game).Scan();
        using var peds = new PedInstaller(game).PrepareRemoveMany(names, discovery.Entries);
        int code = RunPlan(game, peds, false, watch, library);
        if (code != 0)
            return code;
    }
    using var menu = new PedSelectorComponent(game).PrepareRemove(withLooks: true);
    return RunPlan(game, menu, false, watch, library);
}

static int PedsRemoveMany(string root, string namesPath)
{
    var game = GameInstall.Open(root);
    using var gate = GameOperationGate.Acquire(game);
    if (game.Edition != GameEdition.Legacy)
        throw new ArgumentException(L.T("cli.gmmAddonpedsSupportsGtaV"));
    var library = PedLibrary();
    var names = ReadPedJson<List<string>>(namesPath);
    var discovery = new PedDiscoveryService(game).Scan();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    using var plan = new PedInstaller(game).PrepareRemoveMany(names, discovery.Entries);
    return RunPlan(game, plan, false, watch, library);
}
