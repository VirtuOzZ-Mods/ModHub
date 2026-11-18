using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class FileTransactionEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-tx-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(_root, "game", "mods");
    private string Work => Path.Combine(_root, "work");
    private string Src => Path.Combine(_root, "src");

    public FileTransactionEngineTests()
    {
        Directory.CreateDirectory(Target);
        Directory.CreateDirectory(Src);
        Write(Target, @"update\update.rpf", "ORIGINAL-UPDATE");
        Write(Target, @"x64\keep.txt", "KEEP");
        Write(Target, @"x64\remove.txt", "REMOVE-ME");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private static string Write(string root, string rel, string text)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    private string H(string rel) => FileTransactionEngine.HashOrNull(Path.Combine(Target, rel))!;

    private Dictionary<string, string> Snapshot() =>
        Directory
            .EnumerateFiles(Target, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Target, f), File.ReadAllText);

    private List<FileChange> ThreeChanges() =>
        [
            new(@"update\update.rpf", H(@"update\update.rpf"), Write(Src, "u", "NEW-UPDATE")),
            new(@"update\x64\dlcpacks\car\dlc.rpf", null, Write(Src, "d", "NEW-DLC")),
            new(@"x64\remove.txt", H(@"x64\remove.txt"), null),
        ];

    private FileTransactionEngine Engine(
        Action<string>? fault = null,
        Func<bool>? running = null,
        Func<long>? free = null
    ) =>
        new(Target, Work)
        {
            FaultPoint = fault ?? (_ => { }),
            IsGameRunning = running ?? (() => false),
            FreeSpace = free ?? (() => long.MaxValue),
        };

    [Fact]
    public void Commit_applies_add_replace_delete_and_completes()
    {
        var r = Engine().Execute(ThreeChanges());

        Assert.Equal(TxState.Completed, r.State);
        var s = Snapshot();
        Assert.Equal("NEW-UPDATE", s[@"update\update.rpf"]);
        Assert.Equal("NEW-DLC", s[@"update\x64\dlcpacks\car\dlc.rpf"]);
        Assert.False(s.ContainsKey(@"x64\remove.txt"));
        Assert.Equal("KEEP", s[@"x64\keep.txt"]);
    }

    [Fact]
    public void Precondition_mismatch_rejects_without_any_write() // crash before commit
    {
        var before = Snapshot();
        var changes = ThreeChanges();
        changes[0] = changes[0] with { ExpectedPreHash = "DEADBEEF" };

        var r = Engine().Execute(changes);

        Assert.Equal(TxState.Rejected, r.State);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Running_game_blocks_all_writes() // T15
    {
        var before = Snapshot();
        var r = Engine(running: () => true).Execute(ThreeChanges());
        Assert.Equal(TxState.Rejected, r.State);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Insufficient_space_rejects_before_staging() // T07
    {
        var before = Snapshot();
        var r = Engine(free: () => 1024).Execute(ThreeChanges());
        Assert.Equal(TxState.Rejected, r.State);
        Assert.Equal(before, Snapshot());
        Assert.False(Directory.Exists(Path.Combine(Work, "tx")));
    }

    [Theory]
    [InlineData(@"..\escape.txt")]
    [InlineData(@"C:\Windows\x.txt")]
    [InlineData(@"a\..\..\b.txt")]
    public void Paths_outside_target_are_rejected(string rel) // crash recovery (partial)
    {
        var r = Engine().Execute([new FileChange(rel, null, Write(Src, "x", "x"))]);
        Assert.Equal(TxState.Rejected, r.State);
    }

    public static TheoryData<string> CrashPoints() =>
        new()
        {
            "staged",
            "before-op:0",
            "started:0",
            "after-backup:0",
            "after-apply:0",
            "started:1",
            "after-backup:1",
            "after-apply:1",
            "started:2",
            "after-backup:2",
            "after-apply:2",
            "verified",
        };

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public void Crash_at_any_point_recovers_to_original_state(string point) // T05, T20
    {
        var before = Snapshot();
        var crashing = Engine(fault: p =>
        {
            if (p == point)
                throw new SimulatedCrashException(p);
        });

        Assert.Throws<SimulatedCrashException>(() => crashing.Execute(ThreeChanges()));

        var results = Engine().Recover(); // "new application launch"
        Assert.Single(results);
        Assert.Equal(TxState.RolledBack, results[0].State);
        Assert.Equal(before, Snapshot());
        Assert.Empty(Engine().Recover()); // idempotent: a second run does nothing
    }

    [Fact]
    public void External_edit_after_crash_leads_to_needs_intervention_not_overwrite()
    {
        var crashing = Engine(fault: p =>
        {
            if (p == "after-apply:0")
                throw new SimulatedCrashException(p);
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Execute(ThreeChanges()));

        // An external editor changed a file we had already replaced.
        File.WriteAllText(Path.Combine(Target, @"update\update.rpf"), "EDITED-BY-OPENIV");

        var r = Engine().Recover().Single();
        Assert.Equal(TxState.NeedsIntervention, r.State);
        Assert.Equal("EDITED-BY-OPENIV", File.ReadAllText(Path.Combine(Target, @"update\update.rpf")));
    }

    [Fact]
    public void Concurrent_change_during_commit_rolls_back_applied_ops() // crash during commit
    {
        var before = Snapshot();
        var engine = Engine(fault: p =>
        {
            if (p == "before-op:2")
                File.WriteAllText(Path.Combine(Target, @"x64\remove.txt"), "CHANGED-EXTERNALLY");
        });

        var r = engine.Execute(ThreeChanges());

        Assert.Equal(TxState.RolledBack, r.State);
        before[@"x64\remove.txt"] = "CHANGED-EXTERNALLY";
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Work_dir_inside_target_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new FileTransactionEngine(Target, Path.Combine(Target, "work")));
    }
}
