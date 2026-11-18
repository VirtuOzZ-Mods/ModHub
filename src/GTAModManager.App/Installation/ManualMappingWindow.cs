using System.IO;
using System.Windows;
using System.Windows.Controls;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;

namespace GTAModManager.App.Installation;

internal sealed class ManualMappingWindow : Window
{
    public Dictionary<string, string> Mappings { get; } = [];

    public ManualMappingWindow(Window owner, string source)
    {
        Owner = owner;
        Title = L.T("ui.installation.fileDestinations");
        Width = 880;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("Bg");
        Foreground = System.Windows.Media.Brushes.White;
        var panel = new DockPanel { Margin = new Thickness(20) };
        Content = panel;
        var head = InstallUi.Text(L.T("ui.installation.enterPathsOnlyFilesNeed"));
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        var rows = new StackPanel();
        var inputs = new List<(string source, TextBox target)>();
        var save = InstallUi.Button(L.T("ui.installation.useSelectedFiles"), () =>
        {
            foreach (var row in inputs)
                if (!string.IsNullOrWhiteSpace(row.target.Text))
                    Mappings[row.source] = row.target.Text.Trim();
            if (Mappings.Count > 0)
                DialogResult = true;
        });
        DockPanel.SetDock(save, Dock.Bottom);
        panel.Children.Add(save);
        panel.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        IReadOnlyCollection<string> files;
        if (new[] { ".zip", ".rar", ".7z" }.Contains(Path.GetExtension(source).ToLowerInvariant()))
        {
            using var archive = ModArchive.OpenExpanded(source);
            files = archive.Files.ToArray();
        }
        else
            files = [source];
        foreach (var f in files.Take(500))
        {
            rows.Children.Add(InstallUi.Text(f));
            var input = new TextBox
            {
                Margin = new Thickness(0, 0, 0, 10),
                ToolTip = L.T("ui.installation.destinationPathRelativeGameFolder"),
            };
            rows.Children.Add(input);
            inputs.Add((f, input));
        }
        if (files.Count > 500)
            rows.Children.Add(InstallUi.Text(L.T("ui.installation.showingFirst500FilesExtract")));
    }
}
