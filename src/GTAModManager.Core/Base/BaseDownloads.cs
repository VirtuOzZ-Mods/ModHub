using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Base;

/// <summary>
/// Downloads base components from their official sources in the background:
/// Script Hook V from the dev-c.com page: the page itself is opened first and the archive link is taken from it,
/// then the file is requested the same way a visitor's click would (with the page URL as Referer), so the site
/// counts a regular download from its own page. ScriptHookVDotNet uses the official GitHub releases API
/// (file from the release link; GitHub keeps the download count). No mirrors or third-party copies.
/// </summary>
public static partial class BaseDownloads
{
    public static readonly Uri ScriptHookPage = new("https://www.dev-c.com/gtav/scripthookv/");
    private const long MaxArchive = 200L << 20;

    [GeneratedRegex(@"href=""(?<h>/files/ScriptHookV_[\d.]+_[\d.]+\.zip)""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ScriptHookLink();

    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var http = handler is null
            ? new HttpClient(
                new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false }
            )
            : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ModHub/1.0");
        return http;
    }

    /// <summary>Link to the current Script Hook V archive on the official page (without the SDK).</summary>
    public static Uri? FindScriptHookLink(string pageHtml, Uri page)
    {
        var m = ScriptHookLink().Match(pageHtml);
        return m.Success ? new Uri(page, m.Groups["h"].Value) : null;
    }

    public static async Task<string> DownloadScriptHookAsync(HttpClient http, string destDir, CancellationToken ct)
    {
        using var pageResp = await http.GetAsync(ScriptHookPage, ct).ConfigureAwait(false);
        if (pageResp.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(L.T("core.base.scriptHookVPageResponded", (int)pageResp.StatusCode));
        var html = await pageResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var link = FindScriptHookLink(html, ScriptHookPage)
            ?? throw new InvalidDataException(L.T("core.base.noArchiveLinkFoundScript"));
        if (!LinkSafety.HostIs(link, "dev-c.com"))
            throw new InvalidDataException(L.T("core.base.archiveLinkLeadsAnotherWebsite"));

        using var req = new HttpRequestMessage(HttpMethod.Get, link);
        req.Headers.Referrer = ScriptHookPage; // coming from the download page, like a visitor
        return await SaveAsync(http, req, destDir, Path.GetFileName(link.LocalPath), ct).ConfigureAwait(false);
    }

    /// <summary>Archive of the latest ScriptHookVDotNet release on GitHub (the only matching .zip in the release).</summary>
    public static async Task<string> DownloadShvdnAsync(
        HttpClient http,
        ShvdnVariant variant,
        string destDir,
        CancellationToken ct
    )
    {
        var repo = ModdingBase.ShvdnRepo(variant);
        using var api = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        api.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var apiResp = await http.SendAsync(api, ct).ConfigureAwait(false);
        if (apiResp.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(L.T("core.base.githubReturned", (int)apiResp.StatusCode));
        using var doc = JsonDocument.Parse(await apiResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var assets = doc
            .RootElement.GetProperty("assets")
            .EnumerateArray()
            .Select(a =>
                (
                    name: a.GetProperty("name").GetString() ?? "",
                    url: a.GetProperty("browser_download_url").GetString() ?? ""
                )
            )
            .Where(a =>
                a.name.StartsWith("ScriptHookVDotNet", StringComparison.OrdinalIgnoreCase)
                && a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        if (assets is not [var asset])
            throw new InvalidDataException(L.T("core.base.noUnambiguousScripthookvdotnetArchiveFound"));
        if (!LinkSafety.IsSafeWebUrl(asset.url, out var u) || u is null || !LinkSafety.HostIs(u, "github.com"))
            throw new InvalidDataException(L.T("core.base.invalidReleaseLink"));
        using var req = new HttpRequestMessage(HttpMethod.Get, u);
        return await SaveAsync(http, req, destDir, asset.name, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads following https-only redirects (GitHub serves files through its CDN) and verifies the result is a ZIP.</summary>
    private static async Task<string> SaveAsync(
        HttpClient http,
        HttpRequestMessage first,
        string destDir,
        string fileName,
        CancellationToken ct
    )
    {
        if (
            fileName.Length == 0
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidDataException(L.T("core.base.invalidArchiveName"));
        var req = first;
        HttpResponseMessage? resp = null;
        try
        {
            for (int hop = 0; ; hop++)
            {
                resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc && hop < 5)
                {
                    var next = loc.IsAbsoluteUri ? loc : new Uri(req.RequestUri!, loc);
                    if (!LinkSafety.IsSafeWebUrl(next.AbsoluteUri))
                        throw new InvalidDataException(L.T("core.base.redirectUnsafeAddress"));
                    // Without the page referrer dev-c redirects to the home page, meaning the file was not served.
                    if (next.AbsolutePath is "/" or "")
                        throw new InvalidDataException(L.T("core.base.websiteDidNotProvideFile"));
                    resp.Dispose();
                    if (req != first)
                        req.Dispose();
                    req = new HttpRequestMessage(HttpMethod.Get, next);
                    continue;
                }
                break;
            }
            if (resp.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException(L.T("core.base.downloadResponded", (int)resp.StatusCode));
            if (resp.Content.Headers.ContentLength > MaxArchive)
                throw new InvalidDataException(L.T("core.base.archiveTooLarge"));
            Directory.CreateDirectory(destDir);
            var path = Path.Combine(destDir, fileName);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(path))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > MaxArchive)
                        throw new InvalidDataException(L.T("core.base.archiveTooLarge"));
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            // ZIP signature check: an error page instead of an archive does not get any further.
            using (var fs = File.OpenRead(path))
                if (fs.Length < 4 || fs.ReadByte() != 'P' || fs.ReadByte() != 'K')
                {
                    fs.Close();
                    File.Delete(path);
                    throw new InvalidDataException(L.T("core.base.downloadedFileNotZipArchive"));
                }
            return path;
        }
        finally
        {
            resp?.Dispose();
            if (req != first)
                req.Dispose();
        }
    }
}
