using System.Reflection.PortableExecutable;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;

namespace GTAModManager.Core.Installation;

public sealed record AdjusterComponent(
    string Id,
    string Title,
    string Repository,
    string Asset,
    string Asi,
    string Ini,
    string[] InstalledNames
)
{
    /// <summary>GTA5-Mods page, if the author publishes fresh builds there rather than in GitHub releases.</summary>
    public Uri? Gta5ModsPage { get; init; }

    /// <summary>Only for these editions (null means all).</summary>
    public GameEdition[]? Editions { get; init; }

    /// <summary>Our own ini instead of the one in the archive (values recommended by the author).</summary>
    public string? IniContent { get; init; }

    public bool AppliesTo(GameEdition edition) => Editions is null || Editions.Contains(edition);

    /// <summary>The log the ASI writes to the game folder on launch, and the line indicating success.</summary>
    public string? LogFile { get; init; }
    public string? OkMarker { get; init; }
}

public enum AdjusterState
{
    NotRunYet,
    Working,
    Failed,
}

public sealed record AdjusterHealth(AdjusterComponent Component, AdjusterState State, string Message);

public static class AdjusterComponents
{
    public static IReadOnlyList<AdjusterComponent> Catalog { get; } =
    [
        new(
            "heap",
            "Heap Adjuster",
            "Chiheb-Bacha/HeapAdjusterEnhanced",
            "HeapAdjusterEnhanced.zip",
            "HeapAdjuster.asi",
            "HeapAdjuster.ini",
            ["HeapAdjuster.asi", "ZHeapAdjuster.asi", "GTAV.HeapAdjuster.asi", "HeapAdjusterEnhanced.asi"]
        )
        {
            LogFile = "HeapAdjuster.log",
            OkMarker = "Heap Size Adjusted to",
        },
        new(
            "packfile",
            "Packfile Limit Adjuster",
            "Chiheb-Bacha/PackfileLimitAdjusterEnhanced",
            "PackFileLimitAdjusterEnhanced.zip",
            "PackfileLimitAdjusterEnhanced.asi",
            "PackfileLimitAdjusterEnhanced.ini",
            ["PackfileLimitAdjuster.asi", "PackfileLimitAdjusterEnhanced.asi"]
        )
        // Enhanced 1.0.1158.13+ is supported only by version 1.2 on GTA5-Mods; the latest GitHub release (v1.0.0.1) lacks it.
        {
            Gta5ModsPage = new Uri("https://www.gta5-mods.com/scripts/packfile-limit-adjuster-enhanced"),
            LogFile = "PackfileLimitAdjusterEnhanced.log",
            OkMarker = "Packfile List Size Adjusted to",
        },
        // Modkit limit: the author recommends 65536 (the maximum id); with 2048 add-on cars with tuning crash. The sources already default to it.
        new(
            "modkit",
            "Modkit Limit Adjuster",
            "Chiheb-Bacha/ModkitLimitAdjusterEnhanced",
            "ModkitLimitAdjusterEnhanced.zip",
            "ModkitLimitAdjusterEnhanced.asi",
            "ModkitLimitAdjusterEnhanced.ini",
            ["ModkitLimitAdjusterEnhanced.asi", "ModKitLimitExtender.asi"]
        )
        {
            Editions = [GameEdition.Enhanced],
            LogFile = "ModkitLimitAdjusterEnhanced.log",
            OkMarker = "Modkit List Size Adjusted to",
            IniContent = "[MODKIT_SETTINGS]\r\nMODKITARRAY_SIZE = 65536\r\n",
        },
        // StaticBounds pool (0xC0000005 crashes from heavy maps and MLOs). The archive name changes with the version, so it comes from GTA5-Mods.
        new(
            "staticbounds",
            "Static Bounds Limit Adjuster",
            "Nochala/StaticboundsLimitAdjuster",
            "",
            "StaticBoundsLimitAdjuster.asi",
            "StaticBoundsLimitAdjuster.ini",
            ["StaticBoundsLimitAdjuster.asi", "StaticBoundLimitAdjuster.asi"]
        )
        {
            Editions = [GameEdition.Enhanced],
            Gta5ModsPage = new Uri("https://www.gta5-mods.com/scripts/static-bound-limit-adjuster"),
            LogFile = "StaticBoundLimitAdjuster.log",
        },
    ];

    public static bool Present(GameInstall game, string id) =>
        Catalog.Single(c => c.Id == id).InstalledNames.Any(n => File.Exists(Path.Combine(game.Root, n)));

    /// <summary>Adjusters to offer. None next to Resource Extensions: it replaces all of them (see LimitSetup).</summary>
    public static IReadOnlyList<AdjusterComponent> Missing(GameInstall game) =>
        LimitSetup.HasResourceExtensions(game)
            ? []
            : Catalog.Where(c => c.AppliesTo(game.Edition) && !Present(game, c.Id)).ToArray();

    /// <summary>
    /// Whether installed adjusters worked during the last game launch, judged by their own logs. A log older than the ASI
    /// means the game has not been launched yet; no success line, a "FATAL ERROR" / "[FAILURE]" line or an empty log means the adjuster
    /// did not find its target in the game code, i.e. it is incompatible with this version.
    /// </summary>
    public static IReadOnlyList<AdjusterHealth> Health(GameInstall game) =>
        Health(game, GameReadiness.LastLaunchUtc(game.Root));

