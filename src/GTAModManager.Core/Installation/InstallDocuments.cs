using System.Text;
using System.Xml;
using System.Xml.Linq;
using GTAModManager.Core.Dlc;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Installation;

public enum XmlDocumentKind
{
    GameConfig,
    DlcList,
    Other,
}

public sealed record XmlChange(string Path, string? Before, string? After, string Explanation);

public sealed record DlcComparison(
    IReadOnlyList<string> Existing,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Duplicates
);

public static class InstallDocuments
{
    public static XDocument Parse(string text)
    {
        using var reader = XmlReader.Create(
            new StringReader(text.TrimStart('\uFEFF')),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 8L << 20,
            }
        );
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    public static string ReadText(string path)
    {
        if (new FileInfo(path).Length > 8L << 20)
            throw new InvalidDataException(L.T("core.installation.documentExceeds8Mb"));
        return File.ReadAllText(path);
    }

    public static XmlDocumentKind Kind(string text)
    {
        var d = Parse(text);
        if (d.Root?.Name == "SMandatoryPacksData" && d.Root.Element("Paths") is not null)
        {
            foreach (var item in d.Root.Element("Paths")!.Elements("Item"))
                ValidatePack(item.Value);
            return XmlDocumentKind.DlcList;
        }
        return
            d.Root?.Name == "fwAllConfigs"
            && d.Root.Element("ConfigArray") is not null
            && d.Descendants("Config").Any(e => (string?)e.Attribute("type") == "CGameConfig")
            ? XmlDocumentKind.GameConfig
            : XmlDocumentKind.Other;
    }

    private static string ValidatePack(string path)
    {
        path = path.Trim().Replace('\\', '/');
        bool platform = path.StartsWith("platform:/dlcPacks/", StringComparison.OrdinalIgnoreCase);
        if (!platform && !path.StartsWith("dlcpacks:/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("core.installation.unknownDlcPath", path));
        var name = path[(platform ? "platform:/dlcPacks/".Length : "dlcpacks:/".Length)..].Trim('/');
        if (
            string.IsNullOrWhiteSpace(name)
            || name != name.Trim()
            || name.Trim('.').Length == 0
            || name.Contains('/')
            || name.Contains(':')
            || name.Contains("..")
            || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || "_-. ".Contains(c)))
        )
            throw new InvalidDataException(L.T("core.installation.invalidDlcName", name));
        return platform ? "platform:/dlcPacks/" + name + "/" : name;
    }

    private static IReadOnlyList<string> Packs(string text) =>
        Parse(text).Root!.Element("Paths")!.Elements("Item").Select(e => ValidatePack(e.Value)).ToArray();

    public static DlcComparison CompareDlc(string current, string incoming)
    {
        if (Kind(current) != XmlDocumentKind.DlcList || Kind(incoming) != XmlDocumentKind.DlcList)
            throw new InvalidDataException(L.T("core.installation.validDlcListRequired"));
        var a = Packs(current);
        var b = Packs(incoming);
        return new(
            a,
            b.Except(a, StringComparer.OrdinalIgnoreCase).ToArray(),
            a.Except(b, StringComparer.OrdinalIgnoreCase).ToArray(),
            b.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToArray()
        );
    }

    public static string MergeDlc(string current, string incoming)
    {
        var comparison = CompareDlc(current, incoming);
        var doc = Parse(current);
        foreach (var name in comparison.Added)
            doc.Root!.Element("Paths")!
                .Add(
                    new XElement(
                        "Item",
                        name.StartsWith("platform:/", StringComparison.OrdinalIgnoreCase) ? name : DlcList.ItemFor(name)
                    )
                );
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    public static IReadOnlyList<XmlChange> Compare(string before, string after)
    {
        var a = Flatten(Parse(before));
        var b = Flatten(Parse(after));
        return a
            .Keys.Concat(b.Keys)
            .Distinct()
            .Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k))
            .Select(k => new XmlChange(
                k,
                a.GetValueOrDefault(k),
                b.GetValueOrDefault(k),
                k.Contains("PoolSize")
                    ? L.T("core.installation.changesPoolSizeImpactDepends")
                    : L.T("core.installation.parameterSPurposeUnconfirmed")
            ))
            .ToArray();
    }

    public static Dictionary<string, string> Flatten(XDocument document)
    {
        var result = new Dictionary<string, string>();
        void Visit(XElement e, string parent)
        {
            string id = e.Elements().FirstOrDefault(x => x.Name.LocalName is "PoolName" or "Name")?.Value ?? "";
            string suffix =
                id.Length > 0
                    ? "[" + id + "]"
                    : "[" + ((e.Parent?.Elements(e.Name).TakeWhile(x => x != e).Count() ?? 0) + 1) + "]";
            string path = parent + "/" + e.Name.LocalName + suffix;
            foreach (var attr in e.Attributes())
                result[path + "/@" + attr.Name.LocalName] = attr.Value;
            if (!e.HasElements)
                result[path] = e.Value;
            foreach (var child in e.Elements())
                Visit(child, path);
        }
        if (document.Root is not null)
            Visit(document.Root, "");
        return result;
    }
}
