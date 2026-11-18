using System.Text;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Library;

/// <summary>Explains recorded undo effects without guessing what a package title means.</summary>
public static class UndoDescription
{
    public static string ForRecord(BackupRecord record)
    {
        var text = new StringBuilder(
            L.T("core.library.undoPackage", Mods.ModNames.DisplayOperationTitle(record.Title))
        );
        AddGroup(
            text,
            L.T("core.library.deleteAddedFiles"),
            record.Files.Where(f => f.OldBlob is null).Select(f => f.Rel)
        );
        AddGroup(
            text,
            L.T("core.library.restoreReplacedFiles"),
            record.Files.Where(f => f.OldBlob is not null && f.NewHash is not null).Select(f => f.Rel)
        );
        AddGroup(
            text,
            L.T("core.library.restoreDeletedFiles"),
            record.Files.Where(f => f.OldBlob is not null && f.NewHash is null).Select(f => f.Rel)
        );
        var entries = record
            .Archives.SelectMany(a => a.Entries.Select(e => (Change: e, Path: $"{a.Rel} → {e.Path}")))
            .ToList();
        AddGroup(
            text,
            L.T("core.library.deleteAddedRpfEntries"),
            entries.Where(e => e.Change.Kind == InnerKind.Added).Select(e => e.Path)
        );
        AddGroup(
            text,
            L.T("core.library.restoreReplacedRpfEntries"),
            entries.Where(e => e.Change.Kind == InnerKind.Replaced).Select(e => e.Path)
        );
        AddGroup(
            text,
            L.T("core.library.restoreDeletedRpfEntries"),
            entries.Where(e => e.Change.Kind == InnerKind.Deleted).Select(e => e.Path)
        );
        var created = record.Archives.Count(a => a.Created);
        if (created > 0)
            text.AppendLine(L.T("core.library.createdRpfArchivesTheyWill", created)).AppendLine();
        text.Append(L.T("core.library.restorationReturnsPreviousContentIncluding"));
        return text.ToString();
    }

    public static string ForJournal(TxJournal journal)
    {
        var text = new StringBuilder(
            L.T(
                "core.library.undoPackage",
                Mods.ModNames.DisplayOperationTitle(journal.Title ?? L.T("core.library.untitled"))
            )
        );
        AddGroup(text, L.T("core.library.fullFilesFromOldLog"), journal.Ops.Select(o => o.Rel));
        text.Append(L.T("core.library.previousFileVersionsWillRestored"));
        return text.ToString();
    }

    private static void AddGroup(StringBuilder text, string title, IEnumerable<string> paths)
    {
        var items = paths.ToList();
        if (items.Count == 0)
            return;
        text.AppendLine($"{title}: {items.Count}");
        foreach (var path in items.Take(6))
            text.AppendLine("  • " + path);
        if (items.Count > 6)
            text.AppendLine(L.T("core.library.more", items.Count - 6));
        text.AppendLine();
    }
}
