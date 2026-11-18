using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;
using Microsoft.Win32;

namespace GTAModManager.Core.Installation;

public sealed record RecommendedMod(
    string Id,
    string Version,
    string Hash,
    bool Enhanced,
    int Api,
    string MinimumApi,
    string[] Libraries
)
{
    public bool Supports(GameEdition edition) => Hash.Length > 0 && (edition == GameEdition.Legacy || Enhanced);
}

/// <summary>Reviewed versions of six recommendations. Uses the ordinary dependency model and OIV pipeline.</summary>
public static class RecommendedMods
{
    public static IReadOnlyList<RecommendedMod> Catalog { get; } =
    [
        new(
            "map-editor",
            "2.13",
            "A4100AB581A3B01D6F36FB4E18AB45E84D0B60737B2C8007AF191132C0C7E940",
            false,
            2,
            "2.10.9.0",
            ["nativeui"]
        ),
        new(
            "andres-map-editor",
            "3.0",
            "0CF3087E38D8DA0BFCB552EE71CD727F7A193EFA3B24AC109ED56B38D91ED14A",
            false,
            3,
            "3.6.0.0",
            ["lemonui"]
        ),
        new(
            "enable-all-interiors",
            "47.1.1",
            "9265D26FDAE65BED2331F01E1DF43460F18E36D7221CFEA78AA093FFB0FC622D",
            true,
            3,
            "3.7.0.110",
            ["lemonui"]
        ),
        new(
            "trafficx-custom",
            "0.2.1",
            "9F5C2B4040187A0D76BEBF88D3D3C5B80634604A101ACA37DA10281CC0B71EDA",
            true,
            3,
            "3.7.0.188",
            ["lemonui"]
        ),
        new(
            "ghost-replay",
            "2.4.1",
            "F3CC7E259FA41C06DB05A8EFD0A06EF19B89B0459E0862747A1C6EDFEA13D23D",
            false,
            0,
            "0.0",
            []
        ),
        new("100-save-game", "6.1", "", false, 0, "0.0", []),
    ];

    public static RecommendedMod? Find(string id) => Catalog.SingleOrDefault(c => c.Id == id);

    public static RecommendedMod Get(string id) =>
        Find(id) ?? throw new InvalidDataException(L.T("recommended.unknown", id));

    public static void ValidateArchive(string id, string source)
    {
        var component = Get(id);
        if (component.Hash.Length == 0)
            throw new InvalidOperationException(L.T("recommended.save"));
        if (FileTransactionEngine.Hash(source) != component.Hash)
            throw new InvalidDataException(L.T("recommended.changed", id, component.Version));
    }

    const string EaiRuntimeHash = "52592D2F03B8B19FCAEB47D0442E4A2AE3E70A19E125A211CF76D5F7526AEBE3";
    static string EaiTarget => @"scripts\iFruitAddon2.dll";

    public static void CheckAutomaticDependencies(GameInstall game, string id, IEnumerable<DependencyNeed> dependencies)
    {
        foreach (var need in dependencies.Where(d => d.Required && !d.Present))
        {
            if (ProvidesBundledDependency(id, need.Id) || need.Id is "shv" or "asi" or "mods")
                continue;
            if (need.Id == "shvdn" && !Base.ModdingBase.Inspect(game).Shvdn)
                continue;
            var library = ScriptLibraries.Catalog.SingleOrDefault(c => c.Id == need.Id);
            if (
                library is not null
                && library.Supports(game.Edition)
                && !File.Exists(Path.Combine(game.Root, library.Target))
            )
                continue;
            throw new InvalidOperationException(L.T("recommended.manualDependency", need.Title));
        }
    }

    public static bool ProvidesBundledDependency(string id, string dependency) =>
        id == "enable-all-interiors" && dependency == "ifruitaddon2-eai";

    static bool EaiPresent(GameInstall game) =>
        File.Exists(Path.Combine(game.Root, EaiTarget))
        && FileTransactionEngine.Hash(Path.Combine(game.Root, EaiTarget)) == EaiRuntimeHash;

