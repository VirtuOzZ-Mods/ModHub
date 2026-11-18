using System.Security.Cryptography;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Crypto;

/// <summary>
/// magic.dat from the open-source CodeWalker project (dexyfex): NG keys and tables encrypted with the game's AES key.
/// The file is useless without the user's game exe. ModHub does not redistribute it: if it is missing, it is downloaded
/// from the official CodeWalker repository on GitHub into the ModHub data folder and verified by SHA-256.
/// </summary>
public static class MagicDat
{
    public const string OfficialUrl =
        "https://raw.githubusercontent.com/dexyfex/CodeWalker/master/CodeWalker.Core/Resources/magic.dat";

    /// <summary>SHA-256 of the official file (CodeWalker.Core/Resources/magic.dat).</summary>
    public const string Sha256 = "DC35981F822E892CED3AA81D31E7A96927D573EE28F67417592B5AFEAF330832";
    private const int MaxSize = 1 << 20;

    public static bool IsValid(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxSize)
                return false;
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == Sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures magic.dat exists. Downloads only when missing; on a network error throws KeysNotFoundException with a clear reason.
    /// An existing user file is never overwritten (even if it is a different version).
    /// </summary>
    public static void Ensure(string path, HttpMessageHandler? handler = null)
    {
        if (File.Exists(path))
            return;
        byte[] data;
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler);
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ModHub/1.0 (GTA V mod manager)");
            using var resp = http.GetAsync(OfficialUrl, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter()
                .GetResult();
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(L.T("core.crypto.githubReturned", (int)resp.StatusCode));
            if (resp.Content.Headers.ContentLength > MaxSize)
                throw new InvalidDataException(L.T("core.crypto.fileTooLarge"));
            data = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            throw new KeysNotFoundException(L.T("core.crypto.couldNotDownloadMagicDat", ex.Message), true);
        }
        if (data.Length > MaxSize || Convert.ToHexString(SHA256.HashData(data)) != Sha256)
            throw new KeysNotFoundException(L.T("core.crypto.downloadedMagicDatDoesNot"), true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }
}
