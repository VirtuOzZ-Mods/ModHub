using System.Text.Json;
using GTAModManager.Core.Game;

namespace GTAModManager.Core.Discover;

public sealed class ThanksEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string GameRoot { get; set; } = "";
    public DateTime InstalledUtc { get; set; }
    public bool Done { get; set; }
}

/// <summary>
/// Support the Author: Mod Hub downloads mods from GTA5-Mods on the user's behalf, so once a mod has been tried
/// in game (the game was launched after installation), it suggests once to visit the mod page and like, rate
/// or comment. It never sends anything itself; it only opens the page.
/// </summary>
public sealed class AuthorThanks(string? path = null)
{
    /// <summary>
    /// Disabled in the beta: deciding that "the mod works" should rely on mod logs, and those are missing in a write-protected
    /// game folder. To be revisited after the beta. While disabled, nothing is recorded and the card is never shown.
    /// </summary>
    public static bool Enabled { get; set; }

    private readonly string _path = path ?? AppPaths.LibraryFile("author-thanks.json");
    private const int MaxEntries = 200;

    public static bool IsGta5Mods(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && (
            u.Host.Equals("gta5-mods.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".gta5-mods.com", StringComparison.OrdinalIgnoreCase)
        )
        && !u.Host.StartsWith("files.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Mod page URL without the trailing "/download/N".</summary>
    public static string ModPage(string url)
    {
        var i = url.IndexOf("/download/", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? url[..i] : url;
    }

    public List<ThanksEntry> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<ThanksEntry>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Save(List<ThanksEntry> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(list.TakeLast(MaxEntries).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Records a mod installed from GTA5-Mods. Entries already handled (Done) are not re-queued.</summary>
    public void Remember(string url, string title, string gameRoot, DateTime? installedUtc = null)
    {
        if (!Enabled || !IsGta5Mods(url))
            return;
        var page = ModPage(url);
        var list = Load();
        var existing = list.FirstOrDefault(e => e.Url.Equals(page, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!existing.Done)
            {
                existing.InstalledUtc = installedUtc ?? DateTime.UtcNow;
                existing.GameRoot = gameRoot;
            }
        }
        else
            list.Add(
                new ThanksEntry
                {
                    Url = page,
                    Title = title,
                    GameRoot = gameRoot,
                    InstalledUtc = installedUtc ?? DateTime.UtcNow,
                }
            );
        Save(list);
    }

    /// <summary>A mod that has already been tried in game (this game was launched after installation), or null.</summary>
    public ThanksEntry? Next(GameInstall game, DateTime? lastLaunchUtc)
    {
        if (lastLaunchUtc is null)
            return null;
        return Load()
            .Where(e =>
                !e.Done
                && e.GameRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase)
                && e.InstalledUtc < lastLaunchUtc
            )
            .OrderBy(e => e.InstalledUtc)
            .FirstOrDefault();
    }

    public void Finish(string url)
    {
        var list = Load();
        foreach (var e in list.Where(e => e.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            e.Done = true;
        Save(list);
    }
}
