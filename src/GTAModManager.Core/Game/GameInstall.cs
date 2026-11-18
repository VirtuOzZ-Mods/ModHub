using GTAModManager.Core.Localization;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Game;

public enum GameEdition
{
    Legacy,
    Enhanced,
}

public sealed record ModLoaderStatus(bool Installed, string Name, string Message)
{
    /// <summary>The loader is installed but turned itself off during the last game launch (RageOpenV.status.txt): the game does not see the mods folder.</summary>
    public string? DisabledReason { get; init; }
}

/// <summary>
/// A GTA V installation and OpenIV-style path rules:
/// everything related to RPF and update\ goes to mods\; ASI, scripts\ and other loose files go to the root.
/// Original game files are protected from writes and deletion.
/// </summary>
public sealed class GameInstall
{
    public string Root { get; }
    public GameEdition Edition { get; }
    public string ModsDir => Path.Combine(Root, "mods");

    /// <summary>
    /// Service folder on the same volume, outside the game folder (staging, backups, journals): X:\ModHub\Legacy|Enhanced.
    /// It must share the game's volume so installs and undo are instant renames. Until an old X:\.gmm folder is
    /// migrated (<see cref="WorkDirMigration"/>), that folder keeps being used.
    /// </summary>
    public string WorkDir => WorkDirMigration.Resolve(Root, Edition);

