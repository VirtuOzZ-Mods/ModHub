using System.Text;
using GTAModManager.Core.Localization;
using GTAModManager.Core.NaturalVision;

namespace GTAModManager.Core.Library;

public sealed partial class BackupLibrary
{
    internal const string ContentsName = "README.txt";

    /// <summary>
    /// Writes README.txt: a plain-language description of the folder, the original game files kept
    /// in vanilla.pack and every install with what it changed. The packs themselves are binary, so this
    /// is how a person sees what is inside. Best effort: a failure here never breaks an install.
    /// </summary>
    void WriteContents(IReadOnlyList<BackupRecord> records)
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine(L.T("backup.contents.title"));
            text.AppendLine(L.T("backup.contents.updated", DateTime.Now.ToString("g")));
            text.AppendLine();
            text.AppendLine(L.T("backup.contents.vanilla", FormatSize(UsedBytes(BackupOrigin.Vanilla))));
            text.AppendLine(L.T("backup.contents.mods", FormatSize(UsedBytes(BackupOrigin.Mods))));
            text.AppendLine(L.T("backup.contents.journal"));
            text.AppendLine(L.T("backup.contents.naturalVision", NveStore.CacheRootName));
            text.AppendLine(L.T("backup.contents.manual", Inventory.ModFileBackup.FolderName));

            var live = records.Where(r => !r.Undone).ToList();
            var originals = live.SelectMany(r =>
                    r.Files.Where(f => f.OldBlob is not null && f.Origin == BackupOrigin.Vanilla)
                        .Select(f => f.Rel)
                        .Concat(
                            r.Archives.SelectMany(a =>
                                a.Entries.Where(e => e.Old is { Origin: BackupOrigin.Vanilla })
                                    .Select(e => a.Rel + " → " + e.Path)
                            )
                        )
                        .Select(path => $"[{r.Edition}] {path}")
                )
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            text.AppendLine();
            text.AppendLine(L.T("backup.contents.originals", originals.Count));
            foreach (string path in originals)
                text.AppendLine("  " + path);

            text.AppendLine();
            text.AppendLine(L.T("backup.contents.installs", records.Count));
            foreach (var r in records.OrderByDescending(r => r.Id))
            {
                text.AppendLine();
                text.Append($"[{r.Edition}] {r.Created.LocalDateTime:g}  {Mods.ModNames.DisplayOperationTitle(r.Title)}");
                if (r.Undone)
                {
                    text.AppendLine("  (" + L.T("backup.contents.undone") + ")");
                    continue;
                }
                text.AppendLine();
                foreach (var f in r.Files.Where(f => !f.Rel.StartsWith("ModHubState\\", StringComparison.OrdinalIgnoreCase)))
                    text.AppendLine("  " + f.Rel + "  — " + Describe(f.OldBlob is null, f.NewHash is null, f.Origin));
                foreach (var a in r.Archives)
                foreach (var e in a.Entries)
                    text.AppendLine(
                        "  " + a.Rel + " → " + e.Path + "  — "
                            + Describe(e.Old is null, e.Kind == InnerKind.Deleted, e.Old?.Origin ?? BackupOrigin.Mods)
                    );
            }
            AtomicWrite(Path.Combine(Root, ContentsName), Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text.ToString())).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string Describe(bool added, bool deleted, BackupOrigin origin) =>
        added ? L.T("backup.contents.added")
        : L.T(
            deleted ? "backup.contents.deletedKept" : "backup.contents.kept",
            origin == BackupOrigin.Vanilla ? VanillaPackName : ModsPackName
        );
}
