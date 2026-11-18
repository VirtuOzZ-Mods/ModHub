using GTAModManager.Core.Library;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public class UndoDescriptionTests
{
    private static BackupRecord Record(string title = "Пакет") =>
        new()
        {
            Id = "1",
            Title = title,
            Edition = "Legacy",
        };

    [Fact]
    public void Added_files_are_removed_regardless_of_package_title()
    {
        var record = Record("Uninstall something");
        record.Files.Add(new() { Rel = "mods/add.rpf", NewHash = "new" });
        var text = UndoDescription.ForRecord(record);
        Assert.Contains("Uninstall something", text);
        Assert.Contains("Удалить добавленные файлы: 1", text);
        Assert.Contains("mods/add.rpf", text);
        Assert.Contains("Только изменения из этой записи", text);
    }

    [Theory]
    [InlineData("new", "заменённые")]
    [InlineData(null, "удалённые")]
    public void Previous_files_are_restored(string? newHash, string category)
    {
        var record = Record("Install");
        record.Files.Add(
            new()
            {
                Rel = "mods/previous.rpf",
                OldBlob = "old",
                NewHash = newHash,
            }
        );
        var text = UndoDescription.ForRecord(record);
        Assert.Contains($"Восстановить {category} файлы: 1", text);
        Assert.Contains("mods/previous.rpf", text);
    }

    [Fact]
    public void Mixed_archive_changes_describe_inner_paths_and_conditional_archive_removal()
    {
        var record = Record();
        record.Archives.Add(
            new()
            {
                Rel = "mods/a.rpf",
                Created = true,
                Entries =
                [
                    new() { Path = "added.ymt", Kind = InnerKind.Added },
                    new() { Path = "replaced.ymt", Kind = InnerKind.Replaced },
                    new() { Path = "deleted.ymt", Kind = InnerKind.Deleted },
                ],
            }
        );
        var text = UndoDescription.ForRecord(record);
        Assert.Contains("Удалить добавленные записи RPF: 1", text);
        Assert.Contains("Восстановить заменённые записи RPF: 1", text);
        Assert.Contains("Восстановить удалённые записи RPF: 1", text);
        Assert.Contains("mods/a.rpf → deleted.ymt", text);
        Assert.Contains("Созданные архивы RPF: 1", text);
        Assert.Contains("если не менялись", text);
    }

    [Fact]
    public void Long_path_lists_are_bounded_but_counts_remain_visible()
    {
        var record = Record();
        for (int i = 0; i < 20; i++)
            record.Files.Add(new() { Rel = $"mods/{i}.dat", NewHash = "new" });
        var text = UndoDescription.ForRecord(record);
        Assert.Contains("Удалить добавленные файлы: 20", text);
        Assert.Contains("ещё 14", text);
        Assert.DoesNotContain("mods/19.dat", text);
    }

    [Fact]
    public void Legacy_journal_describes_whole_file_rollback_without_guessing_package_semantics()
    {
        var journal = new TxJournal
        {
            Id = "old",
            Title = "Uninstall old mod",
            TargetRoot = "root",
            StagingDir = "stage",
            Ops = [new() { Rel = "mods/a.rpf", Backup = "backup" }],
        };
        var text = UndoDescription.ForJournal(journal);
        Assert.Contains("Uninstall old mod", text);
        Assert.Contains("Полные файлы из старого журнала: 1", text);
        Assert.Contains("mods/a.rpf", text);
        Assert.Contains("другие изменения в этих файлах", text);
        Assert.DoesNotContain("установк", text);
    }
}
