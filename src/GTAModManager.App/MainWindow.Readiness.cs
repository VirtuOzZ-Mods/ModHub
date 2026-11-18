using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using GTAModManager.App.Base;
using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

/// <summary>
/// First-run safety: until the selected game has the base package, mod installation is covered by a step-by-step screen
/// (first launch → BattlEye off → one-click base package). Once the package is in place, no further checks run.
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<string> _readyRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _battlEyeSkipped = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _baseSkipped = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _starterFailed = new(StringComparer.OrdinalIgnoreCase);
    private bool _readinessChecking,
        _starterRunning;

    private void InitializeReadiness()
    {
        Closed += (_, _) => _runtimeClosed = true;
        MyModsView.ScanActivityChanged += (active, text) => SetBackgroundActivity("inventory", active, text);
        Gate.ActionRequested += () =>
        {
            if (Gate.Step == GateStep.BasePackage)
                _ = InstallStarterAsync();
            else
                OnPlayGame(this, new RoutedEventArgs());
        };
        Gate.CheckRequested += () => _ = RefreshReadinessAsync(force: true);
        Gate.SkipRequested += OnGateSkip;
        MyModsView.Scanned += () =>
        {
            var r = MyModsView.Report;
            MyModsTabInfo.Text = r is null ? "" : L.T("ui.inventory.installedCount", r.Groups.Count);
            MyModsTabInfo.Visibility = r is null ? Visibility.Collapsed : Visibility.Visible;
            RefreshHistory();
        };
        MyModsView.ClearHistoryRequested += () => OnClearHistory(this, new RoutedEventArgs());
        Activated += (_, _) =>
        {
            RefreshGameControls();
            if (!_isBusy && Current is { } cur)
                _ = MyModsView.ScanAsync(cur);
        };
        Activated += (_, _) =>
        {
            RefreshAdjusterHealth();
            _ = RefreshPermissionsAsync(Current);
            _ = RefreshThanksAsync();
            if (Current is { } g && !IsReady(g))
                _ = RefreshReadinessAsync();
        };
        FilesView.Changed += () => _ = RefreshReadinessAsync(force: true);
        FilesView.BaseCard.Installed += () => _ = RefreshReadinessAsync(force: true);
    }

    /// <summary>Adapters that failed during the last game launch (incompatible with the game version): yellow banner on the left.</summary>
    private bool _healthChecking;

    private async void RefreshAdjusterHealth()
    {
        if (AdjusterBanner is null || _healthChecking || _runtimeClosed)
            return;
        var game = Current;
        if (game is null)
        {
            AdjusterBanner.Visibility = Visibility.Collapsed;
            return;
        }
        _healthChecking = true;
        try
        {
            var problems = await Task.Run(() =>
                game.IsRunning()
                    ? []
                    : AdjusterComponents
                        .Health(game)
                        .Where(h => h.State == AdjusterState.Failed)
                        .Select(h => h.Message)
                        .Concat(LimitSetup.Conflicts(game))
                        .ToList()
            );
            if (_runtimeClosed || Current?.Root != game.Root)
                return;
            AdjusterBanner.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AdjusterBannerText.Text = L.Join(Environment.NewLine + Environment.NewLine, problems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (Current?.Root == game.Root)
                AdjusterBanner.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _healthChecking = false;
            if (!_runtimeClosed && Current?.Root != game.Root)
                RefreshAdjusterHealth();
        }
    }

    private bool IsReady(GameInstall g) => _readyRoots.Contains(g.Root) || _baseSkipped.Contains(g.Root);

    /// <summary>Whether mods can be installed now; if not, shows the preparation screen.</summary>
    private bool EnsureReady()
    {
        if (Current is not { } g || IsReady(g))
            return true;
        if (InstallTab.IsChecked != true && DiscoverTab.IsChecked != true && PedsTab.IsChecked != true)
            InstallTab.IsChecked = true;
        UpdateGate();
        StatusText.Text = L.T("ui.readiness.prepareGameFirstModsWill");
        _ = RefreshReadinessAsync();
        return false;
    }

    private void UpdateGate()
    {
        if (Gate is null)
            return;
        Gate.Visibility =
            Current is { } g && !IsReady(g) && InstallPagesOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly HashSet<string> _gateIntroduced = new(StringComparer.OrdinalIgnoreCase);

    private bool InstallPagesOpen =>
        InstallTab.IsChecked == true || DiscoverTab.IsChecked == true || PedsTab.IsChecked == true;

    private Task? _readinessTask;

    /// <summary>Readiness check. If a check is already running, wait for it first (otherwise a file passed through Open With
    /// at startup would be rejected before we knew the base package is installed).</summary>
    private async Task RefreshReadinessAsync(bool force = false)
    {
        if (_readinessTask is { IsCompleted: false } running)
            await running;
        _readinessTask = RefreshReadinessCoreAsync(force);
        await _readinessTask;
    }

    private async Task RefreshReadinessCoreAsync(bool force)
    {
        var g = Current;
        if (g is null)
        {
            UpdateGate();
            return;
        }
        if (_readinessChecking || _starterRunning)
            return;
        if (!force && _readyRoots.Contains(g.Root))
        {
            UpdateGate();
            return;
        }
        _readinessChecking = true;
        try
        {
            bool skipBe = _battlEyeSkipped.Contains(g.Root);
            var (step, be, status) = await Task.Run<(GateStep, BattlEyeState, StarterStatus?)>(() =>
            {
                // Mods were turned off with Disable Mods: that is the user's choice, not a missing base package.
                if (new ModModeService(g).Inspect().WritesBlocked)
                    return (GateStep.Ready, BattlEyeState.Unknown, null);
                var s = StarterPackage.Inspect(g);
                if (s.Complete)
                    return (GateStep.Ready, BattlEyeState.Unknown, s);
                if (!GameReadiness.FirstLaunchDone(g.Edition))
                    return (GateStep.FirstLaunch, BattlEyeState.Unknown, null);
                var state = GameReadiness.BattlEye(g.Root);
                if (state != BattlEyeState.Disabled && !skipBe)
                    return (GateStep.BattlEye, state, null);
                return (GateStep.BasePackage, state, s);
            });
            if (step == GateStep.Ready)
            {
                _readyRoots.Add(g.Root);
                _starterFailed.Remove(g.Root);
            }
            else
                _readyRoots.Remove(g.Root);
            if (Current?.Root.Equals(g.Root, StringComparison.OrdinalIgnoreCase) == true && step != GateStep.Ready)
            {
                Gate.Show(g.Edition, step, be, status, _starterFailed.Contains(g.Root));
                // A game without the base package: open the preparation screen once per game, whatever page is open,
                // so the base package (and the ScriptHookVDotNet choice on Legacy) is offered right away.
                if (_gateIntroduced.Add(g.Root) && !_isBusy && !InstallPagesOpen)
                    InstallTab.IsChecked = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Gate.SetBusy(false, L.T("ui.readiness.couldNotCheckGame", L.Owned(ex.Message)));
        }
        finally
        {
            _readinessChecking = false;
            UpdateGate();
            _ = RefreshThanksAsync();
        }
    }

    private void OnGateSkip()
    {
        if (Current is not { } g)
            return;
        if (Gate.Step == GateStep.BattlEye)
        {
            if (
                MessageBox.Show(
                    this,
                    L.T("ui.readiness.ifBattleyeEnabledModsWill"),
                    "BattlEye",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No
                ) != MessageBoxResult.Yes
            )
                return;
            _battlEyeSkipped.Add(g.Root);
        }
        else if (Gate.Step == GateStep.BasePackage)
        {
            if (
                MessageBox.Show(
                    this,
                    L.T("ui.readiness.withoutBasePackageMostMods"),
                    L.T("ui.common.basePackage"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No
                ) != MessageBoxResult.Yes
            )
                return;
            _baseSkipped.Add(g.Root);
            UpdateGate();
            return;
        }
        _ = RefreshReadinessAsync(force: true);
    }

    /// <summary>Downloads everything missing and installs it with a single gmm starter-install command (one UAC prompt).</summary>
    private async Task InstallStarterAsync()
    {
        if (Current is not { } game || _starterRunning)
            return;
        if (game.IsRunning())
        {
            MessageBox.Show(this, L.T("ui.readiness.closeGtaVBeforeInstalling"), L.T("ui.common.basePackage"));
            return;
        }
        if (AppSettings.Load().OpenLibrary() is null)
        {
            MessageBox.Show(
                this,
                L.T("ui.readiness.basePackageRequiresBackupsSo"),
                L.T("ui.common.basePackage"),
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            if (!ShowBackupSetup() || AppSettings.Load().OpenLibrary() is null)
                return;
            _settings = AppSettings.Load();
        }

        _starterRunning = true;
        var folder = Path.Combine(Path.GetTempPath(), "ModHub", "starter-" + Guid.NewGuid().ToString("N")[..10]);
        var problems = new List<string>();
        bool ok = false;
        try
        {
            Gate.SetBusy(true, L.T("ui.readiness.checkingWhatAlreadyInstalled"));
            var status = await Task.Run(() => StarterPackage.Inspect(game));
            var manifest = new StarterManifest();
            using (var http = BaseDownloads.CreateClient())
            {
                async Task Try(string title, Func<Task> work)
                {
                    Gate.SetBusy(true, L.T("ui.readiness.downloading", title));
                    try
                    {
                        await work();
                    }
                    catch (Exception ex)
                        when (ex
                                is HttpRequestException
                                    or IOException
                                    or InvalidDataException
                                    or InvalidOperationException
                                    or JsonException
                                    or TaskCanceledException
                                    or UnauthorizedAccessException
                                    or ArgumentException
                        )
                    {
                        problems.Add(title + ": " + L.Owned(ex.Message));
                    }
                }
                // The mods folder loader comes from its authors' GitHub release (Mod Hub does not ship it).
                if (status.NeedsModsLoader)
                    await Try("RageOpenV", async () =>
                        manifest.RageOpenV = await BaseDownloads.DownloadRageOpenVAsync(
                            http,
                            folder,
                            CancellationToken.None
                        )
                    );
                if (!status.Base.ScriptHookV || !status.Base.AsiLoader)
                    await Try("Script Hook V", async () =>
                    {
                        var zip = await BaseDownloads.DownloadScriptHookAsync(http, folder, CancellationToken.None);
                        if (!ModdingBase.ScriptHookSupports(zip, status.Base.GameVersion))
                            throw new InvalidOperationException(
                                L.T("ui.readiness.authorHasNotReleasedVersion", status.Base.GameVersion)
                            );
                        manifest.ScriptHookV = zip;
                    });
                if (!status.Base.Shvdn)
                    await Try("ScriptHookVDotNet", async () =>
                    {
                        var variant = game.Edition == GameEdition.Enhanced ? ShvdnVariant.Enhanced : Gate.LegacyShvdn;
                        manifest.Shvdn = await BaseDownloads.DownloadShvdnAsync(
                            http,
                            variant,
                            folder,
                            CancellationToken.None
                        );
                        manifest.ShvdnVariant = variant;
                    });
                foreach (var adjuster in status.MissingAdjusters)
                    await Try(adjuster.Title, async () =>
                        manifest.Adjusters[adjuster.Id] = await BaseDownloads.DownloadAdjusterAsync(
                            http,
                            adjuster.Id,
                            folder,
                            CancellationToken.None
                        )
                    );
            }
            if (!status.GameConfig)
            {
                Gate.SetBusy(true, L.T("ui.readiness.downloadingGameconfigFromGta5Mods"));
                try
                {
                    using var site = StarterPackage.CreateSiteClient();
                    var (archive, source) = await StarterPackage.DownloadGameConfigAsync(
                        site,
                        game.Edition,
                        Path.Combine(folder, "gameconfig"),
                        CancellationToken.None
                    );
                    manifest.GameConfigArchive = archive;
                    manifest.GameConfigSource = source;
                }
                catch (Exception ex)
                    when (ex
                            is HttpRequestException
                                or IOException
                                or InvalidDataException
                                or TaskCanceledException
                                or UnauthorizedAccessException
                    )
                {
                    problems.Add("GameConfig: " + L.Owned(ex.Message));
                }
            }

            bool anything =
                manifest.RageOpenV is not null
                || manifest.ScriptHookV is not null
                || manifest.Shvdn is not null
                || manifest.Adjusters.Count > 0
                || manifest.GameConfigArchive is not null;
            if (anything)
            {
                Gate.SetBusy(true, L.T("ui.readiness.installingGtaVApproveAdministrator", game.Edition));
                SetBusy(true, L.T("ui.readiness.installingBasePackage"), L.T("ui.readiness.doNotLaunchGameUntil"));
                var path = Path.Combine(folder, "starter.json");
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, JsonSerializer.Serialize(manifest));
                var (code, output) = await RunGmmAsync("starter-install", game.Root, path);
                SetBusy(false);
                if (code != 0)
                    problems.Add(Tail(output));
            }
            ok = problems.Count == 0;
        }
        finally
        {
            SetBusy(false);
            _starterRunning = false;
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Gate.SetBusy(false, ok ? L.T("ui.readiness.basePackageInstalled") : "");
            RefreshHistory();
            RefreshBackupInfo();
            RefreshGameControls();
            FilesView.BaseCard.Refresh(Current);
        }
        await RefreshReadinessAsync(force: true);
        if (IsReady(game))
        {
            StatusText.Text = L.T("ui.readiness.basePackageInstalledDropMod");
            return;
        }
        _starterFailed.Add(game.Root);
        await RefreshReadinessAsync(force: true);
        Gate.SetBusy(false, L.T("ui.readiness.someComponentsWereNotInstalled"));
        MessageBox.Show(
            this,
            L.T(
                "ui.readiness.couldNotInstallPartBase",
                L.Join("\n\n", problems.DefaultIfEmpty(L.T("ui.readiness.unknownError")))
            ),
            L.T("ui.common.basePackage"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning
        );
    }

    private static string Tail(string output)
    {
        var lines = output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return L.Join(
            "\n",
            lines
                .Where(l =>
                    l.Contains('✗')
                    || l.StartsWith("==")
                    || l.Contains("Rejected")
                    || l.Contains("Failed")
                    || l.Contains(':')
                )
                .TakeLast(12)
        );
    }
}
