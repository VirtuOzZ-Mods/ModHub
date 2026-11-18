using System.Net;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;

namespace GTAModManager.Core.Base;

/// <summary>Which base package components are already in the game.</summary>
public sealed record StarterStatus(
    ModdingBaseStatus Base,
    IReadOnlyList<AdjusterComponent> MissingAdjusters,
    bool GameConfig
)
{
    public bool Complete => Base.Complete && MissingAdjusters.Count == 0 && GameConfig;
    public bool NeedsModsLoader => !Base.ModsLoader || Base.ModsLoaderUpdateAvailable;

    /// <summary>Missing components in installation order, for the on-screen list.</summary>
    public IReadOnlyList<string> Missing
    {
        get
        {
            var list = new List<string>();
            if (NeedsModsLoader)
                list.Add(L.T("core.base.rageopenvModsFolderLoader"));
            if (!Base.ScriptHookV || !Base.AsiLoader)
                list.Add("Script Hook V + ASI Loader");
            if (!Base.Shvdn)
                list.Add("ScriptHookVDotNet");
            list.AddRange(MissingAdjusters.Select(a => a.Title));
            if (!GameConfig)
                list.Add("GameConfig (mods\\update\\update.rpf)");
            return list;
        }
    }
}

/// <summary>What gmm starter-install installs: files are already downloaded, one administrator prompt for everything.</summary>
public sealed class StarterManifest
{
    public string? RageOpenV { get; set; }
    public string? ScriptHookV { get; set; }
    public ShvdnVariant? ShvdnVariant { get; set; }
    public string? Shvdn { get; set; }
    public Dictionary<string, string> Adjusters { get; set; } = [];
    public string? GameConfigArchive { get; set; }
    public string? GameConfigSource { get; set; }
}

/// <summary>
/// One-click base package: everything mods need to run: RageOpenV, Script Hook V, ScriptHookVDotNet,
/// Heap Adjuster, Packfile Limit Adjuster and GameConfig (F7YO) in mods\update\update.rpf.
/// </summary>
public static class StarterPackage
{
    public static readonly Uri GameConfigPage = new("https://www.gta5-mods.com/misc/gta-5-gameconfig-300-cars");
    public const string GameConfigInner = @"common\data\gameconfig.xml";

    public static StarterStatus Inspect(GameInstall game) =>
        new(
            ModdingBase.Inspect(game),
            AdjusterComponents.Missing(game),
            !LimitSetup.GameConfigNeeded(game) || GameConfigInstalled(game)
        );

    /// <summary>
    /// GameConfig counts as installed if mods\update\update.rpf contains a gameconfig.xml that differs from the original.
    /// If the archives cannot be read (no keys, etc.), it counts as installed when a copy exists in mods,
    /// so experienced users are not locked out.
    /// </summary>
    public static bool GameConfigInstalled(GameInstall game)
    {
        var mods = Path.Combine(game.Root, @"mods\update\update.rpf");
        if (!File.Exists(mods))
            return false;
        try
        {
            var ws = new RpfWorkspace(game);
            var modded = ws.Read(@"mods\update\update.rpf", GameConfigInner);
            var original = ws.Read(RpfWorkspace.Update, GameConfigInner);
            return !modded.AsSpan().SequenceEqual(original);
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or NotSupportedException
                        or ArgumentException
                        or InvalidOperationException
                        or Crypto.KeysNotFoundException
            )
        {
            return true;
        }
    }

    /// <summary>
    /// Default GameConfig variant: the current version (not "Old Versions" or "Backup") for the right edition,
    /// the "For More Mods" preset if available, and stock traffic.
    /// </summary>
    public static GameConfigVariant? PickGameConfig(IReadOnlyList<GameConfigVariant> variants, GameEdition edition)
    {
        static bool Old(GameConfigVariant v) =>
            v
                .Source.Split('\\', '/')
                .Any(p =>
                    p.Equals("Old Versions", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("broken", StringComparison.OrdinalIgnoreCase)
                );
        var current = variants
            .Where(v => !Old(v) && (v.EditionHint is null || v.EditionHint == edition.ToString()))
            .ToList();
        if (current.Count == 0)
            return null;
        var more = current.Where(v => v.Preset.Contains("more mods", StringComparison.OrdinalIgnoreCase)).ToList();
        if (more.Count > 0)
            current = more;
        return current.FirstOrDefault(v => v.Traffic.Contains("stock", StringComparison.OrdinalIgnoreCase))
            ?? current.FirstOrDefault(v => v.Traffic.StartsWith("1x traffic", StringComparison.OrdinalIgnoreCase))
            ?? current[0];
    }

    /// <summary>GameConfig file for the edition from the version list on the page ("… (Enhanced)" / "… (Legacy)").</summary>
    public static Uri? PickGameConfigDownload(
        IReadOnlyList<(string Version, Uri DownloadPage)> versions,
        GameEdition edition
    )
    {
        bool enhanced = edition == GameEdition.Enhanced;
        return versions
            .FirstOrDefault(v =>
                enhanced
                    ? v.Version.Contains("enhanced", StringComparison.OrdinalIgnoreCase)
                    : v.Version.Contains("legacy", StringComparison.OrdinalIgnoreCase)
                        && !v.Version.Contains("enhanced", StringComparison.OrdinalIgnoreCase)
            )
            .DownloadPage;
    }

    public static HttpClient CreateSiteClient()
    {
        var http = new HttpClient(
            new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
            }
        )
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ModHub/1.0");
        return http;
    }

    /// <summary>Downloads F7YO's GameConfig for the edition; returns the archive and the file chosen inside it.</summary>
    public static async Task<(string Archive, string Source)> DownloadGameConfigAsync(
        HttpClient http,
        GameEdition edition,
        string destDir,
        CancellationToken ct
    )
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, GameConfigPage);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(L.T("core.common.gta5ModsReturned", (int)resp.StatusCode));
        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var download = PickGameConfigDownload(Gta5ModsDownloads.FindVersions(html, GameConfigPage), edition)
            ?? throw new InvalidDataException(L.T("core.base.noFileGtaVFound", edition));
        var archive = await Gta5ModsDownloads
            .DownloadAsync(http, new Gta5ModsDownloads.ModFile(GameConfigPage, download, null), destDir, null, ct)
            .ConfigureAwait(false);
        using var opened = ModArchive.OpenExpanded(archive);
        var variant = PickGameConfig(GameConfigVariants.Find(opened), edition)
            ?? throw new InvalidDataException(L.T("core.base.noSuitableVariantFoundGameconfig"));
        return (archive, variant.Source);
    }
}
