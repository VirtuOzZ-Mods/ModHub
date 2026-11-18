namespace GTAModManager.Core.Discover;

/// <summary>
/// Dynamic card data extracted from the mod page. null means no data (shown as "No data",
/// not zero). Stored separately from the catalog, in the cache.
/// </summary>
public sealed record ModMetadata
{
    public string? Title { get; init; }
    public string? Author { get; init; }

    /// <summary>Uploader avatar and profile page (gta5-mods.com only).</summary>
    public string? AuthorAvatarUrl { get; init; }
    public string? AuthorProfileUrl { get; init; }
    public string? ImageUrl { get; init; }
    public string? Version { get; init; }
    public double? Rating { get; init; }
    public int? Votes { get; init; }
    public long? Likes { get; init; }
    public long? Downloads { get; init; }
    public int? Comments { get; init; }
    public DateTimeOffset? Updated { get; init; }
    public List<AuthorLink> Links { get; init; } = [];

    /// <summary>Compatibility declared by the author (tags in the title/version, the site tag).</summary>
    public CompatInfo? Compat { get; init; }
}

/// <summary>Cache entry: data + time of the last successful refresh + local cover file.</summary>
public sealed record CachedMetadata
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public ModMetadata Data { get; init; } = new();
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>Cover file name in the cache folder (a name, not a path: the path is rebuilt so the cache cannot point elsewhere).</summary>
    public string? ImageFile { get; init; }

    /// <summary>Author avatar file name in the same cache folder.</summary>
    public string? AvatarFile { get; init; }

    /// <summary>Parser revision that produced Data; older entries are refreshed to pick up new fields.</summary>
    public int ParserVersion { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastErrorAt { get; init; }
}
