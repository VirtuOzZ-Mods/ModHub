using System.Xml.Linq;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.Peds;

/// <summary>Reads existing registrations of any unencrypted DLC without modifying other packs.</summary>
public sealed class PedDiscoveryService(GameInstall game, string? userModsDirectory = null)
{
    private readonly string userMods = Path.GetFullPath(
        userModsDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Rockstar Games",
                "GTA V",
                "UserMods"
            )
    );

    public PedDiscovery Scan()
    {
        if (game.Edition != GameEdition.Legacy)
            throw new NotSupportedException(L.T("core.peds.gmmAddonpedsSupportsGtaV"));
        var entries = new List<PedEntry>();
        var warnings = new List<string>();
        var diagnostics = new List<string>();
        var active = ActiveDlcs(diagnostics);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var baseDir in new[]
            {
                Path.Combine(game.ModsDir, "update", "x64", "dlcpacks"),
                Path.Combine(game.Root, "update", "x64", "dlcpacks"),
            }
        )
        {
            if (!Directory.Exists(baseDir))
                continue;
            foreach (
                var directory in Directory
                    .EnumerateDirectories(baseDir)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            )
            {
                var name = Path.GetFileName(directory);
                var path = Path.Combine(directory, "dlc.rpf");
                if (!File.Exists(path))
                    continue;
                if (!seen.Add(name))
                    continue; // Only an actual mods archive overrides the original DLC.
                // Discovery remains broad for compatibility with custom PED packs,
                // but an unreadable unrelated GTA DLC is not a character problem.
                bool pedPack =
                    name.Equals("addonpeds", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("pedselector", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(PedInstaller.PackName, StringComparison.OrdinalIgnoreCase);
                try
                {
                    if (
                        (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                        || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                    )
                    {
                        (pedPack ? warnings : diagnostics).Add(L.T("core.peds.skippedFileSystemLink", path));
                        continue;
                    }
                    using var archive = Rpf7Archive.OpenRead(path);
                    var before = entries.Count;
                    ScanDlc(archive, path, entries, warnings, () => pedPack = true);
                    if (entries.Count > before && active is not null && !active.Contains(name))
                        warnings.Add(L.T("core.peds.dlcContainsPedsButMissing", name));
                }
                catch (Exception ex) when (PedInspectionXml.IsReadFailure(ex))
                {
                    (pedPack ? warnings : diagnostics).Add(L.T("core.peds.couldNotVerifyDlc", name, ex.Message));
                }
            }
        }
        return new(entries, warnings) { Diagnostics = diagnostics };
    }

    private HashSet<string>? ActiveDlcs(List<string> warnings)
    {
        var update = new[]
        {
            Path.Combine(game.ModsDir, "update", "update.rpf"),
            Path.Combine(game.Root, "update", "update.rpf"),
        }.FirstOrDefault(File.Exists);
        if (update is null)
        {
            warnings.Add(L.T("core.peds.dlclistXmlNotFoundDlc"));
            return null;
        }
        try
        {
            using var archive = Rpf7Archive.OpenRead(update);
            var doc = ReadXml(archive, @"common\data\dlclist.xml");
            return doc.Descendants()
                .Where(e => e.Name.LocalName == "Item")
                .Select(e => e.Value.Trim().Replace('\\', '/'))
                .Where(p => p.StartsWith("dlcpacks:/", StringComparison.OrdinalIgnoreCase))
                .Select(p => p[10..].Trim('/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (PedInspectionXml.IsReadFailure(ex))
        {
            warnings.Add(L.T("core.peds.couldNotReadDlclistXml", ex.Message));
            return null;
        }
    }

    private void ScanDlc(
        Rpf7Archive archive,
        string dlcPath,
        List<PedEntry> entries,
        List<string> warnings,
        Action markPedPack
    )
    {
        if (archive.Find("setup2.xml") is not RpfBinaryFile)
            return;
        var setup = ReadXml(archive, "setup2.xml");
        string device = setup.Descendants().FirstOrDefault(e => e.Name.LocalName == "deviceName")?.Value.Trim() ?? "";
        string contentPath = setup.Descendants().FirstOrDefault(e => e.Name.LocalName == "datFile")?.Value.Trim() ?? "";
        // Some classic packages omit datFile but use the established content.xml name.
        if (contentPath.Length == 0)
            contentPath = archive.Find("content.xml") is RpfBinaryFile ? "content.xml" : "appdata.xml";
        contentPath = InternalReference(contentPath, device);
        var content = ReadXml(archive, contentPath);
        var declarations = content
            .Descendants()
            .Where(e => e.Name.LocalName == "Item")
            .Select(e => (Type: PedInspectionXml.Value(e, "fileType"), File: PedInspectionXml.Value(e, "filename")))
            .Where(d => d.Type is "PED_METADATA_FILE" or "PEDSTREAM_FILE")
            .ToArray();
        if (declarations.Length > 0)
            markPedPack();
        var streams = new List<string>();
        foreach (var declaration in declarations.Where(d => d.Type == "PEDSTREAM_FILE"))
        {
            try
            {
                var streamPath = InternalReference(declaration.File, device);
                WithArchive(archive, streamPath, stream =>
                {
                    streams.AddRange(stream.Files.Select(f => streamPath + "\\" + f.Path.Replace('/', '\\')));
                    return 0;
                });
            }
            catch (Exception ex) when (PedInspectionXml.IsReadFailure(ex))
            {
                warnings.Add(L.T("core.peds.couldNotVerifyPedStream", declaration.File, dlcPath, ex.Message));
            }
        }
        foreach (var declaration in declarations.Where(d => d.Type == "PED_METADATA_FILE"))
        {
            try
            {
                XDocument doc;
                string source;
                if (declaration.File.StartsWith("user:/", StringComparison.OrdinalIgnoreCase))
                {
                    var relative = PedInspectionXml.SafeRelative(declaration.File[6..]);
                    if (!relative.StartsWith("UserMods\\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(L.T("core.peds.onlyUserUsermodsReferencesAllowed"));
                    var beneath = relative[9..];
                    source = Path.GetFullPath(Path.Combine(userMods, beneath));
                    if (
                        !source.StartsWith(
                            userMods.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        throw new InvalidDataException(L.T("core.peds.linkPointsOutsideUsermods"));
                    RejectReparsePath(source, userMods);
                    using var file = File.OpenRead(source);
                    doc = PedInspectionXml.Read(file, file.Length);
                }
                else
                {
                    var relative = InternalReference(declaration.File, device);
                    source = dlcPath + "!/" + relative;
                    doc = ReadXml(archive, relative);
                }
                foreach (var item in PedInspectionXml.Items(doc))
                {
                    var name = PedInspectionXml.Value(item, "Name");
                    if (name.Length == 0)
                    {
                        warnings.Add(L.T("core.peds.entryWithoutModelName", source));
                        continue;
                    }
                    var resources = streams.Where(p => MatchesModel(p, name)).Select(p => dlcPath + "!/" + p).ToList();
                    if (resources.Count == 0)
                        warnings.Add(L.T("core.peds.noResourcesFoundFromDeclared", name, source));
                    entries.Add(
                        new PedEntry
                        {
                            SpawnName = name,
                            DisplayName = name,
                            Layout = PedInspectionXml.Streamed(item) ? PedLayout.Streamed : PedLayout.Normal,
                            PedType = PedInspectionXml.Value(item, "Pedtype"),
                            RegistrationXml = item.ToString(SaveOptions.DisableFormatting),
                            Managed = false,
                            SourceDlc = dlcPath,
                            MetadataSource = source,
                            ResourcePaths = resources,
                        }
                    );
                }
                // An empty, well-formed pack is the normal state after deleting its last model.
                if (
                    doc.Root?.Name.LocalName != "CPedModelInfo__InitDataList"
                    || !doc.Root.Elements().Any(e => e.Name.LocalName == "InitDatas")
                )
                    warnings.Add(L.T("core.peds.hasInvalidCharacterRegistrationFormat", source));
            }
            catch (Exception ex) when (PedInspectionXml.IsReadFailure(ex))
            {
                warnings.Add(L.T("core.peds.couldNotReadRegistration", declaration.File, dlcPath, ex.Message));
            }
        }
    }

    private static bool MatchesModel(string path, string model)
    {
        var parts = path.Replace('/', '\\').Split('\\');
        return parts.Take(parts.Length - 1).Any(p => p.Equals(model, StringComparison.OrdinalIgnoreCase))
            || Path.GetFileNameWithoutExtension(parts[^1]).Equals(model, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparsePath(string file, string boundary)
    {
        var current = file;
        while (current.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(L.T("core.peds.fileSystemLinksUsermodsNot"));
            if (current.Equals(boundary, StringComparison.OrdinalIgnoreCase))
                break;
            current = Path.GetDirectoryName(current) ?? "";
        }
    }

    private static string InternalReference(string reference, string device)
    {
        reference = reference.Replace("%PLATFORM%", "x64", StringComparison.OrdinalIgnoreCase);
        var marker = reference.IndexOf(":/", StringComparison.Ordinal);
        if (marker >= 0)
        {
            if (!reference[..marker].Equals(device, StringComparison.OrdinalIgnoreCase) || device.Length == 0)
                throw new InvalidDataException(L.T("core.peds.unknownDlcDevice", reference));
            reference = reference[(marker + 2)..];
        }
        return PedInspectionXml.SafeRelative(reference);
    }

    private static XDocument ReadXml(Rpf7Archive archive, string path)
    {
        var split = path.IndexOf(".rpf\\", StringComparison.OrdinalIgnoreCase);
        if (split >= 0)
            return WithArchive(archive, path[..(split + 4)], nested => ReadXml(nested, path[(split + 5)..]));
        if (archive.Find(path) is not RpfBinaryFile file)
            throw new InvalidDataException(L.T("core.peds.xmlMissing", path));
        if (file.UncompressedSize > PedInspectionXml.MaxBytes)
            throw new InvalidDataException(L.T("core.peds.xmlExceeds4Mb"));
        using var bytes = new MemoryStream(archive.ReadContent(file), false);
        return PedInspectionXml.Read(bytes, bytes.Length);
    }

    private static T WithArchive<T>(Rpf7Archive archive, string path, Func<Rpf7Archive, T> action)
    {
        var split = path.IndexOf(".rpf\\", StringComparison.OrdinalIgnoreCase);
        if (split >= 0)
            return WithArchive(archive, path[..(split + 4)], nested =>
                WithArchive(nested, path[(split + 5)..], action)
            );
        if (archive.Find(path) is not RpfBinaryFile file)
            throw new InvalidDataException(L.T("core.peds.pedStreamArchiveMissing", path));
        using var nested = archive.OpenNested(file);
        return action(nested);
    }
}
