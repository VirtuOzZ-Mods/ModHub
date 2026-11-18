using System.IO;
using System.Windows;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

/// <summary>Restore Vanilla Game button: removes all mods and their traces from the selected game folder.</summary>
public partial class MainWindow
{
    private async void OnVanillaRestore(object sender, RoutedEventArgs e)
    {
        if (Current is not { } game || _isBusy)
            return;
        if (game.IsRunning())
        {
            MessageBox.Show(this, L.T("ui.vanilla.closeGtaVBeforeRestoring"), L.T("ui.common.restoreVanillaGame"));
            return;
        }
        VanillaPlan plan;
        try
        {
            plan = await Task.Run(() => VanillaRestore.Inspect(game));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                L.Owned(ex.Message),
                L.T("ui.vanilla.couldNotCheckGame"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }
        bool delete = false;
        if (!plan.Empty)
        {
            var list = plan
                .Items.OrderByDescending(i => i.Bytes)
                .Take(12)
                .Select(i =>
                    L.T(
                        "ui.vanilla.text",
                        (i.Directory ? L.T("ui.vanilla.folder") : ""),
                        i.Rel,
                        (i.Bytes >= 1 << 20 ? "  —  " + BackupLibrary.FormatSize(i.Bytes) : "")
                    )
                );
            var text = L.T(
                "ui.vanilla.allModsAllTracesMods",
                game.Edition,
                L.Join("\n", list),
                (plan.Items.Count > 12 ? L.T("ui.vanilla.more", plan.Items.Count - 12) : ""),
                plan.Items.Count,
                BackupLibrary.FormatSize(plan.Bytes),
                L.Join("\n", plan.Warnings),
                VanillaRestore.QuarantineRoot(game)
            );
            var answer = MessageBox.Show(
                this,
                text,
                L.T("ui.vanilla.restoreVanillaGame"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel
            );
            if (answer == MessageBoxResult.Cancel)
                return;
            delete = answer == MessageBoxResult.Yes;
        }

        SetBusy(true, L.T("ui.vanilla.restoringVanillaGame"), L.T("ui.vanilla.removingModsFromGameFolder"));
        var (code, output) = delete
            ? await RunGmmAsync("vanilla-restore", game.Root, "--delete")
            : await RunGmmAsync("vanilla-restore", game.Root);
        SetBusy(false);

        _readyRoots.Remove(game.Root);
        _baseSkipped.Remove(game.Root);
        RefreshHistory();
        RefreshBackupInfo();
        RefreshGameControls();
        FilesView.BaseCard.Refresh(game);
        await MyModsView.ScanAsync(game, force: true);
        DiscoverView.RefreshStatuses();
        await RefreshReadinessAsync(force: true);

        var marker = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("GMM_VANILLA:"));
        if (code != 0 || marker is null)
        {
            MessageBox.Show(
                this,
                L.T("ui.vanilla.restoreIncomplete", L.Owned(output.Trim())),
                L.T("ui.common.restoreVanillaGame"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }
        var kept = marker["GMM_VANILLA:".Length..];
        MessageBox.Show(
            this,
            L.T(
                "ui.vanilla.gtaVVanillaAgainComplete",
                game.Edition,
                (kept.Length > 0 ? L.T("ui.vanilla.backupRemovedItems", kept) : "")
            ),
            L.T("ui.common.done"),
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );
    }
}
