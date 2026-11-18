using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GTAModManager.Core.Discover;

/// <summary>
/// GTA5-Mods mod page adapter (the site has no public API). Reads only what the site itself publishes
/// for machines and in the page header: schema.org JSON-LD (rating, votes, author, comments, dates), og:image,
/// total download and like counters, links from the author panel and the mod tags. User comments are not read.
/// Independent of WPF; external HTML is untrusted: regexes have timeouts, strings are decoded and truncated.
/// </summary>
public static partial class Gta5ModsPageParser
{
    private const int Timeout = 1000;

    [GeneratedRegex(
        @"<script type=""application/ld\+json"">(?<j>[\s\S]{1,20000}?)</script>",
        RegexOptions.IgnoreCase,
        Timeout
    )]
    private static partial Regex JsonLd();

    [GeneratedRegex(
        @"<meta property=""og:(?<k>title|image)"" content=""(?<v>[^""]{1,1000})""",
        RegexOptions.IgnoreCase,
        Timeout
    )]
    private static partial Regex OpenGraph();

    [GeneratedRegex(@"<span class=""version"">(?<v>[^<]{1,120})</span>", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex HeaderVersion();

    [GeneratedRegex(
        @"class=""file-stat file-(?<k>downloads|likes)[^""]*"">\s*<span class=""num-(?:downloads|likes)"">(?<v>[\d,.\s]{1,20})</span>",
        RegexOptions.IgnoreCase,
        Timeout
    )]
    private static partial Regex TotalStat();

    [GeneratedRegex(@"<a\b[^>]{0,600}>", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex AnchorTag();

    [GeneratedRegex(@"\b(?<k>title|href)=""(?<v>[^""]{0,1000})""", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex Attr();

    [GeneratedRegex(@"/tags/(?<t>[a-z0-9-]{1,40})/", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex TagLink();

    [GeneratedRegex(
        @"legacy\s*(?:\+|&|and|/)\s*enhanced|enhanced\s*(?:\+|&|and|/)\s*legacy",
        RegexOptions.IgnoreCase,
        Timeout
    )]
    private static partial Regex BothEditions();

    [GeneratedRegex(@"legacy\s*only", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex LegacyOnly();

    [GeneratedRegex(@"enhanced\s*only", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex EnhancedOnly();

    /// <summary>Returns null if this is not a mod page (for example an error or challenge page), so the old cache is kept.</summary>
    public static ModMetadata? Parse(string html)
    {
        if (string.IsNullOrEmpty(html) || html.Length > 8_000_000)
            return null;
        try
        {
            string? ogTitle = null,
                ogImage = null;
            foreach (Match m in OpenGraph().Matches(html))
            {
                if (m.Groups["k"].Value.Equals("title", StringComparison.OrdinalIgnoreCase))
                    ogTitle ??= Clean(m.Groups["v"].Value);
                else
                    ogImage ??= m.Groups["v"].Value;
            }

            string? name = null,
                author = null;
            double? rating = null;
            int? votes = null,
                comments = null;
            DateTimeOffset? updated = null;
            bool hasLd = false;
            foreach (Match m in JsonLd().Matches(html))
            {
                try
                {
                    using var doc = JsonDocument.Parse(m.Groups["j"].Value);
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object || Str(r, "@type") != "CreativeWork")
                        continue;
                    hasLd = true;
                    name = Clean(Str(r, "name"));
                    author = r.TryGetProperty("author", out var a)
                        ? Clean(
                            a.ValueKind == JsonValueKind.Object ? Str(a, "name")
                            : a.ValueKind == JsonValueKind.String ? a.GetString()
                            : null
                        )
                        : null;
                    if (r.TryGetProperty("aggregateRating", out var ar) && ar.ValueKind == JsonValueKind.Object)
                    {
                        rating = Num(ar, "ratingValue");
                        votes = (int?)Num(ar, "reviewCount") ?? (int?)Num(ar, "ratingCount");
                    }
                    comments = (int?)Num(r, "comment_count") ?? (int?)Num(r, "commentCount");
                    if (
                        DateTimeOffset.TryParse(
                            Str(r, "dateModified"),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal,
                            out var dm
                        )
                    )
                        updated = dm;
                    break;
                }
                catch (JsonException) { }
            }
            if (!hasLd && ogTitle is null)
                return null;

            // The version comes from the page title (h1), not from All Versions or "More mods by…".
            string? version = null;
            int fileBlock = html.IndexOf("id=\"file\"", StringComparison.OrdinalIgnoreCase);
            int h1 = html.IndexOf("<h1", fileBlock < 0 ? 0 : fileBlock, StringComparison.OrdinalIgnoreCase);
            if (h1 >= 0)
            {
                int h1End = html.IndexOf("</h1>", h1, StringComparison.OrdinalIgnoreCase);
                if (
                    h1End > h1
                    && h1End - h1 < 4000
                    && HeaderVersion().Match(html, h1, h1End - h1) is { Success: true } vm
                )
                    version = Clean(vm.Groups["v"].Value);
            }

            // Total file counters (individual versions also have num-downloads; those are ignored).
            long? downloads = null,
                likes = null;
            foreach (Match m in TotalStat().Matches(html))
            {
                var v = Long(m.Groups["v"].Value);
                if (m.Groups["k"].Value.Equals("downloads", StringComparison.OrdinalIgnoreCase))
                    downloads ??= v;
                else
                    likes ??= v;
            }

            // Cover: https only, from the site's own host.
            string? image =
                LinkSafety.IsSafeWebUrl(WebUtility.HtmlDecode(ogImage ?? ""), out var iu)
                && iu is not null
                && LinkSafety.HostIs(iu, "gta5-mods.com")
                    ? iu.AbsoluteUri
                    : null;

            // The display name may differ from the profile ("Unknown Modder" for /users/alloc8or); both are shown.
            if (UploaderSlug(html) is { } slug)
            {
                if (author is null)
                    author = slug;
                else if (!author.Contains(slug, StringComparison.OrdinalIgnoreCase))
                    author = $"{author} ({slug})";
            }

            var tags = ReadTags(html);
            var title = name ?? ogTitle;
            string? profile = UploaderSlug(html) is { } user ? "https://www.gta5-mods.com/users/" + user : null;
            return new ModMetadata
            {
                Title = title,
                Author = author,
                AuthorAvatarUrl = ReadAvatar(html),
                AuthorProfileUrl = profile,
                ImageUrl = image,
                Version = version,
                Rating = rating is >= 0 and <= 5 ? Math.Round(rating.Value, 2) : null,
                Votes = votes is >= 0 ? votes : null,
                Likes = likes,
                Downloads = downloads,
                Comments = comments is >= 0 ? comments : null,
                Updated = updated,
                Links = ReadAuthorLinks(html),
                Compat = DeriveCompat($"{title} {ogTitle} {version}", tags),
            };
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    [GeneratedRegex(
        @"<a class=""username"" href=""/users/(?<u>[A-Za-z0-9_.\-]{1,40})""",
        RegexOptions.IgnoreCase,
        Timeout
    )]
    private static partial Regex UploaderLink();

    /// <summary>Uploader profile (author panel at the start of the file block).</summary>
    private static string? UploaderSlug(string html)
    {
        int panel = html.IndexOf("class=\"user-panel", StringComparison.OrdinalIgnoreCase);
        if (panel < 0)
            return null;
        var m = UploaderLink().Match(html, panel, Math.Min(2000, html.Length - panel));
        return m.Success ? m.Groups["u"].Value : null;
    }

    [GeneratedRegex(@"<img\b[^>]{0,300}\bsrc=""(?<v>[^""]{1,1000})""", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex ImageTag();

    /// <summary>Uploader avatar: the first image of the author panel, served by the site's own image host.</summary>
    public static string? ReadAvatar(string html)
    {
        int panel = html.IndexOf("class=\"user-panel", StringComparison.OrdinalIgnoreCase);
        if (panel < 0)
            return null;
        int end = html.IndexOf("class=\"username\"", panel, StringComparison.OrdinalIgnoreCase);
        if (end < 0 || end - panel > 2000)
            return null;
        var m = ImageTag().Match(html, panel, end - panel);
        return
            m.Success
            && LinkSafety.IsSafeWebUrl(WebUtility.HtmlDecode(m.Groups["v"].Value), out var u)
            && u is not null
            && LinkSafety.HostIs(u, "gta5-mods.com")
            ? u.AbsoluteUri
            : null;
    }

    /// <summary>Links from the author panel (the first user-social block after the file title), not from comments.</summary>
    public static List<AuthorLink> ReadAuthorLinks(string html)
    {
        var result = new List<AuthorLink>();
        // Author panel: avatar, nickname, social icons and a support button (Patreon). It ends before the version
        // list; the description and user comments are never included.
        int start = html.IndexOf("id=\"file-container\"", StringComparison.OrdinalIgnoreCase);
        int panel = html.IndexOf("class=\"user-panel", start < 0 ? 0 : start, StringComparison.OrdinalIgnoreCase);
        if (panel < 0)
            panel = html.IndexOf("class=\"user-social\"", start < 0 ? 0 : start, StringComparison.OrdinalIgnoreCase);
        if (panel < 0)
            return result;
        int end = html.Length;
        foreach (
            var marker in new[]
            {
                "file-version-container",
                "All Versions",
                "id=\"comments\"",
                "id=\"file-description\"",
                "class=\"file-description\"",
            }
        )
        {
            int i = html.IndexOf(marker, panel, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && i < end)
                end = i;
        }
        if (end - panel > 8000)
            end = panel + 8000;
        foreach (Match a in AnchorTag().Matches(html, panel).TakeWhile(a => a.Index < end))
        {
            string? title = null,
                href = null;
            foreach (Match at in Attr().Matches(a.Value))
            {
                if (at.Groups["k"].Value.Equals("title", StringComparison.OrdinalIgnoreCase))
                    title = WebUtility.HtmlDecode(at.Groups["v"].Value);
                else
                    href = WebUtility.HtmlDecode(at.Groups["v"].Value);
            }
            if (LinkSafety.Classify(title, href) is { } link && result.All(l => l.Kind != link.Kind))
                result.Add(link);
        }
        return result;
    }

    [GeneratedRegex(@"^/q\d+[a-z0-9\-]*/images/(?<p>[^?#]{1,600})$", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex ImagePath();

    private const int MaxGalleryImages = 60;

    /// <summary>
    /// Screenshots of the mod gallery (lightbox links of the file block, the cover first), as card-sized
    /// variants served by the site's own image host. Videos, avatars and other mods' thumbnails are skipped;
    /// the og:image cover is used when the page has no gallery.
    /// </summary>
    public static List<string> ReadGallery(string html)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(html) || html.Length > 8_000_000)
            return result;
        try
        {
            foreach (Match a in AnchorTag().Matches(html))
            {
                if (result.Count >= MaxGalleryImages)
                    break;
                if (!a.Value.Contains("mfp-image", StringComparison.OrdinalIgnoreCase))
                    continue;
                string? href = null;
                foreach (Match at in Attr().Matches(a.Value))
                    if (at.Groups["k"].Value.Equals("href", StringComparison.OrdinalIgnoreCase))
                        href = WebUtility.HtmlDecode(at.Groups["v"].Value);
                if (CardImage(href) is { } url && !result.Contains(url))
                    result.Add(url);
            }
            if (result.Count == 0)
                foreach (Match m in OpenGraph().Matches(html))
                    if (
                        m.Groups["k"].Value.Equals("image", StringComparison.OrdinalIgnoreCase)
                        && CardImage(WebUtility.HtmlDecode(m.Groups["v"].Value)) is { } cover
                    )
                    {
                        result.Add(cover);
                        break;
                    }
        }
        catch (RegexMatchTimeoutException) { }
        return result;
    }

    /// <summary>
    /// A GTA5-Mods image URL resized for a card (800 px wide), or null when the URL is not an image of the
    /// site's own host. Already sized or unknown layouts are kept as they are.
    /// </summary>
    public static string? CardImage(string? url)
    {
        if (!LinkSafety.IsSafeWebUrl(url, out var u) || u is null || !LinkSafety.HostIs(u, "gta5-mods.com"))
            return null;
        if (!u.Host.StartsWith("img.", StringComparison.OrdinalIgnoreCase))
            return null;
        var m = ImagePath().Match(u.AbsolutePath);
        return m.Success ? $"https://{u.Host}/q85-w800/images/{m.Groups["p"].Value}" : u.AbsoluteUri;
    }

    /// <summary>Tags of the mod itself (the tag-list block, not lists of similar mods).</summary>
    public static HashSet<string> ReadTags(string html)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int s = html.IndexOf("id=tag-list", StringComparison.OrdinalIgnoreCase);
        if (s < 0)
            s = html.IndexOf("id=\"tag-list\"", StringComparison.OrdinalIgnoreCase);
        if (s < 0)
            return tags;
        int e = html.IndexOf("id=\"file-dates\"", s, StringComparison.OrdinalIgnoreCase);
        if (e < 0 || e - s > 10000)
            e = Math.Min(html.Length, s + 4000);
        foreach (Match m in TagLink().Matches(html.Substring(s, e - s)))
            tags.Add(m.Groups["t"].Value);
        return tags;
    }

    /// <summary>
    /// Compatibility as declared by the author: "[Legacy + Enhanced]", "(Enhanced &amp; Legacy)", "[Legacy ONLY]" in the title
    /// or version; the site tag "enhanced" means Enhanced support. Anything else stays unknown.
    /// </summary>
    public static CompatInfo? DeriveCompat(string text, IReadOnlySet<string> tags)
    {
        if (BothEditions().IsMatch(text))
            return new CompatInfo { Legacy = true, Enhanced = true };
        if (LegacyOnly().IsMatch(text))
            return new CompatInfo { Legacy = true, Enhanced = false };
        if (EnhancedOnly().IsMatch(text))
            return new CompatInfo { Legacy = false, Enhanced = true };
        if (tags.Contains("enhanced"))
            return new CompatInfo { Enhanced = true };
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return d;
        if (
            v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)
        )
            return d;
        return null;
    }

    private static long? Long(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length is > 0 and < 16 ? long.Parse(digits, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Decodes HTML entities, collapses whitespace, removes control characters and limits length.</summary>
    public static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        var t = WebUtility.HtmlDecode(s);
        t = new string(t.Where(c => !char.IsControl(c)).ToArray());
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Length == 0)
            return null;
        return t.Length > 160 ? t[..160] + "…" : t;
    }
}
