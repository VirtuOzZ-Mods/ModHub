using System.Text.Json;
using GTAModManager.Core.Game;
using GTAModManager.Core.Inventory;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Dlc;

/// <summary>Several sound changes approved at once: undo of old changes first, then the new assignments.</summary>
public sealed class EngineSoundBatchRequest
{
    /// <summary>Cars changed by older Mod Hub versions without a recorded original: undo their sound installations.</summary>
    public List<ModRemoveRequest> Undo { get; set; } = [];

    /// <summary>New sounds, one car per entry; "back to original" is an assignment of the recorded original sound.</summary>
    public List<EngineSoundRequest> Assign { get; set; } = [];
}

public sealed record EngineSoundBatchResult(bool Success, IReadOnlyList<string> Messages);

public static partial class EngineSounds
{
    private static readonly JsonSerializerOptions OriginalsJson = new() { WriteIndented = true };

    /// <summary>Original sounds of cars Mod Hub has changed, in the service folder next to the game.</summary>
    public static string OriginalsFile(GameInstall game) => Path.Combine(game.WorkDir, "engine-sounds", "originals.json");

    // The service folder is shared by every installation of this edition on the drive, so the game root is part of the key.
    private static string Key(GameInstall game, string archiveRel, string model) =>
        (game.Root.TrimEnd('\\') + "|" + archiveRel.Trim() + "|" + model.Trim()).ToLowerInvariant();

    /// <summary>"game|archive|model" → the sound the car had before Mod Hub first changed it.</summary>
    public static IReadOnlyDictionary<string, string> LoadOriginals(GameInstall game)
    {
        try
        {
            var file = OriginalsFile(game);
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? []
                : new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>The sound <paramref name="target"/> had before Mod Hub changed it, or null when it was never recorded.</summary>
    public static string? Original(GameInstall game, IReadOnlyDictionary<string, string> originals, SoundTarget target) =>
        originals.TryGetValue(Key(game, target.ArchiveRel, target.Vehicle.Model), out var sound) ? sound : null;

    /// <summary>Records the car's current sound as its original, only the first time.</summary>
    private static void RememberOriginal(GameInstall game, SoundTarget target)
    {
        var originals = new Dictionary<string, string>(LoadOriginals(game));
        string key = Key(game, target.ArchiveRel, target.Vehicle.Model);
        if (originals.ContainsKey(key))
            return;
        originals[key] = target.Vehicle.EffectiveAudio;
        var file = OriginalsFile(game);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(originals, OriginalsJson));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>
    /// Applies a batch in one elevated run. Each car is its own transaction, so it can be undone separately in History;
    /// a failed car does not stop the others.
    /// </summary>
    public static EngineSoundBatchResult AssignMany(
        GameInstall game,
        Library.BackupLibrary? library,
        EngineSoundBatchRequest request
    )
    {
        var messages = new List<string>();
        bool ok = true;
        foreach (var undo in request.Undo)
        {
            var result = ModRemoval.Remove(game, library, undo);
            ok &= result.Success;
            messages.Add(result.Message);
        }
        foreach (var assign in request.Assign)
        {
            try
            {
                var result = Assign(game, library, assign);
                ok &= result.Success;
                messages.Add(result.Message);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                ok = false;
                messages.Add(L.T("core.dlc.soundFailed", assign.Model, ex.Message));
            }
        }
        return new(ok, messages);
    }
}
