using System.IO;
using System.Text.Json;
using System.Windows;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

public partial class MainWindow
{
    private sealed record GameRuntime(
        GameInstall Game,
        bool Running,
        bool ModsPresent,
        ModModeReport? Mode,
        ModLoaderStatus? Loader,
        string? Error
    );

    private readonly Dictionary<GameEdition, GameRuntime> _gameRuntime = [];
    private readonly Dictionary<string, string> _backgroundActivities = [];
    private bool _runtimeRefreshing,
        _runtimeRefreshQueued,
        _runtimeClosed;
    private GameRuntime? CurrentRuntime =>
        Current is { } game && _gameRuntime.TryGetValue(game.Edition, out var state) && state.Game.Root == game.Root
            ? state
            : null;

    // Disk reads and the cross-process game mutex must never run on the dispatcher timer thread.
    private async void RefreshGameControls()
    {
        if (_runtimeClosed)
            return;
        if (_runtimeRefreshing)
        {
            _runtimeRefreshQueued = true;
            return;
        }
        _runtimeRefreshing = true;
        try
        {
            do
            {
                _runtimeRefreshQueued = false;
                var games = _games.Values.ToArray();
                var work = Task.Run(() =>
                    games
                        .Select(game =>
                        {
                            try
                            {
                                return new GameRuntime(
                                    game,
                                    game.IsRunning(),
                                    Directory.Exists(game.ModsDir),
                                    new ModModeService(game).Inspect(),
                                    game.CheckModLoader(),
                                    null
                                );
                            }
                            catch (Exception ex)
                                when (ex
                                        is IOException
                                            or UnauthorizedAccessException
                                            or InvalidOperationException
                                            or ArgumentException
                                            or JsonException
                                )
                            {
                                return new GameRuntime(game, false, false, null, null, L.Owned(ex.Message));
                            }
                        })
                        .ToArray()
                );
                _ = ShowSlowRefreshAsync(work);
                var states = await work;
                if (_runtimeClosed)
                    return;
                foreach (var state in states)
                    if (_games.GetValueOrDefault(state.Game.Edition)?.Root == state.Game.Root)
                        _gameRuntime[state.Game.Edition] = state;
                ApplyGameControls();
            } while (_runtimeRefreshQueued && !_runtimeClosed);
        }
        finally
        {
            _runtimeRefreshing = false;
            SetBackgroundActivity("state", false, "");
        }
    }

    private async Task ShowSlowRefreshAsync(Task work)
    {
        await Task.WhenAny(work, Task.Delay(300));
        if (!work.IsCompleted && !_runtimeClosed)
            SetBackgroundActivity("state", true, L.T("ui.background.checkingGame"));
    }

    private void SetBackgroundActivity(string key, bool active, string text)
    {
        if (_runtimeClosed || BackgroundActivity is null)
            return;
        if (active)
            _backgroundActivities[key] = text;
        else
            _backgroundActivities.Remove(key);
        BackgroundActivity.Visibility = _backgroundActivities.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BackgroundActivityText.Text = _backgroundActivities.GetValueOrDefault("inventory")
            ?? _backgroundActivities.Values.LastOrDefault()
            ?? "";
        BackgroundActivity.ToolTip = string.Join("\n", _backgroundActivities.Values);
    }
}
