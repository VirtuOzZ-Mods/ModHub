using System.IO;
using System.Net.Http;
using System.Windows;
using GTAModManager.App.Discover;
using GTAModManager.Core.Base;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

/// <summary>
/// Recommendations → Install: checks the game edition, downloads from GTA5-Mods in the background and opens the regular package card
/// on the Install Mod page (what is written where, requirements, optional components). Files are written only when
/// the user clicks Install on the package card, exactly as when an archive is picked manually.
/// </summary>
public partial class MainWindow
{
    private readonly List<string> _discoverDownloads = [];
    private readonly HashSet<string> _recommendationInstalls = new(StringComparer.OrdinalIgnoreCase);

    private void RememberRecommendationInstall(string source) => _recommendationInstalls.Add(Path.GetFullPath(source));

    private bool FinishRecommendationInstall(string? source)
    {
        if (source is null || !_recommendationInstalls.Remove(Path.GetFullPath(source)) || _rebuildTotal > 0)
            return false;
        DiscoverTab.IsChecked = true;
        DiscoverView.RefreshStatuses();
        return true;
    }

    private void InitializeDiscoverInstall()
    {
        LegacyOption.Checked += (_, _) => DiscoverView.SetGame(Current);
        EnhancedOption.Checked += (_, _) => DiscoverView.SetGame(Current);
        DiscoverView.SetGame(Current);
        DiscoverView.InstallRequested += card => _ = InstallFromDiscoverAsync(card);
        InitializeInstalledStatus();
        Closed += (_, _) =>
        {
            foreach (var dir in _discoverDownloads)
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }

    private void OnOpenGta5Mods(object sender, RoutedEventArgs e) =>
        Discover.BrowserView.OpenExternal(new Uri("https://www.gta5-mods.com/"));

    private void OnOpenAuthorProfile(object sender, RoutedEventArgs e) =>
        Discover.BrowserView.OpenExternal(new Uri("https://www.gta5-mods.com/users/VirtuOzZ"));

    private async Task InstallFromDiscoverAsync(ModCardViewModel card)
    {
        var game = Current;
        if (game is null)
        {
            MessageBox.Show(
                this,
                L.T("ui.discover.selectGameLeftFirstLegacy"),
                "Mod Hub by VirtuOzZ",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            return;
        }
        if (Core.Installation.RecommendedMods.Find(card.Entry.Id) is not null)
        {
            await InstallRecommendationAsync(card, game);
            return;
        }
        if (!EnsureReady())
            return;
        if (card.Entry.Install == "script-library")
        {
            await InstallScriptLibraryAsync(card, game);
            return;
        }
        if (card.Entry.Install == "base")
        {
            // Script Hook V and ScriptHookVDotNet are installed by the Modding Essentials card (the right build for the game edition).
            InstallTab.IsChecked = true;
            FilesView.BaseCard.Refresh(game);
            BaseCardHost.BringIntoView();
            StatusText.Text = card.EditionWarning(game.Edition == GameEdition.Enhanced) is null
                ? L.T("ui.discover.partModHubBasePackage", card.Title, game.Edition)
                : L.T("ui.discover.onlyRequiredAnotherGameVersion", card.Title, game.Edition);
            return;
        }
        if (
            card.EditionWarning(game.Edition == GameEdition.Enhanced) is { } warning
            && MessageBox.Show(
                this,
                L.T("ui.discover.continueAnyway", warning),
                L.T("ui.discover.modDifferentGameVersion"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No
            ) != MessageBoxResult.Yes
        )
            return;

        // A card marked "not sure" may be installed on either edition, but only after a launch warning.
        if (
            card.UnsureWarning(game.Edition == GameEdition.Enhanced) is { } unsure
            && MessageBox.Show(
                this,
                unsure,
                L.T("ui.discover.unsureInstallTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No
            ) != MessageBoxResult.Yes
        )
            return;

        if (card.Entry.Install != "download" || !Uri.TryCreate(card.Entry.Url, UriKind.Absolute, out var page))
            return;

        var dir = Path.Combine(Path.GetTempPath(), "ModHub", "discover-" + Guid.NewGuid().ToString("N")[..10]);
        _discoverDownloads.Add(dir);
        card.InstallState = L.T("ui.common.downloading");
        try
        {
            using var http = BaseDownloads.CreateClient();
            var progress = new Progress<double>(p => card.InstallState = L.T("ui.discover.downloading", p * 100));
            var mod = await Gta5ModsDownloads.ResolveAsync(http, page, CancellationToken.None);
            // An author may publish the current version for one edition only ("1.9.5 [Legacy ONLY]").
            string mine = game.Edition == GameEdition.Enhanced ? "Enhanced" : "Legacy";
            if (
                Gta5ModsDownloads.EditionOnly(mod.Version) is { } only
                && only != mine
                && MessageBox.Show(
                    this,
                    L.T("ui.discover.currentVersionGta5ModsGta", card.Title, mod.Version, only, mine),
                    L.T("ui.discover.modDifferentGameVersion"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No
                ) != MessageBoxResult.Yes
            )
            {
                card.InstallState = null;
                return;
            }
            var file = await Gta5ModsDownloads.DownloadAsync(http, mod, dir, progress, CancellationToken.None);
            card.InstallState = null;
            _packageSource = (file, page.AbsoluteUri, card.Title);
            RememberRecommendationInstall(file);
            await AnalyzeAsync(file);
        }
        catch (Exception ex)
            when (ex
                    is HttpRequestException
                        or InvalidDataException
                        or IOException
                        or TaskCanceledException
                        or UnauthorizedAccessException
            )
        {
            card.InstallState = null;
            if (
                MessageBox.Show(
                    this,
                    L.T("ui.discover.couldNotDownloadOpenMod", card.Title, L.Owned(ex.Message)),
                    "Mod Hub by VirtuOzZ",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                ) == MessageBoxResult.Yes
            )
                Discover.BrowserView.OpenExternal(page);
        }
    }

    private async Task InstallRecommendationAsync(ModCardViewModel card, GameInstall game)
    {
        var component = Core.Installation.RecommendedMods.Get(card.Entry.Id);
        if (card.Entry.Install != "download" || !component.Supports(game.Edition))
            return;
        if (!EnsureReady())
            return;
        card.InstallState = Core.Localization.L.T("ui.common.downloading");
        try
        {
            using var http = BaseDownloads.CreateClient();
            var page = new Uri(card.Entry.Url);
            var mod = await Gta5ModsDownloads.ResolveAsync(http, page, CancellationToken.None);
            var downloads = new Core.Installation.ManagedDownloads();
            var directory = Path.Combine(Path.GetTempPath(), "ModHub", "recommended-" + Guid.NewGuid().ToString("N"));
            _discoverDownloads.Add(directory);
            var progress = new Progress<double>(p =>
                card.InstallState = Core.Localization.L.T("ui.discover.downloading", p * 100)
            );
            var file = await Gta5ModsDownloads.DownloadAsync(http, mod, directory, progress, CancellationToken.None);
            var receipt = downloads.Begin(Path.GetFileName(file), page.AbsoluteUri, game.Root);
            File.Move(file, receipt.File);
            receipt = downloads.Complete(receipt.Id);
            Core.Installation.RecommendedMods.ValidateArchive(component.Id, receipt.File);
            RememberRecommendationInstall(receipt.File);
            await ShowFilesViewAsync();
            await FilesView.AddAsync(
                [receipt.File],
                game.Root,
                receipt.Id,
                receipt.SourceUrl,
                recommendedId: component.Id
            );
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or HttpRequestException
                        or InvalidOperationException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or System.Text.Json.JsonException
                        or OperationCanceledException
            )
        {
            MessageBox.Show(this, Core.Localization.L.Owned(ex.Message), card.Title);
        }
        finally
        {
            card.InstallState = null;
        }
    }
}
