using System.Windows;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

/// <summary>Close the open package and Clear History.</summary>
public partial class MainWindow
{
    /// <summary>✕ on the package card: return to the empty drop zone.</summary>
    private async void OnClosePackage(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;
        ResetPackageView();
        StatusText.Text = "";
        if (_rebuildTotal > 0)
            await ContinueRebuildAsync(); // Rebuild from list: ✕ skips the mod
    }

    private async void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (Current is not { } game || _isBusy)
            return;
        if (game.IsRunning())
        {
            MessageBox.Show(this, L.T("ui.historyTools.closeGtaVBeforeClearing"), L.T("ui.common.clearHistory"));
            return;
        }
        if (
            MessageBox.Show(
                this,
                L.T("ui.historyTools.deleteAllInstallationHistoryGta", game.Edition),
                L.T("ui.historyTools.clearHistory"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No
            ) != MessageBoxResult.Yes
        )
            return;
        SetBusy(true, L.T("ui.historyTools.clearingHistory"), L.T("ui.common.approveAdministratorAccessPrompt"));
        var (code, output) = await RunGmmAsync("history-clear", game.Root);
        SetBusy(false);
        RefreshHistory();
        RefreshBackupInfo();
        _ = MyModsView.ScanAsync(game, force: true);
        MessageBox.Show(
            this,
            output.Trim(),
            code == 0 ? L.T("ui.common.done") : L.T("ui.historyTools.historyWasNotCleared"),
            MessageBoxButton.OK,
            code == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning
        );
    }
}
