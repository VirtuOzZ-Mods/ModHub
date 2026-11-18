using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Discover;

/// <summary>A mod page read for the card editor: metadata and gallery images (card-sized URLs, cover first).</summary>
public sealed record Gta5ModsPage(ModMetadata Data, IReadOnlyList<string> Images);

/// <summary>
/// Recommendation card metadata: loading from mod pages, data and cover cache.
/// The cache lives in the program folder (Data\Cache\Mod pages) and is created on first write.
/// Requests are sequential, with a pause between calls to the same site; no authentication, no protection bypass, no mod file downloads.
/// On any network error the old data is kept (offline mode).
/// </summary>
public sealed partial class ModMetadataProvider : IDisposable
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(12);
    public const long DefaultCacheLimit = 100L << 20;
    private const int MaxPageBytes = 4 << 20;
    private const int MaxImageBytes = 4 << 20;
    private static readonly TimeSpan PolitePause = TimeSpan.FromMilliseconds(1500);

    /// <summary>Bumped when the page parser learns new fields (2: author avatar, profile and more platforms).</summary>
    public const int CurrentParserVersion = 2;

    static readonly Lazy<ModMetadataProvider> SharedInstance = new(() => new ModMetadataProvider());

    /// <summary>
    /// The application-wide provider. Pages share it: each instance keeps the whole cache in memory and rewrites
    /// meta.json, so two instances would overwrite each other's entries.
    /// </summary>
    public static ModMetadataProvider Shared => SharedInstance.Value;

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<string, CachedMetadata> _cache;
    private readonly TimeSpan _pause;

    public string CacheDir { get; }
    private string MetaPath => Path.Combine(CacheDir, "meta.json");
    private string ImagesDir => Path.Combine(CacheDir, "img");

    public static string DefaultCacheDir => Path.Combine(AppPaths.Cache, "Mod pages");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ModMetadataProvider(
        string? cacheDir = null,
        HttpMessageHandler? handler = null,
        TimeSpan? politePause = null
    )
    {
        CacheDir = cacheDir ?? DefaultCacheDir;
        _pause = politePause ?? PolitePause;
        _http = handler is null
            ? new HttpClient(
                new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 3,
                }
            )
            : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ModHub/1.0 (GTA V mod manager)");
        _cache = LoadCache();
    }

    public void Dispose()
    {
        _http.Dispose();
        _refreshLock.Dispose();
    }

    // ---------- Reading the cache ----------

    public CachedMetadata? Get(string id)
    {
        lock (_sync)
            return _cache.GetValueOrDefault(id);
    }

    /// <summary>Full path to the cover if the file exists in the cache.</summary>
    public string? ImagePath(CachedMetadata? c)
    {
        if (c?.ImageFile is not { } f || !SafeFileName().IsMatch(f))
            return null;
        var p = Path.Combine(ImagesDir, f);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Full path to the author avatar if the file exists in the cache.</summary>
    public string? AvatarPath(CachedMetadata? c)
    {
        if (c?.AvatarFile is not { } f || !SafeFileName().IsMatch(f))
            return null;
        var p = Path.Combine(ImagesDir, f);
        return File.Exists(p) ? p : null;
    }

    public bool NeedsRefresh(CatalogEntry e, TimeSpan maxAge)
    {
        var c = Get(e.Id);
        if (c is null || c.Url != e.Url)
            return true;
        // The picked cover changed (the card was edited): fetch the new image.
        if (
            e.CoverUrl is { } cover
            && c.ImageFile is { } file
            && !file.StartsWith(FileNameFor(cover), StringComparison.Ordinal)
        )
            return true;
        if (
            e.Source == MetaSource.Gta5Mods
            && c.FetchedAt > DateTimeOffset.MinValue
            && c.ParserVersion < CurrentParserVersion
        )
            return true;
        var last = c.LastErrorAt is { } err && err > c.FetchedAt ? err : c.FetchedAt;
        return DateTimeOffset.UtcNow - last > maxAge;
    }

    public DateTimeOffset? LastSuccess(IEnumerable<CatalogEntry> entries)
    {
        lock (_sync)
        {
            var times = entries
                .Select(e => _cache.GetValueOrDefault(e.Id))
                .Where(c => c is not null && c.FetchedAt > DateTimeOffset.MinValue)
                .Select(c => c!.FetchedAt)
                .ToList();
            return times.Count == 0 ? null : times.Min();
        }
    }

    // ---------- Refresh ----------

    /// <summary>
    /// Refreshes entries (stale ones, or all when forced). Calls onUpdated after each entry so cards
    /// update as data arrives. Returns the number of errors.
    /// </summary>
    public async Task<int> RefreshAsync(
        IEnumerable<CatalogEntry> entries,
        bool force,
        Action<CachedMetadata>? onUpdated,
        CancellationToken ct
    )
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int errors = 0;
            string? lastHost = null;
            foreach (var e in entries.Where(e => !e.Hidden && e.Source != MetaSource.None))
            {
                ct.ThrowIfCancellationRequested();
                if (!force && !NeedsRefresh(e, DefaultMaxAge))
                    continue;
                var host = Uri.TryCreate(e.Url, UriKind.Absolute, out var hu) ? hu.Host : "";
                if (host == lastHost)
                    await Task.Delay(_pause, ct).ConfigureAwait(false);
                lastHost = host;

                var updated = await RefreshOneAsync(e, ct).ConfigureAwait(false);
                if (updated.LastError is not null && updated.LastErrorAt > updated.FetchedAt)
                    errors++;
                lock (_sync)
                    _cache[e.Id] = updated;
                Save();
                onUpdated?.Invoke(updated);
            }
            TrimImages(DefaultCacheLimit);
            return errors;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<CachedMetadata> RefreshOneAsync(CatalogEntry e, CancellationToken ct)
    {
        var old = Get(e.Id);
        try
        {
            ModMetadata? data = e.Source switch
            {
                MetaSource.RazedMods when LinkSafety.HostIs(new Uri(e.Url), "razedmods.com") => new ModMetadata
                {
                    Title = e.Title,
                    Author = e.Author,
                    Compat = e.Compat,
                },
                MetaSource.Gta5Mods => Gta5ModsPageParser.Parse(
                    await GetTextAsync(e.Url, "gta5-mods.com", ct).ConfigureAwait(false)
                ),
                MetaSource.DevC => DevCPageParser.Parse(
                    await GetTextAsync(e.Url, "dev-c.com", ct).ConfigureAwait(false)
                ),
                MetaSource.GitHub when e.Repo is { } repo && RepoName().IsMatch(repo) => GitHubReleaseParser.Parse(
                    await GetTextAsync($"https://api.github.com/repos/{repo}/releases/latest", "api.github.com", ct)
                        .ConfigureAwait(false),
                    repo
                ),
                _ => null,
            };
            if (data is null)
                throw new InvalidDataException(L.T("core.discover.pageNotRecognizedSiteMay"));

            string? imageFile = (e.CoverUrl ?? data.ImageUrl) is { } imgUrl
                ? await CachedImageAsync(imgUrl, old?.ImageFile, ct).ConfigureAwait(false)
                : old?.ImageFile;
            string? avatarFile = data.AuthorAvatarUrl is { } avatarUrl
                ? await CachedImageAsync(avatarUrl, old?.AvatarFile, ct).ConfigureAwait(false)
                : old?.AvatarFile;
            return new CachedMetadata
            {
                Id = e.Id,
                Url = e.Url,
                Data = data,
                FetchedAt = DateTimeOffset.UtcNow,
                ImageFile = imageFile,
                AvatarFile = avatarFile,
                ParserVersion = CurrentParserVersion,
            };
        }
        catch (Exception ex)
            when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException
                && !ct.IsCancellationRequested
            )
        {
            var msg = ex is TaskCanceledException ? L.T("core.discover.responseTimedOut") : ex.Message;
            return old is null
                ? new CachedMetadata
                {
                    Id = e.Id,
                    Url = e.Url,
                    LastError = msg,
                    LastErrorAt = DateTimeOffset.UtcNow,
                }
                : old with
                {
                    LastError = msg,
                    LastErrorAt = DateTimeOffset.UtcNow,
                };
        }
    }

    // ---------- Custom cards (editor) ----------

    /// <summary>
    /// Loads a GTA5-Mods mod page for the card editor: metadata plus the gallery to pick a cover from.
    /// Throws on network errors and when the page is not a mod page.
    /// </summary>
    public async Task<Gta5ModsPage> LoadPageAsync(string url, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var html = await GetTextAsync(url, "gta5-mods.com", ct).ConfigureAwait(false);
            var data = Gta5ModsPageParser.Parse(html)
                ?? throw new InvalidDataException(L.T("core.discover.pageNotRecognizedSiteMay"));
            return new Gta5ModsPage(data, Gta5ModsPageParser.ReadGallery(html));
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>An image to preview in the editor (kept in memory only); null if it is not an image of the site.</summary>
    public async Task<byte[]?> DownloadPreviewAsync(string url, CancellationToken ct)
    {
        if (!LinkSafety.IsSafeWebUrl(url, out var u) || u is null || !LinkSafety.HostIs(u, "gta5-mods.com"))
            return null;
        using var resp = await _http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
            return null;
        var bytes = await ReadLimitedAsync(resp, MaxImageBytes, ct).ConfigureAwait(false);
        return ImageExtension(bytes) is null ? null : bytes;
    }

    /// <summary>
    /// Stores the data the editor has just read for a custom card, downloading the picked cover and the author
    /// avatar, so the card is complete at once without loading the page again. Older cover files are trimmed.
    /// </summary>
    public async Task<CachedMetadata> RememberAsync(CatalogEntry e, ModMetadata data, CancellationToken ct)
    {
        var old = Get(e.Id);
        string? imageFile = (e.CoverUrl ?? data.ImageUrl) is { } imgUrl
            ? await CachedImageAsync(imgUrl, null, ct).ConfigureAwait(false)
            : null;
        string? avatarFile = data.AuthorAvatarUrl is { } avatarUrl
            ? await CachedImageAsync(avatarUrl, old?.AvatarFile, ct).ConfigureAwait(false)
            : old?.AvatarFile;
        var entry = new CachedMetadata
        {
            Id = e.Id,
            Url = e.Url,
            Data = data,
            FetchedAt = DateTimeOffset.UtcNow,
            ImageFile = imageFile,
            AvatarFile = avatarFile,
            ParserVersion = CurrentParserVersion,
        };
        lock (_sync)
            _cache[e.Id] = entry;
        Save();
        TrimImages(DefaultCacheLimit);
        return entry;
    }

    /// <summary>Drops a card's cached data and its now unused images (a custom card was deleted).</summary>
    public void Forget(string id)
    {
        bool removed;
        lock (_sync)
            removed = _cache.Remove(id);
        if (!removed)
            return;
        Save();
        TrimImages(DefaultCacheLimit);
    }

    static readonly string[] SearchCategories = ["scripts", "tools", "misc", "player", "vehicles", "weapons", "maps"];

    /// <summary>
    /// Looks the mod up in GTA5-Mods search, category by category, and returns the page whose title matches the
    /// name exactly (see ModTitles). null means nothing matched; network errors are thrown so they are not
    /// remembered as "not found".
    /// </summary>
    public async Task<string?> FindGta5ModsPageAsync(string name, CancellationToken ct)
    {
        string query = ModTitles.Query(name);
        if (ModTitles.Key(query).Length < 3)
            return null;
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int i = 0; i < SearchCategories.Length; i++)
            {
                if (i > 0)
                    await Task.Delay(_pause, ct).ConfigureAwait(false);
                var html = await GetTextAsync(
                        $"https://www.gta5-mods.com/search/{SearchCategories[i]}/{Uri.EscapeDataString(query)}",
                        "gta5-mods.com",
                        ct
                    )
                    .ConfigureAwait(false);
                if (Gta5ModsSearchParser.Pick(Gta5ModsSearchParser.Parse(html), name) is { } url)
                    return url;
            }
            return null;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Returns the cached file name for an image URL, downloading it once; keeps the old file on failure.</summary>
    private async Task<string?> CachedImageAsync(string url, string? previous, CancellationToken ct)
    {
        var name = FileNameFor(url);
        var existing = new[] { ".jpg", ".png", ".webp", ".gif" }
            .Select(x => name + x)
            .FirstOrDefault(x => File.Exists(Path.Combine(ImagesDir, x)));
        if (existing is not null)
            return existing;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
            return await DownloadImageAsync(url, name, ct).ConfigureAwait(false) ?? previous;
        }
        catch (Exception ex)
            when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidDataException
                && !ct.IsCancellationRequested
            )
        {
            return previous;
        }
    }

    private async Task<string> GetTextAsync(string url, string expectedHost, CancellationToken ct)
    {
        if (!LinkSafety.IsSafeWebUrl(url, out var u) || u is null || !LinkSafety.HostIs(u, expectedHost))
            throw new InvalidDataException(L.T("core.discover.invalidSourceAddress"));
        using var resp = await _http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(L.T("core.discover.websiteResponded", (int)resp.StatusCode));
        // After a redirect we must stay on the same site.
        if (resp.RequestMessage?.RequestUri is { } final && !LinkSafety.HostIs(final, expectedHost))
            throw new InvalidDataException(L.T("core.discover.redirectAnotherWebsite"));
        var bytes = await ReadLimitedAsync(resp, MaxPageBytes, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<string?> DownloadImageAsync(string url, string name, CancellationToken ct)
    {
        if (!LinkSafety.IsSafeWebUrl(url, out var u) || u is null || !LinkSafety.HostIs(u, "gta5-mods.com"))
            return null;
        using var resp = await _http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
            return null;
        if (
            resp.Content.Headers.ContentType?.MediaType is { } mt
            && !mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        )
            return null;
        var bytes = await ReadLimitedAsync(resp, MaxImageBytes, ct).ConfigureAwait(false);
        var ext = ImageExtension(bytes);
        if (ext is null)
            return null; // not an image: do not save
        Directory.CreateDirectory(ImagesDir);
        var file = name + ext;
        var tmp = Path.Combine(ImagesDir, file + ".tmp");
        await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
        File.Move(tmp, Path.Combine(ImagesDir, file), overwrite: true);
        return file;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage resp, int limit, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength > limit)
            throw new InvalidDataException(L.T("core.discover.responseTooLarge"));
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > limit)
                throw new InvalidDataException(L.T("core.discover.responseTooLarge"));
        }
        return ms.ToArray();
    }

    /// <summary>Image type by signature (not by server headers). null if not an image.</summary>
    public static string? ImageExtension(ReadOnlySpan<byte> b)
    {
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return ".jpg";
        if (b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G')
            return ".png";
        if (
            b.Length > 12
            && b[0] == (byte)'R'
            && b[1] == (byte)'I'
            && b[2] == (byte)'F'
            && b[3] == (byte)'F'
            && b[8] == (byte)'W'
            && b[9] == (byte)'E'
            && b[10] == (byte)'B'
            && b[11] == (byte)'P'
        )
            return ".webp";
        if (b.Length > 6 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
            return ".gif";
        return null;
    }

    private static string FileNameFor(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..20].ToLowerInvariant();

    [GeneratedRegex(@"^[a-f0-9]{20}\.(jpg|png|webp|gif)$")]
    private static partial Regex SafeFileName();

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoName();

    // ---------- Storage ----------

    private Dictionary<string, CachedMetadata> LoadCache()
    {
        try
        {
            if (!File.Exists(MetaPath))
                return new(StringComparer.OrdinalIgnoreCase);
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(MetaPath), Json);
            if (file is not { Version: CacheFile.CurrentVersion })
                return new(StringComparer.OrdinalIgnoreCase);
            return file
                .Items.Where(i => !string.IsNullOrEmpty(i.Id))
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
            when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            List<CachedMetadata> items;
            lock (_sync)
                items = [.. _cache.Values];
            var tmp = MetaPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheFile { Items = items }, Json));
            File.Move(tmp, MetaPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class CacheFile
    {
        public const int CurrentVersion = 1;
        public int Version { get; set; } = CurrentVersion;
        public List<CachedMetadata> Items { get; set; } = [];
    }

    // ---------- Size and cleanup ----------

    public long CacheSize()
    {
        try
        {
            return Directory.Exists(CacheDir)
                ? new DirectoryInfo(CacheDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Deletes unreferenced covers, then the oldest ones, until the cache fits the limit.</summary>
    public void TrimImages(long limit)
    {
        try
        {
            if (!Directory.Exists(ImagesDir))
                return;
            HashSet<string> used;
            lock (_sync)
                used = _cache
                    .Values.SelectMany(c => new[] { c.ImageFile, c.AvatarFile })
                    .OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = new DirectoryInfo(ImagesDir).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
            foreach (var f in files.Where(f => !used.Contains(f.Name)).ToList())
            {
                f.Delete();
                files.Remove(f);
            }
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= limit)
                    break;
                total -= f.Length;
                f.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Clears the whole recommendations cache (from Settings).</summary>
    public void Clear()
    {
        lock (_sync)
            _cache = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(CacheDir))
                Directory.Delete(CacheDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
