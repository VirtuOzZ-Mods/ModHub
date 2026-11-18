using System.Text.RegularExpressions;
using System.Xml.Linq;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Dlc;

/// <summary>
/// Minimal dlclist.xml edits that preserve formatting:
/// existing lines are never rewritten, and adding is idempotent.
/// </summary>
public static partial class DlcList
{
    // Real packs contain dots and spaces ("N.O.S - Nitro Overdrive System").
    [GeneratedRegex(@"^[A-Za-z0-9_\-. ]+$")]
    private static partial Regex PackName();

    public static string ItemFor(string pack) => $"dlcpacks:/{pack}/";

    /// <summary>Normalized pack names from all &lt;Item&gt; elements.</summary>
    public static IReadOnlyList<string> Packs(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Descendants("Item")
            .Select(i => Normalize(i.Value))
            .Where(n => n is not null)
            .Cast<string>()
            .ToList();
    }

    public static (string xml, bool changed) Add(string xml, string pack)
    {
        Validate(pack);
        var doc = XDocument.Parse(xml); // XML well-formedness check
        if (doc.Descendants("Paths").FirstOrDefault() is null)
            throw new FormatException(L.T("core.dlc.dlclistXmlContainsNoPaths"));
        if (Packs(xml).Contains(pack, StringComparer.OrdinalIgnoreCase))
            return (xml, false);

        string nl = xml.Contains("\r\n") ? "\r\n" : "\n";
        int last = xml.LastIndexOf("</Item>", StringComparison.OrdinalIgnoreCase);
        string result;
        if (last >= 0)
        {
            int lineStart = xml.LastIndexOf('\n', last) + 1;
            string indent = new(xml[lineStart..].TakeWhile(c => c is ' ' or '\t').ToArray());
            int insertAt = last + "</Item>".Length;
            result = xml.Insert(insertAt, $"{nl}{indent}<Item>{ItemFor(pack)}</Item>");
        }
        else
        {
            int close = xml.IndexOf("</Paths>", StringComparison.OrdinalIgnoreCase);
            if (close < 0)
                throw new FormatException(L.T("core.dlc.emptyPathsNotSupportedWithout"));
            result = xml.Insert(close, $"\t<Item>{ItemFor(pack)}</Item>{nl}\t");
        }
        XDocument.Parse(result);
        return (result, true);
    }

    public static (string xml, bool changed) Remove(string xml, string pack)
    {
        Validate(pack);
        XDocument.Parse(xml);
        var rx = new Regex(
            @"[ \t]*<Item>\s*dlcpacks:[/\\]" + Regex.Escape(pack) + @"[/\\]?\s*</Item>[ \t]*(\r?\n)?",
            RegexOptions.IgnoreCase
        );
        var result = rx.Replace(xml, "");
        if (result == xml)
            return (xml, false);
        XDocument.Parse(result);
        return (result, true);
    }

    private static string? Normalize(string value)
    {
        var v = value.Trim().Replace('\\', '/');
        if (!v.StartsWith("dlcpacks:/", StringComparison.OrdinalIgnoreCase))
            return null;
        return v["dlcpacks:/".Length..].Trim('/');
    }

    private static void Validate(string pack)
    {
        if (!PackName().IsMatch(pack) || pack.Contains("..") || pack != pack.Trim() || pack.Trim('.').Length == 0)
            throw new ArgumentException(L.T("core.dlc.invalidDlcPackName", pack));
    }
}
