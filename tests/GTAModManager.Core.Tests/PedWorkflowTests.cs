using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Peds;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class PedWorkflowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gmm-ped-workflow-" + Guid.NewGuid().ToString("N"));
    private readonly string failWork = Path.Combine(
        Path.GetTempPath(),
        "gmm-ped-workflow-tx-" + Guid.NewGuid().ToString("N")
    );
    private readonly GameInstall game;
    private readonly BackupLibrary library;

    public PedWorkflowTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "GTA5.exe"), "synthetic");
        File.WriteAllText(Path.Combine(root, "OpenIV.asi"), "MZ synthetic");
        Directory.CreateDirectory(Path.Combine(root, "mods", "update"));
        using (var file = File.Create(Path.Combine(root, "mods", "update", "update.rpf")))
            new Rpf7Builder()
                .AddFile(
                    @"common\data\dlclist.xml",
                    new RpfBinarySource(
                        Encoding.UTF8.GetBytes(
                            "<SMandatoryPacksData><Paths>\n<Item>dlcpacks:/existing/</Item>\n</Paths></SMandatoryPacksData>"
                        )
                    )
                )
                .Write(file);
        game = GameInstall.Open(root);
        game.RunningCheckOverride = () => false;
        library = BackupLibrary.Create(Path.Combine(root, "test-backups"), 1L << 30);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException) { }
        try
        {
            if (Directory.Exists(failWork))
                Directory.Delete(failWork, true);
        }
        catch (IOException) { }
    }

    private PedArchiveAnalysis Archive(string name)
    {
        string path = Path.Combine(root, name + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var ext in new[] { ".yft", ".ymt", ".ydd", ".ytd" })
                using (var stream = zip.CreateEntry(name + ext).Open())
                    stream.Write([0x52, 0x53, 0x43, 0x37, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            using (var writer = new StreamWriter(zip.CreateEntry("Readme.txt").Open()))
                writer.Write(
                    "Model "
                        + name
                        + "\nby Test Author\nCredits:\nOriginal contributors\n-----\nINSTALLATION - Add-on\nUse ModHub\n"
                );
            using (var stream = zip.CreateEntry("images/" + name + ".png").Open())
                stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        }
        return PedArchiveAnalyzer.Analyze(path);
    }

    private static PedSelection Choice(PedArchiveAnalysis analysis) =>
        new(analysis.Candidates[0].Id, analysis.Candidates[0].SpawnName, PedGender.Male, analysis.Candidates[0].Layout);

    private void Install(InstallPlan plan)
    {
        Assert.True(plan.CanInstall, string.Join("; ", plan.Errors));
        Assert.Equal(TxState.Completed, new OivInstaller(game).Install(plan, library).State);
    }

    [Fact]
    public void Installed_package_cache_survives_deleted_original_and_contains_no_model_resources()
    {
        var analysis = Archive("cached");
        using (var plan = new PedInstaller(game).PrepareInstall(analysis, [Choice(analysis)]))
            Install(plan);
        var model = Assert.Single(new PedInstaller(game).ListManaged().Entries);
        Assert.False(string.IsNullOrWhiteSpace(model.PackageId));
        File.Delete(analysis.ArchivePath);
        var presentation = new PedPackageLibrary(game).Load([model]);
        Assert.Equal("Model cached", presentation.Title);
        Assert.Equal("Test Author", presentation.Details.Authors);
        Assert.Contains("Original contributors", presentation.Details.Credits);
        Assert.Contains("Use ModHub", presentation.Readme);
        Assert.Equal(analysis.ImagePaths, presentation.ImagePaths);
        Assert.True(File.Exists(presentation.MediaArchivePath));
        using var cache = ZipFile.OpenRead(presentation.MediaArchivePath);
        Assert.NotNull(cache.GetEntry("images/cached.png"));
        Assert.DoesNotContain(cache.Entries, e =>
            new[] { ".yft", ".ymt", ".ydd", ".ytd" }.Contains(Path.GetExtension(e.FullName))
        );
    }

    [Fact]
    public void Old_entries_fall_back_to_original_and_missing_source_is_explicit()
    {
        var analysis = Archive("old");
        var entry = new PedEntry
        {
            SpawnName = "old",
            DisplayName = "Old model",
            SourceArchive = analysis.ArchivePath,
        };
        var existing = new PedPackageLibrary(game).Load([entry]);
        Assert.Equal(analysis.ArchivePath, existing.MediaArchivePath);
        Assert.Equal("Test Author", existing.Details.Authors);
        File.Delete(analysis.ArchivePath);
        var absent = new PedPackageLibrary(game).Load([entry]);
        Assert.Empty(absent.ImagePaths);
        Assert.Contains("недоступ", absent.Readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("old", absent.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_does_not_stage_cache_or_read_image_bodies()
    {
        var analysis = Archive("preview");
        using var plan = new PedInstaller(game).PrepareInstall(analysis, [Choice(analysis)], preview: true);
        Assert.Empty(plan.Errors);
        Assert.True(plan.Preview);
        Assert.Empty(Directory.EnumerateFiles(plan.PrepDir, "*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(root, "GMMAddonPeds", "packages")));
    }

    [Fact]
    public void Multi_remove_rolls_back_and_undo_preserves_unselected_models()
    {
        foreach (string name in new[] { "one", "two", "three" })
        {
            var archive = Archive(name);
            using var plan = new PedInstaller(game).PrepareInstall(archive, [Choice(archive)]);
            Install(plan);
        }
        var installer = new PedInstaller(game);
        using (var failed = installer.PrepareRemoveMany(["one", "two"]))
        {
            Assert.True(failed.CanInstall, string.Join("; ", failed.Errors));
            var before = failed.Changes.ToDictionary(
                c => c.RelativePath,
                c => FileTransactionEngine.HashOrNull(Path.Combine(root, c.RelativePath))
            );
            var engine = new FileTransactionEngine(root, failWork)
            {
                FaultPoint = p =>
                {
                    if (p == "after-apply:1")
                        throw new IOException("Synthetic failure");
                },
            };
            Assert.Equal(TxState.RolledBack, engine.Execute(failed.Changes).State);
            foreach (var pair in before)
                Assert.Equal(pair.Value, FileTransactionEngine.HashOrNull(Path.Combine(root, pair.Key)));
        }
        using var remove = installer.PrepareRemoveMany(["one", "two"]);
        Assert.True(remove.CanInstall, string.Join("; ", remove.Errors));
        var result = new OivInstaller(game).Install(remove, library);
        Assert.Equal(TxState.Completed, result.State);
        Assert.Equal("three", Assert.Single(installer.ListManaged().Entries).SpawnName);
        Assert.Equal(TxState.Undone, new OivInstaller(game).Undo(result.Id, library).State);
        Assert.Equal(
            new[] { "one", "three", "two" },
            installer.ListManaged().Entries.Select(e => e.SpawnName).Order().ToArray()
        );
        using var foreign = installer.PrepareRemoveMany(["one", "foreign"]);
        Assert.False(foreign.CanInstall);
        Assert.Equal(3, installer.ListManaged().Entries.Count);
    }

    [Fact]
    public void Batch_continues_after_independent_failure_and_outcomes_roundtrip_for_UI()
    {
        var first = Archive("first");
        var last = Archive("last");
        var outcomes = PedInstallBatch.Run(
            game,
            [
                new(first.ArchivePath, [Choice(first)]),
                new(Path.Combine(root, "missing.zip"), []),
                new(last.ArchivePath, [Choice(last)]),
            ],
            library
        );
        Assert.Equal(new[] { true, false, true }, outcomes.Select(o => o.Succeeded).ToArray());
        Assert.All(outcomes.Where(o => o.Succeeded), o => Assert.False(string.IsNullOrWhiteSpace(o.TransactionId)));
        Assert.Null(outcomes[1].TransactionId);
        Assert.Equal(2, new PedInstaller(game).ListManaged().Entries.Count);
        var decoded = JsonSerializer.Deserialize<List<PedInstallOutcome>>(JsonSerializer.Serialize(outcomes))!;
        Assert.Equal(outcomes, decoded);
    }

    [Fact]
    public void Unsafe_cache_ID_cannot_escape_owned_namespace()
    {
        var entry = new PedEntry
        {
            SpawnName = "bad",
            SourceArchive = "",
            PackageId = "..\\private",
        };
        var fallback = new PedPackageLibrary(game).Load([entry]);
        Assert.Empty(fallback.MediaArchivePath);
        Assert.Contains("недоступ", fallback.Readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Corrupted_cache_manifest_without_original_returns_unavailable_presentation()
    {
        var analysis = Archive("corruptedcache");
        using (var plan = new PedInstaller(game).PrepareInstall(analysis, [Choice(analysis)]))
            Install(plan);
        var model = Assert.Single(new PedInstaller(game).ListManaged().Entries);
        var cache = new PedPackageLibrary(game).Load([model]).MediaArchivePath;
        using (var zip = ZipFile.Open(cache, ZipArchiveMode.Update))
        {
            var manifest = Assert.Single(zip.Entries, e => e.FullName.EndsWith("manifest.json"));
            string path = manifest.FullName;
            manifest.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(path).Open());
            writer.Write("{\"Version\":999}");
        }
        File.Delete(analysis.ArchivePath);
        var fallback = new PedPackageLibrary(game).Load([model]);
        Assert.Empty(fallback.MediaArchivePath);
        Assert.Contains("недоступ", fallback.Readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_archive_in_middle_fails_only_its_own_outcome()
    {
        var first = Archive("validbefore");
        var last = Archive("validafter");
        string malformed = Path.Combine(root, "malformed.zip");
        File.WriteAllBytes(malformed, [0x50, 0x4b, 3, 4, 0, 0, 0]);
        var outcomes = PedInstallBatch.Run(
            game,
            [
                new(first.ArchivePath, [Choice(first)]),
                new(malformed, [new("model", "Broken", PedGender.Male, PedLayout.Normal)]),
                new(last.ArchivePath, [Choice(last)]),
            ],
            library
        );
        Assert.Equal(new[] { true, false, true }, outcomes.Select(o => o.Succeeded).ToArray());
        Assert.Equal(2, new PedInstaller(game).ListManaged().Entries.Count);
    }

    [Fact]
    public void Batch_stops_after_unrecoverable_rollback_and_marks_remaining_archives_not_started()
    {
        var first = Archive("needsrepair");
        var second = Archive("mustnotstart");
        var third = Archive("alsonotstarted");
        int executed = 0;
        var outcomes = PedInstallBatch.Run(
            game,
            [
                new(first.ArchivePath, [Choice(first)]),
                new(second.ArchivePath, [Choice(second)]),
                new(third.ArchivePath, [Choice(third)]),
            ],
            library,
            (plan, backup) =>
            {
                executed++;
                if (executed > 1)
                    return new OivInstaller(game).Install(plan, backup);
                int index = plan.Changes.FindIndex(c =>
                    c.RelativePath.StartsWith(PedPackageLibrary.CacheDirectory, StringComparison.OrdinalIgnoreCase)
                );
                Assert.True(index >= 0);
                var engine = new FileTransactionEngine(root, failWork)
                {
                    FaultPoint = point =>
                    {
                        if (point != "after-apply:" + index)
                            return;
                        File.WriteAllText(
                            Path.Combine(root, plan.Changes[index].RelativePath),
                            "External modification prevents rollback"
                        );
                        throw new IOException("Synthetic device failure");
                    },
                };
                var failed = engine.Execute(plan.Changes, plan.Title);
                Assert.Equal(TxState.NeedsIntervention, failed.State);
                return failed;
            }
        );
        Assert.Equal(1, executed);
        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.False(o.Succeeded));
        Assert.All(outcomes.Skip(1), o => Assert.Contains("не запускалось", o.Message));
        Assert.Empty(new PedInstaller(game).ListManaged().Entries);
    }

    [Fact]
    public void Existing_unrecovered_journal_blocks_a_new_batch_before_any_mutation()
    {
        var analysis = Archive("blockedbyjournal");
        string id = "ped-guard-test-" + Guid.NewGuid().ToString("N");
        string journalPath = Path.Combine(game.WorkDir, "journals", id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        try
        {
            File.WriteAllText(
                journalPath,
                JsonSerializer.Serialize(
                    new TxJournal
                    {
                        Id = id,
                        TargetRoot = game.Root,
                        StagingDir = failWork,
                        State = TxState.NeedsIntervention,
                    }
                )
            );
            int executed = 0;
            var outcomes = PedInstallBatch.Run(game, [new(analysis.ArchivePath, [Choice(analysis)])], library, (_, _) =>
            {
                executed++;
                return new("unexpected", TxState.Completed, "must not execute");
            });
            Assert.Equal(0, executed);
            Assert.Contains("не запускалось", Assert.Single(outcomes).Message);
            Assert.False(File.Exists(Path.Combine(root, PedInstaller.PackPath)));
            Assert.Empty(library.Records());
        }
        finally
        {
            File.Delete(journalPath);
        } // Only this test's exact GUID journal, never the shared directory.
    }
}
