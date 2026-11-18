using System.Diagnostics;
using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Base;

public enum ShvdnVariant
{
    Nightly,
    Enhanced,
}

public enum BaseSetupStage
{
    ModsLoader,
    ScriptHookV,
    ScriptHookVDotNet,
    Complete,
}

/// <summary>State of the base modding components in the game folder.</summary>
public sealed record ModdingBaseStatus
{
    public bool AsiLoader { get; init; }
    public string? AsiLoaderName { get; init; }
    public bool ScriptHookV { get; init; }
    public bool ModsLoader { get; init; }
    public string? ModsLoaderName { get; init; }
    public bool ModsLoaderUpdateAvailable { get; init; }
    public bool Shvdn { get; init; }
    public string? ShvdnName { get; init; }

    /// <summary>Game exe version (1.0.3889.0 / 1.0.1158.13), used to check Script Hook V compatibility.</summary>
    public string? GameVersion { get; init; }

    public bool Complete => AsiLoader && ScriptHookV && ModsLoader && Shvdn && !ModsLoaderUpdateAvailable;
}

/// <summary>
/// Modding essentials: ASI Loader and Script Hook V (official dev-c.com archive), the mods\ folder loader
/// (RageOpenV, downloaded from its authors' GitHub release, see RageOpenVRelease) and ScriptHookVDotNet (official GitHub release). Installed as a regular
/// transaction with a backup and undo in History. Existing files are never overwritten:
/// if a component is already present (in any variant), it is left alone.
/// </summary>
public static partial class ModdingBase
{
    public const string RageOpenVAsi = "RageOpenV.asi";

