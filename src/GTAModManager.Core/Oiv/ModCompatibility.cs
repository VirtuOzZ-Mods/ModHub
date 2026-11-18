using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Oiv;

// Per-install receipts travel through the normal file transaction and backup.
// They contain requirements, never inferred maxima from a different gameconfig.
internal sealed class CompatibilityReceipt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string?> Bases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Authored { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> AddedPacks { get; set; } = [];
    public List<string> RemovedPacks { get; set; } = [];
    public List<string> BaselinePacks { get; set; } = [];
    public List<PoolMinimum> Minimums { get; set; } = [];
    public bool UnsupportedConfig { get; set; }
    public Dictionary<string, string> Mirrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Path => ModCompatibility.ReceiptDirectory + "\\" + Id + ".json";
}

internal sealed record PoolMinimum(string Section, string Name, long Value);

internal static class ModCompatibility
{
    internal const string ReceiptDirectory = @"ModHubState\compatibility";
    internal const string Dlc = @"mods\update\update.rpf|common\data\dlclist.xml";
    internal const string Config = @"mods\update\update.rpf|common\data\gameconfig.xml";
    internal const string OverlayDlc = @"onigiri\common\data\dlclist.xml";
    internal const string OverlayConfig = @"onigiri\common\data\gameconfig.xml";
    internal static readonly string[] Documents = [Dlc, Config, OverlayDlc, OverlayConfig];

