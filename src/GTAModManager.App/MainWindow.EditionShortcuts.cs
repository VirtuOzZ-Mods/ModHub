using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

public partial class MainWindow
{
    private void RefreshEditionShortcuts()
    {
        foreach (
            var (edition, play, folder) in new[]
            {
                (GameEdition.Legacy, LegacyPlayShortcut, LegacyFolderShortcut),
                (GameEdition.Enhanced, EnhancedPlayShortcut, EnhancedFolderShortcut),
            }
        )
        {
            if (play is null || folder is null)
                continue;
            var game = _games.GetValueOrDefault(edition);
            folder.IsEnabled = game is not null;
            folder.ToolTip = game is null
                ? L.T("ui.main.notFound2")
                : L.T("ui.gameControls.openGameDirectory", edition) + "\n" + game.Root;
            play.IsEnabled = false;
            play.ToolTip = game is null ? L.T("ui.main.notFound2") : L.T("ui.gameControls.play", edition);
            if (game is null || _isBusy)
                continue;
            try
            {
                var state = _gameRuntime.GetValueOrDefault(edition);
                if (state is null || state.Game.Root != game.Root || state.Error is not null)
                    continue;
                bool running = state.Running;
                bool launching = _launchPending.TryGetValue(edition, out var until) && DateTime.UtcNow < until;
                play.IsEnabled = !running && !launching && state.Mode!.State != ModModeState.Incomplete;
                if (running)
                    play.ToolTip = L.T("ui.common.gameRunning");
                else if (launching)
                    play.ToolTip = L.T("ui.gameControls.launching");
            }
            catch (Exception ex)
                when (ex
                        is IOException
                            or UnauthorizedAccessException
                            or InvalidOperationException
                            or System.Text.Json.JsonException
                )
            {
                play.ToolTip = L.Owned(ex.Message);
            }
        }
    }

    private GameInstall? ShortcutGame(object sender) =>
        sender is Button { Tag: string tag } && Enum.TryParse<GameEdition>(tag, out var edition)
            ? _games.GetValueOrDefault(edition)
            : null;

    private void OnEditionPlay(object sender, RoutedEventArgs e) => PlayGame(ShortcutGame(sender));

    private void OnEditionFolder(object sender, RoutedEventArgs e)
    {
        var game = ShortcutGame(sender);
        if (game is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(game.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                this,
                L.Owned(ex.Message),
                L.T("ui.gameControls.openGameDirectory", game.Edition),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
    }
}
