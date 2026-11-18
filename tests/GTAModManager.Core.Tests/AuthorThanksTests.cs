using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using Xunit;

namespace GTAModManager.Core.Tests;

public class AuthorThanksTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gmm-thanks-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    private GameInstall Game()
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "game")).FullName;
        File.WriteAllText(Path.Combine(root, "GTA5.exe"), "x");
        return GameInstall.Open(root);
    }

    [Fact]
    public void Card_appears_only_after_the_game_was_launched_after_install()
    {
        var game = Game();
        AuthorThanks.Enabled = true; // disabled in the beta; the logic is still tested
        var thanks = new AuthorThanks(Path.Combine(_dir, "thanks.json"));
        var installed = new DateTime(2026, 11, 14, 1, 0, 0, DateTimeKind.Utc);
        thanks.Remember(
            "https://www.gta5-mods.com/scripts/n-o-s-nitro-overdrive-system/download/204263",
            "N.O.S.",
            game.Root,
            installed
        );
        thanks.Remember("https://files.gta5-mods.com/uploads/x/file.zip", "skip", game.Root, installed); // not a mod page
        thanks.Remember("https://github.com/someone/mod", "skip", game.Root, installed);

        Assert.Null(thanks.Next(game, null)); // the game has not been launched
        Assert.Null(thanks.Next(game, installed.AddMinutes(-5))); // the launch happened before installation
        var next = thanks.Next(game, installed.AddMinutes(30));
        Assert.Equal("https://www.gta5-mods.com/scripts/n-o-s-nitro-overdrive-system", next!.Url);

        thanks.Finish(next.Url);
        Assert.Null(thanks.Next(game, installed.AddHours(5)));
        thanks.Remember(
            "https://www.gta5-mods.com/scripts/n-o-s-nitro-overdrive-system",
            "N.O.S.",
            game.Root,
            installed.AddHours(1)
        );
        Assert.Null(thanks.Next(game, installed.AddHours(5))); // reinstalling does not remind again
    }

    [Fact]
    public void Last_launch_time_is_read_from_launcher_log()
    {
        string[] log =
        [
            @"[2026-11-13 17:37:42.051] [DISPLAY] [ 9436] [gamelaunch] Launch Path: D:\Games\Grand Theft Auto V Legacy\GTA5.exe",
            @"[2026-11-13 18:10:00.000] [DISPLAY] [ 9436] [gamelaunch] Launch Path: D:\Games\Grand Theft Auto V Enhanced\GTA5_Enhanced.exe",
        ];
        var t = GameReadiness.LastLaunchFromLog(log, @"D:\Games\Grand Theft Auto V Legacy");
        Assert.Equal(new DateTime(2026, 11, 13, 17, 37, 42, DateTimeKind.Local).ToUniversalTime(), t);
        Assert.Null(GameReadiness.LastLaunchFromLog(log, @"C:\Other"));
    }
}
