using System.IO.Compression;
using System.Xml;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Oiv;

public abstract record OivOp;

public sealed record OivAdd(string Source, string Target) : OivOp;

public sealed record OivDelete(string Target) : OivOp;

public sealed record OivArchive(string Path, bool CreateIfNotExist, string Type, IReadOnlyList<OivOp> Ops) : OivOp;

public sealed record OivXml(string Path, IReadOnlyList<OivXmlOp> Ops) : OivOp;

public sealed record OivText(string Path, bool CreateIfNotExist, IReadOnlyList<OivTextOp> Ops) : OivOp;

/// <summary>
/// Replaces individual sounds inside an .awc bank (Path is inside the archive): what OpenIV does by exporting to .oac,
/// dropping in .wav files and importing. Sound.Name is the sound name ("DUMP_VALVE" or "0x0103B3B2"), Source is the .wav from the mod.
/// </summary>
public sealed record OivAwc(string Path, IReadOnlyList<OivAwcSound> Sounds) : OivOp;

public sealed record OivAwcSound(string Source, string Name);

/// <summary>
/// The folder inside the archive must already exist (path from the author's instructions). Otherwise the installation stops:
/// a silently created folder would mean the files land where the game never looks. HintFile helps show where it actually is.
/// </summary>
public sealed record OivRequire(string Path, string HintFile) : OivOp;

public enum OivXmlKind
{
    Add,
    Remove,
    Replace,
}

/// <param name="ContentXml">Inner XML of the command (for add/replace).</param>
public sealed record OivXmlOp(OivXmlKind Kind, string XPath, string? Append, string ContentXml);

public enum OivTextKind
{
    Insert,
    Add,
    Replace,
    Delete,
}

public sealed record OivTextOp(OivTextKind Kind, string? Where, string? Line, string? Condition, string Text);

public sealed record OivInfo(
    string Name,
    string Version,
    string Author,
    string Description,
    string FormatVersion,
    string Target
)
{
    /// <summary>Header colour from the package &lt;colors&gt;, #AARRGGBB format.</summary>
    public string? HeaderColor { get; init; }
    public bool HeaderBlackText { get; init; }
    public string? IconBackground { get; init; }
    public string? AuthorLink { get; init; }
    public string? Licence { get; init; }
    public string? LargeDescription { get; init; }
    public string? FooterLink { get; init; }
    public string? FooterLinkTitle { get; init; }
}

public class OivFormatException(string message) : Exception(message);

/// <summary>
/// The package or author instructions refer to an archive that does not exist in this game (an outdated path or another game version).
/// The whole installation stops; Hints lists archives with the same name at other paths (a hint only, never an automatic substitution).
/// </summary>
public sealed class MissingGamePathException(string path, IReadOnlyList<string> hints, string message)
    : OivFormatException(message)
{
    public string Path { get; } = path;
    public IReadOnlyList<string> Hints { get; } = hints;
}

/// <summary>.oiv package: a ZIP with assembly.xml and a content/ folder. Nothing is executed.</summary>
public sealed class OivPackage : Mods.IModSource, IDisposable
{
    private readonly ZipArchive _zip;
    public OivInfo Info { get; }
    public IReadOnlyList<OivOp> Ops { get; }
    public string FilePath { get; }

    private OivPackage(string path, ZipArchive zip, OivInfo info, IReadOnlyList<OivOp> ops)
    {
        FilePath = path;
        _zip = zip;
        Info = info;
        Ops = ops;
    }

