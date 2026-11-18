using System.Diagnostics;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Game;

/// <summary>Builds an official launcher request; never executes GTA5.exe directly.</summary>
public static class GameLauncher
{
    public static ProcessStartInfo Prepare(GameInstall game)
    {
        if (game.IsRunning())
            throw new InvalidOperationException(L.T("core.game.versionGtaVAlreadyRunning"));
        var redirector = Path.Combine(game.Root, "PlayGTAV.exe");
        if (!File.Exists(redirector))
            throw new FileNotFoundException(L.T("core.game.officialPlaygtavExeNotFound"), redirector);
        if ((File.GetAttributes(redirector) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(L.T("core.game.playgtavExeLinkVerifyInstallation"));
        return new ProcessStartInfo(redirector) { UseShellExecute = true, WorkingDirectory = game.Root };
    }
}
