using GTAModManager.Core.Transactions;
using Xunit;

namespace GTAModManager.Core.Tests;

public class HistoryClearTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gmm-histclear-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    [Fact]
    public void Clear_removes_journals_but_keeps_game_files()
    {
        var target = Directory.CreateDirectory(Path.Combine(_dir, "game")).FullName;
        var work = Path.Combine(_dir, "work");
        var src = Path.Combine(_dir, "mod.asi");
        File.WriteAllText(src, "mod");
        var engine = new FileTransactionEngine(target, work);
        var result = engine.Execute([new FileChange("mod.asi", null, src)], "Тестовый мод");
        Assert.Equal(TxState.Completed, result.State);
        Assert.Single(engine.History());

        Assert.Equal(1, engine.ClearHistory());
        Assert.Empty(engine.History());
        Assert.True(File.Exists(Path.Combine(target, "mod.asi")));
        Assert.False(Directory.Exists(Path.Combine(work, "tx", result.Id)));
    }
}
