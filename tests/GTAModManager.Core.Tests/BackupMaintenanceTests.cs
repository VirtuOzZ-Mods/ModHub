using GTAModManager.Core.Library;

namespace GTAModManager.Core.Tests;

public sealed class BackupMaintenanceTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("gmm-backup-maintenance-").FullName;

    BackupLibrary Library() => BackupLibrary.Create(Path.Combine(root, "library"), 100_000_000);

    static BackupRecord Record(string blob, bool undone) =>
        new()
        {
            Id = "20261115000000000-test",
            Title = "test",
            Edition = "Legacy",
            Undone = undone,
            Files =
            [
                new FileBackup
                {
                    Rel = "test.asi",
                    OldBlob = blob,
                    NewHash = "new",
                },
            ],
        };

    [Fact]
    public void Prune_removes_only_unreferenced_blobs()
    {
        var library = Library();
        string live = library.PutBlob([1, 2, 3, 4], BackupOrigin.Mods);
        string unused = library.PutBlob([5, 6, 7], BackupOrigin.Mods);
        library.SaveRecord(Record(live, false));
        Assert.Equal(3, library.PruneUnused());
        Assert.True(library.HasBlob(live));
        Assert.False(library.HasBlob(unused));
        Assert.Single(library.Records());
    }

    [Fact]
    public void Reset_recreates_empty_library_with_same_quota()
    {
        var library = Library();
        string blob = library.PutBlob([1, 2, 3], BackupOrigin.Mods);
        library.SaveRecord(Record(blob, true));
        library.Reset();
        var reopened = BackupLibrary.TryOpen(library.Root)!;
        Assert.NotNull(reopened);
        Assert.Equal(100_000_000, reopened.QuotaBytes);
        Assert.Empty(reopened.Records());
        Assert.Equal(0, reopened.UsedBytes());
    }

    [Fact]
    public void Reset_refuses_live_backups_and_keeps_them_intact()
    {
        var library = Library();
        string blob = library.PutBlob([1, 2, 3], BackupOrigin.Mods);
        library.SaveRecord(Record(blob, false));
        Assert.Throws<InvalidOperationException>(() => library.Reset());
        Assert.True(library.HasBlob(blob));
        Assert.Single(library.Records());
    }

    [Fact]
    public void Reset_refuses_unrelated_files_in_selected_folder()
    {
        var library = Library();
        string file = Path.Combine(library.Root, "personal.txt");
        File.WriteAllText(file, "keep me");
        Assert.Throws<IOException>(() => library.Reset());
        Assert.Equal("keep me", File.ReadAllText(file));
    }

    [Fact]
    public void Reset_refuses_unreadable_records_instead_of_losing_unknown_backups()
    {
        var library = Library();
        string directory = Directory.CreateDirectory(Path.Combine(library.Root, "records", "Legacy")).FullName;
        File.WriteAllText(Path.Combine(directory, "broken.json"), "not-json");
        Assert.Throws<IOException>(() => library.Reset());
        Assert.True(File.Exists(Path.Combine(directory, "broken.json")));
    }

    [Fact]
    public async Task Prune_waits_until_an_install_publishes_its_backup_record()
    {
        var library = Library();
        using var release = new ManualResetEventSlim();
        var created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Dedicated threads (LongRunning): in the full parallel suite the shared thread pool can be busy for
        // seconds, which would time the test out without saying anything about the gate.
        var publication = Task.Factory.StartNew(
            () =>
            {
                using var gate = GTAModManager.Core.Game.GameOperationGate.Acquire(library.Root);
                string hash = library.PutBlob([1, 2, 3], BackupOrigin.Mods);
                created.SetResult(hash);
                if (!release.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException();
                library.SaveRecord(Record(hash, false));
            },
            TaskCreationOptions.LongRunning
        );
        string blob = await created.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var cleanup = Task.Factory.StartNew(library.PruneUnused, TaskCreationOptions.LongRunning);
        bool ranEarly;
        try
        {
            await Task.Delay(150);
            ranEarly = cleanup.IsCompleted;
        }
        finally
        {
            release.Set();
            await publication;
            await cleanup;
        }
        Assert.False(ranEarly);
        Assert.True(library.HasBlob(blob));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("broken-json")]
    public void Prune_refuses_unreadable_live_record_and_preserves_its_blob(string contents)
    {
        var library = Library();
        string blob = library.PutBlob([1, 2, 3], BackupOrigin.Mods);
        library.SaveRecord(Record(blob, false));
        File.WriteAllText(Path.Combine(library.Root, BackupLibrary.JournalName), contents);
        Assert.Throws<IOException>(() => library.PruneUnused());
        Assert.True(library.HasBlob(blob));
    }

    [Fact]
    public void Legacy_folders_are_migrated_into_packs_and_journal()
    {
        string dir = Path.Combine(root, "legacy");
        BackupLibrary.Create(dir, 100_000_000);
        byte[] vanilla = [1, 2, 3],
            mods = [4, 5, 6, 7];
        string vh = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(vanilla));
        string mh = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(mods));
        Directory.CreateDirectory(Path.Combine(dir, "vanilla", vh[..2]));
        File.WriteAllBytes(Path.Combine(dir, "vanilla", vh[..2], vh), vanilla);
        Directory.CreateDirectory(Path.Combine(dir, "mods", mh[..2]));
        File.WriteAllBytes(Path.Combine(dir, "mods", mh[..2], mh), mods);
        Directory.CreateDirectory(Path.Combine(dir, "records", "Legacy"));
        File.WriteAllText(
            Path.Combine(dir, "records", "Legacy", "20261115000000000-test.json"),
            System.Text.Json.JsonSerializer.Serialize(Record(vh, false))
        );
        Directory.CreateDirectory(Path.Combine(dir, "naturalvision-sources", "scope", "empty"));

        var library = BackupLibrary.TryOpen(dir)!;

        Assert.Equal(vanilla, library.ReadBlob(vh));
        Assert.Equal(mods, library.ReadBlob(mh));
        Assert.True(library.UsedBytes(BackupOrigin.Vanilla) > 0);
        Assert.Equal(vh, Assert.Single(library.Records("Legacy")).Files[0].OldBlob);
        Assert.Equal(
            new[] { "journal.json", "library.json", "mods.pack", "README.txt", "vanilla.pack" },
            Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)).Order(StringComparer.OrdinalIgnoreCase).ToArray()
        );
    }

    [Fact]
    public void Torn_tail_of_a_pack_is_dropped_before_the_next_append()
    {
        var library = Library();
        string first = library.PutBlob([1, 2, 3], BackupOrigin.Mods);
        string pack = Path.Combine(library.Root, BackupLibrary.ModsPackName);
        using (var fs = new FileStream(pack, FileMode.Append))
            fs.Write([0x42, 0x4C, 0x4F, 0x42, 9, 9]); // interrupted entry header
        string second = library.PutBlob([4, 5], BackupOrigin.Mods);
        var reopened = BackupLibrary.TryOpen(library.Root)!;
        Assert.Equal([1, 2, 3], reopened.ReadBlob(first));
        Assert.Equal([4, 5], reopened.ReadBlob(second));
    }

    public void Dispose() => Directory.Delete(root, true);
}
