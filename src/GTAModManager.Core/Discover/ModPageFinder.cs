using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GTAModManager.Core.Game;

namespace GTAModManager.Core.Discover;

/// <summary>One result of a GTA5-Mods search page.</summary>
public sealed record ModSearchHit(string Url, string Title);

/// <summary>
/// Matching installed mod names with GTA5-Mods titles. Uploaders decorate titles with editions, versions and
/// punctuation ("N.O.S. - Nitro Overdrive System [Legacy + Enhanced]"), so both sides are reduced to letters
/// and digits without bracketed parts and trailing versions. Only an exact match of that form is accepted:
/// a wrong page with a confident look is worse than no page.
/// </summary>
public static partial class ModTitles
{
    [GeneratedRegex(@"\[[^\]]{0,80}\]|\([^)]{0,80}\)|\{[^}]{0,80}\}", RegexOptions.None, 200)]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s+v?\d+(?:\.\d+){0,3}[a-z]?\s*$", RegexOptions.IgnoreCase, 200)]
    private static partial Regex TrailingVersion();

    public static string Key(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";
        string s = Bracketed().Replace(WebUtility.HtmlDecode(title), " ").Trim();
        s = TrailingVersion().Replace(s, "");
        var b = new StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(c))
                b.Append(c);
        return b.ToString();
    }

    public static bool Same(string? a, string? b) => Key(a) is { Length: >= 3 } k && k == Key(b);

    /// <summary>Search query: the readable part of the name, without brackets and versions.</summary>
    public static string Query(string name) => TrailingVersion().Replace(Bracketed().Replace(name, " "), "").Trim();
}

/// <summary>GTA5-Mods search results (file-list-obj cards). External HTML is untrusted: bounded regexes, site paths only.</summary>
public static partial class Gta5ModsSearchParser
{
    [GeneratedRegex(
        @"<div class=""file-list-obj"">\s*<a href=""(?<u>/(?<c>[a-z]+)/[a-z0-9\-]{1,200})"" title=""(?<t>[^""]{1,300})""",
        RegexOptions.IgnoreCase,
        1000
    )]
    private static partial Regex Card();

    static readonly HashSet<string> FileCategories = new(
        ["scripts", "tools", "misc", "player", "vehicles", "weapons", "maps", "paintjobs"],
        StringComparer.OrdinalIgnoreCase
    );

    public static IReadOnlyList<ModSearchHit> Parse(string html)
    {
        var result = new List<ModSearchHit>();
        if (string.IsNullOrEmpty(html) || html.Length > 8_000_000)
            return result;
        try
        {
            foreach (Match m in Card().Matches(html))
                if (FileCategories.Contains(m.Groups["c"].Value) && result.Count < 60)
                    result.Add(
                        new(
                            "https://www.gta5-mods.com" + m.Groups["u"].Value,
                            WebUtility.HtmlDecode(m.Groups["t"].Value)
                        )
                    );
        }
        catch (RegexMatchTimeoutException) { }
        return result;
    }

    /// <summary>The single hit whose title matches the name; null when none or several different pages match.</summary>
    public static string? Pick(IEnumerable<ModSearchHit> hits, string name)
    {
        var urls = hits.Where(h => ModTitles.Same(h.Title, name)).Select(h => h.Url).Distinct().ToList();
        return urls.Count == 1 ? urls[0] : null;
    }
}

/// <summary>
/// Remembers which GTA5-Mods page belongs to an installed mod (found by search or the catalog), and when a
/// search found nothing, so the site is not asked again on every visit. A link set by the user always wins and
/// is stored in mod labels, not here.
/// </summary>
public sealed class ModPages(string? path = null)
{
    public static readonly TimeSpan RetryNotFound = TimeSpan.FromDays(7);
    readonly string _path = path ?? AppPaths.LibraryFile("mod-pages.json");
    static readonly object Gate = new();

    public sealed record Entry(string? Url, DateTimeOffset CheckedAt);

    Dictionary<string, Entry> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) is { } d
                    ? new(d, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public Entry? Get(string name)
    {
        lock (Gate)
            return Load().GetValueOrDefault(name);
    }

    /// <summary>Whether the site should be searched for this name now.</summary>
    public bool NeedsSearch(string name, DateTimeOffset now) =>
        Get(name) is not { } e || e.Url is null && now - e.CheckedAt > RetryNotFound;

    public void Set(string name, string? url, DateTimeOffset now)
    {
        lock (Gate)
        {
            var all = Load();
            all[name] = new(url, now);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(
                    temp,
                    JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true })
                );
                File.Move(temp, _path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Catalog entry with the same title on GTA5-Mods, if the built-in catalog already knows the mod.</summary>
    public static string? FromCatalog(IEnumerable<CatalogEntry> catalog, string name)
    {
        var urls = catalog
            .Where(e => e.Source == MetaSource.Gta5Mods && ModTitles.Same(e.Title, name))
            .Select(e => e.Url)
            .Distinct()
            .ToList();
        return urls.Count == 1 ? urls[0] : null;
    }

    /// <summary>
    /// Catalog entry whose marker file (the same "detect" rule Recommendations use) belongs to this mod. Works for
    /// mods installed from a local archive too, and does not depend on how the archive named the mod.
    /// </summary>
    public static string? FromCatalogFiles(IEnumerable<CatalogEntry> catalog, IEnumerable<string> files)
    {
        var set = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var urls = catalog
            .Where(e =>
                e.Source == MetaSource.Gta5Mods
                && e.Detect.Any(rule => set.Contains(rule.Split('|')[0].Trim().Replace('/', '\\')))
            )
            .Select(e => e.Url)
            .Distinct()
            .ToList();
        return urls.Count == 1 ? urls[0] : null;
    }

    /// <summary>Synthetic catalog entry used to load and cache metadata of an installed mod's page.</summary>
    public static CatalogEntry MetadataEntry(string name, string url) =>
        new()
        {
            Id = "installed:" + url.ToLowerInvariant(),
            Category = "installed",
            Url = url,
            Title = name,
            SourceName = "gta5mods",
        };
}
