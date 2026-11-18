using GTAModManager.Core.Game;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class GameOperationGateTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "gmm-gate-" + Guid.NewGuid().ToString("N"));

    public GameOperationGateTests() => Directory.CreateDirectory(home);

    [Fact]
    public void Mode_switch_waits_for_the_same_game_operation_gate()
    {
        var root = Path.Combine(home, "game");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "GTA5.exe"), "test");
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        File.WriteAllText(Path.Combine(root, "mods", "test.txt"), "mod");
        File.WriteAllText(Path.Combine(root, "test.asi"), "loader");
        var game = GameInstall.Open(root);
        game.RunningCheckOverride = () => false;
        var service = new ModModeService(game, Path.Combine(home, "storage"));
        using var started = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;
        var lease = GameOperationGate.Acquire(root);
        var thread = new Thread(() =>
        {
            started.Set();
            try
            {
                service.Disable();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                finished.Set();
            }
        });
        thread.Start();
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(finished.Wait(TimeSpan.FromMilliseconds(150)));
            Assert.True(Directory.Exists(Path.Combine(root, "mods")));
        }
        finally
        {
            lease.Dispose();
        }
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        thread.Join();
        Assert.Null(failure);
        Assert.Equal(ModModeState.Disabled, service.Inspect().State);
        Assert.True(File.Exists(Path.Combine(root, "test.asi.gmm-off")));
        Assert.True(File.Exists(Path.Combine(root, "mods", "test.txt")));
    }

    [Fact]
    public void Blocked_engine_rejects_execute_undo_and_recovery_without_game_writes()
    {
        var root = Path.Combine(home, "game");
        Directory.CreateDirectory(root);
        var engine = new FileTransactionEngine(root, Path.Combine(home, "work")) { IsWriteBlocked = () => true };
        var source = Path.Combine(home, "source.txt");
        File.WriteAllText(source, "test");
        Assert.Equal(TxState.Rejected, engine.Execute([new FileChange("test.txt", null, source)]).State);
        Assert.Equal(TxState.Rejected, engine.Undo("anything").State);
        Assert.Equal(TxState.Rejected, Assert.Single(engine.Recover()).State);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    public void Dispose() => Directory.Delete(home, true);
}
