using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Installation;

internal static class InstallUi
{
    public static TextBlock Text(string text, double size = 13) =>
        new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            Margin = new Thickness(0, 4, 0, 8),
        };

    public static Button Button(string text, Action click)
    {
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(0, 4, 8, 4),
            Padding = new Thickness(12, 8, 12, 8),
            Style = (Style)Application.Current.FindResource("Ghost"),
        };
        b.Click += (_, _) => click();
        return b;
    }

    public static Window Window(Window owner, string title, double width = 800, double height = 620) =>
        new()
        {
            Owner = owner,
            Title = title,
            Width = width,
            Height = height,
            MinWidth = 580,
            MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.FindResource("Bg"),
            Foreground = Brushes.White,
        };

    public static GameInstall? ChooseGame(
        Window owner,
        IReadOnlyList<GameInstall> games,
        GameInstall? current,
        string documentName = "DLC List"
    )
    {
        if (games.Count == 0)
        {
            MessageBox.Show(owner, L.T("ui.installation.selectGtaVFolderFirst"), L.T("ui.installation.selectGame"));
            return null;
        }
        if (games.Count == 1)
            return games[0];
        var window = Window(owner, L.T("ui.installation.whichVersionShouldInstalled", documentName), 620, 280);
        var panel = new StackPanel { Margin = new Thickness(24) };
        window.Content = panel;
        panel.Children.Add(Text(L.T("ui.installation.bothVersionsInstalledComputerChoose", documentName), 16));
        GameInstall? result = null;
        foreach (var game in games)
            panel.Children.Add(Button("GTA V " + game.Edition + " · " + game.Root, () =>
                {
                    result = game;
                    window.DialogResult = true;
                }));
        return window.ShowDialog() == true ? result : null;
    }

    public static void Error(Window owner, Exception ex) =>
        MessageBox.Show(
            owner,
            L.Owned(ex.Message),
            "Mod Hub by VirtuOzZ",
            MessageBoxButton.OK,
            MessageBoxImage.Warning
        );
}
