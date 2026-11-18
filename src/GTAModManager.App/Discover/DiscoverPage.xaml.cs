using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Discover;

/// <summary>
/// Recommendations page: a showcase of cards from the built-in catalog. Data comes from the cache immediately and refreshes in the background
/// (at most every 12 hours or on demand). Offline, saved covers and stats are shown.
/// </summary>
public partial class DiscoverPage : UserControl
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(int),
        typeof(DiscoverPage),
        new PropertyMetadata(3)
    );

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private const double MinCardWidth = 290;

    private readonly DiscoverCatalog _catalog;
    private readonly List<CatalogCategory> _categories;
    private readonly List<ModCardViewModel> _cards;
    private readonly CustomCards _customCards = new();

    /// <summary>Built-in cards removed by hand from editable tabs (category id → card ids).</summary>
    private readonly Dictionary<string, HashSet<string>> _hidden = [];
    private ModMetadataProvider? _provider;
    private CancellationTokenSource? _cts;
    private string _category;
    private string _edition = "all";
    private string _sort = "default";

    /// <summary>"Edit list" is on: cards show the order arrows and "Delete card".</summary>
    private bool _editing;
    private bool _refreshing;
    private int _lastErrors;

    /// <summary>Opens the mod page (the window decides between the embedded and the system browser).</summary>
    public event Action<Uri>? OpenRequested;

    /// <summary>Install button on a card (the window performs the download and installation).</summary>
    public event Action<ModCardViewModel>? InstallRequested;

    public DiscoverPage()
    {
        InitializeComponent();
        _catalog = DiscoverCatalog.LoadBuiltIn();
        _categories =
        [
            .. _catalog.Categories,
            new CatalogCategory
            {
                Id = CustomCards.UserCategory,
                Title = L.T("ui.discover.myGta5ModsCards"),
                Subtitle = L.T("ui.discover.myGta5ModsCardsSubtitle"),
            },
        ];
        _cards = _catalog.Entries.Where(e => !e.Hidden).Select(e => new ModCardViewModel(e)).ToList();
        _cards.AddRange(LoadCustomCards());
        LoadHidden();
        _category = _categories.FirstOrDefault()?.Id ?? "";
        BuildTabs();
        BuildChips();
        UpdateSearchHint();
    }

    private ModMetadataProvider Provider => _provider ??= ModMetadataProvider.Shared;

    /// <summary>Tab opened: show cached data and refresh in the background if needed.</summary>
    public void Show()
    {
        foreach (var c in _cards)
            c.Apply(Provider.Get(c.Entry.Id), Provider);
        ApplyView();
        RefreshStatuses();
        if (!_refreshing && _cards.Any(c => Provider.NeedsRefresh(c.Entry, ModMetadataProvider.DefaultMaxAge)))
            _ = RefreshAsync(force: false);
        else
            UpdateStatus();
    }

    /// <summary>Stops background requests (application exit).</summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        // The provider is shared with My Mods; its HTTP client lives until the process exits.
        _provider = null;
    }

    public void SetGame(GTAModManager.Core.Game.GameInstall? game)
    {
        _game = game;
        foreach (var card in _cards)
            card.SetGame(game);
        RefreshStatuses();
    }

    // ---------- Installed state and updates ----------

    private GTAModManager.Core.Game.GameInstall? _game;
    private readonly InstalledMods _installed = new();

    /// <summary>Card statuses were recalculated (for the updates bell).</summary>
    public event Action? StatusesChanged;

    /// <summary>Cards of mods that have an update (for the selected game).</summary>
    public IReadOnlyList<ModCardViewModel> Updates =>
        _cards.Where(c => c.Status == CatalogInstallStatus.UpdateAvailable).ToList();

    public CatalogEntry? FindEntry(string? id, string? url)
    {
        if (id is not null && _catalog.Entries.FirstOrDefault(e => e.Id == id) is { } byId)
            return byId;
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u))
            return null;
        static string Norm(Uri x) =>
            x.Host.ToLowerInvariant().Replace("www.", "") + x.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        return _catalog.Entries.FirstOrDefault(e =>
            Uri.TryCreate(e.Url, UriKind.Absolute, out var eu) && Norm(eu) == Norm(u)
        );
    }

    /// <summary>Mod version on the site (from the page cache), treated as installed right after installation.</summary>
    public string? SiteVersion(string id) => Provider.Get(id)?.Data.Version;

    /// <summary>Reconciles Installed / Update available for the selected game (files are checked in the background).</summary>
    public async void RefreshStatuses()
    {
        var game = _game;
        var snapshot = _cards.Select(c => (Card: c, Version: Provider.Get(c.Entry.Id)?.Data.Version)).ToList();
        List<(ModCardViewModel Card, CatalogInstallStatus Status)> result;
        try
        {
            result = game is null
                ? snapshot.Select(s => (s.Card, CatalogInstallStatus.NotInstalled)).ToList()
                : await Task.Run(() =>
                    snapshot.Select(s => (s.Card, _installed.Resolve(game, s.Card.Entry, s.Version))).ToList()
                );
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (!ReferenceEquals(game, _game))
            return; // another game was selected while we were checking
        foreach (var (card, status) in result)
            card.Status = status;
        StatusesChanged?.Invoke();
    }

    /// <summary>Background update check: latest versions from the pages of installed mods (at most every 12 hours per mod).</summary>
    public async Task CheckUpdatesAsync()
    {
        if (_refreshing || _game is null)
            return;
        var installed = _cards
            .Where(c => c.Status != CatalogInstallStatus.NotInstalled && c.Entry.Source != MetaSource.None)
            .Select(c => c.Entry)
            .ToList();
        if (installed.Count > 0 && installed.Any(e => Provider.NeedsRefresh(e, ModMetadataProvider.DefaultMaxAge)))
        {
            _refreshing = true;
            var provider = Provider;
            try
            {
                await Task.Run(() =>
                    provider.RefreshAsync(
                        installed,
                        false,
                        updated =>
                            Dispatcher.InvokeAsync(() =>
                            {
                                if (_cards.FirstOrDefault(c => c.Entry.Id == updated.Id) is { } card)
                                    card.Apply(updated, provider);
                            }),
                        CancellationToken.None
                    )
                );
            }
            catch (Exception ex)
                when (ex
                        is OperationCanceledException
                            or ObjectDisposedException
                            or System.Net.Http.HttpRequestException
                            or System.IO.IOException
                ) { }
            finally
            {
                _refreshing = false;
            }
        }
        RefreshStatuses();
    }

    /// <summary>Shows a mod card: switches to its category and clears the search.</summary>
    public void FocusCard(string id)
    {
        if (_cards.FirstOrDefault(c => c.Entry.Id == id) is not { } card)
            return;
        _category = card.Entry.Category;
        _edition = "all";
        _editing = false;
        SearchBox.Text = "";
        BuildTabs();
        BuildChips();
        ApplyView();
    }

    public long CacheSize() => Provider.CacheSize();

    public void ClearCache()
    {
        _cts?.Cancel();
        Provider.Clear();
        foreach (var c in _cards)
            c.Apply(null, Provider);
        UpdateStatus();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync(force: true);

    private async Task RefreshAsync(bool force)
    {
        if (_refreshing)
            return;
        _refreshing = true;
        var cts = _cts = new CancellationTokenSource();
        RefreshButton.IsEnabled = false;
        StatusText.Text = L.T("ui.discover.loadingDataFromModWebsites");
        // Current category first: those are the cards the user is looking at.
        var order = _cards.OrderBy(c => c.Entry.Category == _category ? 0 : 1).ThenBy(c => c.Entry.Order).ToList();
        foreach (var c in order)
        {
            c.Refreshing = true;
            c.Apply(c.Cached, Provider);
        }
        var provider = Provider;
        try
        {
            _lastErrors = await Task.Run(
                () =>
                    provider.RefreshAsync(
                        order.Select(c => c.Entry),
                        force,
                        updated =>
                            Dispatcher.InvokeAsync(() =>
                            {
                                if (_cards.FirstOrDefault(c => c.Entry.Id == updated.Id) is { } card)
                                {
                                    card.Refreshing = false;
                                    card.Apply(updated, provider);
                                }
                                if (_sort != "default")
                                    ApplyView();
                            }),
                        cts.Token
                    ),
                cts.Token
            );
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _refreshing = false;
            if (_cts == cts)
                _cts = null;
            if (_provider == provider)
            {
                foreach (var c in _cards)
                {
                    c.Refreshing = false;
                    c.Apply(provider.Get(c.Entry.Id), provider);
                }
                RefreshButton.IsEnabled = true;
                ApplyView();
                UpdateStatus();
                RefreshStatuses(); // a new version may have been published
            }
        }
    }

    private void UpdateStatus()
    {
        var cached = _cards.Select(c => c.Cached).Where(c => c is not null).ToList();
        var ok = cached.Where(c => c!.FetchedAt > DateTimeOffset.MinValue).Select(c => c!.FetchedAt).ToList();
        var failed = cached.Count(c => c!.LastErrorAt is { } t && t > c.FetchedAt);
        if (ok.Count == 0)
            StatusText.Text = failed > 0 ? L.T("ui.discover.cannotConnectModSitesData") : "";
        else if (failed > 0)
            StatusText.Text = L.T(
                "ui.discover.someDataCouldNotRefreshed",
                failed,
                L.T("format.datetime.readable", ok.Min().ToLocalTime())
            );
        else
            StatusText.Text = L.T("ui.discover.updated2", L.T("format.datetime.readable", ok.Max().ToLocalTime()));
    }

    // ---------- Categories, filters, sorting ----------

    private void BuildTabs()
    {
        CategoryTabs.Children.Clear();
        foreach (var cat in _categories)
        {
            var count = _cards.Count(c => c.Entry.Category == cat.Id && Shown(c));
            var rb = new RadioButton
            {
                Style = (Style)FindResource("TabOption"),
                GroupName = "discover-cat",
                Content = L.T("ui.common.text", cat.Title, count),
                IsChecked = cat.Id == _category,
                Margin = new Thickness(0, 0, 28, 0),
            };
            rb.Checked += (_, _) =>
            {
                _category = cat.Id;
                _sort = "default";
                _editing = false;
                BuildChips();
                ApplyView();
            };
            CategoryTabs.Children.Add(rb);
        }
    }

    private void BuildChips()
    {
        EditionChips.Children.Clear();
        foreach (
            var (id, text, tip) in new[]
            {
                ("all", L.T("ui.discover.all"), L.T("ui.discover.allCards")),
                ("legacy", "Legacy", L.T("ui.discover.onlyConfirmedGtaVLegacy")),
                ("enhanced", "Enhanced", L.T("ui.discover.confirmedGtaVEnhancedSupport")),
            }
        )
            EditionChips.Children.Add(Chip("discover-ed", text, tip, _edition == id, () =>
                {
                    _edition = id;
                    ApplyView();
                }));

        SortChips.Children.Clear();
        var cat = _categories.FirstOrDefault(c => c.Id == _category);
        var defaultText = cat?.DefaultSort == "kind" ? L.T("ui.discover.byType") : L.T("ui.discover.authorSOrder");
        foreach (
            var (id, text) in new[]
            {
                ("default", defaultText),
                ("popular", L.T("ui.discover.popularity")),
                ("rating", L.T("ui.discover.rating")),
                ("name", L.T("ui.discover.name")),
                ("updated", L.T("ui.discover.update")),
            }
        )
            SortChips.Children.Add(Chip("discover-sort", text, null, _sort == id, () =>
                {
                    _sort = id;
                    ApplyView();
                }));
    }

    private RadioButton Chip(string group, string text, string? tip, bool isChecked, Action onChecked)
    {
        var rb = new RadioButton
        {
            Style = (Style)FindResource("UnitChip"),
            GroupName = group,
            Content = text,
            IsChecked = isChecked,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = tip,
        };
        rb.Checked += (_, _) => onChecked();
        return rb;
    }

    private void ApplyView()
    {
        var cat = _categories.FirstOrDefault(c => c.Id == _category);
        if (cat is not null)
        {
            Hero.Visibility = cat.Hero ? Visibility.Visible : Visibility.Collapsed;
            CategoryText.Visibility = cat.Hero ? Visibility.Collapsed : Visibility.Visible;
            HeroTitle.Text = GTAModManager.Core.Localization.L.Display(cat.Title);
            HeroText.Text = GTAModManager.Core.Localization.L.Display(cat.Subtitle);
            CategoryText.Text = GTAModManager.Core.Localization.L.Display(cat.Subtitle);
        }

        var q = SearchBox.Text.Trim();
        IEnumerable<ModCardViewModel> list = _cards.Where(c => c.Entry.Category == _category && Shown(c));
        if (q.Length > 0)
            list = list.Where(c =>
                c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Entry.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            );
        if (_edition != "all")
            list = list.Where(c => c.MayRunOn(_edition == "enhanced"));

        list = _sort switch
        {
            "popular" => list.OrderByDescending(c => c.SortDownloads).ThenBy(c => c.Entry.Order),
            "rating" => list.OrderByDescending(c => c.SortRating).ThenByDescending(c => c.SortDownloads),
            "name" => list.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase),
            "updated" => list.OrderByDescending(c => c.SortUpdated).ThenBy(c => c.Entry.Order),
            // Essentials: component kind, then compatibility (both editions first), then catalog order.
            _ when cat?.DefaultSort == "kind" => list.OrderBy(c => c.Entry.KindOrder)
                .ThenByDescending(c => (c.Compat.Legacy == true ? 1 : 0) + (c.Compat.Enhanced == true ? 1 : 0))
                .ThenBy(c => c.Entry.Order),
            // Editable tabs keep the order set with the arrows.
            _ when EditableCollection(_category) is not null => TabOrder(_category) is var order
                ? list.OrderBy(c => order.IndexOf(c.Entry.Id))
                : list,
            _ => list.OrderBy(c => c.Entry.Order),
        };
        var items = list.ToList();
        // Moving is shown only over the whole tab in its own order, so a step is never hidden by a filter.
        bool editable = EditableCollection(_category) is not null;
        bool editing = editable && _editing;
        bool reorder = editing && _sort == "default" && q.Length == 0 && _edition == "all";
        foreach (var card in _cards)
        {
            bool here = card.Entry.Category == _category;
            card.CanReorder = reorder && here;
            card.CanDelete = editing && here;
        }
        for (int i = 0; i < items.Count; i++)
            items[i].Position = i + 1;
        Cards.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text =
            _edition != "all" ? L.T("ui.discover.noCardsHaveConfirmedSupport")
            : q.Length == 0 && _category == CustomCards.UserCategory ? L.T("ui.discover.noCustomCardsYet")
            : L.T("ui.discover.nothingFound");
        EditorBar.Visibility = EditableCollection(_category) is null ? Visibility.Collapsed : Visibility.Visible;
        ExportButton.Visibility =
            _category == CustomCards.RecommendedCategory ? Visibility.Visible : Visibility.Collapsed;
        EditListButton.Content = L.T(editing ? "ui.discover.editListDone" : "ui.discover.editList");
        var hidden = editing ? HiddenIn(_category).Count : 0;
        RestoreHiddenButton.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreHiddenButton.Content = L.T("ui.discover.restoreHidden", hidden);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchHint();
        if (IsLoaded)
            ApplyView();
    }

    private void OnSearchFocus(object sender, RoutedEventArgs e) => UpdateSearchHint();

    private void UpdateSearchHint() =>
        SearchHint.Visibility =
            SearchBox.Text.Length == 0 && !SearchBox.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 3–4 cards on a wide window, fewer on a narrow one.
        var width = e.NewSize.Width + 16;
        Columns = Math.Clamp((int)(width / (MinCardWidth + 16)), 1, 4);
    }

    // ---------- Actions ----------

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement { Tag: ModCardViewModel card })
            return;
        if (LinkSafety.IsSafeWebUrl(card.Entry.Url, out var uri) && uri is not null)
            OpenRequested?.Invoke(uri);
    }

    private void OnOpenPageClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (
            sender is FrameworkElement { Tag: ModCardViewModel card }
            && LinkSafety.IsSafeWebUrl(card.Entry.Url, out var uri)
            && uri is not null
        )
            OpenRequested?.Invoke(uri);
    }

    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: ModCardViewModel card } && card.InstallEnabled)
            InstallRequested?.Invoke(card);
    }

    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        // Links to the author's platforms (YouTube, Discord…) open in the system browser, validated https only.
        if (sender is FrameworkElement { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out var u))
            BrowserView.OpenExternal(u);
    }

    // ---------- Custom cards ----------

    /// <summary>The user's cards, plus Recommended drafts while that tab's editor is enabled.</summary>
    private IEnumerable<ModCardViewModel> LoadCustomCards() =>
        _customCards
            .Load()
            .Where(c => c.Collection == CustomCardCollection.User || CustomCards.RecommendedEditorEnabled)
            .Select(c => new ModCardViewModel(c));

    /// <summary>Which custom collection the tab edits, or null for a read-only tab.</summary>
    private static CustomCardCollection? EditableCollection(string category) =>
        category == CustomCards.UserCategory ? CustomCardCollection.User
        : category == CustomCards.RecommendedCategory && CustomCards.RecommendedEditorEnabled
            ? CustomCardCollection.Recommended
        : null;

    private void ReloadCustomCards()
    {
        _cards.RemoveAll(c => c.Custom is not null);
        var custom = LoadCustomCards().ToList();
        foreach (var card in custom)
        {
            card.SetGame(_game);
            card.Apply(Provider.Get(card.Entry.Id), Provider);
        }
        _cards.AddRange(custom);
        BuildTabs();
        ApplyView();
        RefreshStatuses();
    }

    private void OnAddCardClick(object sender, RoutedEventArgs e)
    {
        if (EditableCollection(_category) is { } collection)
            _ = EditCardAsync(collection, null);
    }

    private void OnEditCardClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: ModCardViewModel { Custom: { } custom } })
            _ = EditCardAsync(custom.Collection, custom);
    }

    private async Task EditCardAsync(CustomCardCollection collection, CustomCard? existing)
    {
        var window = new CustomCardWindow(Window.GetWindow(this), Provider, collection, existing);
        if (window.ShowDialog() != true || window.Result is not { } result || window.Page is not { } page)
            return;
        CustomCard saved;
        try
        {
            // A changed link is a different card: the old one is replaced, not kept alongside.
            if (existing is not null && CustomCards.Normalize(result.Url) != existing.Url)
            {
                _customCards.Remove(existing.Id);
                Provider.Forget(existing.Id);
            }
            saved = _customCards.Save(result);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                L.T("ui.discover.couldNotSaveCard", L.Owned(ex.Message)),
                "Mod Hub by VirtuOzZ",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }
        ReloadCustomCards();
        StatusText.Text = L.T("ui.discover.cardSaved", saved.Title);
        try
        {
            var cached = await Provider.RememberAsync(saved.ToCatalogEntry(), page, CancellationToken.None);
            if (_cards.FirstOrDefault(c => c.Entry.Id == cached.Id) is { } card)
                card.Apply(cached, Provider);
        }
        catch (Exception ex)
            when (ex
                    is System.Net.Http.HttpRequestException
                        or System.IO.IOException
                        or OperationCanceledException
                        or System.IO.InvalidDataException
            ) { }
        RefreshStatuses();
    }

    private void OnDeleteCardClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: ModCardViewModel card } || EditableCollection(_category) is null)
            return;
        var custom = card.Custom;
        if (
            MessageBox.Show(
                Window.GetWindow(this),
                L.T(custom is null ? "ui.discover.hideCardConfirm" : "ui.discover.deleteCardConfirm", card.Title),
                L.T("ui.discover.deleteCard"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No
            ) != MessageBoxResult.Yes
        )
            return;
        try
        {
            if (custom is null)
            {
                // A built-in catalog card: it stays in the catalog, the tab just stops showing it (can be restored).
                _customCards.Hide(_category, card.Entry.Id);
                LoadHidden();
                BuildTabs();
                ApplyView();
                return;
            }
            _customCards.Remove(custom.Id);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                L.T("ui.discover.couldNotSaveCard", L.Owned(ex.Message)),
                "Mod Hub by VirtuOzZ",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }
        Provider.Forget(custom.Id);
        ReloadCustomCards();
    }

    private void OnEditListClick(object sender, RoutedEventArgs e)
    {
        _editing = !_editing;
        // The arrows follow the author's order: switch to it, so moving a card is never hidden by a sort.
        if (_editing && _sort != "default")
        {
            _sort = "default";
            BuildChips();
        }
        ApplyView();
    }

    private void OnRestoreHiddenClick(object sender, RoutedEventArgs e)
    {
        if (EditableCollection(_category) is null)
            return;
        try
        {
            _customCards.RestoreHidden(_category);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText.Text = L.T("ui.discover.couldNotSaveCard", L.Owned(ex.Message));
            return;
        }
        LoadHidden();
        BuildTabs();
        ApplyView();
    }

    private void LoadHidden()
    {
        _hidden.Clear();
        foreach (var category in new[] { CustomCards.UserCategory, CustomCards.RecommendedCategory })
            _hidden[category] = _customCards.LoadHidden(category);
    }

    private HashSet<string> HiddenIn(string category) => _hidden.GetValueOrDefault(category) ?? [];

    /// <summary>False for a built-in card removed by hand from an editable tab.</summary>
    private bool Shown(ModCardViewModel card) =>
        card.Custom is not null
        || EditableCollection(card.Entry.Category) is null
        || !HiddenIn(card.Entry.Category).Contains(card.Entry.Id);

    /// <summary>
    /// The tab's cards in the order set with the arrows; cards never moved follow in the catalog order
    /// (built-in cards first, then the custom ones as added).
    /// </summary>
    private List<string> TabOrder(string category)
    {
        var cards = _cards.Where(c => c.Entry.Category == category && Shown(c)).ToList();
        var known = cards.Select(c => c.Entry.Id).ToHashSet();
        var order = _customCards.LoadOrder(category).Where(known.Contains).ToList();
        order.AddRange(
            cards
                .OrderBy(c => c.Custom is null ? 0 : 1)
                .ThenBy(c => c.Entry.Order)
                .Select(c => c.Entry.Id)
                .Where(id => !order.Contains(id))
        );
        return order;
    }

    private void OnMoveEarlierClick(object sender, RoutedEventArgs e) => MoveCard(sender, e, -1);

    private void OnMoveLaterClick(object sender, RoutedEventArgs e) => MoveCard(sender, e, 1);

    private void MoveCard(object sender, RoutedEventArgs e, int step)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: ModCardViewModel card } || EditableCollection(_category) is null)
            return;
        var order = TabOrder(_category);
        if (!CustomCards.Move(order, card.Entry.Id, step))
            return;
        try
        {
            _customCards.SaveOrder(_category, order);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText.Text = L.T("ui.discover.couldNotSaveCard", L.Owned(ex.Message));
            return;
        }
        ApplyView();
    }

    /// <summary>Recommended drafts and the tab order as catalog.json data, to be pasted into the built-in catalog.</summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var drafts = _customCards.Load(CustomCardCollection.Recommended);
        var hidden = HiddenIn(CustomCards.RecommendedCategory);
        if (
            drafts.Count == 0
            && hidden.Count == 0
            && _customCards.LoadOrder(CustomCards.RecommendedCategory).Count == 0
        )
        {
            StatusText.Text = L.T("ui.discover.nothingToExport");
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "recommended-entries.json",
            Filter = "JSON|*.json",
            Title = L.T("ui.discover.exportToCatalog"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            System.IO.File.WriteAllText(
                dialog.FileName,
                CustomCards.ExportCatalogEntries(drafts, TabOrder(CustomCards.RecommendedCategory), hidden)
            );
            StatusText.Text = L.T("ui.discover.exported", drafts.Count, dialog.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText.Text = L.T("ui.discover.couldNotSaveCard", L.Owned(ex.Message));
        }
    }
}
