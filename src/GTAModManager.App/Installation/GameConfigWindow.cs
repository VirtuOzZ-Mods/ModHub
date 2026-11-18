using System.Windows;
using System.Windows.Controls;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Installation;

internal sealed class GameConfigWindow : Window
{
    private sealed record Choice(string Value, string Label);

    public GameConfigVariant? Selected { get; private set; }

    public GameConfigWindow(Window owner, IReadOnlyList<GameConfigVariant> variants)
    {
        Owner = owner;
        Title = L.T("ui.installation.gameconfigVariant");
        Width = 760;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("Bg");
        Foreground = System.Windows.Media.Brushes.White;
        var p = new StackPanel { Margin = new Thickness(24) };
        Content = p;
        p.Children.Add(InstallUi.Text(L.T("ui.installation.gameconfigSettings"), 20));
        p.Children.Add(InstallUi.Text(L.T("ui.installation.selectVersionFolderProfileTraffic")));
        var folder = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            ItemsSource = variants
                .Select(v => v.Folder)
                .Distinct()
                .OrderBy(f => f.Contains("Old Versions", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
        };
        var preset = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            DisplayMemberPath = nameof(Choice.Label),
            SelectedValuePath = nameof(Choice.Value),
        };
        var traffic = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            DisplayMemberPath = nameof(Choice.Label),
            SelectedValuePath = nameof(Choice.Value),
        };
        p.Children.Add(InstallUi.Text(L.T("ui.installation.versionFolder")));
        p.Children.Add(folder);
        p.Children.Add(InstallUi.Text(L.T("ui.installation.profile")));
        p.Children.Add(preset);
        p.Children.Add(InstallUi.Text(L.T("ui.installation.trafficPedestrians")));
        p.Children.Add(traffic);
        folder.SelectionChanged += (_, _) =>
        {
            preset.ItemsSource = variants
                .Where(v => v.Folder == (string?)folder.SelectedItem)
                .Select(v => new Choice(v.Preset, v.PresetLabel))
                .Distinct()
                .ToArray();
            preset.SelectedIndex = -1;
            traffic.ItemsSource = null;
        };
        preset.SelectionChanged += (_, _) =>
        {
            traffic.ItemsSource = variants
                .Where(v => v.Folder == (string?)folder.SelectedItem && v.Preset == (string?)preset.SelectedValue)
                .Select(v => new Choice(v.Traffic, v.TrafficLabel))
                .Distinct()
                .ToArray();
            traffic.SelectedIndex = -1;
        };
        p.Children.Add(InstallUi.Button(L.T("ui.installation.selectVariant"), () =>
            {
                Selected = variants.FirstOrDefault(v =>
                    v.Folder == (string?)folder.SelectedItem
                    && v.Preset == (string?)preset.SelectedValue
                    && v.Traffic == (string?)traffic.SelectedValue
                );
                if (Selected is null)
                {
                    MessageBox.Show(this, L.T("ui.installation.selectAllThreeFields"));
                    return;
                }
                DialogResult = true;
            }));
    }
}
