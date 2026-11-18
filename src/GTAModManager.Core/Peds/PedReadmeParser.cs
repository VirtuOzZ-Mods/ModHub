using System.Text;
using System.Text.RegularExpressions;

namespace GTAModManager.Core.Peds;

/// <summary>Read-only, conservative presentation of author-supplied README text.
/// Unknown prose remains available in PedArchiveAnalysis.Readme; no links are fetched.</summary>
public static partial class PedReadmeParser
{
    private enum Section
    {
        Other,
        Authors,
        Credits,
        Installation,
    }

    private const int MaxInput = 262144;
    private const int MaxLinks = 64;

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(
        @"^(?:by|created by|made by)\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex ByPattern();

    [GeneratedRegex(
        @"^(?:authors?|авторы?|создатель)\s*:\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex AuthorPattern();

    [GeneratedRegex(
        @"^(?:credits?|acknowledg(?:e)?ments?|благодарности|кредиты|участники)\s*:?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex CreditsPattern();

    [GeneratedRegex(
        @"^(?:installation|install(?:ation)? instructions|how to install|установка|инструкция(?: по установке)?)(?:\s*[:\-–].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex InstallPattern();

    [GeneratedRegex(
        @"^(?:mod features?|features?|follow us|social(?: links)?|support|requirements?|change ?log|особенности|требования|поддержка|контакты|соцсети)\s*:?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex OtherHeadingPattern();

    [GeneratedRegex(
        @"^(facebook|instagram|twitter|x|vk|вконтакте|youtube|patreon|5\s*mods|gta5[\- ]?mods)\s*:\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex SocialPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex HandlePattern();

    public static PedReadmeDetails Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return PedReadmeDetails.Empty;
        text = text[..Math.Min(text.Length, MaxInput)].Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF');
        var authors = new StringBuilder();
        var credits = new StringBuilder();
        var installation = new StringBuilder();
        var links = new List<PedAuthorLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string title = "";
        Section section = Section.Other;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 4096)
                line = line[..4096];
            ExtractLinks(line, links, seen);
            if (Separator(line))
            {
                section = Section.Other;
                continue;
            }
            if (CreditsPattern().IsMatch(line))
            {
                section = Section.Credits;
                continue;
            }
            if (InstallPattern().IsMatch(line))
            {
                section = Section.Installation;
                Append(installation, line);
                continue;
            }
            if (OtherHeadingPattern().IsMatch(line))
            {
                section = Section.Other;
                continue;
            }
            // Nested credit/author lines such as "texture design by ..." are kept intact.
            if (section == Section.Other)
            {
                var by = ByPattern().Match(line);
                var author = AuthorPattern().Match(line);
                if (by.Success)
                {
                    Append(authors, by.Groups[1].Value);
                    continue;
                }
                if (author.Success)
                {
                    section = string.IsNullOrWhiteSpace(author.Groups[1].Value) ? Section.Authors : Section.Other;
                    Append(authors, author.Groups[1].Value);
                    continue;
                }
                if (title.Length == 0 && IsTitle(line))
                    title = Cut(line, 300);
            }
            switch (section)
            {
                case Section.Authors:
                    Append(authors, line);
                    break;
                case Section.Credits:
                    Append(credits, line);
                    break;
                case Section.Installation:
                    Append(installation, line);
                    break;
            }
        }
        return new(
            title,
            Cut(authors.ToString().Trim(), 4096),
            Cut(credits.ToString().Trim(), 65536),
            Cut(installation.ToString().Trim(), 131072),
            links
        );
    }

    private static void Append(StringBuilder target, string line)
    {
        if (target.Length == 0 && line.Length == 0)
            return;
        target.AppendLine(line);
    }

    private static string Cut(string value, int limit) => value.Length <= limit ? value : value[..limit];

    private static bool Separator(string line) =>
        line.Length >= 3 && line.All(c => c is '-' or '=' or '_' or '*' or '─' or ' ');

    private static bool IsTitle(string line) =>
        line.Length > 0
        && !line.StartsWith('#')
        && !line.StartsWith('>')
        && !line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        && !SocialPattern().IsMatch(line)
        && !line.Equals("readme", StringComparison.OrdinalIgnoreCase)
        && !line.Replace('\\', '/').Split('/')[^1].Equals("readme.txt", StringComparison.OrdinalIgnoreCase)
        && !line.Replace('\\', '/').Split('/')[^1].Equals("readme.md", StringComparison.OrdinalIgnoreCase)
        && !line.StartsWith('©')
        && !line.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase);

    private static void ExtractLinks(string line, List<PedAuthorLink> result, HashSet<string> seen)
    {
        if (result.Count >= MaxLinks)
            return;
        var social = SocialPattern().Match(line);
        foreach (Match match in UrlPattern().Matches(line))
        {
            string value = TrimUrl(match.Value);
            if (!SafeUrl(value, out var uri))
                continue;
            Add(new(social.Success ? social.Groups[1].Value : uri!.Host, value, value), uri!, result, seen);
            if (result.Count >= MaxLinks)
                return;
        }
        if (!social.Success || UrlPattern().IsMatch(social.Groups[2].Value))
            return;
        string handle = social.Groups[2].Value.Trim();
        string bare = handle.TrimStart('@');
        if (!HandlePattern().IsMatch(bare) || bare.StartsWith('.') || bare.EndsWith('.') || bare.Contains(".."))
            return;
        string platform = social.Groups[1].Value.ToLowerInvariant();
        string? url = platform switch
        {
            "facebook" when bare.Length >= 5 && bare.All(c => char.IsAsciiLetterOrDigit(c) || c == '.') =>
                "https://www.facebook.com/" + bare,
            "instagram" when bare.Length <= 30 && bare.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_') =>
                "https://www.instagram.com/" + bare,
            "twitter" or "x" when bare.Length <= 15 && bare.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') =>
                "https://x.com/" + bare,
            "vk" or "вконтакте" => "https://vk.com/" + bare,
            "youtube" when handle.StartsWith('@') => "https://www.youtube.com/@" + bare,
            "patreon" => "https://www.patreon.com/" + bare,
            _ => null,
        };
        if (url is not null && SafeUrl(url, out var inferred))
            Add(new(social.Groups[1].Value, handle, url, true), inferred!, result, seen);
    }

    private static bool SafeUrl(string value, out Uri? uri)
    {
        uri = null;
        if (
            value.Length > 2048
            || value.Contains('\\')
            || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Host.Length == 0
            || parsed.Scheme is not ("http" or "https")
            || parsed.UserInfo.Length > 0
        )
            return false;
        uri = parsed;
        return true;
    }

    private static void Add(PedAuthorLink link, Uri uri, List<PedAuthorLink> result, HashSet<string> seen)
    {
        string key =
            uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant()
            + uri.PathAndQuery
            + uri.Fragment;
        if (result.Count < MaxLinks && seen.Add(key))
            result.Add(link);
    }

    private static string TrimUrl(string value)
    {
        value = value.TrimEnd('.', ',', ';', ':', '!', '?');
        // Parentheses inside a URL are significant. Remove only enclosing prose's
        // surplus closing bracket, keeping balanced path and query punctuation.
        foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('{', '}') })
            while (value.EndsWith(close) && value.Count(c => c == close) > value.Count(c => c == open))
                value = value[..^1];
        return value.TrimEnd('.', ',', ';', ':', '!', '?');
    }
}
