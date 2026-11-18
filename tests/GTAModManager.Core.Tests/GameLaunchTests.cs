using GTAModManager.Core.Game;

namespace GTAModManager.Core.Tests;

public sealed class GameLaunchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gmm-launch-" + Guid.NewGuid().ToString("N"));

    public GameLaunchTests() => Directory.CreateDirectory(root);

    private GameInstall Game(bool enhanced = false)
    {
        File.WriteAllText(Path.Combine(root, enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe"), "test");
        // Do not depend on whether a real GTA V is running on this machine.
        var game = GameInstall.Open(root);
        game.RunningCheckOverride = () => false;
        return game;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Uses_official_redirector_and_selected_game_directory(bool enhanced)
    {
        var game = Game(enhanced);
        File.WriteAllText(Path.Combine(root, "PlayGTAV.exe"), "test");
        var start = GameLauncher.Prepare(game);
        Assert.Equal(Path.Combine(root, "PlayGTAV.exe"), start.FileName);
        Assert.Equal(root, start.WorkingDirectory);
        Assert.True(start.UseShellExecute);
        Assert.Empty(start.ArgumentList);
    }

    [Fact]
    public void Missing_redirector_does_not_fall_back_to_direct_game_launch()
    {
        Assert.Throws<FileNotFoundException>(() => GameLauncher.Prepare(Game()));
    }

    [Fact]
    public void Running_game_blocks_duplicate_launch()
    {
        var game = Game();
        game.RunningCheckOverride = () => true;
        File.WriteAllText(Path.Combine(root, "PlayGTAV.exe"), "test");
        Assert.Throws<InvalidOperationException>(() => GameLauncher.Prepare(game));
    }

    public void Dispose() => Directory.Delete(root, true);
}
