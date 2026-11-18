using GTAModManager.Core.Game;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Oiv;

public sealed partial class OivInstaller
{
    /// <summary>
    /// Whether these changes need the onigiri layer: onigiri stays (or becomes) installed, or copies left from an
    /// earlier onigiri are still listed and must go.
    /// </summary>
    private bool OnigiriLayerApplies(List<FileChange> changes, out bool active)
    {
        active = false;
        if (game.Edition != GameEdition.Enhanced)
            return false;
        var loader = changes.FirstOrDefault(c =>
            c.RelativePath.Equals(GameInstall.OnigiriAsi, StringComparison.OrdinalIgnoreCase)
        );
        active = loader is not null ? loader.SourcePath is not null : game.UsesOnigiri;
        return active || File.Exists(Path.Combine(game.Root, OnigiriLayer.StatePath));
    }

    private void FinishOnigiriLayer(Context ctx)
    {
        if (ctx.Preview || !OnigiriLayerApplies(ctx.Plan.Changes, out bool active))
            return;
        var notes = new List<(string Target, string Message)>();
        var before = ctx.Plan.Changes.ToDictionary(
            c => c.RelativePath,
            c => c.ExpectedPreHash,
            StringComparer.OrdinalIgnoreCase
        );
        OnigiriLayer.Reconcile(
            game,
            active,
            ctx.Plan.Changes,
            ctx.Plan.ArchiveBackups.Values.SelectMany(a => a.Entries.Select(e => (a.Rel, e.Path))),
            ctx.Plan.PrepDir,
            ctx.Plan.DerivedFiles,
            notes
        );
        foreach (var (target, message) in notes)
            ctx.Plan.Items.Add(new("onigiri", target, message));
        ctx.Plan.BackupEstimate += ctx
            .Plan.Changes.Where(c => !before.ContainsKey(c.RelativePath) && c.ExpectedPreHash is not null)
            .Sum(c => new FileInfo(Path.Combine(game.Root, c.RelativePath)).Length);
    }

    private void UndoOnigiriLayer(Library.BackupRecord record, List<FileChange> changes, string prep)
    {
        if (!OnigiriLayerApplies(changes, out bool active))
            return;
        OnigiriLayer.Reconcile(
            game,
            active,
            changes,
            record.Archives.SelectMany(a => a.Entries.Select(e => (a.Rel, e.Path))),
            prep,
            [],
            []
        );
    }
}