    // Files shipped by the game or launcher itself (seen in both installations). Only with explicit permission.
    private static readonly HashSet<string> ProtectedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "GTA5.exe",
        "GTA5_BE.exe",
        "GTA5_Enhanced.exe",
        "GTA5_Enhanced_BE.exe",
        "PlayGTAV.exe",
        "GTAVLauncher.exe",
        "GTAVLanguageSelect.exe",
        "uninstall.exe",
        "index.bin",
        "title.rgl",
        "version.txt",
        "versioninfo.txt",
        "rpf.cache",
        "bink2w64.dll",
        "d3dcompiler_46.dll",
        "d3dcsx_46.dll",
        "GFSDK_ShadowLib.win64.dll",
        "GFSDK_TXAA.win64.dll",
        "GFSDK_TXAA_AlphaResolve.win64.dll",
        "GFSDK_Aftermath_Lib.x64.dll",
        "GPUPerfAPIDX11-x64.dll",
        "NvPmApi.Core.win64.dll",
        "amd_ags_x64.dll",
        "amd_fidelityfx_dx12.dll",
        "dstorage.dll",
        "dstoragecore.dll",
        "nvngx_dlss.dll",
        "nvngx_dlssg.dll",
        "oo2core_5_win64.dll",
        "libcurl.dll",
        "libtox.dll",
        "zlib1.dll",
        "XCurl.dll",
        "xinput1_4.dll",
        "opus.dll",
        "opusenc.dll",
        "fvad.dll",
        "sl.common.dll",
        "sl.dlss.dll",
        "sl.dlss_g.dll",
        "sl.interposer.dll",
        "sl.pcl.dll",
        "sl.reflex.dll",
    };

    private static readonly HashSet<string> ProtectedRootDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "x64",
        "update",
        "BattlEye",
        "Redistributables",
        "D3D12-REDIST",
    };

    private GameInstall(string root, GameEdition edition)
    {
        Root = Path.GetFullPath(root).TrimEnd('\\');
        Edition = edition;
    }

    public static GameInstall Open(string root)
    {
        GameInstall game =
            File.Exists(Path.Combine(root, "GTA5_Enhanced.exe")) ? new(root, GameEdition.Enhanced)
            : File.Exists(Path.Combine(root, "GTA5.exe")) ? new(root, GameEdition.Legacy)
            : throw new DirectoryNotFoundException(L.T("core.game.hasNoGta5ExeGta5", root));
        WorkDirMigration.EnsureMigrated(game.Root, game.Edition);
        return game;
    }

    /// <summary>ModHub's own data folder (inside the program folder, see <see cref="AppPaths"/>).</summary>
    public static string AppDataDir => AppPaths.Data;

    private Crypto.Gta5Keys? _keys;

    /// <summary>Decryption keys from this installation's exe (cached locally).</summary>
    public Crypto.Gta5Keys LoadKeys() =>
        _keys ??= Crypto.Gta5Keys.Load(
            Path.Combine(Root, Edition == GameEdition.Enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe"),
            AppPaths.KeyCache,
            AppPaths.MagicDat
        );

    public Crypto.Gta5Keys? TryLoadKeys()
    {
        try
        {
            KeysError = null;
            return LoadKeys();
        }
        catch (Exception ex) when (ex is Crypto.KeysNotFoundException or IOException or UnauthorizedAccessException)
        {
            KeysError =
                ex is Crypto.KeysNotFoundException
                    ? ex.Message
                    : L.T(
                        "core.game.couldNotReadMayLocked",
                        (Edition == GameEdition.Enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe"),
                        ex.Message
                    );
            return null;
        }
    }

    /// <summary>Audio bank (.awc) key: from the key cache, or for an older cache from magic.dat using the same AES key.</summary>
    public uint[]? TryLoadAwcKey()
    {
        var keys = TryLoadKeys();
        if (keys is null)
            return null;
        if (keys.AwcKey.Length == 4)
            return keys.AwcKey;
        try
        {
            var magic = AppPaths.MagicDat;
            var key = File.Exists(magic) ? Crypto.Gta5Keys.AwcKeyFromMagic(keys.AesKey, File.ReadAllBytes(magic)) : [];
            return key.Length == 4 ? key : null;
        }
        catch (Exception ex) when (ex is Crypto.KeysNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reason for the last failed key load (for a clear user message).</summary>
    public string? KeysError { get; private set; }

    /// <summary>
    /// Mod loader for the mods\ folder: Legacy uses OpenIV.asi (installed via OpenIV → ASI Manager),
    /// Enhanced uses OpenRPF.asi. The open-source RageOpenV.asi works for both versions.
    /// </summary>
    public ModLoaderStatus CheckModLoader()
    {
        // NaturalVision Enhanced: onigiri is the loader. It reads onigiri\ instead of mods\ and turns RageOpenV off
        // (NVE also warns about OpenRPF), so mods\ content reaches the game through onigiri copies (OnigiriLayer).
        if (UsesOnigiri)
            return new(true, OnigiriAsi, L.T("core.game.onigiriLoader"));
        string[] accepted =
            Edition == GameEdition.Legacy ? ["OpenIV.asi", "RageOpenV.asi"] : ["OpenRPF.asi", "RageOpenV.asi"];
        foreach (var name in accepted)
        {
            var path = Path.Combine(Root, name);
            if (File.Exists(path) && IsPeFile(path))
            {
                var status = new ModLoaderStatus(true, name, L.T("core.game.installed", name));
                if (name == "RageOpenV.asi" && RageOpenVDisabledReason() is { } reason)
                    status = status with { DisabledReason = reason };
                return status;
            }
        }
        return new(false, "RageOpenV.asi", L.T("core.game.modsLoaderWasNotFound", string.Join(" / ", accepted)));
    }

    /// <summary>RageOpenV writes its state next to the exe; disabled / failed-restart-required means the hooks were not enabled.</summary>
    private string? RageOpenVDisabledReason()
    {
        var file = Path.Combine(Root, "RageOpenV.status.txt");
        try
        {
            if (!File.Exists(file))
                return null;
            var lines = File.ReadAllLines(file);
            var state = lines
                .FirstOrDefault(l => l.StartsWith("state=", StringComparison.OrdinalIgnoreCase))
                ?[6..].Trim();
            if (state is not ("disabled" or "failed-restart-required"))
                return null;
            var reason = lines
                .FirstOrDefault(l => l.StartsWith("reason=", StringComparison.OrdinalIgnoreCase))
                ?[7..].Trim()
                ?? state;
            return L.T("core.game.modsFolderLoaderRageopenvTurned", reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public const string OnigiriAsi = "onigiri.asi";

    /// <summary>mods\ folder loaders that cannot work next to onigiri (RageOpenV turns itself off, NVE warns about OpenRPF).</summary>
    public static readonly string[] LoadersReplacedByOnigiri = ["OpenRPF.asi", "RageOpenV.asi"];

    /// <summary>NaturalVision Enhanced: the onigiri loader mounts dlcpacks:/ from its own onigiri\dlcpacks folder.</summary>
    public bool UsesOnigiri =>
        Edition == GameEdition.Enhanced
        && File.Exists(Path.Combine(Root, OnigiriAsi))
        && Directory.Exists(Path.Combine(Root, "onigiri"));

    /// <summary>
    /// Path of a new add-on DLC pack ("update\x64\dlcpacks\&lt;pack&gt;\…", also under mods\): the remainder starting at the pack name;
    /// null if it is not a pack or it is the game's own pack (its copy still goes to mods\).
    /// </summary>
    public string? AddonPackRemainder(string rel)
    {
        var parts = rel.Split('\\');
        int start = parts.Length > 0 && parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (
            parts.Length < start + 5
            || !parts[start].Equals("update", StringComparison.OrdinalIgnoreCase)
            || !parts[start + 1].Equals("x64", StringComparison.OrdinalIgnoreCase)
            || !parts[start + 2].Equals("dlcpacks", StringComparison.OrdinalIgnoreCase)
        )
            return null;
        var pack = parts[start + 3];
        if (Directory.Exists(Path.Combine(Root, "update", "x64", "dlcpacks", pack)))
            return null;
        return string.Join('\\', parts[(start + 3)..]);
    }

    private static bool IsPeFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Overrides the running-game check (for tests).</summary>
    public Func<bool>? RunningCheckOverride { get; set; }

    public bool IsRunning() =>
        RunningCheckOverride?.Invoke()
        ?? GameProcessGuard.AnyRunning(
            Edition == GameEdition.Enhanced ? GameProcessGuard.EnhancedProcesses : GameProcessGuard.LegacyProcesses,
            Root
        );

    public FileTransactionEngine CreateEngine() =>
        new(Root, WorkDir)
        {
            IsGameRunning = IsRunning,
            IsWriteBlocked = () => new ModModeService(this).Inspect().WritesBlocked,
        };

    public static string Normalize(string path)
    {
        var p = path.Replace('/', '\\').Trim().TrimStart('\\');
        var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(s => s is "." or "..") || p.Contains(':'))
            throw new ArgumentException(L.T("core.common.invalidPath", path));
        return string.Join('\\', parts);
    }

    /// <summary>Where a package path is actually written (relative to the game root).</summary>
    public string MapWriteTarget(string packagePath)
    {
        var p = Normalize(packagePath);
        // Keep a canonical copy for removal of NVE; OivInstaller mirrors registered addon packs for onigiri.
        if (IsInMods(p))
            return p;
        // NaturalVision Enhanced uses its own mod-only namespace, not the game archives.
        if (Edition == GameEdition.Enhanced && p.StartsWith("onigiri\\", StringComparison.OrdinalIgnoreCase))
            return p;
        if (IsGameDataPath(p))
            return "mods\\" + p;
        return p;
    }

    public static bool IsInMods(string rel) => rel.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Game data paths that OpenIV redirects to mods\.</summary>
    public static bool IsGameDataPath(string rel)
    {
        var first = rel.Split('\\')[0];
        return rel.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)
            || first.Equals("update", StringComparison.OrdinalIgnoreCase)
            || first.Equals("x64", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reason why writing/deleting this path is forbidden, or null.</summary>
    public string? WhyProtected(string rel)
    {
        if (IsInMods(rel))
            return null;
        var parts = rel.Split('\\');
        if (parts.Length == 1 && ProtectedRootFiles.Contains(parts[0]))
            return L.T("core.game.gameFile", rel);
        if (parts.Length == 1 && rel.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            return L.T("core.game.gameArchive", rel);
        if (ProtectedRootDirs.Contains(parts[0]))
        {
            // Exception: an archive with an unencrypted table of contents in update\ is a mod placed outside mods\.
            var full = Path.Combine(Root, rel);
            if (File.Exists(full) && IsPlainRpf(full))
                return null;
            return L.T("core.game.gameDirectory", rel);
        }
        return null;
    }

    public static bool IsPlainRpf(string path)
    {
        if (!path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var h = RpfHeader.Read(fs);
            return h.IsRpf7 && h.TocIsPlain;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
