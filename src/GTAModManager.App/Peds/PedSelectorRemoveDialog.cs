using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Peds;

public enum PedSelectorRemoval
{
    Cancel,

    /// <summary>Only the in-game menu; characters and their pack stay installed.</summary>
    MenuOnly,

    /// <summary>The menu, every Mod Hub character and the looks saved in game.</summary>
    Everything,
}

/// <summary>Asks how far to remove Ped Selector. Shared by Add-on Characters and My Mods.</summary>
public static class PedSelectorRemoveDialog
{
    public static PedSelectorRemoval Ask(Window owner)
    {
        var result = PedSelectorRemoval.Cancel;
        var window = new Window
        {
            Owner = owner,
            Title = L.T("ui.peds.removeSelectorTitle"),
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.FindResource("Bg"),
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.peds.removeSelectorTitle"),
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.peds.removeSelectorQuestion"),
                Foreground = (Brush)Application.Current.FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 16),
            }
        );
        Button Choice(string title, string detail, PedSelectorRemoval value, string style)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            content.Children.Add(
                new TextBlock
                {
                    Text = detail,
                    FontSize = 12,
                    Opacity = 0.8,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0),
                }
            );
            var button = new Button
            {
                Content = content,
                Style = (Style)Application.Current.FindResource(style),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
            };
            button.Click += (_, _) =>
            {
                result = value;
                window.DialogResult = true;
            };
            return button;
        }
        panel.Children.Add(
            Choice(
                L.T("ui.peds.removeMenuOnly"),
                L.T("ui.peds.removeMenuOnlyDetail"),
                PedSelectorRemoval.MenuOnly,
                "Ghost"
            )
        );
        panel.Children.Add(
            Choice(
                L.T("ui.peds.removeEverything"),
                L.T("ui.peds.removeEverythingDetail"),
                PedSelectorRemoval.Everything,
                "Ghost"
            )
        );
        var cancel = new Button
        {
            Content = L.T("ui.common.cancel"),
            Style = (Style)Application.Current.FindResource("Ghost"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
            IsCancel = true,
        };
        panel.Children.Add(cancel);
        window.Content = panel;
        window.ShowDialog();
        return result;
    }
}
