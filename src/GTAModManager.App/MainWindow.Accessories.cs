using System.Windows;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Peds;

namespace GTAModManager.App;

/// <summary>Character Accessories section and gender selection for untagged MP character clothing.</summary>
public partial class MainWindow
{
    private bool _accessoriesWired;

    private void OnAccessoriesTab(object sender, RoutedEventArgs e)
    {
        OnPageChanged(sender, e); // hides the other sections
        if (!_accessoriesWired)
        {
            _accessoriesWired = true;
            AccessoriesView.OpenHistoryRequested += () => HistoryTab.IsChecked = true;
            AccessoriesView.InstallRequested += () => OnPickPackage(this, new RoutedEventArgs());
        }
        AccessoriesHost.Visibility = Visibility.Visible;
        _ = AccessoriesView.LoadAsync(Current, Core.Library.AppSettings.Load().OpenLibrary());
    }

    private void OnAccessoriesTabOff(object sender, RoutedEventArgs e) =>
        AccessoriesHost.Visibility = Visibility.Collapsed;

    /// <summary>
    /// MP character clothing without a gender tag: ask who it is for and remember the choice for this archive
    /// (the installer reads the same value). Returns false if the user cancelled.
    /// </summary>
    private async Task<bool> EnsurePedGenderAsync(GameInstall game, string path, bool? known = null)
    {
        bool needs;
        try
        {
            needs = known
                ?? await Task.Run(() =>
                {
                    using var archive = ModArchive.Open(path);
                    return ModAnalyzer.Analyze(archive, game).NeedsGender;
                });
        }
        catch (Exception ex)
            when (ex
                    is System.IO.IOException
                        or System.IO.InvalidDataException
                        or InvalidOperationException
                        or ArgumentException
                        or NotSupportedException
            )
        {
            return true;
        } // the analysis will report the read error anyway
        if (!needs)
            return true;
        var answer = MessageBox.Show(
            this,
            L.T("ui.accessories.clothingMpCharacterButArchive", System.IO.Path.GetFileName(path)),
            L.T("ui.accessories.whoClothing"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel
        );
        if (answer == MessageBoxResult.Cancel)
            return false;
        PedAssetChoices.Set(path, answer == MessageBoxResult.Yes ? PedGender.Male : PedGender.Female);
        return true;
    }
}