    // Only our published build or a byte-identical, journaled ModHub installation can be updated automatically.
    public static bool CanUpdateRageOpenV(GameInstall game, string bundledAsi)
    {
        if (!IsPe(bundledAsi) || game.CheckModLoader().Name != RageOpenVAsi)
            return false;
        var hash = FileTransactionEngine.HashOrNull(Path.Combine(game.Root, RageOpenVAsi));
        if (hash is null || hash == FileTransactionEngine.Hash(bundledAsi))
            return false;
        if (
            hash.Equals(
                "8BA4CE692B52B2D2DA9D5B125B4607B8F27ED98B891DF68EA220BEF76AC8D829",
                StringComparison.OrdinalIgnoreCase
            )
        )
            return true;
        return game.CreateEngine()
            .History()
            .Any(j =>
                j.State == TxState.Completed
                && j.TargetRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase)
                && j.Title == "База для моддинга: RageOpenV"
                && j.Ops.Any(o => o.Rel == RageOpenVAsi && o.PostHash == hash)
            );
    }

    public static BaseSetupStage NextSetupStage(ModdingBaseStatus status) =>
        !status.ModsLoader || status.ModsLoaderUpdateAvailable ? BaseSetupStage.ModsLoader
        : !status.ScriptHookV || !status.AsiLoader ? BaseSetupStage.ScriptHookV
        : !status.Shvdn ? BaseSetupStage.ScriptHookVDotNet
        : BaseSetupStage.Complete;

    private static readonly string[] AsiLoaders =
    [
        "dinput8.dll",
        "dsound.dll",
        "version.dll",
        "winmm.dll",
        "xinput1_3.dll",
        "xinput9_1_0.dll",
    ];

    /// <summary>
    /// GTA5_Enhanced.exe imports XINPUT1_4, VERSION, WINMM and DSOUND but never loads dinput8.dll: an ASI loader named
    /// dinput8.dll (the Legacy one from the Script Hook V archive) does nothing there, and no .asi, Script Hook V script
    /// or NaturalVision runs. The Enhanced loader is "GTA V ENHANCED ASI LOADER" named xinput1_4.dll (shipped with NVE).
    /// The game has no xinput1_4.dll of its own in its folder (it uses the system one).
    /// </summary>
    public static readonly string[] EnhancedAsiLoaders = ["xinput1_4.dll", "version.dll", "winmm.dll", "dsound.dll"];

    public const string EnhancedLoaderName = "xinput1_4.dll";

    /// <summary>ASI loaders this edition's exe actually loads.</summary>
    public static IReadOnlyList<string> LoadersFor(GameEdition edition) =>
        edition == GameEdition.Enhanced ? EnhancedAsiLoaders : AsiLoaders;

    private static readonly string[] ShvdnAsis = ["ScriptHookVDotNet.asi", "ScriptHookVDotNet-Loader.asi"];

    public static ModdingBaseStatus Inspect(GameInstall game)
    {
        var loader = LoadersFor(game.Edition).FirstOrDefault(n => IsPe(Path.Combine(game.Root, n)));
        var mods = game.CheckModLoader();
        var shvdn = ShvdnAsis.FirstOrDefault(n => IsPe(Path.Combine(game.Root, n)));
        return new ModdingBaseStatus
        {
            AsiLoader = loader is not null,
            AsiLoaderName = loader,
            ScriptHookV = IsPe(Path.Combine(game.Root, "ScriptHookV.dll")),
            ModsLoader = mods.Installed,
            ModsLoaderName = mods.Installed ? mods.Name : null,
            // Mod Hub no longer ships a loader build to compare with: an installed loader is left as it is.
            ModsLoaderUpdateAvailable = false,
            Shvdn = shvdn is not null,
            ShvdnName = shvdn,
            GameVersion = GameVersion(game),
        };
    }

    public static string? GameVersion(GameInstall game)
    {
        var exe = Path.Combine(game.Root, game.Edition == GameEdition.Enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe");
        try
        {
            return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).FileVersion?.Replace(", ", ".") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Default SHVDN variant: Enhanced gets the Enhanced build only; on Legacy the user chooses.</summary>
    public static IReadOnlyList<ShvdnVariant> ShvdnChoices(GameEdition edition) =>
        edition == GameEdition.Enhanced ? [ShvdnVariant.Enhanced] : [ShvdnVariant.Nightly, ShvdnVariant.Enhanced];

    public static string ShvdnRepo(ShvdnVariant v) =>
        v == ShvdnVariant.Nightly
            ? "scripthookvdotnet/scripthookvdotnet-nightly"
            : "Chiheb-Bacha/ScriptHookVDotNetEnhanced";

    [GeneratedRegex(@"(?<!\d)(?<v>1\.0\.\d{3,5}\.\d{1,3})(?!\d)")]
    private static partial Regex PatchRx();

    /// <summary>
    /// Whether a Script Hook V archive supports the game version: the archive name "ScriptHookV_3889.0_1158.13.zip" contains the builds.
    /// The build number (3889, 1158) is compared; the last digit is the hotfix revision (1158.13 → 1158.16), and
    /// Script Hook V keeps working across it, so it is ignored.
    /// </summary>
    public static bool ScriptHookSupports(string archiveName, string? gameVersion)
    {
        if (gameVersion is null)
            return true; // version unknown: do not block, Script Hook V will warn by itself
        var parts = gameVersion.Split('.');
        if (parts.Length < 3)
            return true;
        return Regex.IsMatch(
            Path.GetFileNameWithoutExtension(archiveName),
            $@"(^|_){Regex.Escape(parts[2])}\.\d+(_|$)"
        );
    }

    // ---------- Install plans ----------

    /// <summary>Script Hook V from the official archive: ScriptHookV.dll and, if no ASI Loader is present, its dinput8.dll. NativeTrainer is not installed.</summary>
    public static InstallPlan PrepareScriptHookV(GameInstall game, string zipPath) =>
        Prepare(
            game,
            "Script Hook V",
            () =>
            {
                if (
                    !Path.GetFileName(zipPath).StartsWith("ScriptHookV_", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(zipPath).Contains("SDK", StringComparison.OrdinalIgnoreCase)
                )
                    throw new ArgumentException(L.T("core.base.notScriptHookVDistribution"));
                var status = Inspect(game);
                if (!ScriptHookSupports(zipPath, status.GameVersion))
                    throw new ArgumentException(L.T("core.base.scriptHookVDoesNot", status.GameVersion));
                var archive = ModArchive.Open(zipPath);
                var ops = new List<OivOp>();
                if (!status.ScriptHookV)
                    ops.Add(new OivAdd(Single(archive, "ScriptHookV.dll"), "ScriptHookV.dll"));
                // The archive's loader is dinput8.dll, which only Legacy loads (see EnhancedAsiLoaders). On Enhanced it is
                // used only if the archive also ships the Enhanced loader; otherwise NaturalVision Enhanced brings it.
                if (!status.AsiLoader && game.Edition == GameEdition.Legacy)
                    ops.Add(new OivAdd(Single(archive, "dinput8.dll"), "dinput8.dll"));
                else if (
                    !status.AsiLoader
                    && archive.Files.FirstOrDefault(f =>
                        Path.GetFileName(f).Equals(EnhancedLoaderName, StringComparison.OrdinalIgnoreCase)
                    )
                        is { } enhancedLoader
                )
                    ops.Add(new OivAdd(enhancedLoader, EnhancedLoaderName));
                return (ops, archive);
            },
            allowGameRootFiles: true
        );

    /// <summary>
    /// The GTA V Enhanced ASI loader (xinput1_4.dll, "GTA V ENHANCED ASI LOADER", shipped with NaturalVision Enhanced)
    /// as its own transaction: removing NVE later keeps it, so the other ASI mods and scripts still load.
    /// xinput1_4.dll is on the protected list; if the folder ever had one, undo puts it back.
    /// </summary>
    public static InstallPlan PrepareEnhancedLoader(GameInstall game, string loaderFile) =>
        Prepare(
            game,
            "ASI Loader (GTA V Enhanced)",
            () =>
            {
                if (game.Edition != GameEdition.Enhanced)
                    throw new ArgumentException(L.T("core.base.enhancedLoaderOnlyEnhanced"));
                if (!IsPe(loaderFile))
                    throw new ArgumentException(L.T("core.base.enhancedLoaderDamaged"));
                if (Inspect(game).AsiLoader)
                    return ([], null);
                return ([new OivAdd(Path.GetFullPath(loaderFile), EnhancedLoaderName)], null);
            },
            allowGameRootFiles: true
        );

    /// <summary>RageOpenV (the downloaded official build), only if the game has no other mod loader (OpenIV.asi / OpenRPF.asi).</summary>
    public static InstallPlan PrepareRageOpenV(GameInstall game, string bundledAsi) => Prepare(game, "RageOpenV", () =>
        {
            if (!IsPe(bundledAsi))
                throw new ArgumentException(L.T("core.base.rageopenvAsiComponentMissingFrom"));
            var status = Inspect(game);
            if (status.ModsLoader && !CanUpdateRageOpenV(game, bundledAsi))
                throw new ArgumentException(L.T("core.base.modLoaderAlreadyInstalled", status.ModsLoaderName));
            return (new List<OivOp> { new OivAdd(Path.GetFullPath(bundledAsi), RageOpenVAsi) }, null);
        });

    /// <summary>
    /// ScriptHookVDotNet from the official release: the runtime .asi/.dll files from the archive root and ScriptHookVDotNet.ini if not present yet.
    /// Debug symbols, documentation and licenses are not copied. An already installed SHVDN (any variant) is left alone.
    /// </summary>
    public static InstallPlan PrepareShvdn(GameInstall game, ShvdnVariant variant, string zipPath) =>
        Prepare(game, "ScriptHookVDotNet", () =>
        {
            if (!ShvdnChoices(game.Edition).Contains(variant))
                throw new ArgumentException(L.T("core.base.onlyScripthookvdotnetEnhancedSuitableGta"));
            if (Inspect(game).Shvdn)
                throw new ArgumentException(L.T("core.base.scripthookvdotnetAlreadyInstalled"));
            var archive = ModArchive.Open(zipPath);
            var root = archive.Files.Where(f => !f.Contains('/') && !f.Contains('\\')).ToList();
            var asi = root.Where(f => f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)).ToList();
            if (asi.Count != 1 || !ShvdnAsis.Contains(asi[0], StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(L.T("core.base.archiveDoesNotLookLike"));
            var ops = new List<OivOp>();
            foreach (
                var f in root.Where(f =>
                    f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                if (File.Exists(Path.Combine(game.Root, f)))
                    throw new ArgumentException(L.T("core.base.alreadyExistsGameFolderNot", f));
                ops.Add(new OivAdd(f, f));
            }
            if (
                root.FirstOrDefault(f => f.Equals("ScriptHookVDotNet.ini", StringComparison.OrdinalIgnoreCase))
                    is { } ini
                && !File.Exists(Path.Combine(game.Root, ini))
            )
                ops.Add(new OivAdd(ini, ini));
            return (ops, archive);
        });

    private static string Single(ModArchive archive, string name) =>
        archive
            .Files.Where(f => Path.GetFileName(f.Replace('\\', '/')).Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList() switch
        {
            [var one] => one,
            [] => throw new ArgumentException(L.T("core.base.archiveHasNo", name)),
            _ => throw new ArgumentException(L.T("core.base.multipleFilesArchiveAmbiguous", name)),
        };

    private static InstallPlan Prepare(
        GameInstall game,
        string title,
        Func<(List<OivOp> ops, IModSource? source)> build,
        bool allowGameRootFiles = false
    )
    {
        ModArchive? archive = null;
        try
        {
            if (game.IsRunning())
                throw new ArgumentException(L.T("core.base.gameRunningCloseGtaV"));
            var (ops, source) = build();
            archive = source as ModArchive;
            if (ops.Count == 0)
                throw new ArgumentException(L.T("core.base.everythingAlreadyInstalled", title));
            return new OivInstaller(game).Prepare(
                global::System.String.Format("База для моддинга: {0}", title),
                ops,
                source,
                allowGameRootFiles: allowGameRootFiles
            );
        }
        catch (Exception ex)
            when (ex
                    is ArgumentException
                        or IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                        or OivFormatException
            )
        {
            var plan = new InstallPlan
            {
                Title = global::System.String.Format("База для моддинга: {0}", title),
                PrepDir = Path.Combine(game.WorkDir, "prep", "base-rejected-" + Guid.NewGuid().ToString("N")[..8]),
            };
            plan.Errors.Add(ex.Message);
            return plan;
        }
        finally
        {
            (archive as IDisposable)?.Dispose();
        }
    }

    private static bool IsPe(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
