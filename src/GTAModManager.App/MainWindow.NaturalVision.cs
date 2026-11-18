using System.IO;
using System.Text.Json;
using System.Windows;
using GTAModManager.App.NaturalVision;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Inventory;
using GTAModManager.Core.Localization;
using GTAModManager.Core.NaturalVision;

namespace GTAModManager.App;

public partial class MainWindow
{
    void InitializeNaturalVision()
    {
        MyModsView.NaturalVisionRequested += (state, removeAll) => _ = ManageNaturalVisionAsync(state, removeAll);
        MyModsView.LcppRepairRequested += state => _ = RepairLcppAsync(state);
        MyModsView.PedSelectorRemoveRequested += () => _ = RemovePedSelectorAsync();
        MyModsView.ModRemoveRequested += group => _ = RemoveModAsync(group);
        MyModsView.ModBackupRequested += group => _ = BackupModAsync(group);
        MyModsView.EngineSoundRequested += (carPack, sound) => _ = EngineSoundAsync(carPack, sound);
        MyModsView.ComponentRemoveRequested += (group, operations, names) =>
            _ = RemoveModAsync(group, operations, names);
    }

    /// <summary>Copies a mod's own files into &lt;backup folder&gt;\Mods Backup\&lt;mod name&gt;\.</summary>
    async Task BackupModAsync(ModGroup group)
    {
        var game = Current;
        if (game is null)
            return;
        if (string.IsNullOrWhiteSpace(_settings.BackupDir))
        {
            MessageBox.Show(
                this,
                L.T("ui.inventory.backupNoLibrary"),
                group.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            return;
        }
        try
        {
            SetBusy(true, L.T("ui.inventory.backingUp", group.DisplayName));
            var files = group.Files.Select(f => f.Rel).ToList();
            var result = await Task.Run(() =>
                ModFileBackup.Save(game, _settings.BackupDir!, group.DisplayName, files)
            );
            SetBusy(false);
            string message =
                result.Copied == 0
                    ? L.T("ui.inventory.backupNothing")
                    : L.T(
                        "ui.inventory.backupDone",
                        result.Copied,
                        Core.Library.BackupLibrary.FormatSize(result.Bytes),
                        result.Folder
                    );
            if (result.Skipped.Count > 0)
                message += "\n\n" + L.T("ui.inventory.backupSkipped", string.Join(", ", result.Skipped));
            MessageBox.Show(
                this,
                message,
                group.DisplayName,
                MessageBoxButton.OK,
                result.Copied == 0 ? MessageBoxImage.Warning : MessageBoxImage.Information
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetBusy(false);
            MessageBox.Show(this, ex.Message, group.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Removes a whole mod, or only the chosen components (<paramref name="names"/>) of a built-in package.</summary>
    async Task RemoveModAsync(ModGroup group, IReadOnlyList<string>? operations = null, string? names = null)
    {
        var game = Current;
        if (game is null || !CanChangePackages(game))
            return;
        if (
            MessageBox.Show(
                this,
                names is null
                    ? L.T("ui.inventory.removeModConfirm", group.DisplayName)
                    : L.T("ui.inventory.removeComponentConfirm", names, group.DisplayName),
                group.DisplayName,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No
            ) != MessageBoxResult.Yes
        )
            return;
        string directory = Path.Combine(Path.GetTempPath(), "ModHub");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "mod-remove-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(
                file,
                JsonSerializer.Serialize(
                    new ModRemoveRequest
                    {
                        Name = names ?? group.Name,
                        Operations = [.. operations ?? group.Operations],
                    }
                )
            );
            SetBusy(
                true,
                L.T("ui.inventory.removingMod", group.DisplayName),
                L.T("ui.common.approveAdministratorAccessPrompt")
            );
            var (_, output) = await RunGmmAsync("mod-remove", game.Root, file);
            SetBusy(false);
            RefreshHistory();
            RefreshBackupInfo();
            RefreshGameControls();
            var line = output
                .Split('\n')
                .Select(l => l.Trim())
                .LastOrDefault(l => l.StartsWith("MOD_REMOVE_RESULT:", StringComparison.Ordinal));
            var result = line is null
                ? null
                : JsonSerializer.Deserialize<ModRemoveResult>(line["MOD_REMOVE_RESULT:".Length..]);
            MessageBox.Show(
                this,
                result?.Message ?? output.Trim(),
                group.DisplayName,
                MessageBoxButton.OK,
                result?.Success == true ? MessageBoxImage.Information : MessageBoxImage.Warning
            );
            await MyModsView.ScanAsync(game, force: true);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            MessageBox.Show(this, L.Owned(ex.Message), group.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    async Task<bool> TryNaturalVisionAsync(string source, DownloadReceipt? receipt = null)
    {
        bool candidate;
        try
        {
            candidate = await Task.Run(() => NveBundle.IsCandidate(source));
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or ArgumentException
                        or InvalidOperationException
                        or Core.Oiv.OivFormatException
                        or NotSupportedException
            )
        {
            return false;
        }
        if (!candidate)
            return false;
        string productTitle = "Mod Hub";
        try
        {
            ResetPackageView();
            SetBusy(true, L.T("lcpp.reading"), L.T("ui.naturalVision.readingPackageContentsDescriptionsGame"));
            var catalog = await Task.Run(() =>
            {
                using var bundle = new NveBundle(source);
                return bundle.Analyze();
            });
            productTitle = catalog.Title;
            SetBusy(false);
            if (!_games.TryGetValue(catalog.Edition, out var game))
            {
                MessageBox.Show(
                    this,
                    L.T("ui.naturalVision.archiveGtaVSpecifyFolder", catalog.Title, catalog.Edition),
                    productTitle
                );
                return true;
            }
            (catalog.Edition == GameEdition.Legacy ? LegacyOption : EnhancedOption).IsChecked = true;
            await RefreshReadinessAsync();
            if (!EnsureReady())
                return true;
            if (new NveStore(game).Active().Any(s => s.Product == catalog.Product))
            {
                MyModsTab.IsChecked = true;
                await MyModsView.ScanAsync(game, force: true);
                MessageBox.Show(
                    this,
                    catalog.IsLcpp
                        ? L.T("lcpp.alreadyInstalled")
                        : L.T("ui.naturalVision.naturalvisionAlreadyInstalledItsInstallation")
                );
                return true;
            }
            var dialog = new NaturalVisionWindow(this, game, catalog);
            if (dialog.ShowDialog() != true || dialog.Request is null)
                return true;
            if (_settings.OpenLibrary() is null)
            {
                ShowBackupSetup();
                if (_settings.OpenLibrary() is null)
                    return true;
            }
            if (receipt is not null && !receipt.GameRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase))
                new ManagedDownloads().Retarget(receipt.Id, receipt.GameRoot, game.Root);
            dialog.Request.DownloadId = receipt?.Id;
            var result = await RunNaturalVisionAsync("nve-install", game.Root, dialog.Request, catalog.Title);
            if (result?.Success == true)
            {
                if (!catalog.IsLcpp)
                    ModLabels.Default().SetUrl(catalog.Title, "https://razedmods.com/");
                if (receipt is not null)
                    try
                    {
                        var settings = Core.Library.AppSettings.Load();
                        new ManagedDownloads().Finish(
                            receipt.Id,
                            settings.KeepDownloadedArchives,
                            settings.ArchiveLibrary
                        );
                    }
                    catch (Exception ex)
                        when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        StatusText.Text = L.T(
                            "ui.naturalVision.naturalvisionInstalledDownloadedArchiveSaved",
                            L.Owned(ex.Message)
                        );
                    }
                if (!FinishRecommendationInstall(source))
                    MyModsTab.IsChecked = true;
                await MyModsView.ScanAsync(game, force: true);
            }
            return true;
        }
        catch (Exception ex)
        {
            SetBusy(false);
            MessageBox.Show(this, L.Owned(ex.Message), productTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }
    }

    void ShowComponentProgress(NveProgress update)
    {
        if (!_isBusy)
            return;
        BusyActivity.Text = update.ActivityText;
        BusyCounter.Text = update.CounterText;
        BusyActivity.Visibility = Visibility.Visible;
        BusyCounter.Visibility = Visibility.Visible;
        BusyProgress.Maximum = update.Total;
        BusyProgress.Value = update.Completed;
        BusyProgress.IsIndeterminate = false;
    }

    async Task<NveResult?> RunNaturalVisionAsync(string verb, string root, object request, string productTitle)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ModHub");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "nve-request-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(request));
            SetBusy(true, productTitle, L.T("ui.naturalVision.largeGraphicsPackagesProcessedSequence"));
            if (request is NveInstallRequest install && install.Components.Count > 0)
            {
                BusyProgress.Maximum = install.Components.Count;
                BusyProgress.Value = 0;
                BusyProgress.IsIndeterminate = false;
                BusyCounter.Text = L.T("suite.progress.installedCount", 0, install.Components.Count);
                BusyCounter.Visibility = Visibility.Visible;
            }
            var result = await RunGmmWithOutputAsync(
                line =>
                {
                    if (NveProgress.TryParse(line, out var update) && !Dispatcher.HasShutdownStarted)
                        Dispatcher.Invoke(() => ShowComponentProgress(update));
                },
                verb,
                root,
                file
            );
            SetBusy(false);
            RefreshHistory();
            RefreshBackupInfo();
            RefreshGameControls();
            var line = result.output.Split('\n').LastOrDefault(l => l.StartsWith("NVE_RESULT:"));
            var operation = line is null ? null : JsonSerializer.Deserialize<NveResult>(line["NVE_RESULT:".Length..]);
            MessageBox.Show(
                this,
                operation?.Message ?? result.output.Trim(),
                productTitle,
                MessageBoxButton.OK,
                operation?.Success == true ? MessageBoxImage.Information : MessageBoxImage.Warning
            );
            return operation;
        }
        finally
        {
            SetBusy(false);
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    async Task RemovePedSelectorAsync()
    {
        var mode = Peds.PedSelectorRemoveDialog.Ask(this);
        if (mode == Peds.PedSelectorRemoval.Cancel)
            return;
        await LoadPedsAsync();
        await PedsView.RemoveSelectorAsync(mode);
        await MyModsView.ScanAsync(Current, force: true);
    }

    async Task RepairLcppAsync(NveState state)
    {
        try
        {
            if (!_games.Values.Any(g => g.Root.Equals(state.GameRoot, StringComparison.OrdinalIgnoreCase)))
                return;
            await RunNaturalVisionAsync(
                "nve-repair",
                state.GameRoot,
                new NveRemoveRequest { InstallationId = state.Id },
                state.ProductTitle
            );
            await MyModsView.ScanAsync(Current, force: true);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            MessageBox.Show(
                this,
                L.Owned(ex.Message),
                state.ProductTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
    }

    async Task ManageNaturalVisionAsync(NveState state, bool removeAll)
    {
        try
        {
            if (!_games.Values.Any(g => g.Root.Equals(state.GameRoot, StringComparison.OrdinalIgnoreCase)))
                return;
            NveRemoveRequest? request;
            if (removeAll)
                request = new() { InstallationId = state.Id };
            else
            {
                var window = new NaturalVisionManageWindow(this, state);
                if (window.ShowDialog() != true)
                    return;
                if (window.InstallPreservedAdjusters)
                {
                    await RunNaturalVisionAsync(
                        "nve-runtime",
                        state.GameRoot,
                        new NveRemoveRequest { InstallationId = state.Id },
                        state.ProductTitle
                    );
                    await MyModsView.ScanAsync(Current, force: true);
                    return;
                }
                request = window.Request;
            }
            if (request is null)
                return;
            string question = request.Components is null
                ? L.T("ui.naturalVision.removeAllRestoreFilesTheir", state.ProductTitle)
                : L.T("ui.naturalVision.removeSelectedOptionsOtherComponents", state.ProductTitle);
            if (
                MessageBox.Show(
                    this,
                    question,
                    state.ProductTitle + " · " + state.Edition,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                ) != MessageBoxResult.Yes
            )
                return;
            await RunNaturalVisionAsync("nve-remove", state.GameRoot, request, state.ProductTitle);
            await MyModsView.ScanAsync(Current, force: true);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            MessageBox.Show(
                this,
                L.Owned(ex.Message),
                state.ProductTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
    }
}
