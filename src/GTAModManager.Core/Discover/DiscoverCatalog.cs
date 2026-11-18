using System.Text.Json;
using System.Text.Json.Serialization;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Discover;

/// <summary>Where the card's dynamic data comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetaSource>))]
public enum MetaSource
{
    None,
    Gta5Mods,
    GitHub,
    DevC,
    RazedMods,
}

/// <summary>Confirmed compatibility: null means unconfirmed (no badge is shown).</summary>
public sealed record CompatInfo
{
    public bool? Legacy { get; init; }
    public bool? Enhanced { get; init; }
    public string? Note { get; init; }

    public bool Supports(bool enhanced) => (enhanced ? Enhanced : Legacy) == true;

    public bool IsKnown => Legacy is not null || Enhanced is not null;
}

public sealed record CatalogCategory
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";

    /// <summary>manual: the author's order; kind: component kind and compatibility.</summary>
    public string DefaultSort { get; init; } = "manual";

    /// <summary>A separate collection page with a large header.</summary>
    public bool Hero { get; init; }
}

/// <summary>Static catalog entry. Covers, rating and downloads are not here but in the metadata cache.</summary>
public sealed record CatalogEntry
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public int Order { get; init; }

    /// <summary>Component kind (for Essentials) and its order.</summary>
    public string? Kind { get; init; }
    public int KindOrder { get; init; }
    public string SourceName { get; init; } = "";
    public required string Url { get; init; }

    /// <summary>GitHub repository "owner/name" for the github source.</summary>
    public string? Repo { get; init; }

    /// <summary>Fallback title and author while metadata is not loaded or unavailable.</summary>
    public required string Title { get; init; }
    public string? Author { get; init; }

    /// <summary>Manually confirmed compatibility; takes precedence over what is inferred from the page.</summary>
    public CompatInfo? Compat { get; init; }
    public bool Hidden { get; init; }

    /// <summary>What the Install button does: base uses the Modding Essentials card, download fetches from GTA5-Mods, none shows the page only.</summary>
    public string? InstallMode { get; init; }

    /// <summary>
    /// Marker files used to detect an installed mod (installed outside Recommendations): a path from the game root,
    /// optionally with a condition on the file description, e.g. "ScriptHookVDotNet.asi|product~Enhanced". Any match is enough.
    /// </summary>
    /// <remarks>Stored as a single string (rules separated by newlines) so catalog entries compare by value.</remarks>
    public string? DetectRules { get; init; }

    [JsonIgnore]
    public IReadOnlyList<string> Detect => DetectRules is { Length: > 0 } rules ? rules.Split('\n') : [];

    /// <summary>Bundled cover (a file in Discover\Assets\covers) used when the source provides no image.</summary>
    public string? Cover { get; init; }

    /// <summary>Cover picked from the mod gallery (a GTA5-Mods image); used instead of the page's og:image.</summary>
    public string? CoverUrl { get; init; }

    /// <summary>ScriptHookVDotNet builds the mod needs on Legacy: Nightly and/or Legacy + Enhanced (Chiheb-Bacha).</summary>
    public bool ShvdnNightly { get; init; }
    public bool ShvdnLegacyEnhanced { get; init; }

    /// <summary>
    /// The game version or the ScriptHookVDotNet build is not confirmed ("not sure" in the editor): installing
    /// is allowed on both editions, with a warning that the game may stop launching.
    /// </summary>
    public bool EditionUnsure { get; init; }
    public bool ShvdnUnsure { get; init; }

    [JsonIgnore]
    public string Install => InstallMode?.ToLowerInvariant() ?? (Source == MetaSource.Gta5Mods ? "download" : "none");

    [JsonIgnore]
    public MetaSource Source =>
        SourceName.ToLowerInvariant() switch
        {
            "gta5mods" => MetaSource.Gta5Mods,
            "github" => MetaSource.GitHub,
            "devc" => MetaSource.DevC,
            "razedmods" => MetaSource.RazedMods,
            _ => MetaSource.None,
        };
}

/// <summary>ScriptHookVDotNet build names used in catalog JSON ("shvdn": ["nightly", "enhanced"]).</summary>
public static class ShvdnNames
{
    public const string Nightly = "nightly";
    public const string LegacyEnhanced = "enhanced";
}

/// <summary>Unconfirmed choices in catalog JSON ("unsure": ["edition", "shvdn"]).</summary>
public static class UnsureNames
{
    public const string Edition = "edition";
    public const string Shvdn = "shvdn";
}

