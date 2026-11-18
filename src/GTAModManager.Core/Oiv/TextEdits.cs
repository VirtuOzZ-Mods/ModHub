using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Oiv;

/// <summary>Text and XML edits of OIV commands. They work on file bytes and preserve the BOM and line endings.</summary>
public static class TextEdits
{
    public static (string text, bool bom) Decode(byte[] bytes)
    {
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    public static byte[] Encode(string text, bool bom)
    {
        var body = Encoding.UTF8.GetBytes(text);
        return bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    public static bool LineMatches(string line, string pattern, string? condition)
    {
        switch ((condition ?? "Equal").ToLowerInvariant())
        {
            case "mask":
                var rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                return Regex.IsMatch(line, rx, RegexOptions.IgnoreCase);
            case "startswith":
                return line.TrimStart().StartsWith(pattern.Trim(), StringComparison.OrdinalIgnoreCase);
            case "endswith":
                return line.TrimEnd().EndsWith(pattern.Trim(), StringComparison.OrdinalIgnoreCase);
            case "contains":
                return line.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            default:
                return string.Equals(line.Trim(), pattern.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Applies text commands. Unlike OpenIV, insert/add are skipped if the line already exists
    /// (reinstalling does not create duplicates in dlclist.xml).
    /// </summary>
    public static (string text, List<string> log) ApplyText(string text, IEnumerable<OivTextOp> ops)
    {
        var log = new List<string>();
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        bool trailingNl = text.EndsWith('\n');
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (trailingNl)
            lines.RemoveAt(lines.Count - 1);

        foreach (var op in ops)
        {
            var newLines = op.Text.Replace("\r\n", "\n").Split('\n');
            bool exists = newLines.All(n =>
                lines.Any(l => l.Trim().Equals(n.Trim(), StringComparison.OrdinalIgnoreCase))
            );
            switch (op.Kind)
            {
                case OivTextKind.Insert:
                {
                    if (exists)
                    {
                        log.Add(L.T("core.oiv.lineAlreadyExists", op.Text.Trim()));
                        break;
                    }
                    int idx = lines.FindIndex(l => LineMatches(l, op.Line ?? "", op.Condition));
                    if (idx < 0)
                        throw new OivFormatException(L.T("core.oiv.couldNotFindLineInsertion", op.Line));
                    int at = string.Equals(op.Where, "After", StringComparison.OrdinalIgnoreCase) ? idx + 1 : idx;
                    lines.InsertRange(at, newLines);
                    log.Add(L.T("core.oiv.inserted", op.Text.Trim()));
                    break;
                }
                case OivTextKind.Add:
                    if (exists)
                    {
                        log.Add(L.T("core.oiv.lineAlreadyExists", op.Text.Trim()));
                        break;
                    }
                    lines.AddRange(newLines);
                    log.Add(L.T("core.oiv.added", op.Text.Trim()));
                    break;
                case OivTextKind.Replace:
                {
                    int n = 0;
                    for (int i = 0; i < lines.Count; i++)
                        if (LineMatches(lines[i], op.Line ?? "", op.Condition))
                        {
                            lines[i] = op.Text;
                            n++;
                        }
                    log.Add(L.T("core.oiv.linesReplaced", n));
                    break;
                }
                case OivTextKind.Delete:
                {
                    int n = lines.RemoveAll(l => LineMatches(l, op.Text, op.Condition));
                    log.Add(L.T("core.oiv.linesDeleted", n));
                    break;
                }
            }
        }
        return (string.Join(nl, lines) + (trailingNl ? nl : ""), log);
    }

    /// <summary>XML commands via XPath 1.0. A missing node is a warning for remove and an error for add/replace.</summary>
    public static (byte[] bytes, List<string> log) ApplyXml(byte[] input, IEnumerable<OivXmlOp> ops)
    {
        var log = new List<string>();
        var (text, bom) = Decode(input);
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using (
            var r = XmlReader.Create(
                new StringReader(text),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }
            )
        )
            doc.Load(r);

        foreach (var op in ops)
        {
            var nodes = doc.SelectNodes(op.XPath)?.Cast<XmlNode>().ToList() ?? [];
            switch (op.Kind)
            {
                case OivXmlKind.Remove:
                    if (nodes.Count == 0)
                    {
                        log.Add(L.T("core.oiv.notFoundSkipped", op.XPath));
                        break;
                    }
                    foreach (var n in nodes)
                    {
                        if (n.PreviousSibling is XmlWhitespace ws)
                            ws.ParentNode!.RemoveChild(ws);
                        n.ParentNode!.RemoveChild(n);
                    }
                    log.Add(L.T("core.oiv.nodesDeleted", nodes.Count, op.XPath));
                    break;
                case OivXmlKind.Add:
                {
                    if (nodes.Count == 0)
                        throw new OivFormatException(L.T("core.oiv.xpathNotFoundAdd", op.XPath));
                    var target = nodes[0];
                    var fragment = Fragment(doc, op.ContentXml);
                    foreach (var child in fragment)
                    {
                        if (target.ChildNodes.Cast<XmlNode>().Any(c => Same(c, child)))
                        {
                            log.Add(L.T("core.oiv.nodeAlreadyExists", Short(child.OuterXml)));
                            continue;
                        }
                        string indent = IndentOf(target);
                        bool first = string.Equals(op.Append, "First", StringComparison.OrdinalIgnoreCase);
                        if (first)
                        {
                            target.PrependChild(child);
                            target.PrependChild(doc.CreateWhitespace("\n" + indent + "\t"));
                        }
                        else
                        {
                            var last = target.LastChild;
                            if (last is XmlWhitespace tail)
                            {
                                target.InsertBefore(doc.CreateWhitespace("\n" + indent + "\t"), tail);
                                target.InsertBefore(child, tail);
                            }
                            else
                                target.AppendChild(child);
                        }
                        log.Add(L.T("core.oiv.nodeAdded", Short(child.OuterXml)));
                    }
                    break;
                }
                case OivXmlKind.Replace:
                {
                    if (nodes.Count == 0)
                        throw new OivFormatException(L.T("core.oiv.xpathNotFoundReplace", op.XPath));
                    foreach (var n in nodes)
                    {
                        var fragment = Fragment(doc, op.ContentXml);
                        foreach (var child in fragment)
                            n.ParentNode!.InsertBefore(child, n);
                        n.ParentNode!.RemoveChild(n);
                    }
                    log.Add(L.T("core.oiv.nodesReplaced", nodes.Count, op.XPath));
                    break;
                }
            }
        }

        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = doc.FirstChild is not XmlDeclaration,
            Encoding = new UTF8Encoding(false),
            Indent = false,
            NewLineHandling = NewLineHandling.None,
        };
        using (var w = XmlWriter.Create(sb, settings))
            doc.Save(w);
        var output = sb.ToString()
            .Replace(
                "encoding=\"utf-16\"",
                "encoding=\"" + (doc.FirstChild is XmlDeclaration d ? d.Encoding : "UTF-8") + "\""
            );
        // XmlReader normalizes \r\n → \n; restore the source file's line-ending style.
        output = output.Replace("\r\n", "\n");
        if (text.Contains("\r\n"))
            output = output.Replace("\n", "\r\n");
        // The line break after the declaration is lost during serialization; restore it if it was there.
        var declEnd = text.IndexOf("?>", StringComparison.Ordinal);
        if (text.StartsWith("<?xml") && declEnd > 0)
        {
            var gap = new string(text[(declEnd + 2)..].TakeWhile(char.IsWhiteSpace).ToArray());
            var outEnd = output.IndexOf("?>", StringComparison.Ordinal) + 2;
            if (outEnd > 1 && !output[outEnd..].StartsWith(gap))
                output = output[..outEnd] + gap + output[outEnd..].TrimStart();
        }
        return (Encode(output, bom), log);
    }

    private static List<XmlNode> Fragment(XmlDocument doc, string xml)
    {
        var frag = doc.CreateDocumentFragment();
        frag.InnerXml = xml;
        return frag.ChildNodes.Cast<XmlNode>().Where(n => n is XmlElement).ToList();
    }

    private static bool Same(XmlNode a, XmlNode b) =>
        a is XmlElement && Regex.Replace(a.OuterXml, @"\s+", "") == Regex.Replace(b.OuterXml, @"\s+", "");

    private static string IndentOf(XmlNode node) =>
        node.PreviousSibling is XmlWhitespace ws ? ws.Value!.Split('\n').Last() : "";

    private static string Short(string s) => s.Length > 120 ? s[..120] + "…" : s;
}
