using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;

namespace GTAModManager.Core.Installation;

public sealed record ScriptLibraryComponent(
    string Id,
    string Title,
    string Repository,
    string Tag,
    string Asset,
    string Entry,
    string AssemblyName,
    Version Version,
    int Api,
    bool Enhanced
)
{
    public string Target => @"scripts\" + AssemblyName + ".dll";

    public bool Supports(GameEdition edition) => edition == GameEdition.Legacy || Enhanced;

    public Version ApiVersion => Api == 2 ? new(2, 10, 9, 0) : new(3, 6, 0, 0);
}

/// <summary>Only verified release payloads; installation and backups belong to the existing pipeline.</summary>
public static class ScriptLibraries
{
    public static IReadOnlyList<ScriptLibraryComponent> Catalog { get; } =
    [
        new(
            "lemonui",
            "LemonUI",
            "LemonUIbyLemon/LemonUI",
            "v2.2",
            "LemonUI.zip",
            @"SHVDN3\LemonUI.SHVDN3.dll",
            "LemonUI.SHVDN3",
            new(2, 2, 0, 0),
            3,
            false
        ),
        new(
            "nativeui",
            "NativeUI",
            "Guad/NativeUI",
            "1.9.1",
            "Release.zip",
            "NativeUI.dll",
            "NativeUI",
            new(1, 9, 0, 0),
            2,
            false
        ),
        new(
            "ifruitaddon2",
            "iFruitAddon2",
            "Bob74/iFruitAddon2",
            "v3.1.1",
            "iFruitAddon2-3.1.1.zip",
            "iFruitAddon2.dll",
            "iFruitAddon2",
            new(3, 1, 1, 0),
            3,
            true
        ),
    ];

    public static ScriptLibraryComponent Get(string id) =>
        Catalog.SingleOrDefault(c => c.Id == id) ?? throw new InvalidDataException(L.T("libraries.unknown", id));

    public static string SelectFile(string id, IEnumerable<string> files)
    {
        var component = Get(id);
        var matches = files.Where(f => f.Equals(component.Entry, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(L.T("libraries.payload", component.Title));
    }

    public static IReadOnlyList<DependencyNeed> Dependencies(GameInstall game, string id)
    {
        var component = Get(id);
        return DocumentRequirements
            .Inspect(game, "ScriptHookVDotNet", [component.Target])
            .Select(d =>
                d.Id != "shvdn"
                    ? d
                    : d with
                    {
                        Title = $"ScriptHookVDotNet {component.Api} API >= {component.ApiVersion}",
                        Present = HasApi(game, component),
                        Evidence = L.T("libraries.api", component.Api, component.ApiVersion),
                    }
            )
            .ToArray();
    }

    static bool HasApi(GameInstall game, ScriptLibraryComponent component) =>
        HasApi(game, component.Api, component.ApiVersion);

    public static bool HasApi(GameInstall game, int api, Version minimum)
    {
        var status = ModdingBase.Inspect(game);
        if (!status.Shvdn)
            return false;
        if (game.Edition == GameEdition.Enhanced)
        {
            try
            {
                if (
                    FileVersionInfo
                        .GetVersionInfo(Path.Combine(game.Root, status.ShvdnName!))
                        .ProductName?.Contains("Enhanced", StringComparison.OrdinalIgnoreCase) != true
                )
                    return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        try
        {
            using var stream = File.OpenRead(Path.Combine(game.Root, $"ScriptHookVDotNet{api}.dll"));
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            var definition = md.GetAssemblyDefinition();
            return md.GetString(definition.Name) == $"ScriptHookVDotNet{api}" && definition.Version >= minimum;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or BadImageFormatException
                        or InvalidOperationException
            )
        {
            return false;
        }
    }

    public static bool Present(GameInstall game, string id)
    {
        var component = Get(id);
        try
        {
            using var input = File.OpenRead(Path.Combine(game.Root, component.Target));
            using var pe = new PEReader(input);
            var md = pe.GetMetadataReader();
            var definition = md.GetAssemblyDefinition();
            return md.GetString(definition.Name) == component.AssemblyName && definition.Version == component.Version;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or BadImageFormatException
                        or InvalidOperationException
            )
        {
            return false;
        }
    }

    static string Validate(GameInstall game, ScriptLibraryComponent component, ModArchive archive)
    {
        if (!component.Supports(game.Edition))
            throw new InvalidOperationException(L.T("libraries.unsupported", component.Title, game.Edition));
        string file = SelectFile(component.Id, archive.Files);
        if (archive.SourceSize(file) > 16L << 20)
            throw new InvalidDataException(L.T("libraries.payload", component.Title));
        try
        {
            using var bytes = new MemoryStream(archive.Read(file), false);
            using var pe = new PEReader(bytes);
            if (!pe.HasMetadata || (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) == 0)
                throw new BadImageFormatException();
            var md = pe.GetMetadataReader();
            var definition = md.GetAssemblyDefinition();
            if (md.GetString(definition.Name) != component.AssemblyName || definition.Version != component.Version)
                throw new BadImageFormatException();
            var references = md
                .AssemblyReferences.Select(md.GetAssemblyReference)
                .Where(a => md.GetString(a.Name).StartsWith("ScriptHookVDotNet", StringComparison.Ordinal))
                .ToArray();
            if (
                references.Length != 1
                || md.GetString(references[0].Name) != $"ScriptHookVDotNet{component.Api}"
                || references[0].Version != component.ApiVersion
            )
                throw new BadImageFormatException();
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException(L.T("libraries.payload", component.Title), ex);
        }
        return file;
    }

    public static InstallInspection Inspect(GameInstall game, string id, string source)
    {
        var component = Get(id);
        using var archive = ModArchive.Open(source);
        Validate(game, component, archive);
        return new(
            component.Title,
            L.Join(
                "\n",
                component.Title + " " + component.Tag,
                L.T("libraries.api", component.Api, component.ApiVersion)
            ),
            [component.Target],
            Dependencies(game, id),
            []
        );
    }

    public static InstallPlan Prepare(GameInstall game, string id, string source, bool preview)
    {
        var component = Get(id);
        if (!component.Supports(game.Edition))
            throw new InvalidOperationException(L.T("libraries.unsupported", component.Title, game.Edition));
        var missing = Dependencies(game, id).Where(d => d.Required && !d.Present).ToArray();
        if (!preview && missing.Length > 0)
            throw new InvalidOperationException(
                L.T(
                    "core.installation.requiredComponentsNotInstalledInstall",
                    string.Join(", ", missing.Select(d => d.Title))
                )
            );
        using var archive = ModArchive.Open(source);
        string file = Validate(game, component, archive);
        return new OivInstaller(game).Prepare(component.Title, [new OivAdd(file, component.Target)], archive, preview);
    }
}
