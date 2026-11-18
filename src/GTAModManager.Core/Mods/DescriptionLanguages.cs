using System.Text.RegularExpressions;

namespace GTAModManager.Core.Mods;

/// <summary>One language version of a package description: "ru" or "en".</summary>
public sealed record DescriptionPart(string Language, string Text);

/// <summary>
/// Package descriptions often carry the same text twice, in English and in Russian. This finds the two versions so
/// the package screen can show one at a time with a language switch. Two ways, the first that works wins:
/// marker lines ("[RU]", "ENG:", "English", "Русский", "--- RU ---", flags) and, without markers, contiguous blocks of
/// paragraphs written in Cyrillic or Latin letters. Anything not clearly bilingual is returned as a single part.
/// The text itself is never changed.
/// </summary>
public static partial class DescriptionLanguages
{
    [GeneratedRegex(
        @"^[\s\-=*_#\[\(<>|:]*(?<lang>ru|rus|russian|русский|русская версия|на русском|eng?|english|английский|english version|in english|🇷🇺|🇬🇧|🇺🇸)[\s\-=*_#\]\)<>|:.]*$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex MarkerLine();

    private const int MinLetters = 40;

    public static IReadOnlyList<DescriptionPart> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var normalized = text.Replace("\r\n", "\n");
        return ByMarkers(normalized) ?? ByScript(normalized) ?? [new(Detect(normalized) ?? "en", text)];
    }

    private static string LanguageOf(string marker) =>
        marker.ToLowerInvariant() switch
        {
            "ru" or "rus" or "russian" or "русский" or "русская версия" or "на русском" or "🇷🇺" => "ru",
            _ => "en",
        };

    /// <summary>Marker lines split the text; a preamble before the first marker is kept on top of every part.</summary>
    private static List<DescriptionPart>? ByMarkers(string text)
    {
        var lines = text.Split('\n');
        var marks = new List<(int Line, string Lang)>();
        for (int i = 0; i < lines.Length; i++)
            if (MarkerLine().Match(lines[i].Trim()) is { Success: true } m && lines[i].Trim().Length > 0)
                marks.Add((i, LanguageOf(m.Groups["lang"].Value)));
        if (marks.Select(m => m.Lang).Distinct().Count() < 2)
            return null;
        string preamble = string.Join("\n", lines.Take(marks[0].Line)).Trim();
        var parts = new Dictionary<string, List<string>>();
        for (int k = 0; k < marks.Count; k++)
        {
            int end = k + 1 < marks.Count ? marks[k + 1].Line : lines.Length;
            string body = string.Join("\n", lines.Skip(marks[k].Line + 1).Take(end - marks[k].Line - 1)).Trim();
            if (body.Length == 0)
                continue;
            if (!parts.TryGetValue(marks[k].Lang, out var list))
                parts[marks[k].Lang] = list = [];
            list.Add(body);
        }
        if (parts.Count < 2 || parts.Values.Any(p => Letters(string.Join(" ", p)) < MinLetters))
            return null;
        return parts
            .Select(p => new DescriptionPart(
                p.Key,
                (preamble.Length > 0 ? preamble + "\n\n" : "") + string.Join("\n\n", p.Value)
            ))
            .ToList();
    }

    /// <summary>
    /// Without markers: paragraphs are classified by their letters; the text is bilingual only if it forms two
    /// contiguous blocks (all of one language, then all of the other). Paragraphs without letters of either kind
    /// (key lists, links) stay with the block they are in.
    /// </summary>
    private static List<DescriptionPart>? ByScript(string text)
    {
        var paragraphs = Regex.Split(text.Trim(), @"\n\s*\n");
        var langs = paragraphs.Select(Detect).ToList();
        var known = langs.Select((l, i) => (l, i)).Where(x => x.l is not null).ToList();
        if (known.Count < 2)
            return null;
        int switches = 0;
        for (int k = 1; k < known.Count; k++)
            if (known[k].l != known[k - 1].l)
                switches++;
        if (switches != 1)
            return null;
        int boundary = known.First(x => x.l != known[0].l).i;
        // Neutral paragraphs right before the boundary belong to the first block.
        var first = string.Join("\n\n", paragraphs.Take(boundary)).Trim();
        var second = string.Join("\n\n", paragraphs.Skip(boundary)).Trim();
        if (Letters(first) < MinLetters || Letters(second) < MinLetters)
            return null;
        return [new(known[0].l!, first), new(known.First(x => x.l != known[0].l).l!, second)];
    }

    /// <summary>"ru" when Cyrillic letters clearly dominate, "en" for Latin, null when unclear or too short.</summary>
    public static string? Detect(string text)
    {
        int cyr = 0,
            lat = 0;
        foreach (char c in text)
        {
            if (c is >= 'А' and <= 'я' or 'Ё' or 'ё')
                cyr++;
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                lat++;
        }
        if (cyr + lat < 12)
            return null;
        return cyr >= (cyr + lat) * 0.6 ? "ru"
            : lat >= (cyr + lat) * 0.8 ? "en"
            : null;
    }

    private static int Letters(string text) => text.Count(char.IsLetter);
}
