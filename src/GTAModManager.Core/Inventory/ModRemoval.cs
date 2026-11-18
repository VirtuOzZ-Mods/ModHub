using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.NaturalVision;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Inventory;

public sealed class ModRemoveRequest
{
    public string Name { get; set; } = "";
    public List<string> Operations { get; set; } = [];
}

public sealed record ModRemoveResult(bool Success, int Undone, IReadOnlyList<string> Failed, string Message);

/// <summary>
/// "Remove" on a My Mods card: undoes every Mod Hub installation of that mod, newest first. Each undo takes out
/// exactly what the installation wrote (its files and its entries inside shared archives such as update.rpf) and
/// puts back what was there before, even if those files were edited afterwards. Other mods' files and entries
/// stay as they are. NaturalVision and LCPP are left to their own service, which keeps its installation state.
/// </summary>
public static class ModRemoval
{
    public static ModRemoveResult Remove(GameInstall game, BackupLibrary? library, ModRemoveRequest request)
    {
        using var gate = GameOperationGate.Acquire(game);
        if (game.IsRunning())
            throw new InvalidOperationException(L.T("core.common.gameRunningChangesProhibited"));
        if (new ModModeService(game).Inspect().WritesBlocked)
            throw new InvalidOperationException(L.T("core.oiv.enableModsFinishRecoveryFirst"));
        var suites = new NveStore(game)
            .Active()
            .SelectMany(s => s.Components)
            .Select(c => c.TransactionId)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installer = new OivInstaller(game);
        var failed = new List<string>();
        int undone = 0;
        var engine = game.CreateEngine();
        // Add-on pack folders the undone installations wrote to: removed afterwards if nothing is left in them.
        var packDirs = request
            .Operations.Select(engine.LoadJournal)
            .OfType<TxJournal>()
            .SelectMany(j => j.Ops)
            .Select(o => PackFolder(o.Rel))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Journal ids start with a UTC timestamp: descending order undoes the latest installation first.
        foreach (
            var id in request
                .Operations.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id => !suites.Contains(id))
                .OrderByDescending(id => id, StringComparer.Ordinal)
        )
        {
            if (library?.LoadRecord(game.Edition.ToString(), id) is { Undone: true })
                continue;
            var result = installer.Undo(id, library, force: true);
            if (result.State == TxState.Undone)
                undone++;
            else
                failed.Add(result.Message);
        }
        foreach (var dir in packDirs)
            DeleteIfEmpty(Path.Combine(game.Root, dir));
        string name = Mods.ModNames.DisplayOperationTitle(request.Name);
        string message =
            failed.Count > 0 ? L.T("core.modRemoval.partial", name, undone, string.Join("\n", failed.Take(5)))
            : undone > 0 ? L.T("core.modRemoval.done", name, undone)
            : L.T("core.modRemoval.nothing", name);
        return new(failed.Count == 0, undone, failed, message);
    }

    /// <summary>"mods\update\x64\dlcpacks\car\dlc.rpf" → "mods\update\x64\dlcpacks\car" (also onigiri\dlcpacks).</summary>
    static string? PackFolder(string rel)
    {
        foreach (var root in new[] { @"mods\update\x64\dlcpacks\", @"onigiri\dlcpacks\" })
            if (rel.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var pack = rel[root.Length..].Split('\\')[0];
                return pack.Length > 0 && pack != rel[root.Length..] ? root + pack : null;
            }
        return null;
    }

    /// <summary>Removes the pack folder and its empty subfolders only when no file is left in it.</summary>
    static void DeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
                Directory.Delete(dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
