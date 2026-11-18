using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Keybinds;

/// <summary>Only user-confirmed potential conflicts are hidden, scoped to one installation and exact assignments.</summary>
public sealed class PotentialConflictConfirmations
{
    readonly string path;
    Dictionary<string, DateTime> confirmed;

    public PotentialConflictConfirmations(GameInstall game, string? stateDirectory = null)
    {
        string scope = Hash(game.Edition + "|" + Path.GetFullPath(game.Root).TrimEnd('\\').ToUpperInvariant());
        path = Path.Combine(stateDirectory ?? AppPaths.LibraryFile("keybind-confirmations"), scope + ".json");
        try
        {
            confirmed = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            confirmed = [];
        }
    }

    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Key(KeybindConflict conflict)
    {
        string Item(KeybindEntry e) =>
            JsonSerializer.Serialize(
                new
                {
                    e.Id,
                    e.ModId,
                    e.Chord,
                    e.Context,
                    e.Status,
                    e.Format,
                }
            );
        return Hash(
            string.Join("\n", new[] { Item(conflict.A), Item(conflict.B) }.Order(StringComparer.Ordinal))
                + "\n"
                + conflict.Reason
        );
    }

    public bool IsConfirmed(KeybindConflict conflict) =>
        conflict.Kind == ConflictKind.Potential && confirmed.ContainsKey(Key(conflict));

    public List<KeybindConflict> Filter(IEnumerable<KeybindConflict> conflicts) =>
        conflicts.Where(c => !IsConfirmed(c)).ToList();

    public void Confirm(KeybindConflict conflict)
    {
        if (conflict.Kind != ConflictKind.Potential)
            throw new ArgumentException(L.T("core.keybinds.onlyPotentialOverlapsCanConfirmed"));
        confirmed[Key(conflict)] = DateTime.UtcNow;
        Save();
    }

    public void Reset()
    {
        confirmed.Clear();
        Save();
    }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(confirmed));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
