using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.App.Installation;
using GTAModManager.Core.Dlc;
using GTAModManager.Core.Inventory;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Inventory;

/// <summary>
/// Engine sounds as a table: every add-on car with what it plays now and a "new sound" choice. Tick several cars
/// and give them one sound (or their original back) in one go; all changes are applied together under one
/// administrator prompt. Opened from a car card (that car is ticked) or from a sound pack card (that sound is
/// preselected for the bulk choice). Stock cars are listed only on request.
/// </summary>
internal static class EngineSoundWindow
{
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>One entry of a "new sound" list.</summary>
    private sealed record Option(string Label, string? Sound, bool Original)
    {
        public override string ToString() => Label;
    }

    private sealed class Row
    {
        public required SoundTarget Target { get; init; }
        public required CheckBox Check { get; init; }
        public required ComboBox Choice { get; init; }
        public required FrameworkElement View { get; init; }
        public string? Original { get; init; }
        public IReadOnlyList<string> UndoOperations { get; init; } = [];
    }

    public static EngineSoundBatchRequest? Show(
        Window owner,
        GTAModManager.Core.Game.GameInstall game,
        IReadOnlyList<InstalledPack> packs,
        IReadOnlyList<SoundTarget> stock,
        IReadOnlyList<SoundAssignment> history,
        IReadOnlyDictionary<string, string> originals,
        string? carPack = null,
        string? sound = null
    )
    {
        var addons = EngineSounds.Targets(packs);
        var choices = EngineSounds.Choices(packs);
        if (addons.Count == 0 && stock.Count == 0)
        {
            MessageBox.Show(
                owner,
                L.T("ui.engineSound.noCars"),
                L.T("ui.engineSound.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            return null;
        }
        EngineSoundBatchRequest? result = null;
        var window = InstallUi.Window(owner, L.T("ui.engineSound.title"), 980, 680);
        var root = new DockPanel { Margin = new Thickness(20) };

        // ---- Top: hint, search, bulk assignment ----
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        top.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.engineSound.tableHint"),
                Foreground = Res("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            }
        );
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(tools);
        var search = new TextBox
        {
            Width = 220,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = L.T("ui.engineSound.search"),
        };
        tools.Children.Add(new TextBlock { Text = L.T("ui.engineSound.search"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        tools.Children.Add(search);
        var showStock = new CheckBox
        {
            Content = L.T("ui.engineSound.showStock"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            IsEnabled = stock.Count > 0,
        };
        tools.Children.Add(showStock);

        var bulk = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(bulk);
        bulk.Children.Add(new TextBlock { Text = L.T("ui.engineSound.bulkLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var bulkChoice = new ComboBox { Width = 300, Margin = new Thickness(0, 0, 8, 0), MaxDropDownHeight = 360 };
        var originalOption = new Option(L.T("ui.engineSound.originalItem"), null, true);
        bulkChoice.Items.Add(originalOption);
        foreach (var c in choices)
            bulkChoice.Items.Add(new Option(Describe(c), c.Name, false));
        bulkChoice.SelectedItem = bulkChoice.Items.OfType<Option>().FirstOrDefault(o =>
            o.Sound is not null && o.Sound.Equals(sound, StringComparison.OrdinalIgnoreCase)
        );
        bulk.Children.Add(bulkChoice);
        var applyBulk = InstallUi.Button(L.T("ui.engineSound.bulkApply"), () => { });
        bulk.Children.Add(applyBulk);
        var selectAll = InstallUi.Button(L.T("ui.engineSound.selectAll"), () => { });
        bulk.Children.Add(selectAll);
        var selectNone = InstallUi.Button(L.T("ui.engineSound.selectNone"), () => { });
        bulk.Children.Add(selectNone);

        // ---- Bottom: summary and actions ----
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        var apply = new Button
        {
            Content = L.T("ui.engineSound.apply", 0),
            Style = (Style)Application.Current.FindResource("Primary"),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 4, 8, 4),
            IsEnabled = false,
        };
        buttons.Children.Add(apply);
        buttons.Children.Add(InstallUi.Button(L.T("ui.common.cancel"), window.Close));
        var summary = new TextBlock
        {
            Foreground = Res("Muted"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        bottom.Children.Add(summary);

        // ---- The table ----
        var table = new StackPanel();
        var header = RowGrid();
        AddCell(header, 1, L.T("ui.engineSound.colCar"), true);
        AddCell(header, 2, L.T("ui.engineSound.colPack"), true);
        AddCell(header, 3, L.T("ui.engineSound.colCurrent"), true);
        AddCell(header, 4, L.T("ui.engineSound.colNew"), true);
        header.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var rows = new List<Row>();
        void Update()
        {
            int changes = rows.Count(r => r.Choice.SelectedIndex > 0);
            apply.Content = L.T("ui.engineSound.apply", changes);
            apply.IsEnabled = changes > 0;
            int ticked = rows.Count(r => r.Check.IsChecked == true && r.View.Visibility == Visibility.Visible);
            summary.Text = L.T("ui.engineSound.summary", ticked, changes);
        }
        foreach (var target in addons.Concat(stock))
        {
            var changes = history
                .Where(h => h.Model.Equals(target.Vehicle.Model, StringComparison.OrdinalIgnoreCase))
                .ToList();
            string? original = EngineSounds.Original(game, originals, target);
            string current = target.Vehicle.EffectiveAudio;
            var grid = RowGrid();
            var check = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = carPack is not null && target.Pack.Equals(carPack, StringComparison.OrdinalIgnoreCase),
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);
            AddCell(grid, 1, target.Vehicle.Model, false);
            AddCell(
                grid,
                2,
                !target.Stock ? target.Pack
                : target.Pack.Length > 0 ? L.T("ui.engineSound.stockDlc", target.Pack)
                : L.T("ui.engineSound.stockBase"),
                false
            );
            bool modified = original is not null
                ? !original.Equals(current, StringComparison.OrdinalIgnoreCase)
                : changes.Count > 0;
            AddCell(grid, 3, current + (modified ? "  •  " + L.T("ui.engineSound.changedShort") : ""), false);
            var choice = new ComboBox { MaxDropDownHeight = 360, VerticalAlignment = VerticalAlignment.Center };
            choice.Items.Add(new Option(L.T("ui.engineSound.keep"), null, false));
            if (modified)
                choice.Items.Add(
                    new Option(
                        original is not null
                            ? L.T("ui.engineSound.originalNamed", original)
                            : L.T("ui.engineSound.originalItem"),
                        original,
                        true
                    )
                );
            foreach (var c in choices.Where(c => !c.Name.Equals(current, StringComparison.OrdinalIgnoreCase)))
                choice.Items.Add(new Option(Describe(c), c.Name, false));
            choice.SelectedIndex = 0;
            choice.SelectionChanged += (_, _) => Update();
            check.Checked += (_, _) => Update();
            check.Unchecked += (_, _) => Update();
            Grid.SetColumn(choice, 4);
            grid.Children.Add(choice);
            var border = new Border
            {
                Child = grid,
                BorderBrush = Res("Line"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 5, 0, 5),
                Visibility = target.Stock ? Visibility.Collapsed : Visibility.Visible,
            };
            table.Children.Add(border);
            rows.Add(
                new Row
                {
                    Target = target,
                    Check = check,
                    Choice = choice,
                    View = border,
                    Original = original,
                    UndoOperations = original is null ? changes.Select(c => c.Id).ToList() : [],
                }
            );
        }
        root.Children.Add(
            new ScrollViewer { Content = table, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        );

        void Filter()
        {
            string text = search.Text.Trim();
            foreach (var r in rows)
            {
                bool visible =
                    (!r.Target.Stock || showStock.IsChecked == true)
                    && (
                        text.Length == 0
                        || r.Target.Vehicle.Model.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || r.Target.Pack.Contains(text, StringComparison.OrdinalIgnoreCase)
                    );
                r.View.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
            Update();
        }
        search.TextChanged += (_, _) => Filter();
        showStock.Checked += (_, _) => Filter();
        showStock.Unchecked += (_, _) => Filter();
        selectAll.Click += (_, _) =>
        {
            foreach (var r in rows.Where(r => r.View.Visibility == Visibility.Visible))
                r.Check.IsChecked = true;
        };
        selectNone.Click += (_, _) =>
        {
            foreach (var r in rows)
                r.Check.IsChecked = false;
        };
        applyBulk.Click += (_, _) =>
        {
            if (bulkChoice.SelectedItem is not Option wanted)
            {
                MessageBox.Show(window, L.T("ui.engineSound.pickSound"), L.T("ui.engineSound.title"));
                return;
            }
            var ticked = rows.Where(r => r.Check.IsChecked == true && r.View.Visibility == Visibility.Visible).ToList();
            if (ticked.Count == 0)
            {
                MessageBox.Show(window, L.T("ui.engineSound.tickCars"), L.T("ui.engineSound.title"));
                return;
            }
            foreach (var r in ticked)
            {
                // The same option may be missing in a row (its current sound, or no original to go back to).
                var match = r.Choice.Items.OfType<Option>().Skip(1).FirstOrDefault(o =>
                    wanted.Original ? o.Original : o.Sound is not null && o.Sound.Equals(wanted.Sound, StringComparison.OrdinalIgnoreCase)
                );
                r.Choice.SelectedItem = match ?? r.Choice.Items[0];
            }
            Update();
        };
        apply.Click += (_, _) =>
        {
            var batch = new EngineSoundBatchRequest();
            foreach (var r in rows)
            {
                if (r.Choice.SelectedItem is not Option o || r.Choice.SelectedIndex == 0)
                    continue;
                if (o.Original && o.Sound is null)
                {
                    // Changed by an older Mod Hub without a recorded original: undo those installations.
                    if (r.UndoOperations.Count > 0)
                        batch.Undo.Add(
                            new ModRemoveRequest
                            {
                                Name = L.T("ui.engineSound.restoreName", r.Target.Vehicle.Model),
                                Operations = [.. r.UndoOperations],
                            }
                        );
                    continue;
                }
                batch.Assign.Add(
                    new EngineSoundRequest
                    {
                        ArchiveRel = r.Target.ArchiveRel,
                        Model = r.Target.Vehicle.Model,
                        Sound = o.Sound!,
                    }
                );
            }
            if (batch.Undo.Count + batch.Assign.Count == 0)
                return;
            result = batch;
            window.Close();
        };
        Update();
        window.Content = root;
        window.ShowDialog();
        return result;
    }

    private static string Describe(SoundChoice c) =>
        c.Name
        + "   —   "
        + (
            c.Source == DlcPackKind.EngineSounds
                ? L.T("ui.engineSound.fromSoundPack", c.Pack)
                : L.T("ui.engineSound.fromCar", c.Pack)
        );

    private static Grid RowGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        return grid;
    }

    private static void AddCell(Grid grid, int column, string text, bool header)
    {
        var block = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = text,
        };
        if (header)
        {
            block.Foreground = Res("Muted");
            block.FontSize = 12;
        }
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }
}
