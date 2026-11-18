using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.App.Installation;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.NaturalVision;

namespace GTAModManager.App.NaturalVision;

internal sealed class NaturalVisionWindow : Window
{
    readonly NveCatalog catalog;
    readonly NvePreviews previews;
    readonly Dictionary<string, Func<bool>> choices = [];
    readonly TextBlock summary = InstallUi.Text("");
    readonly TextBlock warning = InstallUi.Text("");
    readonly Button install;
    public NveInstallRequest? Request { get; private set; }

    public NaturalVisionWindow(Window owner, GameInstall game, NveCatalog catalog)
    {
        previews = new(catalog);
        Closed += (_, _) => previews.Cancel();
        this.catalog = catalog;
        Owner = owner;
        Title = L.T("ui.nve.modHub", catalog.Title);
        Width = 940;
        Height = 800;
        MinWidth = 700;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Bg");
        Foreground = Brushes.White;
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var banner = new StackPanel { Margin = new Thickness(18) };
        banner.Children.Add(InstallUi.Text(catalog.Title, 27));
        banner.Children.Add(InstallUi.Text(L.T("ui.nve.singlePlayerHeader", game.Edition)));
        header.Children.Add(
            new Border
            {
                Background = new LinearGradientBrush(Color.FromRgb(20, 63, 52), Color.FromRgb(26, 28, 32), 0),
                CornerRadius = new CornerRadius(12),
                Child = banner,
                Margin = new Thickness(0, 0, 0, 12),
            }
        );
        header.Children.Add(InstallUi.Text(L.T("ui.nve.install", game.Root)));
        var bottom = new StackPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        summary.Foreground = (Brush)Application.Current.FindResource("Muted");
        bottom.Children.Add(summary);
        warning.Foreground = (Brush)Application.Current.FindResource("Warn");
        bottom.Children.Add(warning);
        var actions = new WrapPanel();
        bottom.Children.Add(actions);
        install = InstallUi.Button(L.T(catalog.IsLcpp ? "lcpp.install" : "ui.nve.installSelected"), () =>
        {
            var ids = Selected();
            var errors = catalog.Validate(ids);
            if (errors.Count > 0)
            {
                warning.Text = L.Join("\n", errors);
                return;
            }
            Request = new()
            {
                Source = catalog.Source,
                Stamp = catalog.Stamp,
                Components = ids,
            };
            DialogResult = true;
        });
        install.Style = (Style)Application.Current.FindResource("Primary");
        actions.Children.Add(install);
        if (!catalog.IsLcpp)
            actions.Children.Add(
                InstallUi.Button(L.T("ui.nve.visitAuthorSWebsite"), () =>
                    Discover.BrowserView.OpenExternal(new Uri("https://razedmods.com/"))
                )
            );
        actions.Children.Add(InstallUi.Button(L.T("ui.common.cancel"), () => DialogResult = false));
        var content = new StackPanel();
        root.Children.Add(
            new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        );
        content.Children.Add(
            InstallUi.Text(
                catalog.IsLcpp ? L.T("lcpp.required", catalog.Components.Count(c => c.Core))
                    : game.Edition == GameEdition.Legacy ? L.T("ui.nve.mainPartsRequiredInstalledOrder")
                    : L.T("ui.nve.enhancedMainPackageInstalledUsing"),
                17
            )
        );
        foreach (var component in catalog.Components.Where(c => c.Core))
        {
            if (catalog.IsLcpp)
            {
                content.Children.Add(InstallUi.Text("✓ " + component.Title));
                choices[component.Id] = () => true;
                continue;
            }
            var row = new CheckBox
            {
                IsChecked = true,
                IsEnabled = false,
                Content = component.Title,
                Margin = new Thickness(0, 4, 0, 4),
            };
            content.Children.Add(row);
            content.Children.Add(InstallUi.Text(component.Summary));
            choices[component.Id] = () => true;
        }
        if (game.Edition == GameEdition.Enhanced)
            content.Children.Add(InstallUi.Text(L.T("ui.nve.authorRequiresCleanEnhancedInstallation")));
        if (catalog.Components.Any(c => !c.Core))
            content.Children.Add(InstallUi.Text(L.T("ui.common.additionalComponents"), 18));
        foreach (var group in catalog.Components.Where(c => !c.Core).GroupBy(c => c.Group))
        {
            var panel = new StackPanel { Margin = new Thickness(14) };
            var list = group.OrderBy(c => c.Source.Split('\\').Length).ThenBy(c => c.Source).ToArray();
            var tile = previews.Tile(list[0]);
            var enabled = new CheckBox
            {
                Content = list.Length == 1 ? list[0].Title : group.Key,
                FontSize = 14,
                IsChecked = list[0].Id == "reshade",
                Margin = new Thickness(0, 0, 0, 6),
            };
            panel.Children.Add(enabled);
            if (list.Length == 1)
            {
                var component = list[0];
                choices[component.Id] = () => enabled.IsChecked == true;
                panel.Children.Add(InstallUi.Text(component.Summary));
                AddDescription(panel, component);
            }
            else
            {
                var variants = new ComboBox
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    IsEnabled = false,
                    ItemsSource = list,
                    DisplayMemberPath = nameof(NveComponent.Title),
                    SelectedIndex = 0,
                };
                panel.Children.Add(variants);
                variants.SelectionChanged += async (_, _) =>
                {
                    if (variants.SelectedItem is NveComponent selected)
                        await previews.ShowAsync(tile, selected);
                };
                var note = InstallUi.Text(list[0].Summary);
                panel.Children.Add(note);
                variants.SelectionChanged += (_, _) =>
                {
                    note.Text = (variants.SelectedItem as NveComponent)?.Summary ?? "";
                    Update();
                };
                enabled.Checked += (_, _) => variants.IsEnabled = true;
                enabled.Unchecked += (_, _) => variants.IsEnabled = false;
                foreach (var component in list)
                    choices[component.Id] = () =>
                        enabled.IsChecked == true && ReferenceEquals(variants.SelectedItem, component);
                var original = new TextBox
                {
                    Text = list[0].Description,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 160,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
                variants.SelectionChanged += (_, _) =>
                    original.Text = (variants.SelectedItem as NveComponent)?.Description ?? "";
                panel.Children.Add(new Expander { Header = L.T("ui.nve.authorSDescription"), Content = original });
            }
            enabled.Checked += (_, _) => Update();
            enabled.Unchecked += (_, _) => Update();
            var card = new Grid();
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.Children.Add(tile);
            Grid.SetColumn(panel, 1);
            card.Children.Add(panel);
            content.Children.Add(
                new Border
                {
                    Background = (Brush)Application.Current.FindResource("Card"),
                    CornerRadius = new CornerRadius(9),
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = card,
                }
            );
        }
        Update();
    }

    static void AddDescription(Panel panel, NveComponent component)
    {
        if (component.Description.Length > 0)
            panel.Children.Add(
                new Expander
                {
                    Header = L.T("ui.nve.authorSDescription"),
                    Content = new TextBox
                    {
                        Text = component.Description,
                        IsReadOnly = true,
                        TextWrapping = TextWrapping.Wrap,
                        MaxHeight = 160,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    },
                }
            );
    }

    List<string> Selected() => choices.Where(p => p.Value()).Select(p => p.Key).ToList();

    void Update()
    {
        var ids = Selected();
        var errors = catalog.Validate(ids);
        warning.Text = L.Join("\n", errors);
        install.IsEnabled = errors.Count == 0;
        var selected = catalog.Components.Where(c => ids.Contains(c.Id)).ToArray();
        summary.Text = L.T(
            "ui.nve.selectedSourceDataAllowIndividual",
            selected.Length,
            BackupLibrary.FormatSize(selected.Sum(c => c.Size)),
            BackupLibrary.FormatSize(selected.Where(c => !c.Core).Sum(c => c.Size))
        );
    }
}
