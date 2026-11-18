using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.NaturalVision;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class VanillaLifecycleTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("gmm-lifecycle-").FullName;
    readonly List<GameInstall> games = [];
    string States => Path.Combine(root, "states");

    GameInstall Game(string name, GameEdition edition = GameEdition.Legacy)
    {
        string path = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(Path.Combine(path, edition == GameEdition.Legacy ? "GTA5.exe" : "GTA5_Enhanced.exe"), "stub");
        var game = GameInstall.Open(path);
        game.RunningCheckOverride = () => false;
        games.Add(game);
        return game;
    }

    NveState Install(GameInstall game, BackupLibrary library)
    {
        string source = Path.Combine(root, "source.txt");
        File.WriteAllText(source, "future mod");
        using var plan = new OivInstaller(game).Prepare("Future composed mod", [new OivAdd(source, "future.asi")]);
        var result = new OivInstaller(game).Install(plan, library);
        Assert.Equal(TxState.Completed, result.State);
        var state = new NveState
        {
            GameRoot = game.Root,
            Edition = game.Edition.ToString(),
            Product = "future-composed-mod",
            BackupRoot = library.Root,
            Phase = "Installed",
            Components =
            [
                new()
                {
                    Active = true,
                    TransactionId = result.Id,
                    Component = new("core-1", "future.oiv", "future", "", "", "", 1, true, 1),
                },
            ],
        };
        new NveStore(game, States).Save(state);
        return state;
    }

    [Theory]
    [InlineData(GameEdition.Legacy)]
    [InlineData(GameEdition.Enhanced)]
    public void Vanilla_cleanup_is_product_independent_and_preserves_another_game_on_same_drive(GameEdition edition)
    {
        var library = BackupLibrary.Create(Path.Combine(root, "library"), 1L << 28);
        var first = Game("first", edition);
        var second = Game("second", edition);
        var a = Install(first, library);
        File.WriteAllText(Path.Combine(second.Root, "future.asi"), "other original");
        var b = Install(second, library);
        var result = VanillaRestore.Execute(first, VanillaRestore.Inspect(first), library, States);
        try
        {
            Assert.Empty(result.Failed);
            Assert.Empty(new NveStore(first, States).Active());
            Assert.Single(new NveStore(second, States).Active());
            Assert.True(File.Exists(Path.Combine(second.Root, "future.asi")));
            Assert.Null(library.LoadRecord(edition.ToString(), a.Components[0].TransactionId!));
            var kept = library.LoadRecord(edition.ToString(), b.Components[0].TransactionId!)!;
            Assert.NotNull(kept);
            Assert.Equal(
                "other original",
                System.Text.Encoding.UTF8.GetString(library.ReadBlob(kept.Files.Single().OldBlob!))
            );
            Assert.Empty(first.CreateEngine().History().Where(j => j.TargetRoot == first.Root));
            Assert.Single(second.CreateEngine().History().Where(j => j.TargetRoot == second.Root));
        }
        finally
        {
            VanillaRestore.DeleteQuarantine(first, result.Folder);
        }
    }

    [Fact]
    public void Disabled_storage_prevents_treating_an_empty_game_as_uninstalled()
    {
        var game = Game("disabled");
        string storage = new ModModeService(game).Inspect().StoragePath;
        Directory.CreateDirectory(storage);
        try
        {
            Assert.True(VanillaRestore.Inspect(game).Empty);
            Assert.False(VanillaModCleanup.TryComplete(game, stateDirectory: States));
        }
        finally
        {
            string full = Path.GetFullPath(storage);
            string expected =
                Path.GetFullPath(Path.Combine(game.WorkDir, "disabled-mods")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(expected, full, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(full, true);
        }
    }

    public void Dispose()
    {
        foreach (var game in games)
            game.CreateEngine().ClearHistory(discardIncomplete: true);
        Directory.Delete(root, true);
    }
}
