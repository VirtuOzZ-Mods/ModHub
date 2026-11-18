using System.Text;
using System.Xml;
using System.Xml.Linq;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.Peds;

/// <summary>Analyzes resource sets while preserving original names and variants.</summary>
public static class PedArchiveAnalyzer
{
    private static readonly HashSet<string> ResourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".yft",
        ".ymt",
        ".ydd",
        ".ytd",
        ".yld",
        ".ybn",
        ".ycd",
    };

    public static PedArchiveAnalysis Analyze(string archivePath)
    {
        using var archive = ModArchive.Open(archivePath);
        return Analyze(archive);
    }

    public static PedArchiveAnalysis Analyze(ModArchive archive)
    {
        var warnings = archive.Skipped.Select(p => L.T("core.peds.skippedInvalidFile", p)).ToList();
        var files = archive.Files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var set = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registrations = new List<(string Path, XElement Item)>();
        var readme = new StringBuilder();
        var images = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (
                name.Equals("peds.meta", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ap_m.xml", StringComparison.OrdinalIgnoreCase)
            )
            {
                try
                {
                    using var s = archive.OpenSource(file);
                    var doc = PedInspectionXml.Read(s, archive.SourceSize(file));
                    registrations.AddRange(PedInspectionXml.Items(doc).Select(item => (file, item)));
                }
                catch (Exception ex) when (PedInspectionXml.IsReadFailure(ex))
                {
                    warnings.Add(L.T("core.common.couldNotRead", file, ex.Message));
                }
            }
            if (
                (
                    name.Contains("readme", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("прочти", StringComparison.OrdinalIgnoreCase)
                )
                && new[] { ".txt", ".md" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                && readme.Length < 262144
            )
            {
                using var s = archive.OpenSource(file);
                using var reader = new StreamReader(s, Encoding.UTF8, true);
                var buffer = new char[Math.Min(65536, 262144 - readme.Length)];
                var count = reader.ReadBlock(buffer, 0, buffer.Length);
                readme.AppendLine(file).Append(buffer, 0, count).AppendLine();
            }
            if (
                images.Count < 32
                && new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }.Contains(
                    Path.GetExtension(file),
                    StringComparer.OrdinalIgnoreCase
                )
                && archive.SourceSize(file) <= 20L * 1024 * 1024
            )
                images.Add(file);
        }
        var candidates = new List<PedCandidate>();
        foreach (var yft in files.Where(p => p.EndsWith(".yft", StringComparison.OrdinalIgnoreCase)))
        {
            var stem = yft[..^4];
            var name = Path.GetFileName(stem);
            var directory = Path.GetDirectoryName(yft) ?? "";
            if (!set.Contains(stem + ".ymt"))
            {
                warnings.Add(L.T("core.peds.incompleteSetMissingYmt", stem));
                continue;
            }
            var componentPrefix = stem + "\\";
            var allComponents = files
                .Where(p =>
                    p.StartsWith(componentPrefix, StringComparison.OrdinalIgnoreCase)
                    && ResourceExtensions.Contains(Path.GetExtension(p))
                )
                .ToArray();
            var components = allComponents.Where(p => !p[componentPrefix.Length..].Contains('\\')).ToArray();
            bool normal = set.Contains(stem + ".ydd");
            bool streamed = components.Any(p => p.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase));
            if (!normal && !streamed)
            {
                warnings.Add(L.T("core.peds.incompleteSetMissingYddModel", stem));
                continue;
            }
            var localWarnings = new List<string>();
            if (components.Length != allComponents.Length)
                localWarnings.Add(L.T("core.peds.nestedVariantFoldersHaveNot"));
            var records = registrations
                .Where(r =>
                    string.Equals(PedInspectionXml.Value(r.Item, "Name"), name, StringComparison.OrdinalIgnoreCase)
                )
                .OrderByDescending(r => CommonDirectoryDepth(Path.GetDirectoryName(r.Path) ?? "", directory))
                .ToArray();
            var registration = records.FirstOrDefault().Item;
            if (records.Length > 1)
                localWarnings.Add(L.T("core.peds.multipleRegistrationEntriesFoundClosest"));
            var layout = normal ? PedLayout.Normal : PedLayout.Streamed;
            if (normal && streamed)
            {
                localWarnings.Add(L.T("core.peds.setContainsBothRegularStreamed"));
                if (registration is not null && PedInspectionXml.Streamed(registration))
                    layout = PedLayout.Streamed;
            }
            if (registration is not null && PedInspectionXml.Streamed(registration) != (layout == PedLayout.Streamed))
                localWarnings.Add(L.T("core.peds.registeredTypeDoesNotMatch"));
            var propsPrefix = stem + "_p\\";
            var propsComponents = files
                .Where(p =>
                    p.StartsWith(propsPrefix, StringComparison.OrdinalIgnoreCase)
                    && !p[propsPrefix.Length..].Contains('\\')
                    && ResourceExtensions.Contains(Path.GetExtension(p))
                )
                .ToArray();
            var selected = files
                .Where(p =>
                    string.Equals(Path.GetDirectoryName(p) ?? "", directory, StringComparison.OrdinalIgnoreCase)
                    && (
                        string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            Path.GetFileNameWithoutExtension(p),
                            name + "_p",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    && ResourceExtensions.Contains(Path.GetExtension(p))
                )
                .Concat(layout == PedLayout.Streamed ? components : [])
                .Concat(propsComponents)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var propsName = registration is null ? "" : PedInspectionXml.Value(registration, "PropsName");
            if (
                propsName.Length > 0
                && !propsName.Equals("null", StringComparison.OrdinalIgnoreCase)
                && !propsName.Equals(name + "_p", StringComparison.OrdinalIgnoreCase)
            )
                localWarnings.Add(L.T("core.peds.nonstandardPropsnameReferenceSharedResources", propsName));
            var resources = new List<PedResource>();
            foreach (var file in selected)
            {
                using var s = archive.OpenSource(file);
                Span<byte> header = new byte[16];
                int read = 0,
                    n;
                while (read < header.Length && (n = s.Read(header[read..])) > 0)
                    read += n;
                if (!RpfResourceSource.IsRsc7(header[..read]))
                    localWarnings.Add(L.T("core.peds.invalidRsc7Header", file));
                var target = directory.Length == 0 ? file : file[(directory.Length + 1)..];
                resources.Add(new(file, target, archive.SourceSize(file)));
            }
            if (layout == PedLayout.Normal && !set.Contains(stem + ".ytd"))
                localWarnings.Add(L.T("core.peds.noSeparateYtdFileCheck"));
            candidates.Add(
                new PedCandidate(yft, name, directory.Length == 0 ? name : directory, layout, resources, localWarnings)
                {
                    RegistrationXml = registration?.ToString(SaveOptions.DisableFormatting),
                    LayoutAmbiguous =
                        normal
                        && streamed
                        && !bool.TryParse(registration?.Element("IsStreamedGfx")?.Attribute("value")?.Value, out _),
                    PreviewPath = MatchPreview(
                        name,
                        directory,
                        images,
                        files.Count(p =>
                            p.EndsWith(".yft", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(
                                Path.GetDirectoryName(p) ?? "",
                                directory,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    ),
                }
            );
        }
        if (candidates.Count == 0)
            warnings.Add(L.T("core.peds.noCompleteAddPedSets"));
        return new(archive.FilePath, candidates, readme.ToString(), images, warnings)
        {
            Details = PedReadmeParser.Parse(readme.ToString()),
        };
    }

    private static string? MatchPreview(
        string name,
        string directory,
        IReadOnlyList<string> images,
        int modelsInDirectory
    )
    {
        static string Key(string value) =>
            new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var modelKey = Key(name);
        var variantKey = Key(Path.GetFileName(directory));
        var named = images
            .Where(p =>
            {
                var key = Key(Path.GetFileNameWithoutExtension(p));
                return key == modelKey
                    || (
                        variantKey.Length > 0
                        && (key == variantKey || key == variantKey + "preview" || key == "preview" + variantKey)
                    );
            })
            .ToArray();
        if (named.Length == 1)
            return named[0];
        var local = images
            .Where(p => string.Equals(Path.GetDirectoryName(p) ?? "", directory, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return modelsInDirectory == 1 && local.Length > 0 ? local[0] : null;
    }

    private static int CommonDirectoryDepth(string first, string second)
    {
        var a = first.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var b = second.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int depth = 0;
        while (depth < a.Length && depth < b.Length && a[depth].Equals(b[depth], StringComparison.OrdinalIgnoreCase))
            depth++;
        return depth;
    }
}

internal static class PedInspectionXml
{
    public const int MaxBytes = 4 * 1024 * 1024;

    public static XDocument Read(Stream stream, long size)
    {
        if (size > MaxBytes)
            throw new InvalidDataException(L.T("core.peds.xmlExceedsAllowedSize4"));
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxBytes,
        };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    public static IEnumerable<XElement> Items(XDocument doc) =>
        doc.Root?.Name.LocalName == "CPedModelInfo__InitDataList"
            ? doc
                .Root.Elements()
                .Where(e => e.Name.LocalName == "InitDatas")
                .SelectMany(e => e.Elements().Where(e => e.Name.LocalName == "Item"))
            : [];

    public static string Value(XElement item, string name) =>
        item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";

    public static bool Streamed(XElement item) =>
        string.Equals(
            item.Elements().FirstOrDefault(e => e.Name.LocalName == "IsStreamedGfx")?.Attribute("value")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase
        );

    public static bool IsReadFailure(Exception ex) =>
        ex
            is IOException
                or InvalidDataException
                or XmlException
                or InvalidOperationException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or RpfException;

    public static string SafeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith('\\'))
            throw new InvalidDataException(L.T("core.peds.absoluteEmptyPathDlcReference"));
        return ModArchive.NormalizeKey(path) ?? throw new InvalidDataException(L.T("core.peds.unsafeDlcLink"));
    }
}
