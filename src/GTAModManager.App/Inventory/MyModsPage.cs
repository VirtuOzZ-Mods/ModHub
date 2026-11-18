using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.App.Installation;
using GTAModManager.Core.Base;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Inventory;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using Microsoft.Win32;

namespace GTAModManager.App.Inventory;

/// <summary>Shows one card per recorded mod installation without scanning game file contents.</summary>
public sealed class MyModsPage : UserControl
{
    private readonly StackPanel _list = new();
    private readonly StackPanel _banners = new();
    private readonly WrapPanel _sections = new() { Margin = new Thickness(0, 0, 0, 8) };

    // Section of every card in the list; null shows all of them.
    private readonly Dictionary<UIElement, ModCategory> _cardSection = [];
    private ModCategory? _section;
    private readonly TextBlock _summary = new()
    {
        Foreground = Res("Muted"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 12),
    };
    private readonly ProgressBar _progress = new()
    {
        IsIndeterminate = true,
        Height = 4,
        Margin = new Thickness(0, 0, 0, 10),
        Foreground = Res("Accent"),
        Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2E, 0x34)),
        BorderThickness = new Thickness(0),
        Visibility = Visibility.Collapsed,
    };
    private GameInstall? _game;
    private InventoryReport? _report;
    private int _scanId;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private Task? _scanTask;
    private string? _requestedRoot;
    public event Action<bool, string>? ScanActivityChanged;

    /// <summary>History entries whose files were changed outside Mod Hub (undoing them is unsafe).</summary>
    public IReadOnlyCollection<string> Frozen => _report?.FrozenJournals ?? [];
    public InventoryReport? Report => _report;
    public event Action? Scanned;
    public event Action<GTAModManager.Core.NaturalVision.NveState, bool>? NaturalVisionRequested;

    /// <summary>LCPP installed before the WorldTravelPatches fix: replace that one file through the installation.</summary>
    public event Action<GTAModManager.Core.NaturalVision.NveState>? LcppRepairRequested;

    /// <summary>The Ped Selector card asks to remove the menu (with or without its characters).</summary>
    public event Action? PedSelectorRemoveRequested;

    /// <summary>"Remove" on a mod card: undo every Mod Hub installation of that mod.</summary>
    public event Action<ModGroup>? ModRemoveRequested;

    /// <summary>"Make backup" on a mod card: copy the mod's files into the backup folder.</summary>
    public event Action<ModGroup>? ModBackupRequested;

    /// <summary>Remove chosen components of a built-in package: the group, their installations and their names.</summary>
    public event Action<ModGroup, IReadOnlyList<string>, string>? ComponentRemoveRequested;
    public event Action? ClearHistoryRequested;

    /// <summary>"Engine sound" on a car card (car pack) or "Give to a car" on a sound pack card (sound name).</summary>
    public event Action<string?, string?>? EngineSoundRequested;

    /// <summary>Rebuild from list: download the selected mods from GTA5-Mods and install them one by one.</summary>
    public event Action<IReadOnlyList<ModListEntry>>? RebuildRequested;

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public MyModsPage()
    {
        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 16) };
        Content = root;
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var head = new DockPanel();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
        };
        DockPanel.SetDock(buttons, Dock.Right);
        // Always reachable: packs installed by hand have no card here, but their sounds and cars are on disk.
        buttons.Children.Add(
            InstallUi.Button(L.T("ui.inventory.engineSound"), () => EngineSoundRequested?.Invoke(null, null))
        );
        buttons.Children.Add(InstallUi.Button(L.T("ui.inventory.saveList"), SaveList));
        buttons.Children.Add(InstallUi.Button(L.T("ui.inventory.buildFromList"), OpenList));
        buttons.Children.Add(InstallUi.Button(L.T("ui.common.refresh"), () => _ = ScanAsync(_game, force: true)));
        head.Children.Add(buttons);
        var titles = new StackPanel();
        titles.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.common.myMods"),
                Style = (Style)Application.Current.FindResource("PageTitle"),
            }
        );
        titles.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.inventory.installedOnlyDescription"),
                Foreground = Res("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            }
        );
        head.Children.Add(titles);
        top.Children.Add(head);
        top.Children.Add(_summary);
        top.Children.Add(_progress);
        top.Children.Add(_banners);
        top.Children.Add(_sections);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    public Task ScanAsync(GameInstall? game, bool force = false)
    {
        if (!force && _scanTask is { IsCompleted: false } && _requestedRoot == game?.Root)
            return _scanTask;
        if (_game?.Root != game?.Root)
        {
            _report = null;
            _list.Children.Clear();
            _banners.Children.Clear();
        }
        _requestedRoot = game?.Root;
        _game = game;
        int id = ++_scanId;
        if (game is null)
        {
            _report = null;
            _list.Children.Clear();
            _summary.Text = L.T("ui.common.selectGameLeft");
            _progress.Visibility = Visibility.Collapsed;
            ScanActivityChanged?.Invoke(false, "");
            return Task.CompletedTask;
        }
        return _scanTask = ScanCoreAsync(game, id);
    }

    private async Task ScanCoreAsync(GameInstall game, int id)
    {
        _progress.Visibility = Visibility.Visible;
        _summary.Text = L.T("ui.background.refreshingMods", game.Edition);
        ScanActivityChanged?.Invoke(true, _summary.Text);
        await _scanGate.WaitAsync();
        try
        {
            if (id != _scanId)
                return;
            var result = await Task.Run(() =>
            {
                if (new GTAModManager.Core.NaturalVision.NveStore(game).Active().Count > 0)
                    VanillaModCleanup.TryComplete(game, GTAModManager.Core.Library.AppSettings.Load().OpenLibrary());
                var report = InstalledModList.Read(game, ModLabels.Default(), AppSettings.Load().OpenLibrary());
                var suites = new GTAModManager.Core.NaturalVision.NveStore(game).Active();
                return (report, suites);
            });
            if (id != _scanId)
                return;
            _report = result.report;
            await RenderAsync(result.suites, id);
            if (id == _scanId)
                Scanned?.Invoke();
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                        or ArgumentException
                        or System.Text.Json.JsonException
            )
        {
            if (id == _scanId)
                _summary.Text = L.T("ui.inventory.couldNotReadGameFolder", L.Owned(ex.Message));
        }
        finally
        {
            _scanGate.Release();
            if (id == _scanId)
            {
                _progress.Visibility = Visibility.Collapsed;
                ScanActivityChanged?.Invoke(false, "");
            }
        }
    }

    private async Task RenderAsync(IReadOnlyList<GTAModManager.Core.NaturalVision.NveState> suites, int id)
    {
        if (_report is not { } r || _game is null)
            return;
        _summary.Text = L.T("ui.inventory.installedOnlyCount", _game.Edition, r.Groups.Count);
        _banners.Children.Clear();
        foreach (var w in r.Warnings)
            _banners.Children.Add(Banner(w, "#3A311C", "Warn"));
        if (r.FrozenJournals.Count > 0)
        {
            var b = Banner(L.T("ui.inventory.historyEntriesDoNotMatch", r.FrozenJournals.Count), "#3A311C", "Warn");
            var clear = InstallUi.Button(L.T("ui.common.clearHistory"), () => ClearHistoryRequested?.Invoke());
            clear.HorizontalAlignment = HorizontalAlignment.Left;
            ((StackPanel)b.Child).Children.Add(clear);
            _banners.Children.Add(b);
        }
        _list.Children.Clear();
        _cardSection.Clear();
        BuildSections(
            suites
                .Select(s => ModCategories.ForSuite(s.ProductTitle))
                .Concat(r.Groups.Where(g => !suites.Any(s => g.Name == s.ProductTitle)).Select(g => g.Category))
                .ToList()
        );

        foreach (var suite in suites)
        {
            if (id != _scanId)
                return;
            var suiteCard = NaturalVisionCard(suite);
            Place(suiteCard, ModCategories.ForSuite(suite.ProductTitle));
            _list.Children.Add(suiteCard);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }
        int rendered = 0;
        _cards.Clear();
        var groups = r.Groups.Where(g => !suites.Any(s => g.Name == s.ProductTitle)).ToList();
        foreach (var g in groups)
        {
            if (id != _scanId)
                return;
            var card = Card(g);
            _cards[g.Name] = card;
            Place(card, g.Category);
            _list.Children.Add(card);
            if (++rendered % 6 == 0)
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }
        if (r.Groups.Count == 0 && suites.Count == 0)
            _list.Children.Add(InstallUi.Text(L.T("ui.inventory.noInstallRecords")));
        _ = EnrichAsync(groups, id);
    }

    /// <summary>Section tabs with mod counts: All, Scripts, Graphics, Maps, Characters, Accessories, Sounds, Vehicles, Other.</summary>
    private void BuildSections(IReadOnlyList<ModCategory> categories)
    {
        _sections.Children.Clear();
        if (_section is { } chosen && !categories.Contains(chosen))
            _section = null;
        RadioButton Tab(string text, ModCategory? section, int count)
        {
            var tab = new RadioButton
            {
                Content = $"{text}  {count}",
                GroupName = "mymods-sections",
                Style = (Style)Application.Current.FindResource("TabOption"),
                Margin = new Thickness(0, 0, 20, 0),
                IsChecked = _section == section,
                IsEnabled = count > 0 || section is null,
                Opacity = count > 0 || section is null ? 1 : 0.45,
            };
            tab.Checked += (_, _) =>
            {
                _section = section;
                ApplySection();
            };
            return tab;
        }
        _sections.Children.Add(Tab(L.T("ui.inventory.sectionAll"), null, categories.Count));
        foreach (var c in ModCategories.All)
            _sections.Children.Add(Tab(ModCategories.Label(c), c, categories.Count(x => x == c)));
    }

    private void Place(UIElement card, ModCategory section)
    {
        _cardSection[card] = section;
        card.Visibility = _section is null || _section == section ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplySection()
    {
        foreach (var (card, section) in _cardSection)
            card.Visibility = _section is null || _section == section ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Spawn names and engine sounds of the mod's add-on packs, recognized from the packs themselves.</summary>
    private void AddPackDetails(Panel body, ModGroup g)
    {
        var lines = new List<string>();
        var cars = g.Packs.SelectMany(p => p.Content.Vehicles).ToList();
        if (cars.Count > 0)
            lines.Add(
                L.T(
                    "ui.inventory.packModels",
                    string.Join(
                        ", ",
                        cars.Take(8).Select(v => L.T("ui.inventory.modelWithSound", v.Model, v.EffectiveAudio))
                    ) + (cars.Count > 8 ? " …" : "")
                )
            );
        var sounds = g
            .Packs.Where(p => p.Content.Kind == Core.Dlc.DlcPackKind.EngineSounds)
            .SelectMany(p => p.Content.EngineSounds.Select(s => s.Name))
            .ToList();
        if (sounds.Count > 0)
            lines.Add(L.T("ui.inventory.packSounds", string.Join(", ", sounds)));
        // The latest change per car is what the game plays now.
        var changes = g
            .SoundAssignments.GroupBy(a => a.Model, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Last())
            .ToList();
        if (changes.Count > 0)
            lines.Add(
                L.T(
                    "ui.inventory.soundChanges",
                    string.Join(", ", changes.Select(c => L.T("ui.inventory.soundChange", c.Model, c.Sound)))
                )
            );
        if (lines.Count > 0)
            body.Children.Add(
                new TextBlock
                {
                    Text = string.Join("\n", lines),
                    Foreground = Res("Muted"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 4),
                }
            );
    }

    private void AddSoundActions(Panel actions, ModGroup g)
    {
        var car = g.Packs.FirstOrDefault(p => p.Content.Vehicles.Any(v => v.CanChangeSound));
        if (car is not null)
            actions.Children.Add(
                InstallUi.Button(L.T("ui.inventory.engineSound"), () => EngineSoundRequested?.Invoke(car.Pack, null))
            );
        var sound = g
            .Packs.Where(p => p.Content.Kind == Core.Dlc.DlcPackKind.EngineSounds)
            .SelectMany(p => p.Content.SoundNames)
            .FirstOrDefault();
        if (sound is not null)
            actions.Children.Add(
                InstallUi.Button(L.T("ui.inventory.giveSoundToCar"), () => EngineSoundRequested?.Invoke(null, sound))
            );
    }

    private static Border Banner(string text, string bg, string fg)
    {
        var p = new StackPanel();
        p.Children.Add(
            new TextBlock
            {
                Text = text,
                Foreground = Res(fg),
                TextWrapping = TextWrapping.Wrap,
            }
        );
        return new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString(bg)!,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10),
            Child = p,
        };
    }

    private Border NaturalVisionCard(GTAModManager.Core.NaturalVision.NveState state)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(InstallUi.Text(state.ProductTitle, 24));
        int options = state.Components.Count(c => c.Active && !c.Component.Core),
            core = state.Components.Count(c => c.Active && c.Component.Core),
            total = state.Components.Count(c => c.Component.Core);
        panel.Children.Add(
            InstallUi.Text(
                L.T(
                    state.IsLcpp ? "lcpp.manage" : "ui.inventory.razedModsGtaVMain",
                    state.Edition,
                    (
                        state.Phase == "Installed"
                            ? L.T("ui.inventory.installed")
                            : L.T("ui.inventory.operationIncomplete")
                    ),
                    core,
                    total,
                    options
                )
            )
        );
        if (state.Message.Length > 0)
            panel.Children.Add(InstallUi.Text(state.Message));
        var actions = new WrapPanel();
        panel.Children.Add(actions);
        if (_game is { } game && GTAModManager.Core.NaturalVision.NveService.NeedsWorldTravelRepair(game, state))
        {
            var warning = Banner(L.T("lcpp.repair.banner"), "#3A311C", "Warn");
            var fix = new Button
            {
                Content = L.T("lcpp.repair.button"),
                Style = (Style)Application.Current.FindResource("Primary"),
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(14, 8, 14, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            fix.Click += (_, _) => LcppRepairRequested?.Invoke(state);
            ((StackPanel)warning.Child).Children.Add(fix);
            panel.Children.Insert(1, warning);
        }
        if (state.PreservedAdjusters.Count > 0)
            panel.Children.Add(
                InstallUi.Text(L.T("lcpp.adjusters.preserved", string.Join(", ", state.PreservedAdjusters)))
            );
        // Both composed mods use the same management card, even without optional addons.
        if (state.Components.Count > 0)
            actions.Children.Add(
                InstallUi.Button(L.T("ui.inventory.manageComponents"), () =>
                    NaturalVisionRequested?.Invoke(state, false)
                )
            );
        actions.Children.Add(
            InstallUi.Button(L.T("ui.inventory.removeEntireMod"), () => NaturalVisionRequested?.Invoke(state, true))
        );
        if (!state.IsLcpp)
            actions.Children.Add(
                InstallUi.Button(L.T("ui.common.visitWebsite"), () =>
                    Discover.BrowserView.OpenExternal(new Uri("https://razedmods.com/"))
                )
            );
        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 0, 6, 12),
            Background = new LinearGradientBrush(Color.FromRgb(20, 63, 52), Color.FromRgb(28, 28, 32), 0),
            Child = panel,
        };
    }

    private Border Chip(ModGroup g) =>
        new()
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background =
                g.Origin == ModOrigin.ModHub
                    ? new SolidColorBrush(Color.FromArgb(0x33, 0x3D, 0xDC, 0x84))
                    : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x99, 0x00)),
            Child = new TextBlock
            {
                Text =
                    g.Origin == ModOrigin.ModHub ? L.T("ui.inventory.viaModHub")
                    : g.Recognized ? L.T("ui.inventory.manual")
                    : L.T("ui.inventory.unrecognized"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
            },
        };

    private static TextBlock Title(string text, double size = 16) =>
        new()
        {
            Text = text,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

    private Border Card(ModGroup g)
    {
        if (!g.Builtin && PageFor(g) is { } page && AuthorThanks.IsGta5Mods(page.Url))
            return PageCard(g, page.Url, page.FoundBySearch);
        var p = new StackPanel();
        var head = new DockPanel();
        var chip = Chip(g);
        DockPanel.SetDock(chip, Dock.Right);
        head.Children.Add(chip);
        head.Children.Add(Title(g.DisplayName));
        p.Children.Add(head);
        p.Children.Add(
            new TextBlock
            {
                Text = g.Builtin ? L.T("ui.inventory.builtinComponent") : L.Owned(g.Kind),
                Foreground = Res("Muted"),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 6),
            }
        );

        AddPackDetails(p, g);
        var actions = new WrapPanel();
        if (g.Builtin)
        // Mod Hub's own components have no page; the useful information is what they put in the game folder.
        {
            // Sound changes of stock cars: no files of their own, each change can be undone separately.
            if (g.Name == InstalledModList.EngineSoundsName)
                actions.Children.Add(
                    InstallUi.Button(L.T("ui.inventory.engineSound"), () => EngineSoundRequested?.Invoke(null, null))
                );
            else
                actions.Children.Add(InstallUi.Button(L.T("ui.inventory.componentFiles"), () => ShowFiles(g)));
            if (g.Name == InstalledModList.PedSelectorName)
                actions.Children.Add(
                    InstallUi.Button(L.T("ui.common.remove"), () => PedSelectorRemoveRequested?.Invoke())
                );
            else if (g.Operations.Count > 0)
                actions.Children.Add(
                    InstallUi.Button(L.T("ui.inventory.removeComponent"), () => ChooseComponentsToRemove(g))
                );
        }
        else
        {
            if (g.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                actions.Children.Add(PageButton(uri));
            AddSoundActions(actions, g);
            AddLabelActions(actions, g);
            AddRemoveAction(actions, g);
        }
        p.Children.Add(actions);
        return new Border
        {
            Style = (Style)Application.Current.FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 6, 10),
            Child = p,
        };
    }

    private void AddRemoveAction(Panel actions, ModGroup g)
    {
        // Manual copy of the mod's own files into the backup folder (NaturalVision/LCPP have their own cards).
        if (!g.Builtin && g.Files.Count > 0)
            actions.Children.Add(InstallUi.Button(L.T("ui.inventory.makeBackup"), () => ModBackupRequested?.Invoke(g)));
        if (!g.Builtin && g.Operations.Count > 0)
            actions.Children.Add(InstallUi.Button(L.T("ui.common.remove"), () => ModRemoveRequested?.Invoke(g)));
    }

    private void AddLabelActions(Panel actions, ModGroup g)
    {
        if (g.Origin == ModOrigin.External)
            actions.Children.Add(
                InstallUi.Button(g.Recognized ? L.T("ui.inventory.renameLink") : L.T("ui.inventory.label"), () =>
                    _ = Label(g)
                )
            );
        else if (g.Url is null)
            actions.Children.Add(InstallUi.Button(L.T("ui.inventory.addLink"), () => _ = Label(g)));
    }

    // ---------- Mod pages: author, cover and links from GTA5-Mods ----------

    private readonly ModPages _pages = new();
    private readonly InstalledMods _recommended = new();
    private static readonly Lazy<IReadOnlyList<CatalogEntry>> Catalog = new(() =>
    {
        try
        {
            return DiscoverCatalog.LoadBuiltIn().Entries;
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    });
    private readonly Dictionary<string, Border> _cards = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _enrichCts;

    /// <summary>The user's link first, then a page remembered from search, then the built-in catalog.</summary>
    private (string Url, bool FoundBySearch)? PageFor(ModGroup g)
    {
        if (g.Builtin)
            return null;
        if (g.Url is { Length: > 0 } own)
            return (own, false);
        // Installed from Recommendations: the catalog entry is known exactly, no title matching needed.
        if (
            _game is { } game
            && InstalledMods.CatalogIdFor(_recommended.ForGame(game.Root), g.Files.Select(f => f.Rel)) is { } id
            && Catalog.Value.FirstOrDefault(e => e.Id == id) is { Source: MetaSource.Gta5Mods } entry
        )
            return (entry.Url, false);
        // Installed some other way but known to the catalog by its marker file.
        if (ModPages.FromCatalogFiles(Catalog.Value, g.Files.Select(f => f.Rel)) is { } marked)
            return (marked, false);
        if (_pages.Get(g.Name)?.Url is { } found)
            return (found, true);
        return ModPages.FromCatalog(Catalog.Value, g.Name) is { } known ? (known, false) : null;
    }

    private static bool Searchable(ModGroup g) =>
        !g.Builtin && (g.Origin == ModOrigin.ModHub || g.Recognized) && !string.IsNullOrWhiteSpace(g.Name);

    /// <summary>
    /// Background pass after the list is shown: finds pages of mods without a link (exact title match only),
    /// loads their metadata through the shared cache and redraws each card as its data arrives.
    /// </summary>
    private async Task EnrichAsync(IReadOnlyList<ModGroup> groups, int id)
    {
        _enrichCts?.Cancel();
        var cts = _enrichCts = new CancellationTokenSource();
        var ct = cts.Token;
        var provider = ModMetadataProvider.Shared;
        try
        {
            foreach (var g in groups.Where(Searchable))
            {
                if (id != _scanId)
                    return;
                var page = PageFor(g);
                if (page is null && _pages.NeedsSearch(g.Name, DateTimeOffset.UtcNow))
                {
                    string? url;
                    try
                    {
                        url = await provider.FindGta5ModsPageAsync(g.Name, ct);
                    }
                    catch (Exception ex)
                        when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException
                            && !ct.IsCancellationRequested
                        )
                    {
                        continue; // offline or the site refused: try again on the next visit
                    }
                    _pages.Set(g.Name, url, DateTimeOffset.UtcNow);
                    page = url is null ? null : (url, true);
                    if (page is not null)
                        Redraw(g, id);
                }
                if (page is not { } found || !AuthorThanks.IsGta5Mods(found.Url))
                    continue;
                var entry = ModPages.MetadataEntry(g.Name, found.Url);
                if (!provider.NeedsRefresh(entry, ModMetadataProvider.DefaultMaxAge))
                    continue;
                await provider.RefreshAsync([entry], force: false, onUpdated: null, ct);
                Redraw(g, id);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void Redraw(ModGroup g, int id)
    {
        if (id != _scanId || !_cards.TryGetValue(g.Name, out var old))
            return;
        int index = _list.Children.IndexOf(old);
        if (index < 0)
            return;
        var card = Card(g);
        _cards[g.Name] = card;
        _cardSection.Remove(old);
        Place(card, g.Category);
        _list.Children[index] = card;
    }

    private Border PageCard(ModGroup g, string url, bool foundBySearch)
    {
        var provider = ModMetadataProvider.Shared;
        var meta = provider.Get(ModPages.MetadataEntry(g.Name, url).Id);
        var data = meta?.Data;
        Uri.TryCreate(url, UriKind.Absolute, out var pageUri);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Cover: the mod's title screenshot, or a neutral tile while it loads.
        var cover = new Border
        {
            Width = 256,
            Height = 144,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = LoadImage(provider.ImagePath(meta), 512) is { } image
                ? new ImageBrush(image) { Stretch = Stretch.UniformToFill }
                : new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2B)),
            Cursor = pageUri is null ? null : System.Windows.Input.Cursors.Hand,
            ToolTip = L.T("ui.inventory.modPage"),
        };
        if (pageUri is not null)
            cover.MouseLeftButtonUp += (_, _) => Discover.BrowserView.OpenExternal(pageUri);
        grid.Children.Add(cover);

        var body = new StackPanel();
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var head = new DockPanel();
        var chip = Chip(g);
        DockPanel.SetDock(chip, Dock.Right);
        head.Children.Add(chip);
        head.Children.Add(Title(data?.Title ?? g.DisplayName, 19));
        body.Children.Add(head);

        if (data?.Author is { Length: > 0 } author)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            if (LoadImage(provider.AvatarPath(meta), 64) is { } avatar)
                row.Children.Add(
                    new System.Windows.Shapes.Ellipse
                    {
                        Width = 28,
                        Height = 28,
                        Margin = new Thickness(0, 0, 8, 0),
                        Fill = new ImageBrush(avatar) { Stretch = Stretch.UniformToFill },
                    }
                );
            var name = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            };
            if (data.AuthorProfileUrl is { } profileUrl && Uri.TryCreate(profileUrl, UriKind.Absolute, out var profile))
            {
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(author))
                {
                    Foreground = Res("Text"),
                    TextDecorations = null,
                    ToolTip = profile.AbsoluteUri,
                };
                link.Click += (_, _) => Discover.BrowserView.OpenExternal(profile);
                name.Inlines.Add(link);
            }
            else
                name.Text = author;
            row.Children.Add(name);
            body.Children.Add(row);
        }

        var stats = new List<string>();
        if (data?.Version is { Length: > 0 } version)
            stats.Add(L.T("ui.inventory.statVersion", version));
        if (data?.Rating is { } rating)
            stats.Add("★ " + rating.ToString("0.0#", System.Globalization.CultureInfo.CurrentCulture));
        if (data?.Downloads is { } downloads)
            stats.Add(L.T("ui.inventory.statDownloads", downloads.ToString("N0")));
        if (data?.Likes is { } likes)
            stats.Add(L.T("ui.inventory.statLikes", likes.ToString("N0")));
        body.Children.Add(
            new TextBlock
            {
                Text = stats.Count > 0 ? string.Join("  ·  ", stats) : L.Owned(g.Kind),
                Foreground = Res("Muted"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                ToolTip = foundBySearch ? L.T("ui.inventory.pageFoundBySearch") : null,
            }
        );

        if (data?.Links is { Count: > 0 } links)
        {
            var socials = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var view in links.Select(Discover.AuthorLinks.View))
                socials.Children.Add(LinkPill(view));
            body.Children.Add(socials);
        }

        AddPackDetails(body, g);
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (pageUri is not null)
            actions.Children.Add(PageButton(pageUri));
        AddSoundActions(actions, g);
        // A page found automatically can be corrected through the same dialog.
        if (g.Origin == ModOrigin.External || g.Url is not null)
            AddLabelActions(actions, g);
        else
            actions.Children.Add(InstallUi.Button(L.T("ui.inventory.changeLink"), () => _ = Label(g)));
        AddRemoveAction(actions, g);
        body.Children.Add(actions);

        return new Border
        {
            Style = (Style)Application.Current.FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 6, 10),
            Child = grid,
        };
    }

    /// <summary>"Open on [5MODS]" for GTA5-Mods pages, a plain "Mod page" button for any other site.</summary>
    private static Button PageButton(Uri uri)
    {
        if (!AuthorThanks.IsGta5Mods(uri.AbsoluteUri))
            return InstallUi.Button(L.T("ui.inventory.modPage"), () => Discover.BrowserView.OpenExternal(uri));
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(
            new TextBlock { Text = L.T("ui.common.openOn"), VerticalAlignment = VerticalAlignment.Center }
        );
        content.Children.Add(
            new ContentControl
            {
                Style = (Style)Application.Current.FindResource("FiveModsPlate"),
                FontSize = 11.5,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var button = InstallUi.Button("", () => Discover.BrowserView.OpenExternal(uri));
        button.Content = content;
        button.ToolTip = uri.AbsoluteUri;
        return button;
    }

    private static Button LinkPill(Discover.LinkView view)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(
            new System.Windows.Shapes.Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = view.Brush,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        content.Children.Add(new TextBlock { Text = view.Label, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content,
            Margin = new Thickness(0, 2, 6, 2),
            Padding = new Thickness(10, 5, 12, 5),
            Style = (Style)Application.Current.FindResource("Ghost"),
            ToolTip = view.Tip,
        };
        button.Click += (_, _) =>
        {
            if (Uri.TryCreate(view.Url, UriKind.Absolute, out var uri))
                Discover.BrowserView.OpenExternal(uri);
        };
        return button;
    }

    private static System.Windows.Media.Imaging.BitmapImage? LoadImage(string? path, int width)
    {
        if (path is null)
            return null;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = width;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static List<string> ComponentFiles(ModGroup g) =>
        g
            .Files.Select(f => f.Rel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Component name from an installation title: "База для моддинга: Script Hook V" → "Script Hook V".</summary>
    private static string ComponentName(string title)
    {
        const string prefix = "База для моддинга: ";
        return title.StartsWith(prefix, StringComparison.Ordinal)
            ? title[prefix.Length..]
            : GTAModManager.Core.Mods.ModNames.DisplayOperationTitle(title);
    }

    /// <summary>Lets the user pick components of a built-in package (Script Hook V, ScriptHookVDotNet, adapters…) to remove.</summary>
    private void ChooseComponentsToRemove(ModGroup g)
    {
        var components = g
            .Operations.GroupBy(id => ComponentName(g.OperationTitles.GetValueOrDefault(id, id)))
            .OrderBy(c => c.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var window = InstallUi.Window(
            Window.GetWindow(this),
            L.T("ui.inventory.removeComponentTitle", g.DisplayName),
            560,
            460
        );
        var root = new DockPanel { Margin = new Thickness(20) };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(Title(g.DisplayName, 19));
        top.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.inventory.removeComponentHint"),
                Foreground = Res("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10),
            }
        );
        root.Children.Add(top);
        var boxes = new List<(CheckBox Box, IGrouping<string, string> Component)>();
        var bottom = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var remove = new Button
        {
            Content = L.T("ui.inventory.removeSelectedComponents"),
            Style = (Style)Application.Current.FindResource("Primary"),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 4, 8, 4),
            IsEnabled = false,
        };
        remove.Click += (_, _) =>
        {
            var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Component).ToList();
            if (chosen.Count == 0)
                return;
            window.Close();
            ComponentRemoveRequested?.Invoke(
                g,
                chosen.SelectMany(c => c).ToList(),
                string.Join(", ", chosen.Select(c => c.Key))
            );
        };
        bottom.Children.Add(remove);
        bottom.Children.Add(InstallUi.Button(L.T("ui.common.cancel"), window.Close));
        root.Children.Add(bottom);
        var list = new StackPanel();
        foreach (var component in components)
        {
            var box = new CheckBox { Content = component.Key, Margin = new Thickness(0, 4, 0, 4) };
            box.Click += (_, _) => remove.IsEnabled = boxes.Any(b => b.Box.IsChecked == true);
            boxes.Add((box, component));
            list.Children.Add(box);
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = root;
        window.ShowDialog();
    }

    private void ShowFiles(ModGroup g)
    {
        if (_game is not { } game)
            return;
        var window = InstallUi.Window(
            Window.GetWindow(this),
            L.T("ui.inventory.componentFilesTitle", g.DisplayName),
            760,
            560
        );
        var root = new DockPanel { Margin = new Thickness(20) };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(Title(g.DisplayName, 19));
        var files = ComponentFiles(g);
        top.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.inventory.componentFilesCount", files.Count),
                Foreground = Res("Muted"),
                Margin = new Thickness(0, 4, 0, 10),
            }
        );
        root.Children.Add(top);
        var bottom = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(InstallUi.Button(L.T("ui.inventory.openGameFolder"), () =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(game.Root) { UseShellExecute = true }
                    );
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }));
        root.Children.Add(bottom);
        var list = new StackPanel();
        foreach (var rel in files)
        {
            bool exists = File.Exists(Path.Combine(game.Root, rel));
            list.Children.Add(
                new TextBlock
                {
                    Text = exists ? rel : rel + "  —  " + L.T("ui.inventory.fileMissing"),
                    Foreground = exists ? Res("Text") : Res("Muted"),
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(0, 2, 0, 2),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = root;
        window.ShowDialog();
    }

    /// <summary>Labels a mod: name, GTA5-Mods link (the name is fetched from the site) or readme.</summary>
    private async Task Label(ModGroup g)
    {
        var owner = Window.GetWindow(this);
        var dialog = new LabelWindow(owner, g.Recognized ? g.Name : "", g.Url ?? "");
        if (dialog.ShowDialog() != true)
            return;
        var labels = ModLabels.Default();
        if (g.Origin == ModOrigin.ModHub)
        {
            if (dialog.Url.Length > 0)
                labels.SetUrl(g.Name, dialog.Url);
        }
        else
            labels.Rename(g.Files, g.Name, dialog.ModName, dialog.Url.Length > 0 ? dialog.Url : null);
        await ScanAsync(_game, force: true);
    }

    private void OpenManual()
    {
        if (_report is null || _game is null)
            return;
        var external = _report
            .Groups.Where(g => g.Origin == ModOrigin.External)
            .SelectMany(g => g.Files.Select(f => (File: f, Group: g.Name)))
            .ToList();
        if (external.Count == 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                L.T("ui.inventory.noManuallyAddedFilesNothing"),
                L.T("ui.common.myMods")
            );
            return;
        }
        var w = new ManualGroupWindow(
            Window.GetWindow(this),
            external,
            _report.Groups.Where(g => g.Recognized).Select(g => g.Name).Distinct().ToList()
        );
        w.ShowDialog();
        if (w.Changed)
            _ = ScanAsync(_game, force: true);
    }

    private void SaveList()
    {
        if (_report is null || _game is null)
            return;
        var list = ModList.From(_report, _game);
        if (list.Mods.Count == 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                L.T("ui.inventory.nothingSaveGameHasNo"),
                L.T("ui.inventory.modList")
            );
            return;
        }
        var d = new SaveFileDialog
        {
            Filter = L.Join("|", L.T("filter.inventory.modHubModList"), "*.modlist.json"),
            FileName = L.T("ui.inventory.myGtaVModsModlist", _game.Edition, DateTime.Now),
            Title = L.T("ui.inventory.saveModList"),
        };
        if (d.ShowDialog(Window.GetWindow(this)) != true)
            return;
        list.Save(d.FileName);
        int links = list.Mods.Count(m => m.Downloadable);
        MessageBox.Show(
            Window.GetWindow(this),
            L.T(
                "ui.inventory.modsSavedIncludingGta5Mods",
                list.Mods.Count,
                links,
                Environment.NewLine,
                Environment.NewLine,
                (links < list.Mods.Count ? L.T("ui.inventory.modsWithoutLinksCannotRebuilt") : "")
            ),
            L.T("ui.inventory.modList"),
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );
    }

    private void OpenList()
    {
        if (_game is null)
            return;
        var d = new OpenFileDialog
        {
            Filter = L.Join("|", L.T("filter.inventory.modHubModList"), "*.modlist.json;*.json"),
            Title = L.T("ui.inventory.openModList"),
        };
        if (d.ShowDialog(Window.GetWindow(this)) != true)
            return;
        ModList list;
        try
        {
            list = ModList.Load(d.FileName);
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or InvalidDataException
                        or System.Text.Json.JsonException
                        or UnauthorizedAccessException
            )
        {
            MessageBox.Show(
                Window.GetWindow(this),
                L.T("ui.inventory.couldNotReadList", L.Owned(ex.Message)),
                L.T("ui.inventory.modList")
            );
            return;
        }
        int imported = list.ImportLabels(ModLabels.Default());
        var w = new RebuildWindow(Window.GetWindow(this), list, _game, _report, imported);
        if (w.ShowDialog() == true && w.Selected.Count > 0)
            RebuildRequested?.Invoke(w.Selected);
        _ = ScanAsync(_game, force: true);
    }

    /// <summary>Mod name from its GTA5-Mods page.</summary>
    public static async Task<string?> TitleFromGta5Mods(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !AuthorThanks.IsGta5Mods(uri.AbsoluteUri))
            return null;
        try
        {
            using var http = StarterPackage.CreateSiteClient();
            var html = await http.GetStringAsync(new Uri(AuthorThanks.ModPage(uri.AbsoluteUri)));
            return Gta5ModsPageParser.Parse(html)?.Title;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Label Mod dialog: name, GTA5-Mods link (the name is fetched from the site) or readme.</summary>
internal sealed class LabelWindow : Window
{
    private readonly TextBox _name = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBox _url = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBlock _status = new()
    {
        Foreground = (Brush)Application.Current.FindResource("Muted"),
        TextWrapping = TextWrapping.Wrap,
    };
    public string ModName => _name.Text.Trim();
    public string Url => _url.Text.Trim();

    public LabelWindow(Window owner, string name, string url)
    {
        Owner = owner;
        Title = L.T("ui.inventory.labelMod");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Bg");
        _name.Text = name;
        _url.Text = url;
        var p = new StackPanel { Margin = new Thickness(24) };
        Content = p;
        p.Children.Add(new TextBlock { Text = L.T("ui.inventory.linkModGta5Mods"), FontWeight = FontWeights.SemiBold });
        p.Children.Add(_url);
        var row = new WrapPanel();
        row.Children.Add(InstallUi.Button(L.T("ui.inventory.getNameFromPage"), async () =>
            {
                _status.Text = L.T("ui.inventory.loadingPage");
                var title = await MyModsPage.TitleFromGta5Mods(_url.Text);
                _status.Text = title is null
                    ? L.T("ui.inventory.couldNotReadNameCheck")
                    : L.T("ui.inventory.nameTakenFromPage");
                if (title is not null)
                    _name.Text = title;
            }));
        row.Children.Add(InstallUi.Button(L.T("ui.inventory.getFromReadme"), () =>
            {
                var d = new OpenFileDialog
                {
                    Filter = L.Join(
                        "|",
                        L.T("filter.inventory.readme"),
                        "*.txt;*.md;*.nfo",
                        L.T("filter.common.allFiles"),
                        "*.*"
                    ),
                    Title = L.T("ui.inventory.modReadme"),
                };
                if (d.ShowDialog(this) != true)
                    return;
                var title = ReadmeTitle.FromFile(d.FileName);
                _status.Text = title is null
                    ? L.T("ui.inventory.noNameFoundReadme")
                    : L.T("ui.inventory.nameTakenFromReadme");
                if (title is not null)
                    _name.Text = title;
                if (
                    _url.Text.Length == 0
                    && File.ReadAllText(d.FileName) is var text
                    && System.Text.RegularExpressions.Regex.Match(
                        text,
                        @"https://www\.gta5-mods\.com/[a-z]+/[a-z0-9\-]+",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    )
                        is { Success: true } m
                )
                    _url.Text = m.Value;
            }));
        p.Children.Add(row);
        p.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.inventory.modName"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 0),
            }
        );
        p.Children.Add(_name);
        p.Children.Add(_status);
        var ok = new Button
        {
            Content = L.T("ui.common.save"),
            Style = (Style)Application.Current.FindResource("Primary"),
            Margin = new Thickness(0, 14, 8, 0),
            IsDefault = true,
        };
        ok.Click += (_, _) =>
        {
            if (ModName.Length == 0)
            {
                _status.Text = L.T("ui.inventory.enterModName");
                return;
            }
            if (Url.Length > 0 && !AuthorThanks.IsGta5Mods(Url) && !Uri.TryCreate(Url, UriKind.Absolute, out _))
            {
                _status.Text = L.T("ui.inventory.linkDoesNotLookLike");
                return;
            }
            DialogResult = true;
        };
        var cancel = new Button
        {
            Content = L.T("ui.common.cancel"),
            Style = (Style)Application.Current.FindResource("Ghost"),
            Margin = new Thickness(0, 14, 0, 0),
            IsCancel = true,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        p.Children.Add(buttons);
    }
}

/// <summary>Group Manually dialog: select files and label them with a mod name. Labels are remembered.</summary>
internal sealed class ManualGroupWindow : Window
{
    public bool Changed { get; private set; }

    public ManualGroupWindow(Window owner, List<(ModFileItem File, string Group)> files, List<string> known)
    {
        Owner = owner;
        Title = L.T("ui.inventory.sortFilesIntoMods");
        Width = 900;
        Height = 640;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Bg");
        var root = new DockPanel { Margin = new Thickness(20) };
        Content = root;

        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.inventory.selectFilesBelongingOneMod"),
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
            }
        );
        top.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.inventory.modHubWillRememberThese"),
                Foreground = (Brush)Application.Current.FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10),
            }
        );
        root.Children.Add(top);

        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var name = new ComboBox
        {
            IsEditable = true,
            ItemsSource = known,
            MinWidth = 320,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var url = new TextBox
        {
            MinWidth = 300,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = L.T("ui.inventory.modLinkGta5ModsOptional"),
        };
        var status = new TextBlock
        {
            Foreground = (Brush)Application.Current.FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var row = new WrapPanel();
        row.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.inventory.mod"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            }
        );
        row.Children.Add(name);
        row.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.inventory.link"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            }
        );
        row.Children.Add(url);
        bottom.Children.Add(row);

        var list = new StackPanel();
        var boxes = new List<(CheckBox Box, ModFileItem File)>();
        foreach (var group in files.GroupBy(f => f.Group).OrderBy(g => g.Key))
        {
            list.Children.Add(
                new TextBlock
                {
                    Text = group.Key,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 10, 0, 4),
                }
            );
            foreach (var (file, _) in group)
            {
                var cb = new CheckBox
                {
                    Content = L.T("ui.inventory.text", file.Rel, BackupLibrary.FormatSize(file.Size)),
                    Margin = new Thickness(8, 2, 0, 2),
                };
                boxes.Add((cb, file));
                list.Children.Add(cb);
            }
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(InstallUi.Button(L.T("ui.inventory.getNameFromLink"), async () =>
            {
                status.Text = L.T("ui.inventory.loadingPage");
                var t = await MyModsPage.TitleFromGta5Mods(url.Text);
                status.Text = t is null
                    ? L.T("ui.inventory.couldNotReadNameFrom")
                    : L.T("ui.inventory.nameTakenFromPage");
                if (t is not null)
                    name.Text = t;
            }));
        var apply = new Button
        {
            Content = L.T("ui.inventory.labelSelectedFiles"),
            Style = (Style)Application.Current.FindResource("Primary"),
            Margin = new Thickness(0, 4, 8, 4),
        };
        apply.Click += (_, _) =>
        {
            var chosen = boxes.Where(b => b.Box.IsChecked == true).ToList();
            if (chosen.Count == 0)
            {
                status.Text = L.T("ui.inventory.selectFiles");
                return;
            }
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                status.Text = L.T("ui.inventory.enterModName");
                return;
            }
            ModLabels
                .Default()
                .Assign(chosen.Select(c => c.File), name.Text, url.Text.Trim().Length > 0 ? url.Text.Trim() : null);
            foreach (var c in chosen)
            {
                c.Box.IsChecked = false;
                c.Box.Content += L.T("ui.inventory.linkTarget", name.Text.Trim());
            }
            if (!known.Contains(name.Text.Trim()))
                known.Add(name.Text.Trim());
            name.ItemsSource = null;
            name.ItemsSource = known;
            Changed = true;
            status.Text = L.T("ui.inventory.filesLabeled", chosen.Count, name.Text.Trim());
        };
        actions.Children.Add(apply);
        var close = new Button
        {
            Content = L.T("ui.common.done"),
            Style = (Style)Application.Current.FindResource("Ghost"),
            Margin = new Thickness(0, 4, 0, 4),
            IsCancel = true,
        };
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        bottom.Children.Add(actions);
        bottom.Children.Add(status);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
}
