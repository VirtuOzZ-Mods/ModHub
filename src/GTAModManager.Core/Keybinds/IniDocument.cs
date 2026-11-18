using System.Text;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Keybinds;

/// <summary>An INI "key = value" line with the exact position of the value within the line.</summary>
public sealed record IniValue(
    int Line,
    string Section,
    string Key,
    string Value,
    int ValueStart,
    int ValueLength,
    string? Comment
);

/// <summary>
/// An INI file as a list of lines with preserved line endings. An edit changes only the value characters
/// on the target line; comments, whitespace, order, other settings and line endings stay byte for byte.
/// </summary>
public sealed class IniDocument
{
    private readonly List<(string text, string ending)> _lines;
    public IReadOnlyList<IniValue> Values { get; }

    private IniDocument(List<(string, string)> lines)
    {
        _lines = lines;
        Values = ParseValues();
    }

    public static IniDocument Parse(string text)
    {
        var lines = new List<(string, string)>();
        int i = 0;
        while (i < text.Length)
        {
            int nl = text.IndexOfAny(['\r', '\n'], i);
            if (nl < 0)
            {
                lines.Add((text[i..], ""));
                break;
            }
            string ending =
                text[nl] == '\r' && nl + 1 < text.Length && text[nl + 1] == '\n' ? "\r\n" : text[nl].ToString();
            lines.Add((text[i..nl], ending));
            i = nl + ending.Length;
        }
        return new IniDocument(lines);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (t, e) in _lines)
            sb.Append(t).Append(e);
        return sb.ToString();
    }

    /// <summary>New text where the value on line is replaced with newValue (everything else unchanged).</summary>
    public IniDocument WithValue(int line, string newValue)
    {
        var v = Values.FirstOrDefault(x => x.Line == line)
            ?? throw new ArgumentException(L.T("core.keybinds.lineContainsNoSetting", line + 1));
        var (text, ending) = _lines[line];
        var copy = _lines.ToList();
        copy[line] = (text[..v.ValueStart] + newValue + text[(v.ValueStart + v.ValueLength)..], ending);
        return new IniDocument(copy);
    }

    private List<IniValue> ParseValues()
    {
        var result = new List<IniValue>();
        string section = "";
        for (int n = 0; n < _lines.Count; n++)
        {
            var line = _lines[n].text;
            var t = line.TrimStart();
            if (t.Length == 0 || t[0] is ';' or '#' || t.StartsWith("//"))
                continue;
            if (t[0] == '[')
            {
                int close = t.IndexOf(']');
                if (close > 0)
                    section = t[1..close].Trim();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            if (key.Length == 0)
                continue;

            int start = eq + 1;
            int commentAt = InlineCommentStart(line, start);
            int end = commentAt < 0 ? line.Length : commentAt;
            // The value without surrounding whitespace.
            while (start < end && char.IsWhiteSpace(line[start]))
                start++;
            int stop = end;
            while (stop > start && char.IsWhiteSpace(line[stop - 1]))
                stop--;
            var comment = commentAt < 0 ? null : line[commentAt..].TrimStart(';', '#', '/', ' ', '\t').Trim();
            result.Add(new IniValue(n, section, key, line[start..stop], start, stop - start, comment));
        }
        return result;
    }

    /// <summary>Start of a trailing comment: "//", or ";"/"#" after a space or tab.</summary>
    private static int InlineCommentStart(string line, int from)
    {
        for (int i = from; i < line.Length; i++)
        {
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')
                return i;
            if (line[i] is ';' or '#' && i > from && char.IsWhiteSpace(line[i - 1]))
                return i;
        }
        return -1;
    }
}
