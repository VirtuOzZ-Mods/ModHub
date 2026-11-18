using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GTAModManager.Core.Game;

namespace GTAModManager.Core.Discover;

/// <summary>Which Recommendations tab a custom card belongs to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CustomCardCollection>))]
public enum CustomCardCollection
{
    /// <summary>The user's own GTA5-Mods cards.</summary>
    User,

    /// <summary>Drafts of the built-in "Recommended by VirtuOzZ" tab, exported into catalog.json later.</summary>
    Recommended,
}

/// <summary>
/// A mod card the user built from a GTA5-Mods page: the page link, the picked cover and the game editions and
/// ScriptHookVDotNet builds the mod is for. Rating, author links and the avatar come from the metadata cache.
/// </summary>
public sealed record CustomCard
{
    public CustomCardCollection Collection { get; init; }
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Author { get; init; }

    /// <summary>The cover picked in the editor (a card-sized GTA5-Mods image URL).</summary>
    public string? CoverUrl { get; init; }
    public bool Legacy { get; init; }
    public bool Enhanced { get; init; }

    /// <summary>"Not sure" about the game version: installing is allowed on both editions, with a warning.</summary>
    public bool EditionUnsure { get; init; }

    /// <summary>ScriptHookVDotNet builds needed on Legacy; meaningful only when Legacy (or "not sure") is checked.</summary>
    public bool ShvdnNightly { get; init; }
    public bool ShvdnLegacyEnhanced { get; init; }

    /// <summary>"Not sure" which ScriptHookVDotNet build the mod needs on Legacy.</summary>
    public bool ShvdnUnsure { get; init; }

    /// <summary>The ScriptHookVDotNet choice applies: the mod may run on Legacy.</summary>
    [JsonIgnore]
    public bool ShvdnApplies => Legacy || EditionUnsure;

    /// <summary>
    /// The choices made consistent: "not sure" replaces the specific options of its group, and the
    /// ScriptHookVDotNet builds are dropped when the mod is for Enhanced only.
    /// </summary>
    public CustomCard Consistent()
    {
        var edition = EditionUnsure ? this with { Legacy = false, Enhanced = false } : this;
        bool shvdn = edition.ShvdnApplies;
        bool shvdnUnsure = shvdn && ShvdnUnsure;
        return edition with
        {
            ShvdnUnsure = shvdnUnsure,
            ShvdnNightly = shvdn && !shvdnUnsure && ShvdnNightly,
            ShvdnLegacyEnhanced = shvdn && !shvdnUnsure && ShvdnLegacyEnhanced,
        };
    }

    public int Order { get; init; }
    public DateTimeOffset AddedUtc { get; init; }

    /// <summary>The mod page slug ("menyoo-pc-sp"): the catalog id of an exported card.</summary>
    [JsonIgnore]
    public string Slug => CustomCards.Slug(Url) ?? "";

    /// <summary>Cache and card id, kept apart from built-in catalog ids.</summary>
    [JsonIgnore]
    public string Id =>
        (Collection == CustomCardCollection.User ? "user:" : "draft:")
        + (Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.AbsolutePath.Trim('/') : Url);

    [JsonIgnore]
    public string Category =>
        Collection == CustomCardCollection.User ? CustomCards.UserCategory : CustomCards.RecommendedCategory;

    /// <summary>Checked editions; an edition left unchecked is reported as unsupported, none checked as unknown.</summary>
    [JsonIgnore]
    public CompatInfo? Compat =>
        !EditionUnsure && (Legacy || Enhanced) ? new CompatInfo { Legacy = Legacy, Enhanced = Enhanced } : null;

    /// <summary>The card as a catalog entry (shown, refreshed and installed like the built-in ones).</summary>
    public CatalogEntry ToCatalogEntry()
    {
        var c = Consistent();
        return new()
        {
            Id = Id,
            Category = Category,
            Order = Order,
            SourceName = "gta5mods",
            Url = Url,
            Title = Title,
            Author = Author,
            Compat = c.Compat,
            CoverUrl = CoverUrl,
            EditionUnsure = c.EditionUnsure,
            ShvdnNightly = c.ShvdnNightly,
            ShvdnLegacyEnhanced = c.ShvdnLegacyEnhanced,
            ShvdnUnsure = c.ShvdnUnsure,
        };
    }
}

