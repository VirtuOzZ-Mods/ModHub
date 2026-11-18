using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.Core.Game;
using GTAModManager.Core.Inventory;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Inventory;

/// <summary>
/// Rebuild from list: which mods from the list to download from GTA5-Mods and install. Mods already installed and mods without a link
/// are unchecked; mods without a link are still listed so they can be installed manually.
/// </summary>
internal sealed class RebuildWindow : Window
{
    public List<ModListEntry> Selected { get; } = [];

    public RebuildWindow(Window owner, ModList list, GameInstall game, InventoryReport? current, int importedLabels)
    {
        Owner = owner;
        Title = L.T("ui.common.buildFromList");
        Width = 820;
        Height = 620;
        MinWidth = 600;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Bg");
        var muted = (Brush)Application.Current.FindResource("Muted");
        var root = new DockPanel { Margin = new Thickness(22) };
        Content = root;

        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.inventory.gtaVModList", list.Edition),
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
            }
        );
        var info = L.T(
            "ui.inventory.createdMods",
            list.Created.LocalDateTime,
            (list.GameVersion is null ? "" : L.T("ui.inventory.gameVersion", list.GameVersion)),
            list.Mods.Count
        );
        if (importedLabels > 0)
            info += L.T("ui.inventory.fileLabelsTransferredModHub", importedLabels);
        top.Children.Add(
            new TextBlock
            {
                Text = info,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 6),
            }
        );
        if (!list.Edition.Equals(game.Edition.ToString(), StringComparison.OrdinalIgnoreCase))
            top.Children.Add(
                new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x31, 0x1C)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = new TextBlock
                    {
                        Text = L.T("ui.inventory.listWasMadeGtaV", list.Edition, game.Edition),
                        Foreground = (Brush)Application.Current.FindResource("Warn"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                }
            );
        top.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.inventory.modHubWillDownloadSelected"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            }
        );
        root.Children.Add(top);

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var start = new Button
        {
            Content = L.T("ui.inventory.downloadInstall"),
            Style = (Style)Application.Current.FindResource("Primary"),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };
        var cancel = new Button
        {
            Content = L.T("ui.common.close"),
            Style = (Style)Application.Current.FindResource("Ghost"),
            IsCancel = true,
        };
        bottom.Children.Add(start);
        bottom.Children.Add(cancel);
        root.Children.Add(bottom);

        var rows = new StackPanel();
        var boxes = new List<(CheckBox Box, ModListEntry Entry)>();
        foreach (
            var m in list
                .Mods.OrderByDescending(m => m.Downloadable)
                .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
        )
        {
            bool installed = ModList.Installed(m, current);
            var note =
                installed ? L.T("ui.inventory.alreadyInstalled")
                : m.Downloadable ? m.Url!
                : L.T("ui.inventory.noGta5ModsLinkInstall");
            var cb = new CheckBox
            {
                IsChecked = m.Downloadable && !installed,
                IsEnabled = m.Downloadable,
                Margin = new Thickness(0, 4, 0, 4),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = m.Name,
                            FontWeight = FontWeights.SemiBold,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = L.T("ui.inventory.files", m.Kind, m.Files.Count, note),
                            Foreground = muted,
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            };
            boxes.Add((cb, m));
            rows.Children.Add(cb);
        }
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        start.Click += (_, _) =>
        {
            Selected.AddRange(boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Entry));
            DialogResult = true;
        };
    }
}
