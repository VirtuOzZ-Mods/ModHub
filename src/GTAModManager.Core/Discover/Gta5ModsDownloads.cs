using System.Net;
using System.Text.RegularExpressions;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Discover;

/// <summary>
/// Downloads a mod from GTA5-Mods exactly as a site visitor would: mod page → Download button (/download/N) →
/// file link on files.gta5-mods.com. Each step sends its own page as Referer, so the site counts
/// a regular download for the author. Files are taken only from files.gta5-mods.com, over https, and only mod archives.
/// </summary>
public static partial class Gta5ModsDownloads
{
    private const long MaxFile = 2L << 30;
    private static readonly string[] Extensions = [".oiv", ".zip", ".rar", ".7z"];

    [GeneratedRegex(@"href=""(?<h>/[a-z0-9\-]+/[a-z0-9\-_.%]+/download/\d+)""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex DownloadPageLink();

    [GeneratedRegex(
        @"<a[^>]*\bbtn-download\b[^>]*\bhref=""(?<h>https://files\.gta5-mods\.com/[^""]+)""|<a[^>]*\bhref=""(?<h>https://files\.gta5-mods\.com/[^""]+)""[^>]*\bbtn-download\b",
        RegexOptions.IgnoreCase,
        1000
    )]
    private static partial Regex FileButton();

    [GeneratedRegex(@"https://files\.gta5-mods\.com/uploads/[^""'\s<>]+", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AnyFileLink();

    /// <summary>The first Download button on the mod page (main version), only for the same mod.</summary>
    public static Uri? FindDownloadPage(string html, Uri page)
    {
        var prefix = page.AbsolutePath.TrimEnd('/') + "/download/";
        foreach (Match m in DownloadPageLink().Matches(html))
            if (m.Groups["h"].Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return new Uri(page, m.Groups["h"].Value);
        return null;
    }

    /// <summary>File link from the download page.</summary>
    public static Uri? FindFile(string html)
    {
        var m = FileButton().Match(html);
        var raw =
            m.Success ? m.Groups["h"].Value
            : AnyFileLink().Match(html) is { Success: true } any ? any.Value
            : null;
        if (raw is null)
            return null;
        raw = WebUtility.HtmlDecode(raw);
        return Uri.TryCreate(raw, UriKind.Absolute, out var u) && IsFileHost(u) && IsModFile(FileName(u)) ? u : null;
    }

    public static bool IsFileHost(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("files.gta5-mods.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsModFile(string name) =>
        name.Length > 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>File name without the site's prefix ("0cf657-TrainerV.rar" → "TrainerV.rar").</summary>
    public static string FileName(Uri u)
    {
        var name = Uri.UnescapeDataString(Path.GetFileName(u.AbsolutePath));
        var m = Regex.Match(name, @"^[0-9a-f]{6}-(.+)$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : name;
    }

    [GeneratedRegex(
        @"fa-file""></i>(?:&nbsp;|\s)*(?<v>[^<]{1,120}?)\s*<span>\(current\)</span>",
        RegexOptions.IgnoreCase,
        1000
    )]
    private static partial Regex CurrentVersion();

    /// <summary>Name of the current file version ("1.9.5 [Legacy ONLY]") from the All Versions block.</summary>
    public static string? FindCurrentVersion(string html) =>
        CurrentVersion().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups["v"].Value).Trim() : null;

    /// <summary>The edition the author restricted the file to in the version name: "Legacy", "Enhanced" or null.</summary>
    public static string? EditionOnly(string? version)
    {
        if (string.IsNullOrEmpty(version))
            return null;
        bool legacy = Regex.IsMatch(
            version,
            @"legacy[\s_\-]*only|only[\s_\-]*(for[\s_\-]*)?legacy",
            RegexOptions.IgnoreCase
        );
        bool enhanced = Regex.IsMatch(
            version,
            @"enhanced[\s_\-]*only|only[\s_\-]*(for[\s_\-]*)?enhanced",
            RegexOptions.IgnoreCase
        );
        return legacy == enhanced ? null
            : legacy ? "Legacy"
            : "Enhanced";
    }

    [GeneratedRegex(
        @"fa-file""></i>(?:&nbsp;|\s)*(?<v>[^<]{1,120})(?:<span>\(current\)</span>)?(?:(?!fa-file).){0,4000}?href=""(?<h>/[a-z0-9\-]+/[a-z0-9\-_.%]+/download/\d+)""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        1000
    )]
    private static partial Regex VersionBlock();

    /// <summary>All files from the All Versions block: version name and its download page (same mod only).</summary>
    public static IReadOnlyList<(string Version, Uri DownloadPage)> FindVersions(string html, Uri page)
    {
        var prefix = page.AbsolutePath.TrimEnd('/') + "/download/";
        var result = new List<(string, Uri)>();
        foreach (Match m in VersionBlock().Matches(html))
        {
            if (!m.Groups["h"].Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var uri = new Uri(page, m.Groups["h"].Value);
            var version = WebUtility.HtmlDecode(m.Groups["v"].Value).Trim();
            if (!result.Any(r => r.Item2 == uri))
                result.Add((version, uri));
        }
        return result;
    }

    /// <summary>The mod's main file: download page (Download button) and current version name.</summary>
    public sealed record ModFile(Uri Page, Uri DownloadPage, string? Version);

    public static async Task<ModFile> ResolveAsync(HttpClient http, Uri page, CancellationToken ct)
    {
        if (
            !page.Host.EndsWith("gta5-mods.com", StringComparison.OrdinalIgnoreCase)
            || page.Scheme != Uri.UriSchemeHttps
        )
            throw new InvalidDataException(L.T("core.discover.notGta5ModsPage"));
        var pageHtml = await GetTextAsync(http, page, null, ct).ConfigureAwait(false);
        var downloadPage = FindDownloadPage(pageHtml, page)
            ?? throw new InvalidDataException(L.T("core.discover.noDownloadButtonFoundMod"));
        return new ModFile(page, downloadPage, FindCurrentVersion(pageHtml));
    }

    /// <summary>Downloads the mod file into <paramref name="destDir"/>; returns the archive path.</summary>
    public static async Task<string> DownloadAsync(
        HttpClient http,
        ModFile mod,
        string destDir,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        var downloadPage = mod.DownloadPage;
        var downloadHtml = await GetTextAsync(http, downloadPage, mod.Page, ct).ConfigureAwait(false);
        var file = FindFile(downloadHtml)
            ?? throw new InvalidDataException(L.T("core.discover.websiteDidNotProvideMod"));

        using var req = new HttpRequestMessage(HttpMethod.Get, file);
        req.Headers.Referrer = downloadPage;
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(L.T("core.discover.fileServerReturned", (int)resp.StatusCode));
        var length = resp.Content.Headers.ContentLength;
        if (length > MaxFile)
            throw new InvalidDataException(L.T("core.discover.modFileTooLargeOver"));
        Directory.CreateDirectory(destDir);
        var path = Path.Combine(destDir, FileName(file));
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(path))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > MaxFile)
                        throw new InvalidDataException(L.T("core.discover.modFileTooLargeOver"));
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    if (length > 0)
                        progress?.Report((double)total / length.Value);
                }
            }
            if (!LooksLikeArchive(path))
                throw new InvalidDataException(L.T("core.discover.downloadedFileDoesNotAppear"));
            return path;
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException) { }
            throw;
        }
    }

    /// <summary>ZIP/OIV (PK), RAR and 7z signatures: an error page instead of a file does not get any further.</summary>
    public static bool LooksLikeArchive(string path)
    {
        Span<byte> h = stackalloc byte[6];
        using var fs = File.OpenRead(path);
        if (fs.Read(h) < 4)
            return false;
        return (h[0] == 'P' && h[1] == 'K')
            || (h[0] == 'R' && h[1] == 'a' && h[2] == 'r' && h[3] == '!')
            || (h[0] == '7' && h[1] == 'z' && h[2] == 0xBC && h[3] == 0xAF);
    }

    private static async Task<string> GetTextAsync(HttpClient http, Uri url, Uri? referrer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referrer is not null)
            req.Headers.Referrer = referrer;
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(L.T("core.common.gta5ModsReturned", (int)resp.StatusCode));
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