/// <summary>
/// Custom cards storage in the app data folder (custom-cards.json, never in the game folder).
/// The file is untrusted input: links are validated on load, broken entries are skipped.
/// </summary>
public sealed partial class CustomCards(string? path = null)
{
    /// <summary>
    /// The editor of the "Recommended by VirtuOzZ" tab. Only the author edits that tab, in the built-in catalog
    /// (Discover/catalog.json): user builds show it read-only, developer (Debug) builds keep the draft editor.
    /// </summary>
    public static bool RecommendedEditorEnabled { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Recommendations tab of the user's cards (not part of the built-in catalog).</summary>
    public const string UserCategory = "custom";
    public const string RecommendedCategory = "recommended";

    private const int MaxCards = 500;
    private const int MaxTitle = 160;

    private readonly string _path = path ?? AppPaths.LibraryFile("custom-cards.json");
    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [GeneratedRegex(@"^/[a-z0-9\-]{1,40}/(?<s>[a-z0-9][a-z0-9_\-]{0,120})/?$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ModPath();

    /// <summary>A site language code: "ru", "uk", "pt-br" (never a category, those are longer words).</summary>
    [GeneratedRegex(@"^[a-z]{2}(?:-[a-z]{2,4})?$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex LanguageCode();

    /// <summary>
    /// Canonical GTA5-Mods mod page ("https://www.gta5-mods.com/scripts/menyoo-pc-sp") from a pasted link:
    /// query, fragment, a trailing "/download/N" and the site language ("/ru/scripts/…", "ru.gta5-mods.com")
    /// are dropped. null if it is not a mod page.
    /// </summary>
    public static string? Normalize(string? url)
    {
        var text = url?.Trim() ?? "";
        if (
            text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("gta5-mods.com", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(text, @"^[a-z]{2}(?:-[a-z]{2,4})?\.gta5-mods\.com", RegexOptions.IgnoreCase)
        )
            text = "https://" + text;
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text["http://".Length..];
        if (!AuthorThanks.IsGta5Mods(text) || !LinkSafety.IsSafeWebUrl(text, out var u) || u is null)
            return null;
        // The site language is either a subdomain (redirects to the path form) or the first path segment.
        var sub = u.Host.EndsWith(".gta5-mods.com", StringComparison.OrdinalIgnoreCase)
            ? u.Host[..^".gta5-mods.com".Length]
            : null;
        if (
            !u.Host.Equals("gta5-mods.com", StringComparison.OrdinalIgnoreCase)
            && !"www".Equals(sub, StringComparison.OrdinalIgnoreCase)
            && !(sub is not null && LanguageCode().IsMatch(sub))
        )
            return null;
        var segments = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0 && LanguageCode().IsMatch(segments[0]))
            segments.RemoveAt(0);
        var page = AuthorThanks.ModPage("https://www.gta5-mods.com/" + string.Join('/', segments));
        if (!Uri.TryCreate(page, UriKind.Absolute, out var p))
            return null;
        var m = ModPath().Match(p.AbsolutePath);
        if (!m.Success || p.AbsolutePath.StartsWith("/users/", StringComparison.OrdinalIgnoreCase))
            return null;
        return "https://www.gta5-mods.com" + p.AbsolutePath.TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>The page slug of a mod link, or null.</summary>
    public static string? Slug(string? url) =>
        Normalize(url) is { } page && ModPath().Match(new Uri(page).AbsolutePath) is { Success: true } m
            ? m.Groups["s"].Value
            : null;

    public List<CustomCard> Load()
    {
        lock (Gate)
            return LoadUnlocked().Cards;
    }

    /// <summary>The order the user set for a tab (card ids, built-in and custom); empty if never reordered.</summary>
    public List<string> LoadOrder(string category)
    {
        lock (Gate)
            return LoadUnlocked().Orders.GetValueOrDefault(category) ?? [];
    }

    /// <summary>Built-in catalog cards removed from a tab by hand (ids); empty if none.</summary>
    public HashSet<string> LoadHidden(string category)
    {
        lock (Gate)
            return [.. LoadUnlocked().Hidden.GetValueOrDefault(category) ?? []];
    }

    /// <summary>Removes a built-in card from a tab (the catalog keeps it, the tab no longer shows it). Throws on write errors.</summary>
    public void Hide(string category, string id)
    {
        if (id.Length is 0 or > 300)
            return;
        lock (Gate)
        {
            var store = LoadUnlocked();
            if (!store.Hidden.TryGetValue(category, out var list))
                store.Hidden[category] = list = [];
            if (!list.Contains(id))
                list.Add(id);
            store.Orders.GetValueOrDefault(category)?.Remove(id);
            Write(store);
        }
    }

    /// <summary>Brings back every built-in card removed from a tab; the number restored. Throws on write errors.</summary>
    public int RestoreHidden(string category)
    {
        lock (Gate)
        {
            var store = LoadUnlocked();
            if (!store.Hidden.Remove(category, out var list) || list.Count == 0)
                return 0;
            Write(store);
            return list.Count;
        }
    }

    /// <summary>Remembers the order of a tab's cards (ids as shown, first to last). Throws on write errors.</summary>
    public void SaveOrder(string category, IEnumerable<string> ids)
    {
        lock (Gate)
        {
            var store = LoadUnlocked();
            store.Orders[category] = ids.Where(id => id.Length is > 0 and <= 300)
                .Distinct()
                .Take(MaxCards * 2)
                .ToList();
            Write(store);
        }
    }

    /// <summary>Moves a card one place earlier (-1) or later (+1) in an ordered list of ids; false at the edge.</summary>
    public static bool Move(List<string> ids, string id, int step)
    {
        int i = ids.IndexOf(id),
            j = i + step;
        if (i < 0 || j < 0 || j >= ids.Count)
            return false;
        (ids[i], ids[j]) = (ids[j], ids[i]);
        return true;
    }

    public List<CustomCard> Load(CustomCardCollection collection) =>
        Load().Where(c => c.Collection == collection).OrderBy(c => c.Order).ToList();

    /// <summary>Adds a card or replaces the one with the same page in the same tab; new cards go last. Throws on write errors.</summary>
    public CustomCard Save(CustomCard card)
    {
        var url = Normalize(card.Url) ?? throw new ArgumentException("Not a GTA5-Mods mod page.", nameof(card));
        lock (Gate)
        {
            var store = LoadUnlocked();
            var all = store.Cards;
            var existing = all.FindIndex(c => c.Collection == card.Collection && c.Url == url);
            var saved = card.Consistent() with
            {
                Url = url,
                Title = Trim(card.Title) ?? CustomCards.Slug(url)!,
                Author = Trim(card.Author),
                CoverUrl = Gta5ModsPageParser.CardImage(card.CoverUrl),
            };
            if (existing >= 0)
                all[existing] = saved with { Order = all[existing].Order, AddedUtc = all[existing].AddedUtc };
            else
                all.Add(
                    saved with
                    {
                        Order =
                            all.Where(c => c.Collection == card.Collection).Select(c => c.Order).DefaultIfEmpty(0).Max()
                            + 1,
                        AddedUtc = card.AddedUtc == default ? DateTimeOffset.UtcNow : card.AddedUtc,
                    }
                );
            Write(store);
            return existing >= 0 ? all[existing] : all[^1];
        }
    }

    /// <summary>Removes a card by its id; true if it existed.</summary>
    public bool Remove(string id)
    {
        lock (Gate)
        {
            var store = LoadUnlocked();
            if (store.Cards.RemoveAll(c => c.Id == id) == 0)
                return false;
            foreach (var list in store.Orders.Values)
                list.Remove(id);
            Write(store);
            return true;
        }
    }

    /// <summary>
    /// Recommended drafts for the built-in catalog: "entries" are catalog.json entries ready to paste (covers,
    /// editions, ScriptHookVDotNet builds and "not sure" kept; ids are the page slugs), "order" gives the new
    /// order of the tab's built-in cards, "hidden" the built-in cards removed from the tab (to be marked
    /// "hidden": true in the catalog). Positions follow tabOrder (the ids as the tab shows them); cards
    /// missing from it go last.
    /// </summary>
    public static string ExportCatalogEntries(
        IEnumerable<CustomCard> drafts,
        IReadOnlyList<string> tabOrder,
        IEnumerable<string>? hidden = null
    )
    {
        var list = drafts.Select(c => c.Consistent()).ToList();
        var ids = tabOrder.ToList();
        ids.AddRange(list.OrderBy(c => c.Order).Select(c => c.Id).Where(id => !ids.Contains(id)));
        var entries = new JsonArray();
        var builtIn = new JsonObject();
        for (int i = 0; i < ids.Count; i++)
        {
            int order = i + 1;
            if (list.FirstOrDefault(d => d.Id == ids[i]) is not { } c)
            {
                if (!ids[i].Contains(':'))
                    builtIn[ids[i]] = order;
                continue;
            }
            var entry = new JsonObject
            {
                ["id"] = c.Slug,
                ["category"] = RecommendedCategory,
                ["order"] = order,
                ["source"] = "gta5mods",
                ["url"] = c.Url,
                ["title"] = c.Title,
            };
            if (c.Author is { } author)
                entry["author"] = author;
            if (c.CoverUrl is { } cover)
                entry["coverUrl"] = cover;
            if (c.Compat is { } compat)
                entry["compat"] = new JsonObject { ["legacy"] = compat.Legacy, ["enhanced"] = compat.Enhanced };
            var shvdn = new JsonArray();
            if (c.ShvdnNightly)
                shvdn.Add(ShvdnNames.Nightly);
            if (c.ShvdnLegacyEnhanced)
                shvdn.Add(ShvdnNames.LegacyEnhanced);
            if (shvdn.Count > 0)
                entry["shvdn"] = shvdn;
            var unsure = new JsonArray();
            if (c.EditionUnsure)
                unsure.Add(UnsureNames.Edition);
            if (c.ShvdnUnsure)
                unsure.Add(UnsureNames.Shvdn);
            if (unsure.Count > 0)
                entry["unsure"] = unsure;
            entries.Add(entry);
        }
        var result = new JsonObject { ["entries"] = entries, ["order"] = builtIn };
        var removed = new JsonArray();
        foreach (var id in (hidden ?? []).Where(id => !id.Contains(':')).Order(StringComparer.Ordinal))
            removed.Add(id);
        if (removed.Count > 0)
            result["hidden"] = removed;
        return result.ToJsonString(
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }
        );
    }

    private sealed record Store(
        List<CustomCard> Cards,
        Dictionary<string, List<string>> Orders,
        Dictionary<string, List<string>> Hidden
    )
    {
        public static Store Empty() => new([], [], []);
    }

    private static Dictionary<string, List<string>> IdLists(Dictionary<string, List<string?>?>? lists) =>
        (lists ?? [])
            .Where(o => o.Key is UserCategory or RecommendedCategory && o.Value is not null)
            .ToDictionary(
                o => o.Key,
                o => o.Value!.OfType<string>().Where(id => id.Length is > 0 and <= 300).Distinct().ToList()
            );

    private Store LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_path))
                return Store.Empty();
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), Json);
            if (file is null || file.Version != StoreFile.CurrentVersion)
                return Store.Empty();
            var result = new List<CustomCard>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in file.Cards ?? [])
            {
                // A hand-edited or damaged file must not break the tab: skip what does not validate.
                if (c is null || Normalize(c.Url) is not { } url || Trim(c.Title) is not { } title)
                    continue;
                if (!Enum.IsDefined(c.Collection))
                    continue;
                var card = c.Consistent() with
                {
                    Url = url,
                    Title = title,
                    Author = Trim(c.Author),
                    CoverUrl = Gta5ModsPageParser.CardImage(c.CoverUrl),
                };
                if (seen.Add(card.Id) && result.Count < MaxCards)
                    result.Add(card);
            }
            return new Store(result, IdLists(file.Orders), IdLists(file.Hidden));
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Store.Empty();
        }
    }

    private void Write(Store store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        var file = new StoreFile
        {
            Cards = store.Cards.Take(MaxCards).ToList<CustomCard?>(),
            Orders = Lists(store.Orders),
            Hidden = Lists(store.Hidden) is { Count: > 0 } hidden ? hidden : null,
        };
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static Dictionary<string, List<string?>?> Lists(Dictionary<string, List<string>> lists) =>
        lists.Where(o => o.Value.Count > 0).ToDictionary(o => o.Key, o => (List<string?>?)o.Value.ToList<string?>());

    private static string? Trim(string? s) =>
        Gta5ModsPageParser.Clean(s) is { } t ? (t.Length > MaxTitle ? t[..MaxTitle] : t) : null;

    private sealed class StoreFile
    {
        public const int CurrentVersion = 1;
        public int Version { get; set; } = CurrentVersion;
        public List<CustomCard?>? Cards { get; set; }

        /// <summary>Card order per tab set with the arrows (category id → card ids, first to last).</summary>
        public Dictionary<string, List<string?>?>? Orders { get; set; }

        /// <summary>Built-in cards removed from a tab by hand (category id → card ids).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, List<string?>?>? Hidden { get; set; }
    }
}
