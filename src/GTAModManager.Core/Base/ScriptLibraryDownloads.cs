using System.Text.Json;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Base;

public static partial class BaseDownloads
{
    public static async Task<string> DownloadScriptLibraryAsync(
        HttpClient http,
        string id,
        string directory,
        CancellationToken ct
    )
    {
        var component = ScriptLibraries.Get(id);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{component.Repository}/releases/tags/{component.Tag}"
        );
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1L << 20)
            throw new InvalidDataException(L.T("libraries.payload", component.Title));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var release = json.RootElement;
        if (
            release.GetProperty("tag_name").GetString() != component.Tag
            || release.GetProperty("draft").GetBoolean()
            || release.GetProperty("prerelease").GetBoolean()
        )
            throw new InvalidDataException(L.T("libraries.payload", component.Title));
        var assets = release
            .GetProperty("assets")
            .EnumerateArray()
            .Where(a => a.GetProperty("name").GetString() == component.Asset)
            .ToArray();
        if (assets.Length != 1)
            throw new InvalidDataException(L.T("libraries.payload", component.Title));
        string expected =
            $"https://github.com/{component.Repository}/releases/download/{component.Tag}/{component.Asset}";
        if (assets[0].GetProperty("browser_download_url").GetString() != expected)
            throw new InvalidDataException(L.T("libraries.payload", component.Title));
        using var download = new HttpRequestMessage(HttpMethod.Get, expected);
        return await SaveAsync(http, download, directory, component.Asset, ct).ConfigureAwait(false);
    }
}
