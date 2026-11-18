using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Peds;
using GTAModManager.Core.Transactions;
using Microsoft.Win32;

namespace GTAModManager.App;

public sealed record PlanRow(string Action, string Target, string Detail, Brush BadgeBrush)
{
    public Visibility DetailVisibility => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
}

public sealed record HistoryRow(
    string Id,
    string Title,
    string When,
    string StateText,
    Brush StateBrush,
    string Files,
    bool CanUndo
);

public partial class MainWindow : Window
{
    private AppSettings _settings = AppSettings.Load();
    private readonly Dictionary<GameEdition, GameInstall> _games = [];
    private string? _packagePath;

    /// <summary>Where the open package was downloaded from (GTA5-Mods page), for the Support the Author card.</summary>
    private (string File, string Url, string Title)? _packageSource;

    /// <summary>gmm install command: "oiv" for an OIV package, "mod" for a regular mod archive.</summary>
    private string _installVerb = "oiv";
    private bool _planInstallable;
    private bool _isBusy;
    private ModModeReport? _modMode;
    private readonly System.Windows.Threading.DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    private static readonly Brush CreateBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84));
    private static readonly Brush ReplaceBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57));
    private static readonly Brush DeleteBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8E, 0x95, 0xA2));

    public MainWindow()
    {
        InitializeComponent();
        BuildInfo.Text = BuildLabel();
        InitializeInstallation();
        InitializeNaturalVision();
        InitializeReadiness();
        InitializeRebuild();
        // The Modding Essentials card lives on the main install page (above the package card).
        if (FilesView.BaseCard.Parent is System.Windows.Controls.Panel baseParent)
            baseParent.Children.Remove(FilesView.BaseCard);
        BaseCardHost.Content = FilesView.BaseCard;
        PedsView.BusyChanged += (busy, title) => SetBusy(busy, title, L.T("ui.main.gameMustNotRunningDuring"));
        PedsView.Changed += () =>
        {
            RefreshHistory();
            RefreshBackupInfo();
            RefreshGameControls();
        };
        Loaded += async (_, _) =>
        {
            DetectGames();
            RefreshBackupInfo();
            await RefreshReadinessAsync();
            // magic.dat (CodeWalker) is needed to decrypt game archives; on a new computer download it ahead of time in the background.
            _ = Task.Run(() =>
            {
                try
                {
                    GTAModManager.Core.Crypto.MagicDat.Ensure(GTAModManager.Core.AppPaths.MagicDat);
                }
                catch (GTAModManager.Core.Crypto.KeysNotFoundException)
                { /* retried on the first install, with a clear error */
                }
            });
            // Open With / drop onto the shortcut: the .oiv path comes in the arguments.
            var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(IsSupportedFile);
            if (arg is not null && File.Exists(arg))
                await AnalyzeAsync(arg);
            RefreshAssociationInfo();
            RefreshGameControls();
            _gameTimer.Tick += (_, _) =>
            {
                if (!_isBusy)
                    RefreshGameControls();
            };
            _gameTimer.Start();
        };
        Closed += (_, _) =>
        {
            _gameTimer.Stop();
            KeybindsView.Cancel();
            DiscoverView.Cancel();
            BrowserView.Dispose();
        };
        InitializeDiscoverInstall();
        // Recommendation card → embedded browser; without WebView2, the system browser.
        DiscoverView.OpenRequested += async uri =>
        {
            BrowserView.DownloadGameRoot = Current?.Root;
            BrowserView.OriginUrl = uri.AbsoluteUri;
            // WebView2 is created only for a visible control: show the browser first, then load the page.
            DiscoverHost.Visibility = Visibility.Collapsed;
            BrowserHost.Visibility = Visibility.Visible;
            if (!await BrowserView.OpenAsync(uri))
            {
                BrowserHost.Visibility = Visibility.Collapsed;
                DiscoverHost.Visibility = Visibility.Visible;
                Discover.BrowserView.OpenExternal(uri);
            }
        };
        // Modding essentials: downloaded from official sources in the background, installed through gmm (UAC).
        FilesView.BaseCard.RunGmm = args => RunGmmAsync(args);
        FilesView.BaseCard.Installed += () =>
        {
            OnEditionChanged(this, new RoutedEventArgs());
        };
        BrowserView.CloseRequested += () =>
        {
            BrowserHost.Visibility = Visibility.Collapsed;
            DiscoverHost.Visibility = DiscoverTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        };
        // Ask about backups only after the main window is on screen
        // (a modal dialog in Loaded prevented the main window from appearing).
        EventHandler? firstRender = null;
        firstRender = (_, _) =>
        {
            ContentRendered -= firstRender;
            Dispatcher.BeginInvoke(() =>
            {
                if (_settings.BackupChoicePending)
                    ShowBackupSetup();
            });
        };
        ContentRendered += firstRender;
    }

    private GameInstall? Current =>
        EnhancedOption.IsChecked == true ? _games.GetValueOrDefault(GameEdition.Enhanced)
        : LegacyOption.IsChecked == true ? _games.GetValueOrDefault(GameEdition.Legacy)
        : null;

    // ---------- Games ----------

    private void DetectGames()
    {
        foreach (var d in GameDetector.Detect())
            _games.TryAdd(d.Edition, GameInstall.Open(d.Root));
        foreach (
            var (path, ed) in new[]
            {
                (_settings.Legacy, GameEdition.Legacy),
                (_settings.Enhanced, GameEdition.Enhanced),
            }
        )
        {
            if (path is null)
                continue;
            try
            {
                var g = GameInstall.Open(path);
                if (g.Edition == ed)
                    _games[ed] = g;
            }
            catch (DirectoryNotFoundException) { }
        }
        RefreshGameCards();

        var preferred = _settings.LastEdition == "Legacy" ? GameEdition.Legacy : GameEdition.Enhanced;
        if (!_games.ContainsKey(preferred))
            preferred = _games.Keys.FirstOrDefault();
        (preferred == GameEdition.Legacy ? LegacyOption : EnhancedOption).IsChecked = _games.Count > 0;
        if (_games.Count == 0)
            StatusText.Text = L.T("ui.main.gtaVWasNotFound");
    }

    private void RefreshGameCards()
    {
        LegacyPath.Text = _games.TryGetValue(GameEdition.Legacy, out var l) ? l.Root : L.T("ui.main.notFound2");
        EnhancedPath.Text = _games.TryGetValue(GameEdition.Enhanced, out var e) ? e.Root : L.T("ui.main.notFound2");
        LegacyOption.IsEnabled = l is not null;
        EnhancedOption.IsEnabled = e is not null;
        RefreshEditionShortcuts();
    }

    private void OnBrowseGame(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L.T("ui.main.gtaVFolderContainingGta5") };
        if (dlg.ShowDialog(this) != true)
            return;
        try
        {
            var g = GameInstall.Open(dlg.FolderName);
            _games[g.Edition] = g;
            if (g.Edition == GameEdition.Legacy)
                _settings.Legacy = g.Root;
            else
                _settings.Enhanced = g.Root;
            SaveSettings();
            RefreshGameCards();
            (g.Edition == GameEdition.Legacy ? LegacyOption : EnhancedOption).IsChecked = true;
        }
        catch (DirectoryNotFoundException ex)
        {
            MessageBox.Show(
                this,
                L.Owned(ex.Message),
                L.T("ui.main.doesNotLookLikeGta"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
    }

    private async void OnEditionChanged(object sender, RoutedEventArgs e)
    {
        var g = Current;
        if (g is null)
            return;
        _settings.LastEdition = g.Edition.ToString();
        SaveSettings();
        var running = g.IsRunning();
        RunningBanner.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        var hasUpdate = File.Exists(Path.Combine(g.ModsDir, "update", "update.rpf"));
        var loader = g.CheckModLoader();
        LoaderBanner.Visibility =
            loader.Installed && loader.DisabledReason is null ? Visibility.Collapsed : Visibility.Visible;
        LoaderBannerText.Text = loader.DisabledReason ?? loader.Message;
        FilesView.BaseCard.Refresh(g);
        _ = RefreshReadinessAsync();
        RefreshAdjusterHealth();
        _ = RefreshPermissionsAsync(g);
        _ = MyModsView.ScanAsync(g);
        GameInfo.Text = L.T(
            "ui.main.modsModsUpdateRpf",
            g.Edition,
            (Directory.Exists(g.ModsDir) ? L.T("ui.common.present") : L.T("ui.common.no")),
            (hasUpdate ? L.T("ui.common.present") : L.T("ui.common.no")),
            (loader.Installed ? loader.Message : L.T("ui.main.notFound", loader.Name))
        );
        RefreshHistory();
        RefreshGameControls();
        FilesView.Refresh(_games.Values.ToArray(), Current);
        if (_packagePath is not null)
            await AnalyzeAsync(_packagePath);
        if (KeybindsTab.IsChecked == true)
            await LoadKeybindsAsync();
        if (PedsTab.IsChecked == true)
            await LoadPedsAsync();
        _ = RefreshThanksAsync();
    }

    private void OnPageChanged(object sender, RoutedEventArgs e)
    {
        if (InstallPage is null)
            return;
        InstallPage.Visibility = InstallTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = HistoryTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BackupPage.Visibility = BackupTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        KeybindsHost.Visibility = KeybindsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DiscoverHost.Visibility = DiscoverTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PedsHost.Visibility = PedsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MyModsHost.Visibility = MyModsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (MyModsTab.IsChecked == true)
            _ = MyModsView.ScanAsync(Current);
        BrowserHost.Visibility = Visibility.Collapsed;
        FilesHost.Visibility = Visibility.Collapsed;
        if (InstallTab.IsChecked == true)
            FilesView.Refresh(_games.Values.ToArray(), Current);
        if (DiscoverTab.IsChecked == true)
            DiscoverView.Show();
        if (HistoryTab.IsChecked == true)
            RefreshHistory();
        if (BackupTab.IsChecked == true)
            RefreshBackupInfo();
        if (KeybindsTab.IsChecked == true)
            _ = LoadKeybindsAsync();
        if (PedsTab.IsChecked == true)
            _ = LoadPedsAsync();
        UpdateGate();
    }

    /// <summary>Hotkeys of the selected game (scanned in the background; changes are written through gmm with UAC).</summary>
    private Task LoadKeybindsAsync() => KeybindsView.LoadAsync(Current, args => RunGmmAsync(args));

    private Task LoadPedsAsync() => PedsView.LoadAsync(Current, args => RunGmmAsync(args));

    private void OnClearDiscoverCache(object sender, RoutedEventArgs e)
    {
        if (
            MessageBox.Show(
                this,
                L.T("ui.main.deleteSavedRecommendationCoversStatistics"),
                L.T("ui.main.recommendationsCache"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            ) != MessageBoxResult.Yes
        )
            return;
        DiscoverView.ClearCache();
        RefreshDiscoverCacheInfo();
    }

    private void RefreshDiscoverCacheInfo()
    {
        var size = DiscoverView.CacheSize();
        DiscoverCacheInfo.Text = L.T(
            "ui.main.modCoversStatisticsRecommendationsSo",
            (size == 0 ? L.T("ui.main.empty") : L.T("ui.main.mb100MbUsed", size / 1048576.0))
        );
    }

    // ---------- Package ----------

    private async void OnPickPackage(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = L.Join(
                "|",
                L.T("filter.main.gtaVModsSettings"),
                "*.oiv;*.zip;*.rar;*.7z;*.xml;*.asi;*.ini;*.json;*.rpf",
                L.T("filter.common.allFiles"),
                "*.*"
            ),
            Title = L.T("ui.main.selectModOivPackageArchive"),
        };
        if (dlg.ShowDialog(this) == true)
            await AnalyzeAsync(dlg.FileName);
    }

    /// <summary>What the app can open: OIV packages and regular mod archives.</summary>
    public static bool IsSupportedFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant()
            is ".oiv"
                or ".zip"
                or ".rar"
                or ".7z"
                or ".xml"
                or ".asi"
                or ".ini"
                or ".json"
                or ".rpf";

    /// <summary>An OIV package or mod archive opens in the package card (Description, Controls, Changes, License tabs).</summary>
    private static bool IsPackageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".oiv" or ".zip" or ".rar" or ".7z";

    /// <summary>Special files (.xml, .asi, .ini, .json, .rpf) and browser downloads go to installation jobs.</summary>
    private async Task ShowFilesViewAsync(IEnumerable<string>? files = null)
    {
        InstallTab.IsChecked = true;
        InstallPage.Visibility = Visibility.Collapsed;
        FilesHost.Visibility = Visibility.Visible;
        FilesView.Refresh(_games.Values.ToArray(), Current);
        if (files is not null && EnsureReady())
            await FilesView.AddAsync(files);
    }

    private void OnOpenGameConfig(object sender, RoutedEventArgs e) => FilesView.OpenGameConfig();

    private void OnOpenDlcList(object sender, RoutedEventArgs e) => FilesView.OpenDlcList();

    private void OnOpenRpf(object sender, RoutedEventArgs e) => FilesView.PickRpf();

    private static bool IsModArchive(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".rar" or ".7z";

    /// <summary>
    /// An unpacked mod folder with an add-on DLC pack (a dlc.rpf somewhere inside): analyzed like a mod archive,
    /// so the pack kind is recognized and the pack is registered in dlclist.xml. Other folders keep the files view.
    /// </summary>
    private static bool IsModFolder(string path)
    {
        if (!Directory.Exists(path))
            return false;
        try
        {
            return Directory
                .EnumerateFiles(
                    path,
                    "dlc.rpf",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 8,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    }
                )
                .Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>An archive with character models (same as single-archive analysis: PED candidates and no nested OIV).</summary>
    private static bool IsPedArchive(GameInstall? g, string path)
    {
        try
        {
            if (g is not null)
            {
                using var archive = GTAModManager.Core.Mods.ModArchive.Open(path);
                if (GTAModManager.Core.Mods.ModAnalyzer.Analyze(archive, g).NestedOiv.Count > 0)
                    return false;
            }
            return PedArchiveAnalyzer.Analyze(path).Candidates.Count > 0;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or InvalidDataException
                        or ArgumentException
                        or InvalidOperationException
                        or UnauthorizedAccessException
                        or NotSupportedException
            )
        {
            return false;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects =
            e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f
            && f.All(p => IsSupportedFile(p) || Directory.Exists(p))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return;
        if (!EnsureReady())
            return;
        // The Add-On Characters section is open: everything goes to its queue.
        if (PedsTab.IsChecked == true)
        {
            await LoadPedsAsync();
            await PedsView.AddArchivesAsync(files);
            return;
        }
        if (files.Length == 1)
        {
            await AnalyzeAsync(files[0]);
            return;
        } // a single archive: the analysis detects characters itself

        // Several archives: characters go to the add-on character queue, a regular mod goes to the install page.
        var game = Current;
        var kinds = await Task.Run(() =>
            files.Select(f => (path: f, ped: IsModArchive(f) && IsPedArchive(game, f))).ToList()
        );
        var peds = kinds.Where(k => k.ped).Select(k => k.path).ToList();
        var others = kinds.Where(k => !k.ped).Select(k => k.path).ToList();
        var mods = others.Where(f => IsPackageFile(f) || IsModFolder(f)).ToList();
        var special = others.Except(mods).ToList();
        if (special.Count > 0)
            await ShowFilesViewAsync(special);
        // Several regular mods: one queue, the prevailing kind (say, cars) first, installed one after another.
        if (mods.Count > 1)
            await InstallBatchAsync(mods);
        else if (mods.Count == 1)
            await AnalyzeAsync(mods[0]);
        if (peds.Count > 0)
        {
            PedsTab.IsChecked = true;
            await LoadPedsAsync();
            await PedsView.AddArchivesAsync(peds);
        }
    }

    private async Task AnalyzeAsync(string path)
    {
        var g = Current;
        if (g is null)
        {
            StatusText.Text = L.T("ui.main.selectGameLeftFirst");
            return;
        }
        if ((IsModArchive(path) || Directory.Exists(path)) && await TryNaturalVisionAsync(path))
        {
            _packagePath = null;
            return;
        }
        if (!EnsureReady())
        {
            _packagePath = null;
            return;
        }
        bool mod = IsModArchive(path) || IsModFolder(path);
        if (mod)
        {
            var (ok, pre) = await EnsureVariantChoicesWithAnalysisAsync(g, path);
            if (!ok || !await EnsurePedGenderAsync(g, path, pre?.NeedsGender))
            {
                _packagePath = null;
                return;
            }
        }
        if (!IsPackageFile(path) && !mod)
        {
            _packagePath = null;
            await ShowFilesViewAsync([path]);
            return;
        }
        _packagePath = path;
        _installVerb = mod ? "mod" : "oiv";
        InstallTab.IsChecked = true;
        SetBusy(
            true,
            L.T("ui.main.analyzingMod"),
            mod ? L.T("ui.main.archiveReadItsContentsIdentified") : L.T("ui.main.readingAssemblyXmlCheckingAll")
        );
        try
        {
            if (mod)
            {
                var r = await Task.Run(() => AnalyzeArchive(g, path));
                if (r.Peds is not null)
                {
                    PedsTab.IsChecked = true;
                    await LoadPedsAsync();
                    await PedsView.ShowAnalysisAsync(r.Peds);
                    _packagePath = null;
                    _planInstallable = false;
                    return;
                }
                ShowPackage(g, path, r.Info, r.Icon, r.Kind);
                // The app picked the archive variant for this game itself, so say so.
                if (r.Log.Any(l => l.StartsWith("Выбран вариант для")))
                    EditionChipText.Text = L.T("ui.main.variantGtaV", g.Edition);
                ShowPlan(r.Items, r.Log, r.Errors, r.Changes);
                ShowExtrasAndRequirements(r.Extras, r.Missing);
                ShowVariantsButton(r.Variants);
            }
            else
            {
                var result = await Task.Run(() =>
                {
                    using var pkg = OivPackage.Open(path);
                    using var plan = new OivInstaller(g).Prepare(pkg, preview: true);
                    return (
                        pkg.Info,
                        Icon: pkg.TryReadIcon(),
                        plan.Items.ToList(),
                        plan.Log.ToList(),
                        plan.Errors.ToList(),
                        plan.Changes.ToList(),
                        Controls: GTAModManager.Core.Mods.PackageControls.FromOiv(pkg)
                    );
                });
                ShowPackage(g, path, result.Info, result.Icon, packageControls: result.Controls);
                ShowPlan(result.Item3, result.Item4, result.Item5, result.Item6);
                ShowExtrasAndRequirements([], []);
            }
        }
        catch (Exception ex)
            when (ex is OivFormatException or IOException or InvalidDataException or System.Xml.XmlException)
        {
            PackageCard.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            _planInstallable = false;
            StatusText.Text = L.T("ui.main.couldNotReadPackage", L.Owned(ex.Message));
        }
        finally
        {
            SetBusy(false);
            UpdateInstallButton();
        }
    }

    private sealed record ArchiveView(
        OivInfo Info,
        byte[]? Icon,
        string Kind,
        List<PlanItem> Items,
        List<string> Log,
        List<string> Errors,
        List<FileChange> Changes
    )
    {
        public PedArchiveAnalysis? Peds { get; init; }

        /// <summary>Components next to the OIV (installed after it, optionally).</summary>
        public List<GTAModManager.Core.Mods.ExtraComponent> Extras { get; init; } = [];

        /// <summary>Components required by the README that are missing in the game.</summary>
        public List<GTAModManager.Core.Installation.DependencyNeed> Missing { get; init; } = [];

        /// <summary>Number of variant groups in the archive (for the Change Variant button).</summary>
        public int Variants { get; init; }
    }

    /// <summary>Requirements from all archive documentation (README, changelog, instructions) that are missing in the game.</summary>
    private static List<GTAModManager.Core.Installation.DependencyNeed> MissingRequirements(
        GameInstall g,
        GTAModManager.Core.Mods.ModArchive archive,
        string extraDocs,
        IEnumerable<string> targets
    )
    {
        try
        {
            var docs = GTAModManager.Core.Installation.InstallationService.Documentation(archive) + "\n" + extraDocs;
            return GTAModManager
                .Core.Installation.DocumentRequirements.Inspect(g, docs, targets)
                .Where(d => d.Required && !d.Present)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// Regular mod archive: detection and plan preview. An OIV inside the archive is shown as a package;
    /// in both cases installation runs "gmm mod &lt;archive&gt;".
    /// </summary>
    private static ArchiveView AnalyzeArchive(GameInstall g, string path)
    {
        using var archive = GTAModManager.Core.Mods.ModArchive.Open(path);
        var an = GTAModManager.Core.Mods.ModAnalyzer.Analyze(archive, g);
        // Explicit OIV installation instructions retain priority over loose resources.
        if (an.NestedOiv.Count == 0)
        {
            var peds = PedArchiveAnalyzer.Analyze(archive);
            if (peds.Candidates.Count > 0)
            {
                if (an.Parts.Count > 0)
                    peds = peds with
                    {
                        Warnings = peds
                            .Warnings.Concat([L.T("ui.main.archiveContainsAdditionalComponentsSection")])
                            .ToArray(),
                    };
                return new(
                    new OivInfo(Path.GetFileName(path), "", "", "", "", "Five"),
                    null,
                    L.T("ui.common.addCharacters"),
                    [],
                    [],
                    [],
                    []
                )
                {
                    Peds = peds,
                };
            }
        }

        if (an.NestedOiv.Count == 1)
        {
            var temp = Path.Combine(Path.GetTempPath(), "ModHub", "preview-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(temp);
            try
            {
                var oiv = Path.Combine(temp, Path.GetFileName(an.NestedOiv[0]));
                using (var src = archive.OpenSource(an.NestedOiv[0]))
                using (var dst = File.Create(oiv))
                    src.CopyTo(dst);
                using var pkg = OivPackage.Open(oiv);
                using var oplan = new OivInstaller(g).Prepare(pkg, preview: true);
                return new(
                    pkg.Info,
                    pkg.TryReadIcon(),
                    L.T("ui.main.oivPackageArchive"),
                    oplan.Items.ToList(),
                    an.Notes.Concat(oplan.Log).ToList(),
                    oplan.Errors.ToList(),
                    oplan.Changes.ToList()
                )
                {
                    Extras = an.Extras.ToList(),
                    Missing = MissingRequirements(
                        g,
                        archive,
                        pkg.Info.Description,
                        oplan.Changes.Select(c => c.RelativePath)
                    ),
                    Variants = an.Variants.Count,
                };
            }
            finally
            {
                try
                {
                    Directory.Delete(temp, true);
                }
                catch (IOException) { }
            }
        }

        var description = new GTAModManager.App.Localization.LocalizedStringBuilder();
        if (an.Parts.Count > 0)
        {
            description.AppendLine(L.T("ui.main.willInstalled"));
            foreach (var p in an.Parts)
                description.AppendLine($"  • {L.Owned(p.Kind)}: {p.Source}  →  {p.Target}");
            description.AppendLine();
        }
        if (an.Description.Length > 0)
            description.Append(an.Description);
        var info = new OivInfo(an.Title, "", "", L.TrimEnd(description.ToString()), "", "Five");

        var errors = an.Errors.ToList();
        if (an.NestedOiv.Count > 1)
            errors.Add(
                L.T("ui.main.archiveContainsMultipleOivPackages", L.Join(", ", an.NestedOiv.Select(Path.GetFileName)))
            );
        if (errors.Count > 0 || an.Ops.Count == 0)
            return new(info, null, an.Summary, [], an.Notes.ToList(), errors, []) { Variants = an.Variants.Count };

        using var plan = new OivInstaller(g).Prepare(an.Title, an.Ops, archive, preview: true);
        return new(
            info,
            null,
            an.Summary,
            plan.Items.ToList(),
            an.Notes.Concat(plan.Log).ToList(),
            plan.Errors.ToList(),
            plan.Changes.ToList()
        )
        {
            Missing = MissingRequirements(g, archive, "", plan.Changes.Select(c => c.RelativePath)),
            Variants = an.Variants.Count,
        };
    }

    /// <summary>Optional components (check boxes) and author requirements missing in the game.</summary>
    private void ShowExtrasAndRequirements(
        List<GTAModManager.Core.Mods.ExtraComponent> extras,
        List<GTAModManager.Core.Installation.DependencyNeed> missing
    )
    {
        ExtrasList.Children.Clear();
        foreach (var x in extras)
        {
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
            label.Inlines.Add(
                new System.Windows.Documents.Run(Path.GetFileName(x.Source)) { FontWeight = FontWeights.SemiBold }
            );
            var optionalRun = GTAModManager.App.Localization.LiveLocalization.CreateRun(
                x.Optional ? L.T("ui.main.optional") : L.T("ui.main.recommendedByAuthor")
            );
            optionalRun.Foreground = (Brush)FindResource("Muted");
            label.Inlines.Add(optionalRun);
            if (x.Note.Length > 0)
            {
                label.Inlines.Add(new System.Windows.Documents.LineBreak());
                var noteRun = GTAModManager.App.Localization.LiveLocalization.CreateRun(x.Note);
                noteRun.Foreground = (Brush)FindResource("Muted");
                noteRun.FontSize = 12;
                label.Inlines.Add(noteRun);
            }
            ExtrasList.Children.Add(
                new CheckBox
                {
                    Content = label,
                    Tag = Path.GetFileName(x.Source),
                    IsChecked = !x.Optional,
                    Margin = new Thickness(0, 4, 0, 4),
                    Foreground = Brushes.White,
                }
            );
        }
        ExtrasCard.Visibility = extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RequirementsWarning.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RequirementsText.Text =
            missing.Count == 0
                ? ""
                : L.T("ui.main.modAuthorSReadmeRequires", L.Join(", ", missing.Select(m => m.Title)));
    }

    private void ShowPackage(
        GameInstall g,
        string path,
        OivInfo info,
        byte[]? icon,
        string? modKind = null,
        IReadOnlyList<GTAModManager.Core.Mods.ControlGroup>? packageControls = null
    )
    {
        EmptyState.Visibility = Visibility.Collapsed;
        PackageCard.Visibility = Visibility.Visible;
        DescTab.IsChecked = true;

        // Header in the colours set by the package author.
        var header = ParseColor(info.HeaderColor) ?? Color.FromRgb(0x1B, 0x1B, 0x1B);
        PackageHeader.Background = new LinearGradientBrush(header, Darken(header, 0.75), 0);
        var fg = new SolidColorBrush(info.HeaderBlackText ? Color.FromRgb(0x11, 0x11, 0x11) : Colors.White);
        foreach (
            var t in new[]
            {
                PackageName,
                PackageMeta,
                EditionChipText,
                AuthorLinkText,
                FooterLinkText,
                PackageIconFallback,
            }
        )
            t.Foreground = fg;
        PackageIconBox.Background = new SolidColorBrush(ParseColor(info.IconBackground) ?? Darken(header, 0.8));

        PackageName.Text = GTAModManager.Core.Mods.ModNames.Pretty(info.Name);
        PackageMeta.Text = L.Join(
            "   ·   ",
            new[]
            {
                info.Version.Length > 0 ? L.T("ui.main.version", info.Version) : null,
                info.Author.Length > 0 ? L.T("ui.common.author", info.Author) : null,
                info.FormatVersion.Length > 0 ? "OIV " + info.FormatVersion : null,
                modKind is not null ? L.Owned(modKind) : null,
                modKind is not null ? Path.GetFileName(path) : null,
            }.Where(x => x is not null)
        );
        PackageIconFallback.Text =
            modKind is null || modKind.StartsWith("OIV")
                ? "OIV"
                : Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        PackageIcon.Source = icon is null ? null : LoadImage(icon);
        PackageIconFallback.Visibility = PackageIcon.Source is null ? Visibility.Visible : Visibility.Collapsed;

        SetLink(AuthorLinkButton, AuthorLinkText, info.AuthorLink, L.T("ui.main.authorSPage"));
        SetLink(
            FooterLinkButton,
            FooterLinkText,
            info.FooterLink,
            L.T("ui.main.text", (info.FooterLinkTitle ?? L.T("ui.main.learnMore")))
        );

        var text = info.Description;
        if (info.LargeDescription is { } large && large != text)
            text += "\n\n" + large;
        PackageDescription.Text = text.Length > 0 ? text : L.T("ui.main.authorHasNotAddedDescription");
        ShowDescriptionLanguages(text);
        DescView.ScrollToTop();
        LicenceView.ScrollToTop();
        LicenceTab.Visibility = info.Licence is null ? Visibility.Collapsed : Visibility.Visible;

        // Controls found in the description, plus the hotkeys of the .ini files the package installs: a separate
        // tab with key caps (read-only here; they are changed after installation on the Hotkeys page).
        var controls = GTAModManager.Core.Mods.ControlsParser.Parse(text);
        if (packageControls is { Count: > 0 })
            controls = [.. controls, .. packageControls];
        ControlsTab.Visibility = controls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ControlsTab.Content =
            controls.Count > 0 ? L.T("ui.main.controls", controls.Sum(c => c.Rows.Count)) : L.T("ui.main.controls2");
        ControlsList.ItemsSource = controls.Select(ControlGroupView.From).ToList();
        ControlsView.ScrollToTop();
        LicenceText.Text = info.Licence ?? "";

        var hint = info.Name + " " + Path.GetFileName(path) + " " + info.Description;
        bool saysLegacy = hint.Contains("Legacy", StringComparison.OrdinalIgnoreCase);
        bool saysEnhanced = hint.Contains("Enhanced", StringComparison.OrdinalIgnoreCase);
        var nameHint = info.Name + " " + Path.GetFileName(path);
        bool nameLegacy = nameHint.Contains("Legacy", StringComparison.OrdinalIgnoreCase);
        bool nameEnhanced = nameHint.Contains("Enhanced", StringComparison.OrdinalIgnoreCase);
        EditionChipText.Text =
            nameEnhanced && !nameLegacy ? L.T("ui.main.gtaVEnhanced")
            : nameLegacy && !nameEnhanced ? L.T("ui.main.gtaVLegacy2")
            : L.T("ui.main.gameVersionNotSpecified");
        bool otherEdition =
            g.Edition == GameEdition.Enhanced ? nameLegacy && !nameEnhanced : nameEnhanced && !nameLegacy;
        EditionWarning.Visibility = otherEdition ? Visibility.Visible : Visibility.Collapsed;
        EditionWarningText.Text = L.T("ui.main.nameSuggestsPackageAnotherGame", g.Edition);
        _ = saysLegacy || saysEnhanced;
    }

    /// <summary>
    /// A description with English and Russian versions gets a language switch; the version matching the interface
    /// language is shown first (English for any language other than Russian).
    /// </summary>
    private void ShowDescriptionLanguages(string text)
    {
        DescLanguages.Children.Clear();
        var parts = GTAModManager.Core.Mods.DescriptionLanguages.Split(text);
        if (parts.Count < 2)
        {
            DescLanguages.Visibility = Visibility.Collapsed;
            return;
        }
        string preferred =
            GTAModManager.Core.Localization.LocalizationService.Current.Culture.TwoLetterISOLanguageName == "ru"
                ? "ru"
                : "en";
        var initial = parts.FirstOrDefault(p => p.Language == preferred) ?? parts[0];
        foreach (var part in parts)
        {
            var chip = new System.Windows.Controls.RadioButton
            {
                // Language names are shown in their own language, whatever the interface language is.
                Content = L.Owned(part.Language == "ru" ? "Русский" : "English"),
                Style = (Style)FindResource("UnitChip"),
                GroupName = "descLanguage",
                IsChecked = part == initial,
            };
            chip.Checked += (_, _) =>
            {
                PackageDescription.Text = part.Text;
                DescView.ScrollToTop();
            };
            DescLanguages.Children.Add(chip);
        }
        PackageDescription.Text = initial.Text;
        DescLanguages.Visibility = Visibility.Visible;
    }

    private static void SetLink(
        System.Windows.Controls.Button button,
        System.Windows.Controls.TextBlock label,
        string? url,
        string title
    )
    {
        button.Visibility = url is null ? Visibility.Collapsed : Visibility.Visible;
        button.Tag = url;
        button.ToolTip = url;
        label.Text = title;
    }

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (
            sender is FrameworkElement { Tag: string url }
            && Uri.TryCreate(url, UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
        )
            Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true });
    }

    private void OnPackageTabChanged(object sender, RoutedEventArgs e)
    {
        if (DescView is null)
            return;
        DescView.Visibility = DescTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ControlsView.Visibility = ControlsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ChangesView.Visibility = ChangesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LicenceView.Visibility = LicenceTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Color? ParseColor(string? hex)
    {
        try
        {
            return hex is null ? null : (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Color Darken(Color c, double k) => Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));

    // ---------- Window chrome ----------

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // A maximized window without the system frame extends past the screen edge by the frame width; compensate.
        RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? L.T("ui.main.restore") : L.T("ui.main.expand");
    }

    private void ShowPlan(List<PlanItem> items, List<string> log, List<string> errors, List<FileChange> changes)
    {
        // Analyzer and installer messages are stored in Russian; show them in the selected language.
        PlanErrors.ItemsSource = errors.Select(L.Owned).ToList();
        ChangesTab.Content =
            errors.Count > 0 ? L.T("ui.main.changesErrors", errors.Count) : L.T("ui.main.changes", changes.Count);
        if (errors.Count > 0)
            ChangesTab.IsChecked = true;

        var rows = new List<PlanRow>();
        foreach (
            var c in changes
                .OrderBy(c =>
                    c.SourcePath is null ? 2
                    : c.ExpectedPreHash is null ? 0
                    : 1
                )
                .ThenBy(c => c.RelativePath)
        )
        {
            var (action, brush) =
                c.SourcePath is null ? (L.T("ui.main.delete"), DeleteBrush)
                : c.ExpectedPreHash is null ? (L.T("ui.main.create"), CreateBrush)
                : (L.T("ui.main.replace"), ReplaceBrush);
            var inner = items
                .Where(i => i.Target.StartsWith(c.RelativePath + "\\", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var detail =
                inner.Count > 0
                    ? L.T(
                        "ui.main.insideArchive",
                        L.Join(", ", inner.GroupBy(i => i.Action).Select(gr => $"{Verb(gr.Key)} {gr.Count()}"))
                    )
                    : "";
            if (
                log.Any(l =>
                    l.StartsWith(
                        GameInstall.Normalize(
                            c.RelativePath.StartsWith("mods\\") ? c.RelativePath[5..] : c.RelativePath
                        ) + ": копия оригинала"
                    )
                )
            )
                detail = L.T("ui.main.copyOriginalGameArchiveMods", detail);
            rows.Add(new PlanRow(action, c.RelativePath, detail.TrimEnd(' ', ';'), brush));
        }
        int skipped = log.Count(l => l.EndsWith("не найден (пропуск)"));
        foreach (var l in log.Where(l => !l.EndsWith("не найден (пропуск)") && !l.Contains("копия оригинала")))
            rows.Add(new PlanRow(L.T("ui.main.info"), L.Owned(l), "", MutedBrush));
        if (skipped > 0)
            rows.Add(
                new PlanRow(L.T("ui.main.info"), L.T("ui.main.removalSkippedFilesMissing", skipped), "", MutedBrush)
            );
        PlanList.ItemsSource = rows;

        int create = changes.Count(c => c.SourcePath is not null && c.ExpectedPreHash is null);
        int replace = changes.Count(c => c.SourcePath is not null && c.ExpectedPreHash is not null);
        int delete = changes.Count(c => c.SourcePath is null);
        var backupNote =
            _settings.BackupsDeclined ? L.T("ui.main.backupsDisabledApplyingPackageCannot")
            : _settings.BackupDir is null ? L.T("ui.main.beforeApplyingDecideWhetherSave")
            : L.T("ui.main.backupOnlyFilesPackageWill");
        PlanSummary.Text =
            errors.Count > 0 ? L.T("ui.main.packageCannotAppliedSeeReasons")
            : changes.Count == 0 ? L.T("ui.main.noChangesPackageActionsHave")
            : L.T("ui.main.gameFilesCreateReplaceDelete", create, replace, delete, backupNote);

        _planInstallable = errors.Count == 0 && changes.Count > 0;
        StatusText.Text = _planInstallable ? L.T("ui.main.readyApplyPackageAdministratorAccess") : "";
    }

    private static string Verb(string action) =>
        action switch
        {
            "add" => L.T("ui.main.add"),
            "delete" => L.T("ui.main.delete"),
            "xml" => L.T("ui.main.xmlEdits"),
            "text" => L.T("ui.main.textEdits"),
            _ => action,
        };

    private void UpdateInstallButton()
    {
        var g = Current;
        InstallButton.IsEnabled =
            !_isBusy
            && _planInstallable
            && g is not null
            && CurrentRuntime is { Running: false, Error: null }
            && _modMode is { WritesBlocked: false };
    }

    // ---------- Install and undo (through gmm.exe with UAC) ----------

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        var g = Current;
        if (g is null || _packagePath is null)
            return;
        if (!CanChangePackages(g))
            return;
        if (g.IsRunning())
        {
            RunningBanner.Visibility = Visibility.Visible;
            UpdateInstallButton();
            return;
        }
        if (_settings.BackupChoicePending && !ShowBackupSetup())
            return;
        if (_settings.BackupDir is not null && _settings.OpenLibrary() is null)
        {
            MessageBox.Show(
                this,
                L.T("ui.main.backupFolderUnavailableConnectDrive", _settings.BackupDir),
                L.T("ui.common.backups"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        _ = RefreshPermissionsAsync(g); // permissions are lost after reinstalling the game or verifying files
        var installStarted = DateTime.UtcNow;
        SetBusy(true, L.T("ui.main.applyingPackage"), L.T("ui.main.approveAdministratorPermissionsPromptArchives"));
        var extras = ExtrasList
            .Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (string)c.Tag)
            .ToList();
        var (code, output) =
            _installVerb == "mod" && extras.Count > 0
                ? await RunGmmAsync(_installVerb, _packagePath, g.Root, "--extras", L.Join(";", extras))
                : await RunGmmAsync(_installVerb, _packagePath, g.Root);
        SetBusy(false);

        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        var result = last.LastOrDefault(l =>
            l.StartsWith("Completed")
            || l.StartsWith("RolledBack")
            || l.StartsWith("Rejected")
            || l.StartsWith("NeedsIntervention")
            || l.Contains("UAC отклонён")
            || l.StartsWith("Ошибка")
        )
            ?? last.LastOrDefault()
            ?? "";
        RefreshHistory();
        RefreshBackupInfo();
        if (code == 0)
        {
            var name = PackageName.Text;
            if (_packageSource is { } src && src.File.Equals(_packagePath, StringComparison.OrdinalIgnoreCase))
            {
                new GTAModManager.Core.Discover.AuthorThanks().Remember(src.Url, src.Title, g.Root);
                // Link to the mod page, for My Mods and the mod list (Rebuild).
                GTAModManager
                    .Core.Inventory.ModLabels.Default()
                    .SetUrl(name, GTAModManager.Core.Discover.AuthorThanks.ModPage(src.Url));
                // A mod from Recommendations: Installed status and update tracking.
                RecordCatalogInstall(g, null, src.Url, [], installStarted);
            }
            MessageBox.Show(
                this,
                L.T("ui.main.packageApplied", result),
                L.T("ui.common.done"),
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            var installedSource = _packagePath;
            ResetPackageView();
            StatusText.Text = L.T("ui.main.packageAppliedCanUndoHistory", name);
            if (_rebuildTotal > 0)
                await ContinueRebuildAsync();
            else
                FinishRecommendationInstall(installedSource);
        }
        else
        {
            StatusText.Text = L.T("ui.main.couldNotApplyPackage", result);
            MessageBox.Show(
                this,
                L.T("ui.main.packageWasNotAppliedGame", L.Owned(output.Trim())),
                L.T("ui.common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            await AnalyzeAsync(_packagePath);
        }
    }

    /// <summary>Back to the empty drop zone.</summary>
    private void ResetPackageView()
    {
        _packagePath = null;
        _planInstallable = false;
        PackageCard.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        PlanList.ItemsSource = null;
        PlanErrors.ItemsSource = null;
        UpdateInstallButton();
    }

    /// <summary>A package passed from Explorer (double-click / Open With), including one received by a second app instance.</summary>
    public async Task OpenPackageFromShellAsync(string path)
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false; // bring to the front
        if (BusyOverlay.Visibility == Visibility.Visible)
        {
            StatusText.Text = L.T("ui.main.waitCurrentOperationFinish");
            return;
        }
        InstallTab.IsChecked = true;
        if (File.Exists(path))
            await AnalyzeAsync(path);
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        var g = Current;
        if (g is null || sender is not FrameworkElement { Tag: string id })
            return;
        if (!CanChangePackages(g))
            return;
        var suite = new Core.NaturalVision.NveStore(g)
            .Active()
            .FirstOrDefault(s => s.Components.Any(c => c.Active && c.TransactionId == id));
        if (suite is not null)
        {
            await ManageNaturalVisionAsync(suite, false);
            return;
        }
        string description;
        try
        {
            var record = _settings.OpenLibrary()?.LoadRecord(g.Edition.ToString(), id);
            if (record is not null)
                description = UndoDescription.ForRecord(record);
            else
            {
                var journal = g.CreateEngine().History().FirstOrDefault(j => j.Id == id);
                if (journal is null)
                {
                    HistoryStatus.Text = L.T("ui.main.actionRecordNotFoundRefresh");
                    return;
                }
                description = UndoDescription.ForJournal(journal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            HistoryStatus.Text = L.T("ui.main.couldNotReadUndoDetails", L.Owned(ex.Message));
            return;
        }
        if (
            MessageBox.Show(
                this,
                description,
                L.T("ui.main.undoPackageAction"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No
            ) != MessageBoxResult.Yes
        )
            return;
        SetBusy(true, L.T("ui.main.undoingPackageAction"), L.T("ui.common.approveAdministratorAccessPrompt"));
        var (code, output) = await RunGmmAsync("undo", g.Root, id);
        SetBusy(false);
        RefreshHistory();
        RefreshBackupInfo();
        HistoryStatus.Text =
            code == 0
                ? L.T("ui.main.applyingSelectedPackageWasCanceled")
                : output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
    }

    private static Task<(int code, string output)> RunGmmAsync(params string[] args) =>
        RunGmmWithOutputAsync(null, args);

    private static async Task<(int code, string output)> RunGmmWithOutputAsync(
        Action<string>? onLine,
        params string[] args
    )
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "gmm.exe");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        async Task<string> ReadOutputAsync()
        {
            var output = new StringBuilder();
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                output.AppendLine(line);
                onLine?.Invoke(line);
            }
            return output.ToString();
        }
        var stdout = ReadOutputAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout + await stderr);
    }

    private bool _historyRefreshing,
        _historyQueued;

    private async void RefreshHistory()
    {
        if (_runtimeClosed)
            return;
        if (_historyRefreshing)
        {
            _historyQueued = true;
            return;
        }
        var g = Current;
        if (g is null)
        {
            HistoryList.ItemsSource = null;
            return;
        }
        _historyRefreshing = true;
        try
        {
            var backupDir = _settings.BackupDir;
            var frozen = MyModsView.Frozen.ToHashSet();
            var data = await Task.Run(() =>
            {
                var lib = backupDir is null ? null : BackupLibrary.TryOpen(backupDir);

                var records = lib?.Records(g.Edition.ToString()).ToDictionary(r => r.Id) ?? [];
                var journals = g.CreateEngine()
                    .History()
                    .Where(j => j.TargetRoot.Equals(g.Root, StringComparison.OrdinalIgnoreCase))
                    .Where(j => j.Title is null || !j.Title.StartsWith("Отмена: ")) // internal undo transactions are not shown
                    .Reverse()
                    .ToList();
                var backupSizes = records.ToDictionary(p => p.Key, p => p.Value.Undone ? 0 : p.Value.BackupBytes(lib!));
                return (records, journals, backupSizes);
            });
            if (_runtimeClosed)
                return;
            if (Current?.Root != g.Root)
            {
                _historyQueued = true;
                return;
            }
            var (records, journals, backupSizes) = data;
            HistoryList.ItemsSource = journals
                .Select(j =>
                {
                    var when = L.T("format.datetime.short", j.Updated.ToLocalTime());
                    if (records.TryGetValue(j.Id, out var rec))
                        return frozen.Contains(j.Id) && !rec.Undone
                            ? new HistoryRow(
                                j.Id,
                                Core.Mods.ModNames.DisplayOperationTitle(rec.Title),
                                when,
                                L.T("ui.main.filesChangedOutsideModHub"),
                                DeleteBrush,
                                L.T("ui.main.fileS", j.Ops.Count),
                                false
                            )
                            : new HistoryRow(
                                j.Id,
                                Core.Mods.ModNames.DisplayOperationTitle(rec.Title),
                                when,
                                rec.Undone ? L.T("ui.main.applicationCanceled") : L.T("ui.main.applied"),
                                rec.Undone ? MutedBrush : CreateBrush,
                                rec.Undone
                                    ? L.T("ui.main.fileS", j.Ops.Count)
                                    : L.T(
                                        "ui.main.fileSBackup",
                                        j.Ops.Count,
                                        BackupLibrary.FormatSize(backupSizes[rec.Id])
                                    ),
                                !rec.Undone
                                    && !rec.Title.StartsWith(
                                        Core.NaturalVision.NveBundle.LcppTitle,
                                        StringComparison.Ordinal
                                    )
                            );
                    bool noBackup = j.State == TxState.Completed && j.BackupsDiscarded;
                    if (frozen.Contains(j.Id) && j.State == TxState.Completed)
                        return new HistoryRow(
                            j.Id,
                            Core.Mods.ModNames.DisplayOperationTitle(j.Title ?? L.T("ui.main.untitled")),
                            when,
                            L.T("ui.main.filesChangedOutsideModHub"),
                            DeleteBrush,
                            L.T("ui.main.fileS", j.Ops.Count),
                            false
                        );
                    return new HistoryRow(
                        j.Id,
                        Core.Mods.ModNames.DisplayOperationTitle(j.Title ?? L.T("ui.main.untitled")),
                        when,
                        j.State switch
                        {
                            TxState.Completed when noBackup => L.T("ui.main.appliedWithoutBackup"),
                            TxState.Completed => L.T("ui.main.applied"),
                            TxState.Undone => L.T("ui.main.applicationCanceled"),
                            TxState.RolledBack => L.T("ui.main.notAppliedChangesRolledBack"),
                            TxState.NeedsIntervention => L.T("ui.main.needsChecking"),
                            _ => j.State.ToString(),
                        },
                        j.State switch
                        {
                            TxState.Completed => CreateBrush,
                            TxState.NeedsIntervention => DeleteBrush,
                            _ => MutedBrush,
                        },
                        L.T("ui.main.fileS", j.Ops.Count),
                        j.State == TxState.Completed && !noBackup
                    );
                })
                .ToList();
            HistoryStatus.Text = journals.Count == 0 ? L.T("ui.main.noPackageActionsHaveBeen") : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            HistoryStatus.Text = L.T("ui.common.couldNotReadHistory", L.Owned(ex.Message));
        }
        finally
        {
            _historyRefreshing = false;
            if (_historyQueued && !_runtimeClosed)
            {
                _historyQueued = false;
                RefreshHistory();
            }
        }
    }

    // ---------- Utilities ----------

    private void SetBusy(bool busy, string title = "", string text = "")
    {
        _isBusy = busy;
        RefreshEditionShortcuts();
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyTitle.Text = title;
        BusyText.Text = text;
        BusyProgress.IsIndeterminate = true;
        BusyProgress.Maximum = 1;
        BusyProgress.Value = 0;
        BusyActivity.Text = "";
        BusyCounter.Text = "";
        BusyActivity.Visibility = Visibility.Collapsed;
        BusyCounter.Visibility = Visibility.Collapsed;
        if (PlayButton is not null)
        {
            PlayButton.IsEnabled = false;
            ModModeButton.IsEnabled = false;
            VanillaButton.IsEnabled = false;
        }
        if (!busy)
            RefreshGameControls();
    }

    private static BitmapImage? LoadImage(byte[] data)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(data);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SaveSettings()
    {
        try
        {
            var recent = AppSettings.Load();
            _settings.KeepDownloadedArchives = recent.KeepDownloadedArchives;
            _settings.ArchiveLibrary = recent.ArchiveLibrary;
            _settings.LanguagePackId = recent.LanguagePackId;
            _settings.Save();
        }
        catch (IOException) { }
    }

    // ---------- Backups ----------

    /// <summary>Folder/limit dialog. Returns true if a choice was made (enabled or disabled).</summary>
    private bool ShowBackupSetup()
    {
        _settings = AppSettings.Load();
        var dlg = new BackupSetupWindow(_settings, _games.Values.Select(g => g.Root).ToList()) { Owner = this };
        var ok = dlg.ShowDialog() == true;
        _settings = AppSettings.Load();
        RefreshBackupInfo();
        if (_packagePath is not null && PlanList.ItemsSource is not null)
            StatusText.Text = ok ? StatusText.Text : L.T("ui.main.backupDecisionHasBeenDeferred");
        return ok;
    }

    private void OnConfigureBackups(object sender, RoutedEventArgs e) => ShowBackupSetup();

    // ---------- .oiv association ----------

    private void RefreshAssociationInfo()
    {
        var state = FileAssociation.Check();
        AssociationInfo.Text = state switch
        {
            FileAssociation.State.Default => L.T("ui.main.doubleClickingOivOpensPackage"),
            FileAssociation.State.Registered => L.T("ui.main.appListedOpenOpenOiv"),
            _ => L.T("ui.main.oivFilesCurrentlyOpenAnother"),
        };
        AssociateButton.Visibility =
            state == FileAssociation.State.NotRegistered ? Visibility.Visible : Visibility.Collapsed;
        UnassociateButton.Visibility =
            state == FileAssociation.State.NotRegistered ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAssociate(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociation.Register();
            RefreshAssociationInfo();
            if (FileAssociation.Check() == FileAssociation.State.Registered)
                MessageBox.Show(
                    this,
                    L.T("ui.main.modhubHasBeenAddedOpen"),
                    L.T("ui.main.openingOivFiles"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
        }
        catch (Exception ex)
            when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show(
                this,
                L.T("ui.main.couldNotRegisterOiv", L.Owned(ex.Message)),
                L.T("ui.main.openingOivFiles"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
    }

    private void OnUnassociate(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociation.Unregister();
        }
        catch (Exception ex)
            when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        RefreshAssociationInfo();
    }

    private void OnOpenBackupFolder(object sender, RoutedEventArgs e)
    {
        if (_settings.BackupDir is { } dir && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void RefreshBackupInfo()
    {
        RefreshDiscoverCacheInfo();
        var lib = _settings.OpenLibrary();
        if (lib is null)
        {
            BackupStatus.Text =
                _settings.BackupsDeclined ? L.T("ui.main.backupsDisabled")
                : _settings.BackupDir is null ? L.T("ui.main.noFolderSelected")
                : L.T("ui.main.folderUnavailable");
            BackupPath.Text = _settings.BackupsDeclined
                ? L.T("ui.main.packagesAppliedWithoutBackupsActions")
                : _settings.BackupDir ?? L.T("ui.main.clickConfigureSelectFolderLimit");
            BackupTabInfo.Text = _settings.BackupsDeclined ? L.T("ui.main.disabled") : L.T("ui.main.notConfigured");
            BackupUsagePanel.Visibility = Visibility.Collapsed;
            OpenBackupFolderButton.Visibility = Visibility.Collapsed;
            return;
        }
        long used = lib.UsedBytes();
        double share = lib.QuotaBytes > 0 ? Math.Min(1, used / (double)lib.QuotaBytes) : 0;
        int active = lib.Records().Count(r => !r.Undone);
        BackupStatus.Text = L.T("ui.main.backupsEnabled");
        BackupPath.Text = lib.Root;
        BackupUsage.Text = L.T(
            "ui.main.usedActionsBackups",
            BackupLibrary.FormatSize(used),
            BackupLibrary.FormatSize(lib.QuotaBytes),
            active
        );
        UsedColumn.Width = new GridLength(share, GridUnitType.Star);
        FreeColumn.Width = new GridLength(1 - share, GridUnitType.Star);
        BackupTabInfo.Text = L.T(
            "ui.main.countOfTotal",
            BackupLibrary.FormatSize(used),
            BackupLibrary.FormatSize(lib.QuotaBytes)
        );
        VanillaSize.Text = BackupLibrary.FormatSize(lib.UsedBytes(BackupOrigin.Vanilla));
        ModsSize.Text = BackupLibrary.FormatSize(lib.UsedBytes(BackupOrigin.Mods));
        BackupUsagePanel.Visibility = Visibility.Visible;
        OpenBackupFolderButton.Visibility = Visibility.Visible;
    }
}
