using System.Text.Json;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Base;

public static partial class BaseDownloads
{
    public static async Task<string> DownloadAdjusterAsync(
        HttpClient http,
        string id,
        string directory,
        CancellationToken cancellation
    )
    {
        var component = AdjusterComponents.Catalog.Single(c => c.Id == id);
        if (component.Gta5ModsPage is { } page)
        {
            using var site = StarterPackage.CreateSiteClient();
            var file = await Gta5ModsDownloads.ResolveAsync(site, page, cancellation).ConfigureAwait(false);
            return await Gta5ModsDownloads
                .DownloadAsync(site, file, directory, null, cancellation)
                .ConfigureAwait(false);
        }
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/repos/" + component.Repository + "/releases/latest"
        );
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1L << 20)
            throw new InvalidDataException(L.T("core.base.githubResponseTooLarge"));
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false)
        );
        var matches = json
            .RootElement.GetProperty("assets")
            .EnumerateArray()
            .Where(a => a.GetProperty("name").GetString()!.Equals(component.Asset, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(L.T("core.base.noUnambiguousArchiveFoundOfficial", component.Title));
        string url = matches[0].GetProperty("browser_download_url").GetString()!;
        if (
            !LinkSafety.IsSafeWebUrl(url, out var uri)
            || uri is null
            || !LinkSafety.HostIs(uri, "github.com")
            || !uri.AbsolutePath.StartsWith(
                "/" + component.Repository + "/releases/download/",
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new InvalidDataException(L.T("core.base.linkDoesNotPointOfficial"));
        using var download = new HttpRequestMessage(HttpMethod.Get, uri);
        download.Headers.Referrer = new Uri("https://github.com/" + component.Repository + "/releases");
        return await SaveAsync(http, download, directory, component.Asset, cancellation).ConfigureAwait(false);
    }
}
