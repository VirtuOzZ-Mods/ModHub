namespace GTAModManager.Core.Discover;

public enum LinkKind
{
    YouTube,
    Discord,
    Patreon,
    GitHub,
    Website,
    Steam,
    SocialClub,
    Twitch,
    Twitter,
    Instagram,
    TikTok,
    KoFi,
    Boosty,
}

/// <summary>A validated external link of an author or a mod.</summary>
public sealed record AuthorLink(LinkKind Kind, string Url);

/// <summary>
/// External link validation: only https URLs with a normal host are opened.
/// No file:, javascript:, ms-settings:, UNC paths and the like: a link cannot launch a file or a command.
/// </summary>
public static class LinkSafety
{
    public static bool IsSafeWebUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u))
            return false;
        if (u.Scheme != Uri.UriSchemeHttps || u.IsFile || u.IsUnc || u.IsLoopback)
            return false;
        if (string.IsNullOrEmpty(u.Host) || !u.Host.Contains('.') || u.HostNameType is not UriHostNameType.Dns)
            return false;
        if (!string.IsNullOrEmpty(u.UserInfo))
            return false;
        uri = u;
        return true;
    }

    public static bool IsSafeWebUrl(string? url) => IsSafeWebUrl(url, out _);

    /// <summary>The host equals the domain or is its subdomain (www.youtube.com → youtube.com).</summary>
    public static bool HostIs(Uri u, string domain) =>
        u.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || u.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Link type by the real host (not by its label). The "Website" label is accepted only for https links
    /// that do not lead to a known platform. null means the link is not shown.
    /// </summary>
    public static AuthorLink? Classify(string? title, string? url)
    {
        if (!IsSafeWebUrl(UpgradeKnownPlatform(url), out var u) || u is null)
            return null;
        foreach (var (kind, domains) in Platforms)
            if (domains.Any(d => HostIs(u, d)))
                return new(kind, u.AbsoluteUri);
        var t = title?.Trim() ?? "";
        if (
            t.Contains("website", StringComparison.OrdinalIgnoreCase)
            || t.Contains("homepage", StringComparison.OrdinalIgnoreCase)
            || t.Equals("site", StringComparison.OrdinalIgnoreCase)
        )
            return new(LinkKind.Website, u.AbsoluteUri);
        return null;
    }

    static readonly (LinkKind Kind, string[] Domains)[] Platforms =
    [
        (LinkKind.YouTube, ["youtube.com", "youtu.be"]),
        (LinkKind.Discord, ["discord.gg", "discord.com"]),
        (LinkKind.Patreon, ["patreon.com"]),
        (LinkKind.GitHub, ["github.com"]),
        (LinkKind.Steam, ["steamcommunity.com", "steampowered.com"]),
        (LinkKind.SocialClub, ["socialclub.rockstargames.com"]),
        (LinkKind.Twitch, ["twitch.tv"]),
        (LinkKind.Twitter, ["twitter.com", "x.com"]),
        (LinkKind.Instagram, ["instagram.com"]),
        (LinkKind.TikTok, ["tiktok.com"]),
        (LinkKind.KoFi, ["ko-fi.com"]),
        (LinkKind.Boosty, ["boosty.to"]),
    ];

    /// <summary>
    /// Profiles on known platforms are often entered with http:// (GTA5-Mods keeps them as typed). All of them
    /// serve https, so only those are upgraded; an unknown http site stays rejected.
    /// </summary>
    static string? UpgradeKnownPlatform(string? url)
    {
        if (
            url is null
            || !url.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)
            || !Platforms.Any(p => p.Domains.Any(d => HostIs(u, d)))
        )
            return url;
        return "https://" + url.Trim()["http://".Length..];
    }
}
