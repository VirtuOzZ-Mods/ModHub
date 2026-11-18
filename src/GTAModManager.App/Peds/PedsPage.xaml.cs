using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GTAModManager.Core;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Peds;
using Microsoft.Win32;

namespace GTAModManager.App.Peds;

public sealed class InstalledPedView(PedEntry entry) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public PedEntry Entry { get; } = entry;
    private bool selected;
    public bool Selected
    {
        get => selected;
        set
        {
            selected = Entry.Managed && value;
            PropertyChanged?.Invoke(this, new(nameof(Selected)));
        }
    }
    public string Label =>
        Entry.DisplayName == Entry.SpawnName
            ? $"{Entry.DisplayName} · {Entry.Layout}"
            : $"{Entry.DisplayName} ({Entry.SpawnName}) · {Entry.Layout}";
}

public sealed record InstalledPackageView(string Title, IReadOnlyList<InstalledPedView> Models)
{
    public string Summary => L.T("ui.peds.modelsClickDetails", Models.Count);
}

public partial class PedsPage : UserControl
{
    private const string BatchPrefix = "GMM_PEDS_BATCH_RESULT:";
    private readonly PedInstallQueue queue = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<PedCardView> cards = [];
    private GameInstall? game;
    private Func<string[], Task<(int code, string output)>>? run;
    private List<InstalledPedView> installed = [];
    private bool busy,
        selectingAll;
    private int generation;
    public event Action<bool, string>? BusyChanged;
    public event Action? Changed;

    public PedsPage()
    {
        InitializeComponent();
        CardList.ItemsSource = cards;
        ((INotifyCollectionChanged)queue.Items).CollectionChanged += (_, _) =>
        {
            RebuildCards();
            RefreshQueue();
        };
    }

