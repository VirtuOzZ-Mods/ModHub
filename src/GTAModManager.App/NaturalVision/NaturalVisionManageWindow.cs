using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.App.Installation;
using GTAModManager.Core.Localization;
using GTAModManager.Core.NaturalVision;

namespace GTAModManager.App.NaturalVision;

internal sealed class NaturalVisionManageWindow : Window
{
    public NveRemoveRequest? Request { get; private set; }
    public bool InstallPreservedAdjusters { get; private set; }

    public NaturalVisionManageWindow(Window owner, NveState state)
    {
        Owner = owner;
        Title = L.T("ui.nve.controls", state.ProductTitle);
        Width = 780;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Bg");
        Foreground = Brushes.White;
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        header.Children.Add(InstallUi.Text(state.ProductTitle, 24));
        header.Children.Add(InstallUi.Text("GTA V " + state.Edition + " · " + state.GameRoot));
        header.Children.Add(InstallUi.Text(L.T("ui.nve.mainPartsRemovedTogetherSelect")));
        var items = new StackPanel();
        var checks = new Dictionary<string, CheckBox>();
        foreach (var component in state.Components.Where(c => c.Active))
        {
            if (component.Component.Core)
                items.Children.Add(InstallUi.Text("✓ " + component.Component.Title));
            else
            {
                var check = new CheckBox { Content = component.Component.Title, Margin = new Thickness(0, 6, 0, 6) };
                checks[component.Component.Id] = check;
                items.Children.Add(check);
                items.Children.Add(InstallUi.Text(component.Component.Summary));
            }
        }
        var actions = new WrapPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        if (
            state.IsLcpp
            && state.PreservedAdjusters.Count > 0
            && !state.Components.Any(c => c.Active && c.Component.Id == "lcpp-adjusters")
        )
        {
            header.Children.Add(InstallUi.Text(L.T("lcpp.adjusters.description")));
            header.Children.Add(InstallUi.Text(string.Join("\n", state.PreservedAdjusters)));
            actions.Children.Add(InstallUi.Button(L.T("lcpp.adjusters.replace"), () =>
                {
                    InstallPreservedAdjusters = true;
                    DialogResult = true;
                }));
        }
        actions.Children.Add(InstallUi.Button(L.T("ui.nve.removeSelectedOptions"), () =>
            {
                var selected = checks.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList();
                if (selected.Count == 0)
                {
                    MessageBox.Show(this, L.T("ui.nve.selectOptionsRemove"));
                    return;
                }
                Request = new() { InstallationId = state.Id, Components = selected };
                DialogResult = true;
            }));
        actions.Children.Add(InstallUi.Button(L.T(state.IsLcpp ? "lcpp.remove" : "ui.nve.removeAllNaturalvision"), () =>
            {
                Request = new() { InstallationId = state.Id };
                DialogResult = true;
            }));
        root.Children.Add(new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
}