    /// <param name="lastLaunchUtc">Last game launch according to the launcher log: a log written earlier is not from that launch
    /// (this includes empty logs Mod Hub creates in advance, see ModLogs).</param>
    public static IReadOnlyList<AdjusterHealth> Health(GameInstall game, DateTime? lastLaunchUtc)
    {
        var result = new List<AdjusterHealth>();
        foreach (var c in Catalog.Where(c => c.LogFile is not null))
        {
            var asi = c.InstalledNames.Select(n => Path.Combine(game.Root, n)).FirstOrDefault(File.Exists);
            if (asi is null)
                continue;
            var log = Path.Combine(game.Root, c.LogFile!);
            if (
                !File.Exists(log)
                || lastLaunchUtc is null
                || File.GetLastWriteTimeUtc(log) < lastLaunchUtc
                || File.GetLastWriteTimeUtc(log) < File.GetLastWriteTimeUtc(asi)
            )
            {
                result.Add(new(c, AdjusterState.NotRunYet, L.T("core.installation.gameHasNotBeenLaunched", c.Title)));
                continue;
            }
            string text;
            try
            {
                using var fs = new FileStream(
                    log,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                if (fs.Length > 1 << 20)
                {
                    result.Add(new(c, AdjusterState.NotRunYet, ""));
                    continue;
                }
                using var r = new StreamReader(fs);
                text = r.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            bool failed =
                text.Contains("FATAL ERROR", StringComparison.OrdinalIgnoreCase)
                || text.Contains("[FAILURE]", StringComparison.OrdinalIgnoreCase)
                || c.OkMarker is not null && !text.Contains(c.OkMarker, StringComparison.OrdinalIgnoreCase);
            result.Add(
                failed
                    ? new(
                        c,
                        AdjusterState.Failed,
                        L.T(
                            "core.installation.didNotWorkLastLaunch",
                            c.Title,
                            (Base.ModdingBase.GameVersion(game) ?? "?")
                        )
                    )
                    : new(c, AdjusterState.Working, L.T("core.installation.working", c.Title))
            );
        }
        return result;
    }

    public static InstallPlan Prepare(GameInstall game, IReadOnlyDictionary<string, string> archives)
    {
        if (game.IsRunning())
            throw new InvalidOperationException(L.T("core.installation.closeGtaVBeforeInstalling"));
        foreach (var id in archives.Keys)
            if (!Catalog.Any(c => c.Id == id))
                throw new InvalidDataException(L.T("core.installation.unknownComponent", id));
        var sources = new Dictionary<string, ModArchive>();
        var ops = new List<OivOp>();
        try
        {
            foreach (
                var component in Catalog.Where(c =>
                    archives.ContainsKey(c.Id) && c.AppliesTo(game.Edition) && !Present(game, c.Id)
                )
            )
            {
                var archive = ModArchive.Open(archives[component.Id]);
                sources.Add(component.Id, archive);
                string Find(string name) =>
                    archive.Files.SingleOrDefault(f =>
                        Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)
                    ) ?? throw new InvalidDataException(L.T("core.installation.notFoundRelease", name));
                string asi = Find(component.Asi);
                if (archive.SourceSize(asi) > 32L << 20)
                    throw new InvalidDataException(L.T("core.installation.asiComponentExceeds32Mb"));
                try
                {
                    using var stream = new MemoryStream(archive.Read(asi), false);
                    using var pe = new PEReader(stream);
                    if (
                        pe.PEHeaders.PEHeader is null
                        || pe.PEHeaders.CoffHeader.Machine != Machine.Amd64
                        || (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) == 0
                    )
                        throw new InvalidDataException(
                            L.T("core.installation.expected64BitAsiLibrary", component.Title)
                        );
                }
                catch (BadImageFormatException ex)
                {
                    throw new InvalidDataException(L.T("core.installation.damagedAsiLibrary", component.Title), ex);
                }
                ops.Add(new OivAdd(component.Id + "|" + asi, component.Asi));
                if (!File.Exists(Path.Combine(game.Root, component.Ini)))
                    ops.Add(
                        new OivAdd(
                            component.Id + "|" + (component.IniContent is null ? Find(component.Ini) : IniKey),
                            component.Ini
                        )
                    );
                foreach (
                    var license in archive.Files.Where(f =>
                        Path.GetFileName(f).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
                    )
                )
                    ops.Add(
                        new OivAdd(
                            component.Id + "|" + license,
                            @"ModHubCredits\" + component.Id + "\\" + Path.GetFileName(license)
                        )
                    );
            }
            if (ops.Count == 0)
                throw new InvalidOperationException(L.T("core.installation.requiredComponentsAlreadyInstalled"));
            var inis = Catalog
                .Where(c => c.IniContent is not null)
                .ToDictionary(c => c.Id, c => System.Text.Encoding.UTF8.GetBytes(c.IniContent!));
            return new OivInstaller(game).Prepare("Компоненты GameConfig", ops, new CombinedSource(sources, inis));
        }
        finally
        {
            foreach (var archive in sources.Values)
                archive.Dispose();
        }
    }

    const string IniKey = "#ini";

    sealed class CombinedSource(Dictionary<string, ModArchive> sources, Dictionary<string, byte[]> inis) : IModSource
    {
        (string id, string file) Split(string source)
        {
            int split = source.IndexOf('|');
            if (split < 1)
                throw new InvalidDataException(L.T("core.installation.unknownSource"));
            return (source[..split], source[(split + 1)..]);
        }

        public Stream OpenSource(string source)
        {
            var (id, f) = Split(source);
            return f == IniKey ? new MemoryStream(inis[id], false) : sources[id].OpenSource(f);
        }

        public long SourceSize(string source)
        {
            var (id, f) = Split(source);
            return f == IniKey ? inis[id].Length : sources[id].SourceSize(f);
        }
    }
}