    public async Task LoadAsync(GameInstall? target, Func<string[], Task<(int code, string output)>> runner)
    {
        if (game?.Root != target?.Root)
        {
            queue.Clear();
            installed = [];
            OperationDetails.Visibility = Visibility.Collapsed;
        }
        game = target;
        run = runner;
        int version = ++generation;
        LegacyContent.Visibility = target?.Edition == GameEdition.Legacy ? Visibility.Visible : Visibility.Collapsed;
        if (target?.Edition != GameEdition.Legacy)
        {
            Status.Text = L.T("ui.peds.addCharactersCurrentlySupportedOnly");
            return;
        }
        Status.Text = L.T("ui.peds.updatingCharacterList");
        try
        {
            var result = await Task.Run(() =>
            {
                var scan = new PedDiscoveryService(target).Scan();
                var installer = new PedInstaller(target);
                return (
                    scan,
                    catalog: installer.ListManaged(),
                    selector: new PedSelectorComponent(target).Inspect(),
                    packUpdate: installer.NeedsPackUpdate(),
                    legacy: File.Exists(Path.Combine(target.Root, PedInstaller.LegacyPackPath))
                );
            });
            if (version != generation)
                return;
            var ownNames = result.catalog.Entries.Select(e => e.SpawnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            installed = result
                .catalog.Entries.Concat(result.scan.Entries.Where(e => !ownNames.Contains(e.SpawnName)))
                .Select(e => new InstalledPedView(e))
                .ToList();
            foreach (var model in installed)
                model.PropertyChanged += (_, _) => RefreshSelection();
            var groups = installed
                .GroupBy(v => PackageKey(v.Entry), StringComparer.OrdinalIgnoreCase)
                .Select(g => new InstalledPackageView(PackageTitle(g.First().Entry), g.ToArray()))
                .ToList();
            Installed.ItemsSource = groups;
            InstalledTitle.Text = L.T("ui.peds.characterCollection2", installed.Count);
            CollectionTab.Content =
                installed.Count == 0
                    ? L.T("ui.peds.characterCollection3")
                    : L.T("ui.peds.characterCollection", installed.Count);
            EmptyInstalled.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            selectingAll = true;
            SelectAll.IsChecked = false;
            selectingAll = false;
            SelectAll.Visibility = installed.Any(m => m.Entry.Managed) ? Visibility.Visible : Visibility.Collapsed;
            RefreshSelection();
            Status.Text =
                installed.Count == 0
                    ? L.T("ui.peds.addCharacterArchiveGetStarted")
                    : L.T("ui.peds.modelsInstalledClickPackageName", installed.Count);
            ScanWarnings.Visibility = result.scan.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ScanWarnings.Header = L.T("ui.peds.characterPackageIssues", result.scan.Warnings.Count);
            ScanWarningsText.Text = L.Join("\n", result.scan.Warnings);
            ShowSelectorState(result.selector);
            ShowPackUpdate(result.packUpdate, result.legacy);
            RefreshQueue();
        }
        catch (Exception ex)
        {
            if (version == generation)
                Status.Text = L.T("ui.peds.couldNotUpdateList", L.Owned(ex.Message));
        }
    }

    /// <summary>
    /// In-game menu: one state and one main button. No menu → Install Menu; ours and up to date → Remove Menu;
    /// ours but ModHub has a newer one → Update Menu (+ Remove); a third-party menu gets no buttons.
    /// </summary>
    private void ShowSelectorState(PedSelectorStatus s)
    {
        bool outdated =
            s.InstalledOwn
            && game is not null
            && !SameFile(Path.Combine(game.Root, "GMMPedSelector.asi"), BundledSelector);
        var keys = L.T("ui.peds.selectorControls");
        if (s.InstalledThirdParty && !s.InstalledOwn)
        {
            SelectorTitle.Text = L.T("ui.peds.usingThirdPartyCharacterMenu");
            SelectorText.Text = L.T("ui.peds.ourMenuWillNotOverwrite");
            SelectorMainButton.Visibility = Visibility.Collapsed;
            SelectorRemoveButton.Visibility = Visibility.Collapsed;
        }
        else if (!s.InstalledOwn)
        {
            SelectorTitle.Text = L.T("ui.peds.gameMenuNotInstalled");
            SelectorText.Text = L.T("ui.peds.selectAddCharactersDirectlyGame", keys);
            SelectorMainButton.Content = L.T("ui.peds.installMenu");
            SelectorMainButton.Visibility = Visibility.Visible;
            SelectorRemoveButton.Visibility = Visibility.Collapsed;
        }
        else if (outdated)
        {
            SelectorTitle.Text = L.T("ui.peds.newGameMenuVersionAvailable");
            SelectorText.Text = L.T("ui.peds.updateMenuInstalledCharactersWill");
            SelectorMainButton.Content = L.T("ui.peds.updateMenu");
            SelectorMainButton.Visibility = Visibility.Visible;
            SelectorRemoveButton.Visibility = Visibility.Visible;
        }
        else
        {
            SelectorTitle.Text = L.T("ui.peds.gameMenuInstalled");
            SelectorText.Text = keys;
            SelectorMainButton.Visibility = Visibility.Collapsed;
            SelectorRemoveButton.Visibility = Visibility.Visible;
        }
        Dependencies.Text =
            s.InstalledThirdParty || s.MissingDependencies.Count == 0
                ? ""
                : L.T("ui.peds.requiredGameInstallModdingEssentials", L.Join(", ", s.MissingDependencies));
    }

    private void ShowPackUpdate(bool needed, bool legacy)
    {
        PackUpdateCard.Visibility = needed ? Visibility.Visible : Visibility.Collapsed;
        if (!needed)
            return;
        PackUpdateTitle.Text = legacy ? L.T("ui.peds.updateCharacterPack") : L.T("ui.peds.pedSelectorPackFound");
        PackUpdateText.Text = legacy
            ? L.T("ui.peds.modhubPackNowNamedPedselector")
            : L.T("ui.peds.pedselectorPackGameFolderBut");
        PackUpdateButton.Content = legacy ? L.T("ui.peds.move") : L.T("ui.peds.connect");
    }

    private static string PackageKey(PedEntry entry) =>
        !string.IsNullOrEmpty(entry.PackageId) ? "cache:" + entry.PackageId
        : !string.IsNullOrEmpty(entry.SourceArchive) ? "archive:" + entry.SourceArchive
        : "external:" + entry.SourceDlc;

    private static string PackageTitle(PedEntry entry) =>
        GTAModManager.Core.Mods.ModNames.Pretty(
            !string.IsNullOrEmpty(entry.SourceArchive) ? Path.GetFileNameWithoutExtension(entry.SourceArchive)
            : !string.IsNullOrEmpty(entry.SourceDlc)
                ? Path.GetFileName(Path.GetDirectoryName(entry.SourceDlc)) ?? entry.SourceDlc
            : entry.DisplayName
        );

    // Compatibility entry point for automatic archive routing from MainWindow.
    public Task ShowAnalysisAsync(PedArchiveAnalysis analysis)
    {
        if (game?.Edition != GameEdition.Legacy)
            return Task.CompletedTask;
        AddTab.IsChecked = true;
        queue.Add(analysis);
        Status.Text = L.T("ui.peds.charactersFromArchiveHaveBeen");
        PageScroll.ScrollToTop();
        return Task.CompletedTask;
    }

    /// <summary>Add Characters / Character Collection tabs.</summary>
    private void OnPedsTab(object sender, RoutedEventArgs e)
    {
        if (AddSection is null || CollectionSection is null)
            return;
        AddSection.Visibility = AddTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CollectionSection.Visibility = CollectionTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    public async Task AddArchivesAsync(IEnumerable<string> paths)
    {
        AddTab.IsChecked = true;
        if (game?.Edition != GameEdition.Legacy)
        {
            Status.Text = L.T("ui.peds.selectGtaVLegacyFirst");
            return;
        }
        await WithBusy(L.T("ui.peds.analyzingCharacterArchives"), async () =>
        {
            int added = 0;
            var errors = new List<string>();
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(65))
            {
                try
                {
                    if (
                        !new[] { ".zip", ".rar", ".7z" }.Contains(
                            Path.GetExtension(path),
                            StringComparer.OrdinalIgnoreCase
                        )
                    )
                        throw new ArgumentException(L.T("ui.peds.zipRar7zArchivesContaining"));
                    var analysis = await Task.Run(() => PedArchiveAnalyzer.Analyze(path));
                    int before = queue.Items.Count;
                    queue.Add(analysis);
                    if (queue.Items.Count > before)
                        added++;
                }
                catch (Exception ex)
                {
                    errors.Add(Path.GetFileName(path) + ": " + L.Owned(ex.Message));
                }
            }
            Status.Text = L.T(
                "ui.peds.archivesAdded",
                added,
                (errors.Count > 0 ? L.T("ui.peds.notAddedDetailsBelow", errors.Count) : "")
            );
            if (errors.Count > 0)
                SetLog(L.Join("\n", errors));
            PageScroll.ScrollToTop();
        });
    }

    private void RefreshQueue()
    {
        QueuePanel.Visibility = cards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        int chosen = cards.Count(c => c.Selected);
        QueueTitle.Text = L.T("ui.peds.charactersFound", cards.Count);
        InstallCardsButton.Content = chosen == 0 ? L.T("ui.common.install") : L.T("ui.peds.install", chosen);
        InstallCardsButton.IsEnabled = chosen > 0 && !busy;
        var duplicates = cards
            .Where(c => c.Selected)
            .GroupBy(c => PedMetadata.NameHash(c.Candidate.SpawnName))
            .Where(g => g.Count() > 1)
            .Select(g => g.First().Candidate.SpawnName)
            .ToList();
        CardsHint.Text =
            duplicates.Count > 0 ? L.T("ui.peds.selectedVariantsHaveSameName", L.Join(", ", duplicates))
            : chosen == 0 ? L.T("ui.peds.selectAtLeastOneCharacter")
            : L.T("ui.peds.canChangeGenderCardEverything");
    }

    /// <summary>One card per detected model across all queued archives; selections survive a rebuild.</summary>
    private void RebuildCards()
    {
        var keep = cards.ToDictionary(c => c.Id);
        cards.Clear();
        foreach (var item in queue.Items)
        {
            var dupes = item
                .Analysis.Candidates.GroupBy(c => PedMetadata.NameHash(c.SpawnName))
                .Where(g => g.Count() > 1)
                .SelectMany(g => g)
                .Select(c => c.Id)
                .ToHashSet();
            foreach (var candidate in item.Analysis.Candidates)
            {
                var id = item.ArchivePath + "|" + candidate.Id;
                if (keep.TryGetValue(id, out var old))
                {
                    cards.Add(old);
                    continue;
                }
                var card = new PedCardView(item, candidate, dupes.Contains(candidate.Id));
                card.SelectionChanged += RefreshQueue;
                cards.Add(card);
                _ = card.LoadImageAsync();
            }
        }
    }

    private async void OnPrevImage(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: PedCardView card })
            await card.PrevAsync();
    }

    private async void OnNextImage(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: PedCardView card })
            await card.NextAsync();
    }

    private void OnCardImageClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (
            !busy
            && sender is FrameworkElement { Tag: PedCardView card }
            && e.OriginalSource is not System.Windows.Controls.Primitives.ToggleButton
        )
            card.Selected = !card.Selected;
    }

    private async void OnPick(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        var dialog = new OpenFileDialog
        {
            Filter = L.Join("|", L.T("filter.peds.characterArchives"), "*.zip;*.rar;*.7z"),
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            await AddArchivesAsync(dialog.FileNames);
    }

    /// <summary>Characters from a saved Ped Selector pack (pedselector\dlc.rpf): unpacked and put into the usual queue.</summary>
    private async void OnImportSelector(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        AddTab.IsChecked = true;
        if (game?.Edition != GameEdition.Legacy)
        {
            Status.Text = L.T("ui.peds.selectGtaVLegacyFirst");
            return;
        }
        var dialog = new OpenFolderDialog { Title = L.T("ui.peds.pickSelectorFolder") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        var target = game;
        var path = dialog.FolderName;
        await WithBusy(L.T("ui.peds.importingSelector"), async () =>
        {
            var folder = await Task.Run(() =>
                PedSelectorImport.Extract(target, path, Path.Combine(AppPaths.Cache, "PedSelectorImport"))
            );
            var analysis = await Task.Run(() => PedArchiveAnalyzer.Analyze(folder));
            queue.Add(analysis);
            Status.Text = L.T("ui.peds.selectorImported", analysis.Candidates.Count);
            if (analysis.Warnings.Count > 0)
                SetLog(L.Join("\n", analysis.Warnings.Select(L.Owned)));
            PageScroll.ScrollToTop();
        });
    }

    private async Task<bool> Configure(PedQueueItem item)
    {
        var choices = await PedPackageWindow.ConfigureAsync(
            Window.GetWindow(this),
            item.Analysis,
            item.Selections.Count == 0 ? null : item.Selections
        );
        if (choices is null)
            return false;
        queue.Configure(item, choices);
        return true;
    }

    private async void OnConfigure(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { Tag: PedQueueItem item })
            return;
        try
        {
            await Configure(item);
        }
        catch (Exception ex)
        {
            Status.Text = L.Owned(ex.Message);
        }
    }

    private void OnRemoveQueue(object sender, RoutedEventArgs e)
    {
        if (!busy && sender is Button { Tag: PedQueueItem item })
            queue.Remove(item);
    }

    private void OnClearQueue(object sender, RoutedEventArgs e)
    {
        if (!busy)
            queue.Clear();
    }

    private async void OnInstallQueue(object sender, RoutedEventArgs e)
    {
        if (busy || game is null || run is null)
            return;
        try
        {
            foreach (var item in queue.Items.ToArray())
            {
                var chosen = cards.Where(c => c.Item == item && c.Selected).Select(c => c.ToSelection()).ToList();
                item.Selected = chosen.Count > 0;
                if (chosen.Count > 0)
                    queue.Configure(item, chosen);
            }
            var requests = queue.CreateRequests();
            var target = game;
            await WithBusy(L.T("ui.peds.installingArchives", requests.Count), async () =>
            {
                string request = Path.Combine(
                    Path.GetTempPath(),
                    "gmm-ped-batch-" + Guid.NewGuid().ToString("N") + ".json"
                );
                try
                {
                    await File.WriteAllTextAsync(request, JsonSerializer.Serialize(requests));
                    queue.MarkInstalling(requests.Select(r => r.ArchivePath));
                    var result = await run(["peds-install-batch", target.Root, request]);
                    SetLog(result.output);
                    var line = result
                        .output.Split('\n')
                        .LastOrDefault(l => l.StartsWith(BatchPrefix, StringComparison.Ordinal));
                    var outcomes = line is null
                        ? []
                        : JsonSerializer.Deserialize<List<PedInstallOutcome>>(line[BatchPrefix.Length..]) ?? [];
                    queue.ApplyOutcomes(outcomes);
                    await LoadAsync(target, run);
                    Changed?.Invoke();
                    int succeeded = outcomes.Count(o => o.Succeeded),
                        failed = requests.Count - succeeded;
                    if (failed > 0)
                        ShowFailure(L.T("ui.peds.characterInstallation"), result.output);
                    Status.Text =
                        failed == 0
                            ? L.T("ui.peds.installationCompletePackagesAddedCan", succeeded)
                            : L.T("ui.peds.packagesInstalledIncompleteCheckRemaining", succeeded, failed);
                    PageScroll.ScrollToTop();
                }
                finally
                {
                    File.Delete(request);
                    queue.ApplyOutcomes([]);
                }
            });
        }
        catch (Exception ex)
        {
            queue.ApplyOutcomes([]);
            Status.Text = L.T("ui.peds.installationIncomplete", L.Owned(ex.Message));
        }
    }

    private async void OnOpenPackage(object sender, RoutedEventArgs e)
    {
        if (busy || game is null || sender is not Button { Tag: InstalledPackageView package })
            return;
        PedPackagePresentation? presentation = null;
        var entries = package.Models.Select(m => m.Entry).ToArray();
        var target = game;
        await WithBusy(L.T("ui.peds.readingPackageDetails"), async () =>
            presentation = await Task.Run(() => new PedPackageLibrary(target).Load(entries))
        );
        if (presentation is not null)
            await PedPackageWindow.ShowInstalledAsync(Window.GetWindow(this), presentation, entries);
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (selectingAll || busy)
            return;
        selectingAll = true;
        foreach (var model in installed)
            model.Selected = SelectAll.IsChecked == true;
        selectingAll = false;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        int count = installed.Count(m => m.Selected && m.Entry.Managed);
        DeleteSelected.IsEnabled = count > 0;
        DeleteSelected.Content = count == 0 ? L.T("ui.peds.removeSelected") : L.T("ui.peds.removeSelected2", count);
        if (!selectingAll)
        {
            selectingAll = true;
            int own = installed.Count(m => m.Entry.Managed);
            SelectAll.IsChecked = own > 0 && count == own;
            selectingAll = false;
        }
    }

    private async void OnRemovePed(object sender, RoutedEventArgs e)
    {
        if (!busy && sender is Button { Tag: InstalledPedView { Entry.Managed: true } model })
            await RemoveModels([model.Entry.SpawnName]);
    }

    private async void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        if (!busy)
            await RemoveModels(
                installed.Where(m => m.Selected && m.Entry.Managed).Select(m => m.Entry.SpawnName).ToArray()
            );
    }

    private async Task RemoveModels(IReadOnlyList<string> names)
    {
        if (names.Count == 0 || game is null || run is null)
            return;
        var target = game;
        await WithBusy(L.T("ui.peds.removingCharacters2", names.Count), async () =>
        {
            var request = Path.Combine(Path.GetTempPath(), "gmm-ped-remove-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                await File.WriteAllTextAsync(request, JsonSerializer.Serialize(names));
                var result = await run(["peds-remove-many", target.Root, request]);
                SetLog(result.output);
                await LoadAsync(target, run);
                Changed?.Invoke();
                Status.Text =
                    result.code == 0
                        ? L.T("ui.peds.charactersRemovedCanUndoHistory", names.Count)
                        : L.T("ui.peds.charactersWereNotRemovedOpen");
                if (result.code != 0)
                    ShowFailure(L.T("ui.peds.removingCharacters"), result.output);
                PageScroll.ScrollToTop();
            }
            finally
            {
                File.Delete(request);
            }
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (!busy && run is not null)
            await LoadAsync(game, run);
    }

    private static string BundledSelector =>
        Path.Combine(AppContext.BaseDirectory, "Components", "Legacy", "GMMPedSelector.asi");

    private static bool SameFile(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists)
                return !fb.Exists; // no bundled file, nothing to compare against
            return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private async void OnInstallSelector(object sender, RoutedEventArgs e)
    {
        if (game is null)
            return;
        await SimpleAction(
            ["peds-selector-install", game.Root, BundledSelector],
            L.T("ui.peds.installingMenu"),
            L.T("ui.peds.gameMenuInstalled2")
        );
    }

    private async void OnRemoveSelector(object sender, RoutedEventArgs e)
    {
        if (game is not null && Window.GetWindow(this) is { } owner)
            await RemoveSelectorAsync(PedSelectorRemoveDialog.Ask(owner));
    }

    /// <summary>Removes the menu only, or the menu with every Mod Hub character and the saved looks.</summary>
    public async Task RemoveSelectorAsync(PedSelectorRemoval mode)
    {
        if (game is null || mode == PedSelectorRemoval.Cancel)
            return;
        if (mode == PedSelectorRemoval.Everything)
            await SimpleAction(
                ["peds-selector-remove", game.Root, "--with-peds"],
                L.T("ui.peds.removingEverything"),
                L.T("ui.peds.everythingRemoved")
            );
        else
            await SimpleAction(
                ["peds-selector-remove", game.Root],
                L.T("ui.peds.removingMenu"),
                L.T("ui.peds.gameMenuHasBeenRemoved")
            );
    }

    private async void OnSync(object sender, RoutedEventArgs e)
    {
        if (game is not null)
            await SimpleAction(
                ["peds-sync", game.Root],
                L.T("ui.peds.updatingList"),
                L.T("ui.peds.gameMenuListHasBeen")
            );
    }

    private async Task SimpleAction(string[] args, string title, string success)
    {
        if (run is null)
            return;
        var target = game;
        await WithBusy(title, async () =>
        {
            var result = await run(args);
            SetLog(result.output);
            await LoadAsync(target, run);
            Changed?.Invoke();
            Status.Text = result.code == 0 ? success : L.T("ui.peds.actionWasNotCompletedOpen");
            PageScroll.ScrollToTop();
            if (result.code != 0)
                ShowFailure(title, result.output);
        });
    }

    private void SetLog(string output)
    {
        OperationLog.Text = L.Owned(
            output.Length > 65536 ? L.T("ui.peds.outputTruncated", L.Owned(output[..65536])) : output
        );
        OperationDetails.Visibility = string.IsNullOrWhiteSpace(output) ? Visibility.Collapsed : Visibility.Visible;
        OperationDetails.IsExpanded = false;
    }

    /// <summary>A clear dialog with the failure reason (the error / "Rejected…" lines from gmm output).</summary>
    private void ShowFailure(string title, string output)
    {
        var lines = output
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Where(l =>
                l.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase)
                || l.Contains("Rejected")
                || l.Contains("запрещен")
                || l.StartsWith("·")
                || l.StartsWith("!")
            )
            .Distinct()
            .Take(6)
            .ToList();
        var text =
            lines.Count > 0
                ? L.Join("\n", lines)
                : (
                    output.Trim().Length > 0
                        ? output.Trim()[..Math.Min(600, output.Trim().Length)]
                        : L.T("ui.peds.unknownReason")
                );
        MessageBox.Show(
            Window.GetWindow(this),
            L.Owned(text),
            L.T("ui.peds.notCompleted", title.TrimEnd('…', '.')),
            MessageBoxButton.OK,
            MessageBoxImage.Warning
        );
    }

    private async Task WithBusy(string title, Func<Task> action)
    {
        if (busy)
            return;
        if (game?.IsRunning() == true)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                L.T("ui.peds.gtaVRunningModhubDoes"),
                L.T("ui.peds.closeGtaV"),
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            return;
        }
        busy = true;
        BusyChanged?.Invoke(true, title);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Status.Text = L.T("ui.common.error2", L.Owned(ex.Message));
        }
        finally
        {
            busy = false;
            BusyChanged?.Invoke(false, "");
            RefreshQueue();
        }
    }
}
