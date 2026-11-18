using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

public partial class MainWindow
{
    private readonly Dictionary<GameEdition, DateTime> _launchPending = [];

    private bool CanChangePackages(GameInstall game)
    {
        try
        {
            if (!new ModModeService(game).Inspect().WritesBlocked)
                return true;
            MessageBox.Show(
                this,
                L.T("ui.gameControls.modsDisabledSwitchingIncompleteEnable"),
                L.T("ui.gameControls.changesLocked"),
                MessageBoxButton.OK,
                MessageBoxImage.Information
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
            MessageBox.Show(
                this,
                L.Owned(ex.Message),
                L.T("ui.gameControls.couldNotCheckModMode2"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
        return false;
    }

    private void ApplyGameControls()
    {
        RefreshEditionShortcuts();
        var game = Current;
        if (game is null)
        {
            _modMode = null;
            PlayButton.IsEnabled = ModModeButton.IsEnabled = VanillaButton.IsEnabled = false;
            return;
        }
        try
        {
            var snapshot = CurrentRuntime;
            if (snapshot is null)
                return;
            if (snapshot.Error is not null)
                throw new IOException(snapshot.Error);
            _modMode = snapshot.Mode!;
            var running = snapshot.Running;
            bool launching =
                _launchPending.TryGetValue(game.Edition, out var until) && DateTime.UtcNow < until && !running;
            RunningBanner.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            ModModeText.Text = _modMode.State switch
            {
                ModModeState.Disabled => L.T("ui.gameControls.modLoadersDisabledModFolders"),
                ModModeState.Incomplete => L.T("ui.gameControls.switchingIncompleteModRecoveryRequired"),
                _ when _modMode.Candidates.Count == 0 => L.T("ui.gameControls.modLoadersNotFoundOriginal"),
                _ => L.T("ui.gameControls.modLoadersEnabled", game.Edition),
            };
            ModModeText.ToolTip = L.Join("\n", _modMode.Warnings);
            PlayButton.Content =
                running ? L.T("ui.common.gameRunning")
                : launching ? L.T("ui.gameControls.launching")
                : L.T("ui.gameControls.play", game.Edition);
            PlayButton.IsEnabled = !_isBusy && !running && !launching && _modMode.State != ModModeState.Incomplete;
            ModModeButton.Content = _modMode.State switch
            {
                ModModeState.Disabled => L.T("ui.gameControls.enableMods"),
                ModModeState.Incomplete => L.T("ui.gameControls.recoverMods"),
                _ => L.T("ui.common.disableMods"),
            };
            ModModeButton.IsEnabled =
                !_isBusy
                && !running
                && !launching
                && (_modMode.State != ModModeState.Modded || _modMode.Candidates.Count > 0);
            VanillaButton.IsEnabled = !_isBusy && !running && !launching;
            if (_modMode.WritesBlocked)
                LoaderBanner.Visibility = Visibility.Collapsed;
            else
            {
                var loader = snapshot.Loader!;
                LoaderBanner.Visibility =
                    loader.Installed && loader.DisabledReason is null ? Visibility.Collapsed : Visibility.Visible;
                LoaderBannerText.Text = loader.DisabledReason ?? loader.Message;
            }
            GameInfo.Text = L.T(
                "ui.gameControls.mods",
                game.Edition,
                (snapshot.ModsPresent ? L.T("ui.common.present") : L.T("ui.common.no")),
                (
                    _modMode.State == ModModeState.Disabled ? L.T("ui.gameControls.loadersDisabled")
                    : _modMode.State == ModModeState.Incomplete ? L.T("ui.gameControls.switchingIncomplete")
                    // A missing or switched-off loader is explained in the banner above: here only a short status.
                    : snapshot.Loader!.Installed && snapshot.Loader.DisabledReason is null ? snapshot.Loader.Message
                    : L.T("ui.main.notFound", snapshot.Loader.Name)
                )
            );
            UpdateInstallButton();
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
            _modMode = null;
            ModModeText.Text = L.T("ui.gameControls.couldNotCheckModMode", L.Owned(ex.Message));
            PlayButton.IsEnabled = ModModeButton.IsEnabled = VanillaButton.IsEnabled = InstallButton.IsEnabled = false;
        }
    }

    private async void OnToggleMods(object sender, RoutedEventArgs e)
    {
        var game = Current;
        if (game is null || _isBusy)
            return;
        try
        {
            var report = await Task.Run(() => new ModModeService(game).PrepareDisable());
            if (_runtimeClosed || _isBusy)
                return;
            bool enable = report.State != ModModeState.Modded;
            var details = enable
                ? L.T("ui.gameControls.disabledComponentsWillHaveTheir")
                : L.T(
                    "ui.gameControls.asiLoadersAsiPluginsScripthook",
                    ModModeService.DisabledSuffix,
                    report.Candidates.Count,
                    L.Join("\n", report.Candidates.Take(8).Select(p => "• " + p)),
                    (report.Candidates.Count > 8 ? L.T("ui.common.more", report.Candidates.Count - 8) : "")
                );
            if (report.Warnings.Count > 1)
                details += L.T("ui.gameControls.needsAttention", L.Join("\n", report.Warnings.Skip(1).Take(4)));
            if (
                MessageBox.Show(
                    this,
                    details,
                    enable ? L.T("ui.gameControls.enableMods2") : L.T("ui.gameControls.disableMods"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No
                ) != MessageBoxResult.Yes
            )
                return;
            SetBusy(
                true,
                enable ? L.T("ui.gameControls.enablingMods") : L.T("ui.gameControls.disablingMods"),
                L.T("ui.gameControls.restoringRenamingLoaderComponentsApprove")
            );
            var (code, output) = await RunGmmAsync(enable ? "mods-enable" : "mods-disable", game.Root);
            SetBusy(false);
            MessageBox.Show(
                this,
                output.Trim(),
                code == 0 ? L.T("ui.common.done") : L.T("ui.gameControls.switchingIncomplete2"),
                MessageBoxButton.OK,
                code == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning
            );
            RefreshGameControls();
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
            SetBusy(false);
            MessageBox.Show(
                this,
                L.Owned(ex.Message),
                L.T("ui.gameControls.couldNotSwitchMods"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
    }

    private void OnPlayGame(object sender, RoutedEventArgs e) => PlayGame(Current);

    private async void PlayGame(GameInstall? game)
    {
        if (game is null || _isBusy)
            return;
        try
        {
            if (_launchPending.TryGetValue(game.Edition, out var until) && DateTime.UtcNow < until)
                return;
            _launchPending[game.Edition] = DateTime.MaxValue;
            ApplyGameControls();
            var launch = await Task.Run(() =>
            {
                if (game.IsRunning())
                    return null;
                var mode = new ModModeService(game).Inspect();
                if (mode.State == ModModeState.Incomplete)
                    throw new InvalidOperationException(L.T("ui.gameControls.completeModRecoveryFirst"));
                return GameLauncher.Prepare(game);
            });
            if (_runtimeClosed || _isBusy || launch is null)
            {
                _launchPending.Remove(game.Edition);
                RefreshGameControls();
                return;
            }
            Process.Start(launch);
            _launchPending[game.Edition] = DateTime.UtcNow.AddSeconds(45);
            StatusText.Text = L.T("ui.gameControls.requestLaunchHasBeenSent", game.Edition);
            RefreshGameControls();
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or InvalidOperationException
                        or UnauthorizedAccessException
                        or System.ComponentModel.Win32Exception
                        or JsonException
            )
        {
            _launchPending.Remove(game.Edition);
            RefreshGameControls();
            if (_runtimeClosed)
                return;
            MessageBox.Show(
                this,
                L.Owned(ex.Message),
                L.T("ui.gameControls.couldNotLaunchGame"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
    }
}
