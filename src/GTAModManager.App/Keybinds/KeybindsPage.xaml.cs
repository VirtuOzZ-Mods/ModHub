using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.Core.Game;
using GTAModManager.Core.Keybinds;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Keybinds;

public sealed record ConflictLine(string Text, Brush Brush, KeybindConflict? Conflict = null)
{
    public Visibility AcknowledgeVisibility => Conflict is null ? Visibility.Collapsed : Visibility.Visible;
}

public sealed record EntryView(
    string Id,
    string ModName,
    Brush ModBrush,
    string Action,
    string ChordText,
    string ContextText,
    string Where,
    string StatusText,
    Brush StatusBrush,
    string? Note,
    List<ConflictLine> Conflicts,
    bool CanEdit,
    bool CanDefault,
    string DefaultText,
    bool CanConfirm,
    bool CanDisable
)
{
    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditVisibility => CanEdit ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DefaultVisibility => CanDefault ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConfirmVisibility => CanConfirm ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DisableVisibility => CanDisable ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>A mod's gamepad button: a GTA control shown with the chosen controller's button name.</summary>
public sealed record PadView(string Id, string ModName, Brush ModBrush, string Action, string ButtonText, string Where);

/// <summary>
/// Keybind Manager: mod bindings and the standard GTA V controls on a virtual keyboard,
/// overlaps, and safe reassignment of supported INI hotkeys.
/// </summary>
public partial class KeybindsPage : UserControl
{
    // Mod colours: distinguishable, not red/yellow (used for conflicts) and not grey-blue (GTA V defaults).
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x2E, 0xC4, 0xB6),
        Color.FromRgb(0x9B, 0x5D, 0xE5),
        Color.FromRgb(0xF1, 0x5B, 0xB5),
        Color.FromRgb(0x00, 0xBB, 0xF9),
        Color.FromRgb(0x80, 0xED, 0x99),
        Color.FromRgb(0xFF, 0x99, 0x00),
        Color.FromRgb(0xB3, 0x88, 0xEB),
        Color.FromRgb(0x4D, 0x96, 0xFF),
        Color.FromRgb(0x9A, 0xE6, 0xE6),
        Color.FromRgb(0xE0, 0x7A, 0x5F),
        Color.FromRgb(0x7B, 0xD3, 0x89),
        Color.FromRgb(0xC7, 0x7D, 0xFF),
    ];
    private static readonly Brush GameBrush = Frozen(Color.FromRgb(0x5B, 0x6E, 0x96));
    private static readonly Brush DirectBrush = Frozen(Color.FromRgb(0xE5, 0x38, 0x3B));
    private static readonly Brush PotentialBrush = Frozen(Color.FromRgb(0xF4, 0xD3, 0x5E));
    private static readonly Brush DirectBg = Frozen(Color.FromRgb(0x4A, 0x16, 0x18));
    private static readonly Brush PotentialBg = Frozen(Color.FromRgb(0x3D, 0x34, 0x12));
    private static readonly Brush GameBg = Frozen(Color.FromRgb(0x22, 0x29, 0x38));
    private static readonly Brush ModBg = Frozen(Color.FromRgb(0x2E, 0x31, 0x37));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0xA7, 0xAB, 0xB2));
    private static readonly Brush OkBrush = Frozen(Color.FromRgb(0x3D, 0xDC, 0x84));

    private readonly VirtualKeyboard _keyboard = new();
    private GameInstall? _game;
    private Func<string[], Task<(int code, string output)>>? _runGmm;
    private CancellationTokenSource? _cts;
    private KeybindScanResult? _scan;
    private List<KeybindEntry> _all = [];
    private List<KeybindEntry> _visible = [];
    private List<KeybindConflict> _conflicts = [];
    private Dictionary<int, KeyState> _states = [];
    private readonly Dictionary<string, Brush> _modBrushes = [];
    private string? _modFilter;
    private bool _showGame = true;
    private bool _gamepad;
    private GamepadLayout _layout = GamepadControls.ParseLayout(AppSettings.Load().GamepadLayout);

    public KeybindsPage()
    {
        InitializeComponent();
        KeyboardHost.Content = _keyboard;
        _keyboard.VisualFor = VisualFor;
        _keyboard.KeyClicked += vk =>
        {
            _keyboard.Selected = _keyboard.Selected == vk ? 0 : vk;
            ShowDetails();
        };
        BuildLegend();
        (_layout == GamepadLayout.PlayStation ? PlayStationLayout : XboxLayout).IsChecked = true;
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
                _window.PreviewMouseDown += OnWindowMouseDown;
        };
        Unloaded += (_, _) =>
        {
            Cancel();
            if (_window is not null)
                _window.PreviewMouseDown -= OnWindowMouseDown;
            _window = null;
        };
    }

    private Window? _window;

    // A mouse click anywhere in the window except the search box removes its focus: the caret disappears and the hint returns.
    private void OnWindowMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!SearchBox.IsKeyboardFocusWithin)
            return;
        if (e.OriginalSource is DependencyObject d && IsInside(d, SearchBox))
            return;
        System.Windows.Input.Keyboard.ClearFocus();
    }

    private static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (
            var x = d;
            x is not null;
            x = x is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(x)
                    : LogicalTreeHelper.GetParent(x)
        )
            if (x == container)
                return true;
        return false;
    }

    /// <summary>Cancels the scan (game change, window closing).</summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public async Task LoadAsync(GameInstall? game, Func<string[], Task<(int, string)>> runGmm)
    {
        _game = game;
        _runGmm = runGmm;
        await RescanAsync();
    }

    private async Task RescanAsync()
    {
        Cancel();
        if (_game is null)
        {
            SummaryText.Text = L.T("ui.common.selectGameLeft");
            return;
        }
        var cts = _cts = new CancellationTokenSource();
        RescanButton.IsEnabled = false;
        SummaryText.Text = L.T("ui.keybinds.scanningModConfigurations");
        try
        {
            var settings = AppSettings.Load();
            var scanner = new KeybindScanner(_game, settings.OpenLibrary());
            _scan = await scanner.ScanAsync(cts.Token);
            _all = [.. _scan.Entries, .. GameKeybindProvider.Defaults(_game.Edition)];
            AssignColors();
            BuildModFilter();
            Apply();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SummaryText.Text = L.T("ui.keybinds.couldNotScan", L.Owned(ex.Message));
        }
        finally
        {
            if (_cts == cts)
                RescanButton.IsEnabled = true;
        }
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await RescanAsync();

    private void AssignColors()
    {
        _modBrushes.Clear();
        int i = 0;
        foreach (var mod in ModGroups().Select(g => g.Id).Order())
            _modBrushes[mod] = Frozen(Palette[i++ % Palette.Length]);
        _modBrushes[GameKeybindProvider.ModId] = GameBrush;
    }

    private Brush BrushFor(KeybindEntry e) => _modBrushes.GetValueOrDefault(e.ModId) ?? Muted;

    // ---------- Filters ----------

    /// <summary>Name of a mod or a Mod Hub operation (its title is stored in the install-time language), in the current language.</summary>
    private static string ModTitle(string name) => global::GTAModManager.Core.Mods.ModNames.DisplayOperationTitle(name);

    private void BuildModFilter()
    {
        ModFilterPanel.Children.Clear();
        void Chip(string? id, string text, Brush? dot)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (dot is not null)
                content.Children.Add(
                    new Border
                    {
                        Width = 9,
                        Height = 9,
                        CornerRadius = new CornerRadius(5),
                        Background = dot,
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                );
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton
            {
                Style = (Style)FindResource("UnitChip"),
                Content = content,
                GroupName = "modfilter",
                Margin = new Thickness(0, 0, 6, 6),
                IsChecked = _modFilter == id,
            };
            rb.Checked += (_, _) =>
            {
                _modFilter = id;
                Apply();
            };
            ModFilterPanel.Children.Add(rb);
        }
        var groups = ModGroups().ToList();
        if (_modFilter is not null && groups.All(g => g.Id != _modFilter))
            _modFilter = null;
        Chip(null, L.T("ui.keybinds.allMods"), null);
        foreach (var g in groups.OrderBy(g => g.Name))
            Chip(g.Id, $"{ModTitle(g.Name)} ({g.Count})", _modBrushes[g.Id]);
    }

    /// <summary>Mods with keyboard keys or gamepad buttons: id, name, number of bindings.</summary>
    private IEnumerable<(string Id, string Name, int Count)> ModGroups() =>
        _all.Where(e => !e.IsGame)
            .Select(e => (Id: e.ModId, Name: e.ModName))
            .Concat((_scan?.Pads ?? []).Select(p => (Id: p.ModId, Name: p.ModName)))
            .GroupBy(x => x.Id)
            .Select(g => (g.Key, g.First().Name, g.Count()));

    // ---------- Keyboard / gamepad ----------

    private void OnViewChanged(object sender, RoutedEventArgs e)
    {
        if (KeyboardPanel is null)
            return; // during InitializeComponent
        _gamepad = GamepadView.IsChecked == true;
        KeyboardPanel.Visibility = _gamepad ? Visibility.Collapsed : Visibility.Visible;
        Legend.Visibility = _gamepad ? Visibility.Collapsed : Visibility.Visible;
        LayoutChips.Visibility = _gamepad ? Visibility.Visible : Visibility.Collapsed;
        ShowDetails();
    }

    private void OnLayoutChanged(object sender, RoutedEventArgs e)
    {
        var layout = PlayStationLayout.IsChecked == true ? GamepadLayout.PlayStation : GamepadLayout.Xbox;
        if (layout == _layout)
            return;
        _layout = layout;
        try
        {
            var settings = AppSettings.Load();
            settings.GamepadLayout = layout == GamepadLayout.PlayStation ? "playstation" : "xbox";
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (IsLoaded)
            ShowDetails();
    }

    private string PadButtonText(int control) =>
        GamepadControls.Find(control) is { } c ? GamepadControls.Label(c.Button, _layout) : control.ToString();

    private void ShowPads()
    {
        var q = SearchBox.Text.Trim();
        var pads = (_scan?.Pads ?? [])
            .Where(p => _modFilter is null || p.ModId == _modFilter)
            .Select(p => new PadView(
                p.Id,
                ModTitle(p.ModName),
                _modBrushes.GetValueOrDefault(p.ModId) ?? Muted,
                L.Display(p.Action),
                PadButtonText(p.Control),
                L.T(
                    "ui.keybinds.padWhere",
                    p.File,
                    p.Section,
                    p.Key,
                    p.Raw.Trim(),
                    GamepadControls.Find(p.Control)?.Name
                )
            ))
            .Where(v =>
                q.Length == 0
                || v.Action.Contains(q, StringComparison.OrdinalIgnoreCase)
                || v.ModName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || v.ButtonText.Contains(q, StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(v => v.ModName)
            .ThenBy(v => v.Action)
            .ToList();
        DetailsTitle.Text = pads.Count == 0 ? L.T("ui.keybinds.padNone") : L.T("ui.keybinds.padTitle", pads.Count);
        DetailsHint.Text = L.T("ui.keybinds.padHint");
        DetailsList.Visibility = Visibility.Collapsed;
        PadList.Visibility = Visibility.Visible;
        PadList.ItemsSource = pads;
    }

    private async void OnPadEdit(object sender, RoutedEventArgs e)
    {
        if (
            _game is null
            || sender is not FrameworkElement { Tag: string id }
            || _scan?.Pads.FirstOrDefault(p => p.Id == id) is not { } pad
        )
            return;
        var items = GamepadControls
            .All.OrderBy(c => c.Button)
            .ThenBy(c => c.Context)
            .Select(c => new ComboBoxItem
            {
                Content = $"{GamepadControls.Label(c.Button, _layout)} — {c.Name} ({c.Id})",
                Tag = c.Id,
            })
            .ToList();
        var box = new ComboBox
        {
            ItemsSource = items,
            SelectedItem = items.FirstOrDefault(i => (int)i.Tag == pad.Control),
            Margin = new Thickness(0, 10, 0, 14),
            MinWidth = 380,
        };
        var ok = new Button
        {
            Content = L.T("ui.keybinds.padSave"),
            Style = (Style)FindResource("Primary"),
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var cancel = new Button
        {
            Content = L.T("ui.keybinds.padCancel"),
            Style = (Style)FindResource("Ghost"),
            IsCancel = true,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(
            new TextBlock
            {
                Text = $"{ModTitle(pad.ModName)} — {L.Display(pad.Action)}",
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.keybinds.padPickHint"),
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                Margin = new Thickness(0, 4, 0, 0),
            }
        );
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dlg = new Window
        {
            Title = L.T("ui.keybinds.padChange"),
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            Background = (Brush)FindResource("Card"),
            Foreground = Brushes.White,
        };
        ok.Click += (_, _) => dlg.DialogResult = true;
        if (
            dlg.ShowDialog() != true
            || box.SelectedItem is not ComboBoxItem { Tag: int control }
            || control == pad.Control
        )
            return;
        await RunGmmAsync(
            [
                "keybind-pad-set",
                _game.Root,
                pad.Id,
                control.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ],
            L.T("ui.keybinds.padChanged")
        );
    }

    private async void OnSwitch(object sender, RoutedEventArgs e)
    {
        if (_game is null || sender is not FrameworkElement { Tag: KeybindSwitch sw })
            return;
        await RunGmmAsync(
            ["keybind-switch", _game.Root, sw.File, sw.HotkeysOn ? "off" : "on"],
            L.T("ui.keybinds.switchChanged")
        );
    }

    private void ShowSwitch()
    {
        var sw = _modFilter is null ? null : _scan?.Switches.FirstOrDefault(s => s.ModId == _modFilter);
        SwitchBar.Visibility = sw is null ? Visibility.Collapsed : Visibility.Visible;
        if (sw is null)
            return;
        SwitchTitle.Text = L.T(sw.HotkeysOn ? "ui.keybinds.switchOn" : "ui.keybinds.switchOff", ModTitle(sw.ModName));
        SwitchNote.Text = sw.Note;
        SwitchButton.Content = L.T(sw.HotkeysOn ? "ui.keybinds.switchTurnOff" : "ui.keybinds.switchTurnOn");
        SwitchButton.Tag = sw;
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        UpdateSearchHint();
        if (IsLoaded)
            Apply();
    }

    private void OnSearchFocus(object sender, RoutedEventArgs e) => UpdateSearchHint();

    // The hint is visible only in an empty, unfocused box, so the caret never blinks over the text.
    private void UpdateSearchHint() =>
        SearchHint.Visibility =
            SearchBox.Text.Length == 0 && !SearchBox.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;

    private void OnToggleGame(object sender, RoutedEventArgs e)
    {
        _showGame = !_showGame;
        ShowGame.IsChecked = _showGame;
        Apply();
    }

    private void Apply()
    {
        var q = SearchBox.Text.Trim();
        _visible = _all.Where(e =>
                (_showGame || !e.IsGame)
                && (_modFilter is null || e.ModId == _modFilter || e.IsGame && _showGame)
                && (
                    q.Length == 0
                    || e.Action.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || e.ModName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || e.Chord.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)
                )
            )
            .ToList();
        // Conflicts are computed over all bindings, not only visible ones: a filter must not hide overlaps.
        var basis = _showGame ? _all : _all.Where(e => !e.IsGame).ToList();
        _conflicts = _game is null
            ? KeybindConflictAnalyzer.Analyze(basis)
            : new PotentialConflictConfirmations(_game).Filter(KeybindConflictAnalyzer.Analyze(basis));
        _states = KeybindConflictAnalyzer.KeyStates(
            _visible,
            _conflicts.Where(c => _visible.Contains(c.A) || _visible.Contains(c.B)).ToList()
        );

        var mods = _all.Where(e => !e.IsGame).Select(e => e.ModId).Distinct().Count();
        int direct = _conflicts.Count(c => c.Kind == ConflictKind.Direct),
            potential = _conflicts.Count(c => c.Kind == ConflictKind.Potential);
        int unconfirmed = _all.Count(e => e.Status == KeybindStatus.NeedsConfirmation);
        int disabled = _all.Count(e => e.Status == KeybindStatus.Disabled);
        SummaryText.Text = L.T(
            "ui.keybinds.foundBindingsModsConflictsPossible",
            _game?.Edition,
            _all.Count(e => !e.IsGame),
            mods,
            direct,
            potential,
            unconfirmed,
            (disabled > 0 ? L.T("ui.keybinds.disabled2", disabled) : ""),
            (_scan is { } s ? L.T("ui.keybinds.filesReadFromCache", s.FilesScanned, s.FilesFromCache) : "")
        );
        _keyboard.Refresh();
        ShowDetails();
    }

    // ---------- Keyboard ----------

    private IEnumerable<KeybindEntry> OnKey(int vk) =>
        _visible.Where(e => e.Chord.Keys.Any(k => KeybindConflictAnalyzer.Base(k) == vk));

    private KeyVisual? VisualFor(int vk)
    {
        var entries = OnKey(vk).ToList();
        if (entries.Count == 0)
            return null;
        var state = _states.GetValueOrDefault(vk);
        var stripes = entries.Select(BrushFor).Distinct().Take(4).ToList();
        var (bg, border, glyph, glyphBrush, status) = state switch
        {
            KeyState.Direct => (DirectBg, DirectBrush, "!", DirectBrush, L.T("ui.keybinds.conflict2")),
            KeyState.Potential => (
                PotentialBg,
                PotentialBrush,
                "?",
                PotentialBrush,
                L.T("ui.keybinds.possibleOverlap")
            ),
            KeyState.Game => (GameBg, GameBrush, "", GameBrush, L.T("ui.keybinds.defaultGtaVControls")),
            _ => (ModBg, stripes.Count == 1 ? stripes[0] : Muted, "", Muted, L.T("ui.keybinds.assignedByMod")),
        };
        var tip = L.T(
            "ui.keybinds.bindingLine",
            KeyNames.Get(vk)?.Display,
            status,
            L.Join("\n", entries.Take(6).Select(e => $"• {ModTitle(e.ModName)}: {(L.Display(e.Action))} ({e.Chord})")),
            (entries.Count > 6 ? L.T("ui.common.more", entries.Count - 6) : "")
        );
        return new KeyVisual(bg, border, stripes, glyph, glyphBrush, entries.Count, tip);
    }

    private void BuildLegend()
    {
        void Item(Brush bg, Brush border, string glyph, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6) };
            sp.Children.Add(
                new Border
                {
                    Width = 22,
                    Height = 18,
                    CornerRadius = new CornerRadius(4),
                    Background = bg,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1.5),
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock
                    {
                        Text = glyph,
                        FontSize = 11,
                        FontWeight = FontWeights.Bold,
                        Foreground = border,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                }
            );
            sp.Children.Add(
                new TextBlock
                {
                    Text = text,
                    Foreground = Muted,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
            Legend.Children.Add(sp);
        }
        Item(
            Frozen(Color.FromRgb(0x2A, 0x2D, 0x33)),
            Frozen(Color.FromRgb(0x14, 0x16, 0x19)),
            "",
            L.T("ui.keybinds.availableAccordingKnownData2")
        );
        Item(GameBg, GameBrush, "", L.T("ui.keybinds.gtaVControlsDefault"));
        Item(ModBg, Palette[0] is var c ? Frozen(c) : Muted, "", L.T("ui.keybinds.modBarColorMod"));
        Item(PotentialBg, PotentialBrush, "?", L.T("ui.keybinds.possibleOverlap2"));
        Item(DirectBg, DirectBrush, "!", L.T("ui.keybinds.conflict3"));
        Legend.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.keybinds.nBindingsKey"),
                Foreground = Muted,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
    }

    // ---------- Details panel ----------

    private void ShowDetails()
    {
        ShowSwitch();
        if (_gamepad)
        {
            ShowPads();
            return;
        }
        PadList.Visibility = Visibility.Collapsed;
        DetailsList.Visibility = Visibility.Visible;
        int vk = _keyboard.Selected;
        List<KeybindEntry> list;
        if (vk != 0)
        {
            list = OnKey(vk).ToList();
            DetailsTitle.Text = L.T("ui.keybinds.key", KeyNames.Get(vk)?.Display);
            DetailsHint.Text =
                list.Count == 0
                    ? L.T("ui.keybinds.noBindingsFoundNotGuarantee")
                    : L.T("ui.keybinds.bindingsClickKeyAgainDeselect", list.Count);
        }
        else if (_modFilter is not null)
        {
            // A mod is chosen: all of its keys, so every one can be changed or switched off without hunting on the keyboard.
            list = _visible.Where(e => !e.IsGame && e.ModId == _modFilter).OrderBy(e => e.Action).ToList();
            DetailsTitle.Text = L.T("ui.keybinds.modKeys", list.Count);
            DetailsHint.Text = L.T("ui.keybinds.selectKeyKeyboardSeeAll");
        }
        else
        {
            // Nothing selected: conflicts first, then items that need confirmation.
            var inConflict = _conflicts.SelectMany(c => new[] { c.A, c.B }).Where(e => _visible.Contains(e)).Distinct();
            list = inConflict
                .Concat(_visible.Where(e => e.Status == KeybindStatus.NeedsConfirmation))
                .Distinct()
                .Where(e => !e.IsGame)
                .OrderBy(e => e.ModName)
                .ToList();
            DetailsTitle.Text =
                list.Count == 0 ? L.T("ui.keybinds.noOverlapsFound") : L.T("ui.keybinds.overlapsUnconfirmedBindings");
            DetailsHint.Text = L.T("ui.keybinds.selectKeyKeyboardSeeAll");
        }
        DetailsList.ItemsSource = list.Select(ToView).ToList();
    }

    private EntryView ToView(KeybindEntry e)
    {
        var conflicts = _conflicts
            .Where(c => c.A.Id == e.Id || c.B.Id == e.Id)
            .Select(c =>
            {
                var other = c.A.Id == e.Id ? c.B : c.A;
                return new ConflictLine(
                    L.T(
                        "ui.keybinds.with",
                        (c.Kind == ConflictKind.Direct ? L.T("ui.keybinds.conflict") : L.T("ui.keybinds.possible")),
                        ModTitle(other.ModName),
                        L.Display(other.Action),
                        other.Chord,
                        c.Reason
                    ),
                    c.Kind == ConflictKind.Direct ? DirectBrush : PotentialBrush,
                    c.Kind == ConflictKind.Potential ? c : null
                );
            })
            .ToList();
        var (statusText, statusBrush) = e.IsGame
            ? ("GTA V", GameBrush)
            : e.Status switch
            {
                KeybindStatus.NeedsConfirmation => (L.T("ui.keybinds.confirmationRequired"), PotentialBrush),
                KeybindStatus.Confirmed => (L.T("ui.keybinds.confirmed"), OkBrush),
                KeybindStatus.Disabled => (L.T("ui.keybinds.disabled"), Muted),
                _ => (e.FromManifest ? "modhub.json" : L.T("ui.keybinds.recognized"), OkBrush),
            };
        var where = e.IsGame
            ? L.Owned(GameKeybindProvider.Note)
            : L.T(
                "ui.keybinds.text",
                e.File,
                e.Section,
                L.Join(" + ", e.Params.Select(p => $"{p.Name}={p.Raw}")),
                (e.Editable ? "" : L.T("ui.keybinds.changeUnavailable", L.Owned(e.EditBlockReason ?? "")))
            );
        string ctx = e.Context switch
        {
            KeybindContext.OnFoot => L.T("ui.keybinds.foot"),
            KeybindContext.Vehicle => L.T("ui.keybinds.vehicle"),
            KeybindContext.Menu => L.T("ui.keybinds.menus"),
            KeybindContext.Global => L.T("ui.keybinds.everywhere"),
            _ => "",
        };
        bool canDefault = e.Editable && e.Default is { } d && d != e.Chord;
        bool duplicate = e.StatusNote?.Contains("несколько раз") == true;
        bool canDisable = e.Editable && e.CanDisable && !e.Chord.IsEmpty;
        return new EntryView(
            e.Id,
            ModTitle(e.ModName),
            BrushFor(e),
            L.Display(e.Action),
            e.Chord.ToString(),
            ctx,
            where,
            statusText,
            statusBrush,
            e.IsGame || e.StatusNote is null ? null
                : e.FromManifest ? e.StatusNote
                : L.Owned(e.StatusNote),
            conflicts,
            e.Editable,
            canDefault,
            L.T("ui.keybinds.default", e.Default),
            e.Status == KeybindStatus.NeedsConfirmation && !duplicate,
            canDisable
        );
    }

    // ---------- Actions ----------

    private KeybindEntry? Find(object sender) =>
        sender is FrameworkElement { Tag: string id } ? _all.FirstOrDefault(x => x.Id == id) : null;

    private void OnPotentialConfirmed(object sender, RoutedEventArgs e)
    {
        if (_game is null || sender is not Button { Tag: ConflictLine { Conflict: { } conflict } })
            return;
        try
        {
            new PotentialConflictConfirmations(_game).Confirm(conflict);
            Apply();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SummaryText.Text = L.T("ui.keybinds.couldNotSaveConfirmation", L.Owned(ex.Message));
        }
    }

    private void OnResetPotential(object sender, RoutedEventArgs e)
    {
        if (_game is null)
            return;
        try
        {
            new PotentialConflictConfirmations(_game).Reset();
            Apply();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SummaryText.Text = L.Owned(ex.Message);
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_game is null || Find(sender) is not { } entry)
            return;
        if (
            MessageBox.Show(
                Window.GetWindow(this),
                L.T("ui.keybinds.confirmKeyModFileWill", entry.RawValue, entry.File, entry.Chord),
                L.T("ui.common.confirmation"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            ) != MessageBoxResult.Yes
        )
            return;
        try
        {
            new KeybindScanner(_game, AppSettings.Load().OpenLibrary()).Confirm(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                L.T("ui.keybinds.couldNotSaveConfirmation", L.Owned(ex.Message)),
                L.T("ui.common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }
        _ = RescanAsync();
    }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is not { } entry)
            return;
        var dlg = new KeyCaptureWindow(entry, _all) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Result is { } chord)
            await SaveAsync(entry, chord);
    }

    private async void OnDisable(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is not { } entry)
            return;
        var preview = KeybindEditor.Preview(entry, default, _all);
        if (!preview.Allowed)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                preview.Error,
                L.T("ui.keybinds.cannotDisabled"),
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            return;
        }
        if (
            MessageBox.Show(
                Window.GetWindow(this),
                L.T("ui.keybinds.disableKeyModWillStop", entry.Action, entry.ModName),
                L.T("ui.keybinds.disableKey"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            ) == MessageBoxResult.Yes
        )
            await SaveAsync(entry, default);
    }

    private async void OnRestoreDefault(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is not { Default: { } def } entry)
            return;
        var preview = KeybindEditor.Preview(entry, def, _all);
        if (!preview.Allowed)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                preview.Error,
                L.T("ui.keybinds.cannotRestore"),
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            return;
        }
        var changes = L.Join("\n", preview.Changes.Select(c => $"{c.Param}: {c.Old} → {c.New}"));
        if (
            MessageBox.Show(
                Window.GetWindow(this),
                L.T("ui.keybinds.resetItsDefaultValue", entry.Action, entry.DefaultSource, changes),
                L.T("ui.keybinds.default2"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            ) == MessageBoxResult.Yes
        )
            await SaveAsync(entry, def);
    }

    private Task SaveAsync(KeybindEntry entry, KeyChord chord) =>
        _game is null
            ? Task.CompletedTask
            : RunGmmAsync(["keybind-set", _game.Root, entry.Id, chord.ToString()], L.T("ui.keybinds.hotkeyChanged"));

    /// <summary>Runs an elevated gmm change of a mod's settings file, reports the result and rescans.</summary>
    private async Task RunGmmAsync(string[] args, string successTitle)
    {
        if (_game is null || _runGmm is null)
            return;
        var settings = AppSettings.Load();
        if (settings.BackupsDeclined || settings.OpenLibrary() is null)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                L.T("ui.keybinds.backupsRequiredChangeHotkeysSettings"),
                L.T("ui.common.backups"),
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            return;
        }
        if (_game.IsRunning())
        {
            MessageBox.Show(
                Window.GetWindow(this),
                L.T("ui.keybinds.closeGtaVModhubDoes"),
                L.T("ui.common.gameRunning"),
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            return;
        }
        RescanButton.IsEnabled = false;
        SummaryText.Text = L.T("ui.keybinds.savingApproveAdministratorAccessPrompt");
        var (code, output) = await _runGmm(args);
        var line = output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
        MessageBox.Show(
            Window.GetWindow(this),
            code == 0 ? L.T("ui.keybinds.done", line) : L.T("ui.keybinds.changeWasNotMadeFile", L.Owned(output.Trim())),
            code == 0 ? successTitle : L.T("ui.common.error"),
            MessageBoxButton.OK,
            code == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning
        );
        await RescanAsync();
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