    internal static bool IsReceipt(string rel) =>
        rel.StartsWith(ReceiptDirectory + "\\", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMirror(string rel) =>
        rel.StartsWith(@"onigiri\dlcpacks\", StringComparison.OrdinalIgnoreCase);

    internal static byte[] Encode(string text) => Encoding.UTF8.GetBytes(text);

    internal static string Decode(byte[] bytes) => TextEdits.Decode(bytes).text;

    internal static bool Same(string? a, string? b) =>
        a == b || a is not null && b is not null && XNode.DeepEquals(XDocument.Parse(a), XDocument.Parse(b));

    internal static List<CompatibilityReceipt> Load(GameInstall game)
    {
        var dir = System.IO.Path.Combine(game.Root, ReceiptDirectory);
        if (!Directory.Exists(dir))
            return [];
        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(L.T("core.compat.folderRedirected"));
        return Directory
            .EnumerateFiles(dir, "*.json")
            .Select(file =>
            {
                try
                {
                    if (
                        new FileInfo(file).Length > 2L << 20
                        || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0
                    )
                        throw new IOException(L.T("core.compat.receiptInvalid", file));
                    var receipt = JsonSerializer.Deserialize<CompatibilityReceipt>(File.ReadAllText(file))
                        ?? throw new IOException(L.T("core.compat.receiptCorrupted", file));
                    if (
                        receipt.Bases is null
                        || receipt.Authored is null
                        || receipt.Mirrors is null
                        || receipt.Minimums is null
                        || receipt.AddedPacks is null
                        || receipt.RemovedPacks is null
                        || receipt.BaselinePacks is null
                        || Documents.Any(k => !receipt.Bases.ContainsKey(k))
                        || receipt.Authored.Values.Any(v => v is null)
                        || receipt.Minimums.Any(m =>
                            m is null || m.Section is null || string.IsNullOrWhiteSpace(m.Name) || m.Value < 0
                        )
                        || !Guid.TryParseExact(receipt.Id, "N", out _)
                        || System.IO.Path.GetFileNameWithoutExtension(file) != receipt.Id
                        || receipt
                            .Bases.Keys.Concat(receipt.Authored.Keys)
                            .Any(k => !Documents.Contains(k, StringComparer.OrdinalIgnoreCase))
                    )
                        throw new IOException(L.T("core.compat.receiptCorrupted", file));
                    foreach (var rel in receipt.Mirrors.Keys)
                        if (
                            !IsMirror(rel)
                            || GameInstall.Normalize(rel) != rel
                            || receipt.Mirrors[rel] is not { Length: 64 } hash
                            || hash.Any(c => !Uri.IsHexDigit(c))
                        )
                            throw new IOException(L.T("core.compat.dlcMirrorInvalid"));
                    foreach (var pack in receipt.AddedPacks.Concat(receipt.RemovedPacks).Concat(receipt.BaselinePacks))
                        _ = GTAModManager.Core.Dlc.DlcList.Add(
                            "<SMandatoryPacksData><Paths></Paths></SMandatoryPacksData>",
                            pack
                        );
                    foreach (var xml in receipt.Bases.Values.Concat(receipt.Authored.Values).Where(v => v is not null))
                        _ = XDocument.Parse(xml!);
                    return receipt;
                }
                catch (Exception ex)
                    when (ex is JsonException or System.Xml.XmlException or ArgumentException or NullReferenceException)
                {
                    throw new IOException(L.T("core.compat.receiptCorrupted", file), ex);
                }
            })
            .OrderBy(r => r.Created)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }

    internal static Dictionary<string, string?> Compose(
        IReadOnlyList<CompatibilityReceipt> receipts,
        Dictionary<string, string?> bases
    )
    {
        var result = new Dictionary<string, string?>(bases, StringComparer.OrdinalIgnoreCase);
        if (receipts.Any(r => r.Authored.Count > 0) && receipts.Any(r => r.UnsupportedConfig))
            throw new IOException(L.T("core.compat.gameconfigUnsupported"));
        foreach (var receipt in receipts)
        foreach (var doc in receipt.Authored)
            result[doc.Key] = doc.Value;
        foreach (var key in Documents)
        {
            if (!result.TryGetValue(key, out var text) || text is null)
                continue;
            if (key.EndsWith("dlclist.xml", StringComparison.OrdinalIgnoreCase))
            {
                if (key == OverlayDlc)
                    text = OnigiriOverlay.SyncPacks(text, receipts.SelectMany(r => r.BaselinePacks), []).text;
                foreach (var receipt in receipts)
                    text = OnigiriOverlay.SyncPacks(text, receipt.AddedPacks, receipt.RemovedPacks).text;
            }
            else
                text = ApplyMinimums(text, receipts.SelectMany(r => r.Minimums));
            result[key] = text;
        }
        return result;
    }

    // Build/Platforms and other section selectors belong to the key; debug/PC/console pools never mix.
    private static string Section(XElement config) =>
        config.Name.LocalName
        + ":"
        + (string?)config.Attribute("type")
        + "|"
        + string.Join(
            "|",
            config.Parent?.Elements().Where(e => e != config).Select(e => e.ToString(SaveOptions.DisableFormatting))
                ?? []
        );

    private static IEnumerable<(string Section, XElement Entries)> Sections(XDocument doc) =>
        doc.Descendants("Config")
            .SelectMany(c => c.Elements("PoolSizes").Elements("Entries").Select(e => (Section(c), e)));

    private static Dictionary<string, long> Values(XElement entries) =>
        entries
            .Elements("Item")
            .Where(e =>
                e.Element("PoolName") is not null
                && long.TryParse((string?)e.Element("PoolSize")?.Attribute("value"), out _)
            )
            .GroupBy(e => e.Element("PoolName")!.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Max(e => (long)e.Element("PoolSize")!.Attribute("value")!),
                StringComparer.OrdinalIgnoreCase
            );

    internal static (List<PoolMinimum> Minimums, bool Unsupported) Requirements(
        string before,
        string after,
        IReadOnlyList<OivXmlOp> ops
    )
    {
        var current = XDocument.Parse(before);
        var minimums = new Dictionary<(string Section, string Name), long>();
        bool unsupported = false;
        static (string Name, long Value)? Pool(XElement item)
        {
            if (
                item.Name != "Item"
                || item.HasAttributes
                || item.Elements().Count() != 2
                || item.Element("PoolName") is not { } name
                || name.HasAttributes
                || name.HasElements
                || item.Element("PoolSize") is not { } size
                || size.HasElements
                || size.Attributes().Count() != 1
                || !long.TryParse((string?)size.Attribute("value"), out var value)
                || value < 0
            )
                return null;
            return (name.Value.Trim(), value);
        }
        foreach (var op in ops)
        {
            var selected = current.XPathSelectElements(op.XPath).ToList();
            if (op.Kind == OivXmlKind.Add)
            {
                var wrapper = XElement.Parse("<root>" + op.ContentXml + "</root>");
                if (
                    !wrapper.HasElements
                    || wrapper.Nodes().OfType<XText>().Any(n => !string.IsNullOrWhiteSpace(n.Value))
                )
                    unsupported = true;
                foreach (var node in selected)
                {
                    if (
                        node.Name != "Entries"
                        || node.Parent?.Name != "PoolSizes"
                        || node.Parent.Parent?.Name != "Config"
                    )
                    {
                        unsupported = true;
                        continue;
                    }
                    string section = Section(node.Parent.Parent);
                    foreach (var item in wrapper.Elements())
                    {
                        if (Pool(item) is not { } pool)
                        {
                            unsupported = true;
                            continue;
                        }
                        var key = (section, pool.Name);
                        minimums[key] = Math.Max(minimums.GetValueOrDefault(key), pool.Value);
                    }
                }
            }
            else if (op.Kind == OivXmlKind.Remove)
            {
                foreach (var item in selected)
                {
                    if (
                        item.Parent?.Name != "Entries"
                        || item.Parent.Parent?.Name != "PoolSizes"
                        || item.Parent.Parent.Parent?.Name != "Config"
                        || Pool(item) is not { } pool
                        || !minimums.TryGetValue((Section(item.Parent.Parent.Parent), pool.Name), out var minimum)
                        || pool.Value >= minimum
                    )
                        unsupported = true;
                }
            }
            else
                unsupported = true;
            current = XDocument.Parse(Decode(TextEdits.ApplyXml(Encode(current.ToString()), [op]).bytes));
        }
        unsupported |= !Same(current.ToString(), after);
        return (minimums.Select(p => new PoolMinimum(p.Key.Section, p.Key.Name, p.Value)).ToList(), unsupported);
    }

    internal static string ApplyMinimums(string text, IEnumerable<PoolMinimum> minimums)
    {
        var doc = XDocument.Parse(text);
        bool changed = false;
        var sections = Sections(doc).ToList();
        var requirements = minimums.ToList();
        if (requirements.Any(r => !sections.Any(s => s.Section == r.Section)))
            throw new IOException(L.T("core.compat.sectionMissing"));
        foreach (var (section, entries) in sections)
        foreach (
            var req in requirements
                .Where(r => r.Section == section)
                .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.MaxBy(r => r.Value)!)
        )
        {
            var items = entries
                .Elements("Item")
                .Where(e =>
                    e.Element("PoolName")?.Value.Trim().Equals(req.Name, StringComparison.OrdinalIgnoreCase) == true
                )
                .ToList();
            if (items.Count == 0)
            {
                entries.Add(
                    new XElement(
                        "Item",
                        new XElement("PoolName", req.Name),
                        new XElement("PoolSize", new XAttribute("value", req.Value))
                    )
                );
                changed = true;
            }
            else
                foreach (var item in items)
                    if (
                        item.Element("PoolSize")?.Attribute("value") is { } attr
                        && long.TryParse(attr.Value, out var v)
                        && v < req.Value
                    )
                    {
                        attr.Value = req.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        changed = true;
                    }
        }
        return changed ? doc.ToString(SaveOptions.DisableFormatting) : text;
    }

    internal static List<PoolMinimum> KnownPoolRequirement(string text, string name, long minimum)
    {
        var result = Sections(XDocument.Parse(text))
            .Where(s => Values(s.Entries).ContainsKey(name))
            .Select(s => new PoolMinimum(s.Section, name, minimum))
            .ToList();
        if (result.Count == 0)
            throw new IOException(L.T("core.compat.poolMissing", name));
        return result;
    }
}
