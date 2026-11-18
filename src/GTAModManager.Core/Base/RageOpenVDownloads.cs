using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Base;

/// <summary>
/// RageOpenV (the mods\ folder loader by martonp96 and Chiheb-Bacha) is not shipped with Mod Hub: the authors ask
/// not to re-upload it. It is downloaded from the authors' own GitHub release when the user installs it, pinned to a
/// reviewed version and verified by SHA-256 before anything reaches the game. A new upstream version is adopted by
/// updating these constants after checking it.
/// </summary>
public static class RageOpenVRelease
{
    public const string Repository = "Chiheb-Bacha/RageOpenV";
    public const string Tag = "v1.0";
    public const string Asset = "RageOpenV.zip";

    /// <summary>SHA-256 of RageOpenV.zip in the v1.0 release (the same value GitHub publishes for the asset).</summary>
    public const string ArchiveSha256 = "C39D574CAA9DB4462B20110E87A8229E48DEEB2819883800C8E54F1C6C80548C";

    /// <summary>The loader in the archive root (RageOpenV_MoreLogs\ holds a verbose-logging variant, not used).</summary>
    public const string AsiEntry = "RageOpenV.asi";

    public static readonly Uri ModPage = new("https://www.gta5-mods.com/tools/rageopenv");
    public static readonly Uri SourcePage = new("https://github.com/Chiheb-Bacha/RageOpenV");

    public static string DownloadUrl => $"https://github.com/{Repository}/releases/download/{Tag}/{Asset}";

    /// <summary>Checks the archive hash and extracts RageOpenV.asi into <paramref name="destDir"/>; returns its path.</summary>
    public static string ExtractVerifiedAsi(string zipPath, string destDir)
    {
        string hash;
        using (var stream = File.OpenRead(zipPath))
            hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!hash.Equals(ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("core.base.rageopenvChecksumMismatch", Tag));
        using var archive = ZipFile.OpenRead(zipPath);
        var entry =
            archive.Entries.SingleOrDefault(e => e.FullName.Equals(AsiEntry, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(L.T("core.base.rageopenvChecksumMismatch", Tag));
        Directory.CreateDirectory(destDir);
        string asi = Path.Combine(destDir, AsiEntry);
        entry.ExtractToFile(asi, overwrite: true);
        return asi;
    }
}

public static partial class BaseDownloads
{
    /// <summary>
    /// Downloads RageOpenV.zip of the pinned release from the authors' GitHub (GitHub counts the download), verifies it
    /// and returns the extracted RageOpenV.asi.
    /// </summary>
    public static async Task<string> DownloadRageOpenVAsync(HttpClient http, string destDir, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{RageOpenVRelease.Repository}/releases/tags/{RageOpenVRelease.Tag}"
        );
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(L.T("core.base.githubReturned", (int)response.StatusCode));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var assets = json
            .RootElement.GetProperty("assets")
            .EnumerateArray()
            .Where(a => a.GetProperty("name").GetString() == RageOpenVRelease.Asset)
            .ToArray();
        if (assets is not [var asset] || asset.GetProperty("browser_download_url").GetString() != RageOpenVRelease.DownloadUrl)
            throw new InvalidDataException(L.T("core.base.invalidReleaseLink"));
        using var download = new HttpRequestMessage(HttpMethod.Get, RageOpenVRelease.DownloadUrl);
        string zip = await SaveAsync(http, download, destDir, RageOpenVRelease.Asset, ct).ConfigureAwait(false);
        return RageOpenVRelease.ExtractVerifiedAsi(zip, Path.Combine(destDir, "RageOpenV"));
    }
}