    public static OivPackage Open(string path)
    {
        var zip = ZipFile.OpenRead(path);
        try
        {
            var entry = zip.GetEntry("assembly.xml")
                ?? throw new OivFormatException(L.T("core.oiv.assemblyXmlMissing"));
            var doc = new XmlDocument { XmlResolver = null };
            using (var s = entry.Open())
            using (var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                doc.Load(r);
            return Parse(path, zip, doc);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    public static OivPackage Parse(string path, ZipArchive zip, XmlDocument doc)
    {
        var pkg = doc.DocumentElement ?? throw new OivFormatException(L.T("core.oiv.emptyAssemblyXml"));
        if (pkg.Name != "package")
            throw new OivFormatException(L.T("core.oiv.rootElementNotPackage"));
        var meta = pkg["metadata"];
        var ver = meta?["version"];
        var info = new OivInfo(
            meta?["name"]?.InnerText.Trim() ?? Path.GetFileNameWithoutExtension(path),
            ver is null
                ? ""
                : string.Join(".", new[] { ver["major"]?.InnerText, ver["minor"]?.InnerText }.Where(x => x is not null))
                    + (ver["tag"] is { } tag ? " " + tag.InnerText : ""),
            meta?["author"]?["displayName"]?.InnerText.Trim() ?? "",
            meta?["description"]?.InnerText.Trim() ?? "",
            pkg.GetAttribute("version"),
            pkg.GetAttribute("target")
        )
        {
            HeaderColor = OivColor(pkg["colors"]?["headerBackground"]?.InnerText),
            HeaderBlackText = string.Equals(
                pkg["colors"]?["headerBackground"]?.GetAttribute("useBlackTextColor"),
                "true",
                StringComparison.OrdinalIgnoreCase
            ),
            IconBackground = OivColor(pkg["colors"]?["iconBackground"]?.InnerText),
            AuthorLink = SafeLink(meta?["author"]?["actionLink"]?.InnerText ?? meta?["author"]?["web"]?.InnerText),
            Licence = meta?["licence"]?.InnerText.Trim() is { Length: > 0 } lic ? lic : null,
            LargeDescription = meta?["largeDescription"]?.InnerText.Trim() is { Length: > 0 } ld ? ld : null,
            FooterLink = SafeLink(meta?["description"]?.GetAttribute("footerLink")),
            FooterLinkTitle = meta?["description"]?.GetAttribute("footerLinkTitle") is { Length: > 0 } t ? t : null,
        };
        if (!string.IsNullOrEmpty(info.Target) && info.Target != "Five")
            throw new OivFormatException(L.T("core.oiv.packageDifferentGameTarget", info.Target));
        var content = pkg["content"] ?? throw new OivFormatException(L.T("core.oiv.missingContent"));
        return new OivPackage(path, zip, info, ParseOps(content));
    }

    private static List<OivOp> ParseOps(XmlElement parent)
    {
        var ops = new List<OivOp>();
        foreach (var el in parent.ChildNodes.OfType<XmlElement>())
        {
            ops.Add(
                el.Name switch
                {
                    "add" => new OivAdd(Req(el, "source"), Text(el)),
                    "delete" => new OivDelete(Text(el)),
                    "archive" => new OivArchive(
                        Req(el, "path"),
                        Bool(el, "createIfNotExist"),
                        el.GetAttribute("type"),
                        ParseOps(el)
                    ),
                    "xml" => new OivXml(
                        Req(el, "path"),
                        el.ChildNodes.OfType<XmlElement>().Select(ParseXmlOp).ToList()
                    ),
                    "text" => new OivText(
                        Req(el, "path"),
                        Bool(el, "createIfNotExist"),
                        el.ChildNodes.OfType<XmlElement>().Select(ParseTextOp).ToList()
                    ),
                    _ => throw new OivFormatException(L.T("core.oiv.unknownCommand", el.Name)),
                }
            );
        }
        return ops;
    }

    private static OivXmlOp ParseXmlOp(XmlElement el) =>
        el.Name switch
        {
            "add" => new(
                OivXmlKind.Add,
                Req(el, "xpath"),
                el.GetAttribute("append") is { Length: > 0 } a ? a : null,
                el.InnerXml
            ),
            "remove" => new(OivXmlKind.Remove, Req(el, "xpath"), null, ""),
            "replace" => new(OivXmlKind.Replace, Req(el, "xpath"), null, el.InnerXml),
            _ => throw new OivFormatException(L.T("core.oiv.unknownXmlCommand", el.Name)),
        };

    private static OivTextOp ParseTextOp(XmlElement el) =>
        el.Name switch
        {
            "insert" => new(
                OivTextKind.Insert,
                el.GetAttribute("where"),
                el.GetAttribute("line"),
                el.GetAttribute("condition"),
                el.InnerText
            ),
            "add" => new(OivTextKind.Add, null, null, null, el.InnerText),
            "replace" => new(
                OivTextKind.Replace,
                null,
                el.GetAttribute("line"),
                el.GetAttribute("condition"),
                el.InnerText
            ),
            "delete" => new(OivTextKind.Delete, null, null, el.GetAttribute("condition"), el.InnerText),
            _ => throw new OivFormatException(L.T("core.oiv.unknownTextCommand", el.Name)),
        };

    /// <summary>"$AARRGGBB" → "#AARRGGBB"; anything else is ignored.</summary>
    private static string? OivColor(string? v)
    {
        v = v?.Trim().TrimStart('$', '#');
        return v is { Length: 8 or 6 } && v.All(Uri.IsHexDigit) ? "#" + v : null;
    }

    /// <summary>Package links are shown only if they are http(s).</summary>
    private static string? SafeLink(string? v) =>
        Uri.TryCreate(v?.Trim(), UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u.ToString()
            : null;

    private static string Req(XmlElement el, string attr) =>
        el.GetAttribute(attr) is { Length: > 0 } v
            ? v
            : throw new OivFormatException(L.T("core.oiv.hasNoAttribute", el.Name, attr));

    private static string Text(XmlElement el) =>
        el.InnerText.Trim() is { Length: > 0 } v ? v : throw new OivFormatException(L.T("core.oiv.hasNoPath", el.Name));

    private static bool Bool(XmlElement el, string attr) =>
        string.Equals(el.GetAttribute(attr), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Content of a file from content/ (source is relative to content/).</summary>
    public ZipArchiveEntry GetSource(string source)
    {
        var norm = source.Replace('\\', '/').TrimStart('/');
        if (norm.Split('/').Any(s => s is ".." or "."))
            throw new OivFormatException(L.T("core.oiv.invalidSource", source));
        var e = _zip.GetEntry("content/" + norm)
            ?? _zip.Entries.FirstOrDefault(x =>
                string.Equals(x.FullName, "content/" + norm, StringComparison.OrdinalIgnoreCase)
            )
            ?? throw new OivFormatException(L.T("core.oiv.packageHasNoContentFile", norm));
        return e;
    }

    public byte[] ReadSource(string source)
    {
        var e = GetSource(source);
        if (e.Length > int.MaxValue)
            throw new OivFormatException(L.T("core.oiv.fileTooLargeEditMemory", source));
        using var s = e.Open();
        var buf = new byte[e.Length];
        s.ReadExactly(buf);
        return buf;
    }

    public Stream OpenSource(string source) => GetSource(source).Open();

    public long SourceSize(string source) => GetSource(source).Length;

    public byte[]? TryReadIcon()
    {
        var e = _zip.GetEntry("icon.png");
        if (e is null || e.Length > 4 << 20)
            return null;
        using var s = e.Open();
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose() => _zip.Dispose();
}