    public static IReadOnlyList<DependencyNeed> Dependencies(GameInstall game, string id, IEnumerable<string> targets)
    {
        var mod = Get(id);
        var needs = DocumentRequirements.Inspect(game, "ScriptHookV", targets).ToList();
        var minimum = Version.Parse(
            id == "trafficx-custom" && game.Edition == GameEdition.Enhanced ? "3.9.0.0" : mod.MinimumApi
        );
        for (int i = 0; i < needs.Count; i++)
            if (needs[i].Id == "shvdn")
                needs[i] = needs[i] with
                {
                    Title = $"ScriptHookVDotNet API {mod.Api} >= {minimum}",
                    Present = ScriptLibraries.HasApi(game, mod.Api, minimum),
                    Evidence = L.T("libraries.api", mod.Api, minimum),
                };
        foreach (var library in mod.Libraries)
        {
            var c = ScriptLibraries.Get(library);
            needs.Add(
                new(
                    library,
                    c.Title,
                    true,
                    ScriptLibraries.Present(game, library),
                    L.T("recommended.library", c.Title, c.Tag),
                    $"https://github.com/{c.Repository}/releases/tag/{c.Tag}"
                )
            );
        }
        if (id == "enable-all-interiors")
            needs.Add(
                new(
                    "ifruitaddon2-eai",
                    "iFruitAddon2 2.1.1 (HKH191)",
                    true,
                    EaiPresent(game),
                    L.T("recommended.eaiRuntime"),
                    "https://www.gta5-mods.com/scripts/enable-all-interiors-wip"
                )
            );
        foreach (
            var runtime in id == "map-editor" ? new[] { (2013, "x86"), (2013, "x64"), (2015, "x86"), (2015, "x64") }
            : id == "ghost-replay" ? new[] { (2015, "x64") }
            : []
        )
            needs.Add(
                new(
                    $"vc{runtime.Item1}-{runtime.Item2}",
                    $"Microsoft Visual C++ {runtime.Item1}+ {runtime.Item2}",
                    true,
                    RuntimePresent(runtime.Item1, runtime.Item2),
                    L.T("recommended.runtime"),
                    runtime.Item1 == 2013
                        ? "https://www.microsoft.com/download/details.aspx?id=40784"
                        : "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist"
                )
            );
        return needs;
    }