public sealed class DiscoverCatalog
{
    public int Version { get; init; }
    public List<CatalogCategory> Categories { get; init; } = [];
    public List<CatalogEntry> Entries { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Built-in catalog (Discover/catalog.json). Adding a mod only takes a new JSON entry.</summary>
    public static DiscoverCatalog LoadBuiltIn()
    {
        using var s = typeof(DiscoverCatalog).Assembly.GetManifestResourceStream("GTAModManager.Discover.catalog.json")
            ?? throw new InvalidOperationException(L.T("core.discover.recommendationsCatalogWasNotFound"));
        var source = Parse(s);
        return new DiscoverCatalog
        {
            Version = source.Version,
            Categories = source
                .Categories.Select(category =>
                    category with
                    {
                        Title = L.T($"catalog.category.{category.Id}.title"),
                        Subtitle = string.IsNullOrEmpty(category.Subtitle)
                            ? category.Subtitle
                            : L.T($"catalog.category.{category.Id}.subtitle"),
                    }
                )
                .ToList(),
            Entries = source
                .Entries.Select(entry =>
                    entry with
                    {
                        Kind = string.IsNullOrEmpty(entry.Kind) ? entry.Kind : L.T($"catalog.entry.{entry.Id}.kind"),
                        Compat = entry.Compat is { Note: { Length: > 0 } } compatibility
                            ? compatibility with
                            {
                                Note = L.T($"catalog.entry.{entry.Id}.note"),
                            }
                            : entry.Compat,
                    }
                )
                .ToList(),
        };
    }

    public static DiscoverCatalog Parse(Stream json)
    {
        var raw = JsonSerializer.Deserialize<RawCatalog>(json, Json)
            ?? throw new InvalidDataException(L.T("core.discover.emptyRecommendationsCatalog"));
        var cats = raw.Categories ?? [];
        var entries = new List<CatalogEntry>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in raw.Entries ?? [])
        {
            // Skip broken entries: the catalog must not bring down the whole section.
            if (string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Title) || !ids.Add(e.Id))
                continue;
            if (!LinkSafety.IsSafeWebUrl(e.Url) || cats.All(c => c.Id != e.Category))
                continue;
            entries.Add(e);
        }
        return new DiscoverCatalog
        {
            Version = raw.Version,
            Categories = cats,
            Entries = entries,
        };
    }

    private sealed class RawCatalog
    {
        public int Version { get; set; }
        public List<CatalogCategory>? Categories { get; set; }
        public List<RawEntry>? Entries { get; set; }
    }

    // The JSON field is named "source"; the model has SourceName plus a computed enum.
    private sealed record RawEntry
    {
        public string Id { get; init; } = "";
        public string Category { get; init; } = "";
        public int Order { get; init; }
        public string? Kind { get; init; }
        public int KindOrder { get; init; }
        public string Source { get; init; } = "";
        public string Url { get; init; } = "";
        public string? Repo { get; init; }
        public string Title { get; init; } = "";
        public string? Author { get; init; }
        public CompatInfo? Compat { get; init; }
        public bool Hidden { get; init; }
        public string? Install { get; init; }
        public string? Cover { get; init; }
        public string? CoverUrl { get; init; }
        public List<string>? Shvdn { get; init; }
        public List<string>? Unsure { get; init; }
        public List<string>? Detect { get; init; }

        public static implicit operator CatalogEntry(RawEntry r) =>
            new()
            {
                Id = r.Id,
                Category = r.Category,
                Order = r.Order,
                Kind = r.Kind,
                KindOrder = r.KindOrder,
                SourceName = r.Source,
                Url = r.Url,
                Repo = r.Repo,
                Title = r.Title,
                Author = r.Author,
                Compat = r.Compat,
                Hidden = r.Hidden,
                InstallMode = r.Install,
                // Only a bundled file name: no paths or URLs.
                Cover =
                    r.Cover is { } c && System.Text.RegularExpressions.Regex.IsMatch(c, @"^[a-z0-9\-]+\.(png|jpg)$")
                        ? c
                        : null,
                // A picked cover must be an image of the GTA5-Mods host, like the covers read from pages.
                CoverUrl = Gta5ModsPageParser.CardImage(r.CoverUrl),
                ShvdnNightly = r.Shvdn?.Contains(ShvdnNames.Nightly, StringComparer.OrdinalIgnoreCase) == true,
                ShvdnLegacyEnhanced =
                    r.Shvdn?.Contains(ShvdnNames.LegacyEnhanced, StringComparer.OrdinalIgnoreCase) == true,
                EditionUnsure = r.Unsure?.Contains(UnsureNames.Edition, StringComparer.OrdinalIgnoreCase) == true,
                ShvdnUnsure = r.Unsure?.Contains(UnsureNames.Shvdn, StringComparer.OrdinalIgnoreCase) == true,
                // Marker files: only paths inside the game folder (no ".." or drive letters), Windows-style slashes.
                DetectRules = r
                    .Detect?.Select(d => d.Replace('/', '\\').Trim())
                    .Where(d => d.Length > 0 && !d.Contains("..") && !d.Contains(':') && !d.StartsWith('\\'))
                    .ToList()
                    is { Count: > 0 } detect
                    ? string.Join('\n', detect)
                    : null,
            };
    }
}
