using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.App.Installation;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Peds;

namespace GTAModManager.App.Peds;

/// <summary>
/// Character Accessories: clothing and appearance replacements installed through Mod Hub (MP character clothing,
/// story character replacements such as Dante instead of Trevor). Shows who, which slots and when; removal is done by undoing it in History.
/// </summary>
public sealed class AccessoriesPage : UserControl
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _summary = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 14),
    };
    public event Action? OpenHistoryRequested;
    public event Action? InstallRequested;

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public AccessoriesPage()
    {
        _summary.Foreground = Res("Muted");
        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 16) };
        Content = root;
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var head = new DockPanel();
        var add = new Button
        {
            Content = L.T("ui.peds.installAccessory"),
            Style = (Style)Application.Current.FindResource("Primary"),
            VerticalAlignment = VerticalAlignment.Top,
        };
        add.Click += (_, _) => InstallRequested?.Invoke();
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        var titles = new StackPanel();
        titles.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.common.characterAccessories"),
                Style = (Style)Application.Current.FindResource("PageTitle"),
            }
        );
        titles.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.peds.clothingAppearancesReplaceDefaultsMp"),
                Foreground = Res("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            }
        );
        head.Children.Add(titles);
        top.Children.Add(head);
        top.Children.Add(_summary);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    public async Task LoadAsync(GameInstall? game, BackupLibrary? library)
    {
        _list.Children.Clear();
        if (game is null)
        {
            _summary.Text = L.T("ui.common.selectGameLeft");
            return;
        }
        if (library is null)
        {
            _summary.Text = L.T("ui.peds.listBuiltFromHistoryEnable");
            return;
        }
        IReadOnlyList<InstalledAccessory> items;
        try
        {
            items = await Task.Run(() => PedAccessories.Installed(library, game.Edition));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _summary.Text = L.T("ui.common.couldNotReadHistory", L.Owned(ex.Message));
            return;
        }
        _summary.Text =
            items.Count == 0
                ? L.T("ui.peds.gtaVHasNoAccessories", game.Edition)
                : L.T("ui.peds.gtaVInstalled", game.Edition, items.Count);
        foreach (var a in items)
            _list.Children.Add(Card(a));
    }

    private Border Card(InstalledAccessory a)
    {
        var p = new StackPanel();
        var head = new DockPanel();
        var history = new Button
        {
            Content = L.T("ui.peds.remove"),
            Style = (Style)Application.Current.FindResource("Ghost"),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = L.T("ui.peds.removeUndoInstallationHistoryPrevious"),
        };
        history.Click += (_, _) => OpenHistoryRequested?.Invoke();
        DockPanel.SetDock(history, Dock.Right);
        head.Children.Add(history);
        head.Children.Add(
            new TextBlock
            {
                Text = a.Title,
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            }
        );
        p.Children.Add(head);
        p.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.peds.files", L.Join(", ", a.Characters), a.Paths.Count, a.Created.LocalDateTime),
                Foreground = Res("Muted"),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            }
        );
        if (a.Slots.Count > 0)
            p.Children.Add(
                new TextBlock
                {
                    Text = L.T(
                        "ui.peds.replaced",
                        L.Join(", ", a.Slots.Take(14)),
                        (a.Slots.Count > 14 ? L.T("ui.peds.more", a.Slots.Count - 14) : "")
                    ),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        var files = new TextBox
        {
            Text = L.Join(Environment.NewLine, a.Paths),
            IsReadOnly = true,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        p.Children.Add(
            new Expander
            {
                Header = L.T("ui.common.files"),
                Content = files,
                Margin = new Thickness(0, 6, 0, 0),
            }
        );
        return new Border
        {
            Style = (Style)Application.Current.FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 6, 10),
            Child = p,
        };
    }
}