    static bool RuntimePresent(int year, string architecture)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(
                    $@"SOFTWARE\Microsoft\VisualStudio\{(year == 2013 ? "12.0" : "14.0")}\VC\Runtimes\{architecture}"
                );
                if (key?.GetValue("Installed") is int installed && installed == 1)
                    return true;
            }
            catch (Exception ex)
                when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return false;
    }

    static bool MapFamily(string path, bool modern)
    {
        try
        {
            using var input = File.OpenRead(path);
            using var pe = new PEReader(input);
            var md = pe.GetMetadataReader();
            var refs = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToArray();
            return modern
                ? refs.Contains("LemonUI.SHVDN3") && refs.Contains("ScriptHookVDotNet3")
                : refs.Contains("NativeUI") && !refs.Contains("LemonUI.SHVDN3");
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    static Dictionary<string, string> Mappings(GameInstall game, string id, ModArchive archive)
    {
        if (!Get(id).Supports(game.Edition))
            throw new InvalidOperationException(L.T("libraries.unsupported", id, game.Edition));
        if (
            id == "ghost-replay"
            && Version.TryParse(Base.ModdingBase.GameVersion(game), out var patch)
            && patch < new Version(1, 0, 1868, 0)
        )
            throw new InvalidOperationException(L.T("libraries.unsupported", id, patch));
        if (id is "map-editor" or "andres-map-editor")
        {
            string existing = Path.Combine(game.Root, @"scripts\MapEditor.dll");
            if (File.Exists(existing) && !MapFamily(existing, id == "andres-map-editor"))
                throw new InvalidOperationException(L.T("recommended.mapConflict"));
        }
        if (id == "enable-all-interiors" && File.Exists(Path.Combine(game.Root, EaiTarget)) && !EaiPresent(game))
            throw new InvalidOperationException(L.T("recommended.eaiConflict"));
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in archive.Files)
        {
            string? target = id switch
            {
                "map-editor"
                    when source != "NativeUI.dll"
                        && (
                            source == "MapEditor.dll"
                            || source == "ClearScript.dll"
                            || source is "ObjectList.ini" or "PedList.ini" or "VehicleList.ini"
                            || source.StartsWith(@"MapEditor\", StringComparison.OrdinalIgnoreCase)
                        ) => @"scripts\" + source,
                "andres-map-editor"
                    when source.StartsWith(@"scripts\", StringComparison.OrdinalIgnoreCase)
                        && !source.EndsWith(".md", StringComparison.OrdinalIgnoreCase) => source,
                "trafficx-custom"
                    when source
                        == (
                            game.Edition == GameEdition.Legacy
                                ? @"legacy\TrafficX Custom.dll"
                                : @"Enhanced\TrafficX Custom.dll"
                        ) => @"scripts\TrafficX Custom.dll",
                "enable-all-interiors"
                    when source.EndsWith(@"scripts\EnableAllinteriors.dll", StringComparison.OrdinalIgnoreCase) =>
                    @"scripts\EnableAllinteriors.dll",
                "enable-all-interiors"
                    when source.EndsWith(@"scripts\EnableAllInteriors.ini", StringComparison.OrdinalIgnoreCase) =>
                    @"scripts\EnableAllInteriors.ini",
                "enable-all-interiors"
                    when source.EndsWith(@"scripts\iFruitAddon2.dll", StringComparison.OrdinalIgnoreCase)
                        && !EaiPresent(game) => EaiTarget,
                "ghost-replay"
                    when source == "GhostReplay.asi"
                        || source.StartsWith(@"GhostReplay\", StringComparison.OrdinalIgnoreCase) => source,
                _ => null,
            };
            if (
                target is null
                || source.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            )
                continue;
            if (
                target.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(game.Root, target))
            )
                continue;
            if (!result.TryAdd(source, target))
                throw new InvalidDataException(L.T("recommended.changed", id, Get(id).Version));
        }
        if (result.Count == 0)
            throw new InvalidDataException(L.T("recommended.changed", id, Get(id).Version));
        return result;
    }

    public static InstallInspection Inspect(GameInstall game, string id, string source)
    {
        ValidateArchive(id, source);
        using var archive = ModArchive.Open(source);
        var mappings = Mappings(game, id, archive);
        var dependencies = Dependencies(game, id, mappings.Values);
        return new(
            id,
            L.Join(
                "\n",
                L.T("recommended.version", id, Get(id).Version),
                L.T("recommended.requirements", L.Join(", ", dependencies.Select(d => d.Title)))
            ),
            mappings.Values.ToArray(),
            dependencies,
            []
        );
    }

    public static InstallPlan Prepare(GameInstall game, string id, string source, bool preview)
    {
        ValidateArchive(id, source);
        using var archive = ModArchive.Open(source);
        var mappings = Mappings(game, id, archive);
        var missing = Dependencies(game, id, mappings.Values)
            .Where(d => d.Required && !d.Present && !ProvidesBundledDependency(id, d.Id))
            .ToArray();
        if (!preview && missing.Length > 0)
            throw new InvalidOperationException(
                L.T(
                    "core.installation.requiredComponentsNotInstalledInstall",
                    string.Join(", ", missing.Select(d => d.Title))
                )
            );
        return new OivInstaller(game).Prepare(
            id,
            mappings.Select(p => (OivOp)new OivAdd(p.Key, p.Value)).ToList(),
            archive,
            preview
        );
    }
}
