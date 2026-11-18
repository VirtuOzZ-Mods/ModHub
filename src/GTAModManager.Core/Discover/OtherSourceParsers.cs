using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GTAModManager.Core.Discover;

/// <summary>Official Script Hook V page (dev-c.com): version and release date. It has no ratings or downloads.</summary>
public static partial class DevCPageParser
{
    [GeneratedRegex(
        @"Version\s*(?:</?[a-z]+[^>]{0,80}>\s*){0,4}(?<v>v?[\d.]+(?:\s*/\s*[\d.]+)?)",
        RegexOptions.IgnoreCase,
        1000
    )]
    private static partial Regex VersionRx();

    [GeneratedRegex(
        @"Released\s*(?:</?[a-z]+[^>]{0,80}>\s*){0,4}(?<d>\d{1,2}\s+[A-Za-z]{3,9}\s+\d{4})",
        RegexOptions.IgnoreCase,
        1000
    )]
    private static partial Regex ReleasedRx();

    public static ModMetadata? Parse(string html)
    {
        if (string.IsNullOrEmpty(html) || html.Length > 2_000_000)
            return null;
        try
        {
            if (!html.Contains("Script Hook V", StringComparison.OrdinalIgnoreCase))
                return null;
            var v = VersionRx().Match(html);
            var d = ReleasedRx().Match(html);
            DateTimeOffset? updated =
                d.Success
                && DateTimeOffset.TryParseExact(
                    d.Groups["d"].Value,
                    ["d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var dt
                )
                    ? dt
                    : null;
            return new ModMetadata
            {
                Title = "Script Hook V",
                Author = "Alexander Blade",
                Version = v.Success ? Gta5ModsPageParser.Clean(v.Groups["v"].Value) : null,
                Updated = updated,
            };
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}

/// <summary>Official GitHub REST API: the repository's latest release (version, date) and the author's GitHub link.</summary>
public static class GitHubReleaseParser
{
    public static ModMetadata? Parse(string json, string repo)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
                return null;
            string? tag =
                r.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            DateTimeOffset? published =
                r.TryGetProperty("published_at", out var p)
                && p.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    p.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var pd
                )
                    ? pd
                    : null;
            if (tag is null && published is null)
                return null;
            var owner = repo.Split('/')[0];
            var links = new List<AuthorLink>();
            if (LinkSafety.Classify("GitHub", $"https://github.com/{repo}") is { } gh)
                links.Add(gh);
            return new ModMetadata
            {
                Author = Gta5ModsPageParser.Clean(owner),
                Version = Gta5ModsPageParser.Clean(tag),
                Updated = published,
                Links = links,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
