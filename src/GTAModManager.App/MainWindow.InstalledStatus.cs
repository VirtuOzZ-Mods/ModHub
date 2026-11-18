using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Transactions;

namespace GTAModManager.App;

/// <summary>
/// Installed and Update available in Recommendations: records installs made through Mod Hub,
/// the bell with a red dot in the header, and a background version check on the pages of installed mods.
/// </summary>
public partial class MainWindow
{
    private readonly InstalledMods _installedMods = new();
    private DispatcherTimer? _updatesTimer;

    private void InitializeInstalledStatus()
    {
        DiscoverView.StatusesChanged += RefreshUpdatesBell;
        FilesView.JobCompleted += (game, job) =>
            RecordCatalogInstall(
                game,
                job.RecommendedId ?? job.ScriptLibraryId,
                job.SourceUrl,
                job.TransactionId is { } id ? [id] : [],
                null
            );
        // First check once the window is on screen, then every 6 hours (mod page data is cached for 12 hours).
        ContentRendered += async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            await DiscoverView.CheckUpdatesAsync();
        };
        _updatesTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updatesTimer.Tick += async (_, _) => await DiscoverView.CheckUpdatesAsync();
        _updatesTimer.Start();
        Closed += (_, _) => _updatesTimer?.Stop();
    }

    /// <summary>
    /// An install from Recommendations succeeded: remember the catalog mod, the site version and the anchor files of the install.
    /// Transactions are matched by id, or all completed after sinceUtc (the main install and add-ons after the OIV).
    /// </summary>
    private void RecordCatalogInstall(
        GameInstall game,
        string? catalogId,
        string? sourceUrl,
        IReadOnlyList<string> transactions,
        DateTime? sinceUtc
    )
    {
        if (DiscoverView.FindEntry(catalogId, sourceUrl) is not { } entry)
            return;
        try
        {
            var engine = game.CreateEngine();
            var journals =
                transactions.Count > 0
                    ? transactions.Select(engine.LoadJournal).Where(j => j is not null).Cast<TxJournal>().ToList()
                    : engine
                        .History()
                        .Where(j =>
                            j.State == TxState.Completed && sinceUtc is { } since && j.Updated >= since.AddSeconds(-2)
                        )
                        .ToList();
            var files = journals.SelectMany(j => j.Ops).Where(op => op.PostHash is not null).Select(op => op.Rel);
            _installedMods.Record(game, entry.Id, DiscoverView.SiteVersion(entry.Id), files);
        }
        catch (Exception ex)
            when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        DiscoverView.RefreshStatuses();
    }

    private void RefreshUpdatesBell()
    {
        var updates = DiscoverView.Updates;
        UpdatesBell.Tag = updates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesBell.ToolTip =
            updates.Count > 0
                ? L.T("ui.installedStatus.modUpdatesAvailable", updates.Count)
                : L.T("ui.common.modUpdates");
    }

    private void OnUpdatesBell(object sender, RoutedEventArgs e)
    {
        var updates = DiscoverView.Updates;
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("UpdatesMenu"),
            PlacementTarget = UpdatesBell,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        if (updates.Count == 0)
            menu.Items.Add(
                new MenuItem { Header = L.T("ui.installedStatus.noUpdatesModsFromRecommendations"), IsEnabled = false }
            );
        foreach (var card in updates)
        {
            var known = Current is { } g ? _installedMods.Get(g.Root, card.Entry.Id)?.Version : null;
            var item = new MenuItem
            {
                Header = L.T(
                    "ui.installedStatus.installedSiteHasUpdate",
                    card.Title,
                    known ?? "?",
                    card.Cached?.Data.Version ?? "?"
                ),
            };
            item.Click += (_, _) =>
            {
                DiscoverTab.IsChecked = true;
                DiscoverView.FocusCard(card.Entry.Id);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
