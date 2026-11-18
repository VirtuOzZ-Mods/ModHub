using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.Core.Game;
using GTAModManager.Core.Instructions;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;

namespace GTAModManager.App;

/// <summary>Mod variant selection (Version 1/2, Silent, ON/OFF…) before analysis and installation; remembered per archive.</summary>
public partial class MainWindow
{
    /// <summary>Returns false if the user cancelled.</summary>
    private async Task<bool> EnsureVariantChoicesAsync(GameInstall game, string path, bool force = false) =>
        (await EnsureVariantChoicesWithAnalysisAsync(game, path, force)).Ok;

    /// <summary>Ok=false means the user cancelled; Analysis is the existing analysis if the choice did not change (it can be reused).</summary>
    private async Task<(bool Ok, ModAnalysis? Analysis)> EnsureVariantChoicesWithAnalysisAsync(
        GameInstall game,
        string path,
        bool force = false
    )
    {
        ModAnalysis analysis;
        try
        {
            analysis = await Task.Run(() =>
            {
                using var archive = ModArchive.Open(path);
                return ModAnalyzer.Analyze(archive, game);
            });
        }
        catch (Exception ex)
            when (ex
                    is System.IO.IOException
                        or System.IO.InvalidDataException
                        or InvalidOperationException
                        or ArgumentException
                        or NotSupportedException
                        or Core.Oiv.OivFormatException
            )
        {
            return (true, null);
        } // the analysis will report the read error anyway
        if (analysis.Variants.Count == 0 || analysis.PendingChoices.Count == 0 && !force)
            return (true, analysis);
        var chosen = ShowVariantDialog(System.IO.Path.GetFileName(path), analysis.Variants, analysis.Chosen);
        if (chosen is null)
            return (false, null);
        await Task.Run(() => InstallChoices.Set(path, chosen));
        return (true, null);
    }

    private Dictionary<string, string>? ShowVariantDialog(
        string archiveName,
        IReadOnlyList<VariantGroup> groups,
        IReadOnlyDictionary<string, string> current
    )
    {
        var window = Installation.InstallUi.Window(
            this,
            L.T("ui.variants.chooseModVariant"),
            640,
            Math.Min(760, 260 + groups.Count * 90)
        );
        var root = new DockPanel { Margin = new Thickness(24) };
        window.Content = root;
        var header = Installation.InstallUi.Text(
            L.T("ui.variants.archiveContainsSeveralVariantsChoose", archiveName),
            15
        );
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var list = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var boxes = new List<(VariantGroup Group, ComboBox Box)>();
        foreach (var group in groups)
        {
            var title = group.Title.Length > 0 ? group.Title : L.T("core.common.modVariant");
            list.Children.Add(
                new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 10, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
            var box = new ComboBox
            {
                ItemsSource = group.Options.Select(o => o.Label).ToList(),
                MinWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            int index = current.TryGetValue(group.Key, out var key)
                ? group.Options.ToList().FindIndex(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                : -1;
            box.SelectedIndex = Math.Max(0, index);
            list.Children.Add(box);
            boxes.Add((group, box));
        }

        Dictionary<string, string>? result = null;
        var ok = new Button
        {
            Content = L.T("ui.variants.continue"),
            Padding = new Thickness(18, 8, 18, 8),
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("Primary"),
            IsDefault = true,
        };
        ok.Click += (_, _) =>
        {
            result = boxes.ToDictionary(
                b => b.Group.Key,
                b => b.Group.Options[Math.Max(0, b.Box.SelectedIndex)].Key,
                StringComparer.OrdinalIgnoreCase
            );
            window.DialogResult = true;
        };
        var cancel = new Button
        {
            Content = L.T("ui.common.cancel"),
            Padding = new Thickness(18, 8, 18, 8),
            Style = (Style)FindResource("Ghost"),
            IsCancel = true,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        return window.ShowDialog() == true ? result : null;
    }

    /// <summary>Change Variant button on the mod card, shown when the archive has variants.</summary>
    private void ShowVariantsButton(int variantCount)
    {
        if (variantCount == 0 || _packagePath is not { } path)
            return;
        var button = new Button
        {
            Content = L.T("ui.variants.changeVariant"),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 4, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)FindResource("Ghost"),
        };
        button.Click += async (_, _) =>
        {
            if (Current is not { } game)
                return;
            if (await EnsureVariantChoicesAsync(game, path, force: true))
                await AnalyzeAsync(path);
        };
        ExtrasList.Children.Insert(0, button);
        ExtrasCard.Visibility = Visibility.Visible;
    }
}
