using System.Text.RegularExpressions;

namespace GTAModManager.Core.Mods;

/// <summary>Part of a key notation: a key, a connector ("+", "/", "then") or a parenthesized hint.</summary>
public enum KeyTokenKind
{
    Key,
    Joiner,
    Hint,
}

public sealed record KeyToken(KeyTokenKind Kind, string Text);

public sealed record ControlRow(IReadOnlyList<KeyToken> Keys, string Action);

public sealed record ControlGroup(string Title, List<ControlRow> Rows, List<string> Notes);

/// <summary>
/// Finds "key  —  action" control blocks in a mod description and splits them into groups.
/// Uses strict deterministic rules: a line counts as a control only if its left part consists entirely
/// of known key names. The text is never translated or rewritten.
/// </summary>
public static partial class ControlsParser
{
    // Individual words that key names are made of.
    private static readonly HashSet<string> KeyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ctrl",
        "control",
        "shift",
        "alt",
        "tab",
        "enter",
        "return",
        "space",
        "spacebar",
        "backspace",
        "esc",
        "escape",
        "delete",
        "del",
        "insert",
        "ins",
        "home",
        "end",
        "pageup",
        "pagedown",
        "pgup",
        "pgdn",
        "page",
        "up",
        "down",
        "left",
        "right",
        "caps",
        "capslock",
        "lock",
        "num",
        "numpad",
        "numlock",
        "lmb",
        "rmb",
        "mmb",
        "mouse",
        "button",
        "wheel",
        "scroll",
        "middle",
        "lb",
        "rb",
        "lt",
        "rt",
        "dpad",
        "d-pad",
        "a",
        "b",
        "x",
        "y",
        "start",
        "select",
        "back",
        "arrow",
        "arrows",
        "key",
        "keys",
        "f",
        "minus",
        "plus",
        "pause",
        "print",
        "printscreen",
        "win",
        "windows",
        "menu",
        "клавиша",
        "пробел",
        "стрелки",
        "стрелка",
        "вверх",
        "вниз",
        "влево",
        "вправо",
        "лкм",
        "пкм",
        "скм",
        "колесо",
        "мыши",
    };
    private static readonly HashSet<string> Joiners = new(StringComparer.OrdinalIgnoreCase)
    {
        "or",
        "then",
        "and",
        "или",
        "затем",
        "и",
        "/",
        "+",
        ",",
    };

    // The main separator is a tab or 2+ spaces; "key - action" is used only when there is no main separator
    // (otherwise "Num -" would turn into "Num" + an action).
    [GeneratedRegex(@"^(?<key>\S.*?)(?:\t+|\s{2,})(?<action>\S.*)$")]
    private static partial Regex KeyLine();

    [GeneratedRegex(@"^(?<key>\S.*?)\s[-–—:=]\s+(?<action>\S.*)$")]
    private static partial Regex KeyLineDash();

    [GeneratedRegex(@"(\([^)]*\))|(\s*\+\s*)|(\s*/\s*)|(\s*,\s*)|(\s+)")]
    private static partial Regex Splitter();

    [GeneratedRegex(
        @"^(F([1-9]|1[0-2])|Num\d|Num[+\-*/.]|NumPad\d|Numpad\d|[A-Za-z0-9]|[\[\]\\;'`,.\-=/*+]|~)$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SingleKey();

    [GeneratedRegex(
        @"(controls|hotkeys|keybind|key ?bindings|управлени|горячие клавиши|клавиши)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ControlsHeading();

    /// <summary>Control groups; empty if fewer than three lines were found.</summary>
    public static List<ControlGroup> Parse(string? text)
    {
        var groups = new List<ControlGroup>();
        if (string.IsNullOrWhiteSpace(text))
            return groups;
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();

        ControlGroup? current = null;
        string? pendingTitle = null;
        bool inControls = false; // after the first control line, up to two blank lines in a row
        int blanks = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                blanks++;
                if (blanks >= 2)
                {
                    inControls = false;
                    current = null;
                }
                continue;
            }
            blanks = 0;

            if (TryParseRow(line) is { } row)
            {
                if (current is null || pendingTitle is not null)
                {
                    current = new ControlGroup(pendingTitle ?? "", [], []);
                    groups.Add(current);
                    pendingTitle = null;
                }
                current.Rows.Add(row);
                inControls = true;
                continue;
            }

            // A short line without a separator followed by controls is a group header.
            bool shortLine = line.Length <= 48 && !line.EndsWith('.') && !line.Contains("  ");
            bool nextIsRow = NextNonEmpty(lines, i) is { } next && TryParseRow(next) is not null;
            if (shortLine && nextIsRow)
            {
                pendingTitle = line.TrimEnd(':');
                current = null;
                continue;
            }
            // A general header like "BASIC CONTROLS" starts a block but does not become a group itself.
            if (shortLine && ControlsHeading().IsMatch(line))
            {
                pendingTitle = null;
                current = null;
                continue;
            }

            // A regular paragraph inside a control block is a note for the last group.
            if (inControls && groups.Count > 0)
            {
                groups[^1].Notes.Add(line);
                continue;
            }
            pendingTitle = null;
        }

        // Headers like "BASIC CONTROLS" without their own lines are not needed; groups without lines are removed.
        groups.RemoveAll(g => g.Rows.Count == 0);
        return groups.Sum(g => g.Rows.Count) >= 3 ? groups : [];
    }

    private static string? NextNonEmpty(List<string> lines, int i)
    {
        for (int j = i + 1; j < lines.Count; j++)
            if (lines[j].Trim().Length > 0)
                return lines[j].Trim();
        return null;
    }

    /// <summary>A "keys  action" line, if the left part consists entirely of key names.</summary>
    public static ControlRow? TryParseRow(string line)
    {
        var m = KeyLine().Match(line);
        if (!m.Success)
            m = KeyLineDash().Match(line);
        if (!m.Success)
            return null;
        var keyText = m.Groups["key"].Value.Trim();
        var action = m.Groups["action"].Value.Trim();
        if (keyText.Length > 40 || action.Length < 2)
            return null;
        var tokens = Tokenize(keyText);
        if (tokens is null || !tokens.Any(t => t.Kind == KeyTokenKind.Key))
            return null;
        return new ControlRow(tokens, action);
    }

    private static List<KeyToken>? Tokenize(string keyText)
    {
        var result = new List<KeyToken>();
        var words = new List<string>();
        void FlushWords()
        {
            if (words.Count == 0)
                return;
            result.Add(new KeyToken(KeyTokenKind.Key, string.Join(' ', words)));
            words.Clear();
        }

        int pos = 0;
        foreach (Match sep in Splitter().Matches(keyText))
        {
            var word = keyText[pos..sep.Index];
            if (word.Length > 0 && !AddWord(word))
                return null;
            pos = sep.Index + sep.Length;
            var s = sep.Value.Trim();
            if (s.StartsWith('('))
            {
                FlushWords();
                result.Add(new KeyToken(KeyTokenKind.Hint, s));
            }
            else if (s is "+" or "/" or ",")
            {
                FlushWords();
                result.Add(new KeyToken(KeyTokenKind.Joiner, s));
            }
            // space: words of one key ("Left Mouse Button") stay together
        }
        var tail = keyText[pos..];
        if (tail.Length > 0 && !AddWord(tail))
            return null;
        FlushWords();
        return result;

        bool AddWord(string w)
        {
            if (Joiners.Contains(w))
            {
                FlushWords();
                result.Add(new KeyToken(KeyTokenKind.Joiner, w));
                return true;
            }
            if (
                SingleKey().IsMatch(w)
                || KeyWords.Contains(w)
                || w.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && w.Length <= 7
            )
            {
                // Single-letter keys: each is a separate key; multi-word names stay together.
                if (w.Length == 1 && words.Count > 0 && !KeyWords.Contains(words[^1]))
                    FlushWords();
                words.Add(w);
                return true;
            }
            return false;
        }
    }
}
