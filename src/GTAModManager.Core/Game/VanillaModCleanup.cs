using GTAModManager.Core.Discover;
using GTAModManager.Core.Library;
using GTAModManager.Core.NaturalVision;

namespace GTAModManager.Core.Game;

/// <summary>Completes the lifecycle of every composed installation after the selected game is actually clean.</summary>
public static class VanillaModCleanup
{
    internal static void MarkIncomplete(GameInstall game, string message, string? stateDirectory)
    {
        var store = new NveStore(game, stateDirectory);
        foreach (var state in store.Active())
        {
            state.Phase = "NeedsAttention";
            state.Message = message;
            store.Save(state);
        }
    }

    public static bool TryComplete(GameInstall game, BackupLibrary? library = null, string? stateDirectory = null)
    {
        using var gate = GameOperationGate.Acquire(game);
        if (game.IsRunning() || VanillaRestore.HasDisabledStorage(game))
            return false;
        // An obvious mod at the root already rules out vanilla. Avoid recursively
        // measuring every NVE/DLC file while holding the game operation mutex.
        if (
            Directory
                .EnumerateFileSystemEntries(game.Root)
                .Any(path => !VanillaRestore.IsGameRootEntry(game, Path.GetFileName(path), Directory.Exists(path)))
        )
            return false;
        if (!VanillaRestore.Inspect(game).Empty)
            return false;
        var store = new NveStore(game, stateDirectory);
        var states = store.All();
        var engine = game.CreateEngine();
        var ids = engine
            .History()
            .Where(j => j.TargetRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase))
            .Select(j => j.Id)
            .Concat(states.SelectMany(s => s.Components).Select(c => c.TransactionId).OfType<string>())
            .ToHashSet(StringComparer.Ordinal);
        var libraries = new Dictionary<string, BackupLibrary>(StringComparer.OrdinalIgnoreCase);
        if (library is not null)
            libraries[library.Root] = library;
        foreach (var state in states)
        {
            if (!string.IsNullOrWhiteSpace(state.BackupRoot) && BackupLibrary.TryOpen(state.BackupRoot) is { } original)
                libraries[original.Root] = original;
            state.Phase = "Removed";
            state.PendingComponent = null;
            state.Message = "";
            foreach (var item in state.Components)
            {
                item.Active = false;
                if (item.CacheFile is not null)
                {
                    string file = NveStore.Cache(state, item.CacheFile);
                    if (File.Exists(file))
                        File.Delete(file);
                    item.CacheFile = null;
                    item.CacheHash = null;
                }
            }
            store.Save(state);
        }
        // Old undo data must never be replayed into a freshly restored game.
        engine.ClearHistory(discardIncomplete: true);
        foreach (var backup in libraries.Values)
        {
            using var backupGate = GameOperationGate.Acquire(backup.Root);
            var preserve = backup
                .Records(game.Edition.ToString())
                .Where(r => !ids.Contains(r.Id))
                .Select(r => r.Id)
                .ToHashSet(StringComparer.Ordinal);
            backup.DeleteRecords(game.Edition.ToString(), preserve);
            backup.PruneUnused();
        }
        new InstalledMods().ForgetGame(game.Root);
        return true;
    }
}
