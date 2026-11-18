using System.IO;
using System.Net.Http;
using System.Windows;
using GTAModManager.Core.Base;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Inventory;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

/// <summary>
/// Rebuild from list: mods from the list are downloaded from GTA5-Mods (as a site visitor, so the download counts for the author)
/// and opened one by one in the regular package card. The user installs each with Apply; ✕ skips the mod.
/// </summary>
public partial class MainWindow
{
    private readonly Queue<(string File, string Url, string Name)> _rebuild = new();
    private int _rebuildTotal;

    private void InitializeRebuild() => MyModsView.RebuildRequested += mods => _ = StartRebuildAsync(mods);

    private async Task StartRebuildAsync(IReadOnlyList<ModListEntry> mods)
    {
        if (Current is not { } game)
            return;
        if (!EnsureReady())
            return;
        var dir = Path.Combine(Path.GetTempPath(), "ModHub", "rebuild-" + Guid.NewGuid().ToString("N")[..10]);
        _discoverDownloads.Add(dir);
        var skipped = new List<string>();
        string mine = game.Edition == GameEdition.Enhanced ? "Enhanced" : "Legacy";
        _rebuild.Clear();
        try
        {
            using var http = StarterPackage.CreateSiteClient();
            for (int i = 0; i < mods.Count; i++)
            {
                var m = mods[i];
                SetBusy(
                    true,
                    L.T("ui.rebuild.downloadingMods", i + 1, mods.Count),
                    L.T("ui.rebuild.fromGta5ModsJustLike", m.Name)
                );
                try
                {
                    var file = await Gta5ModsDownloads.ResolveAsync(
                        http,
                        new Uri(AuthorThanks.ModPage(m.Url!)),
                        CancellationToken.None
                    );
                    if (Gta5ModsDownloads.EditionOnly(file.Version) is { } only && only != mine)
                    {
                        skipped.Add(L.T("ui.rebuild.currentVersionGtaVOnly", m.Name, file.Version, only));
                        continue;
                    }
                    var path = await Gta5ModsDownloads.DownloadAsync(
                        http,
                        file,
                        Path.Combine(dir, i.ToString()),
                        null,
                        CancellationToken.None
                    );
                    _rebuild.Enqueue((path, m.Url!, m.Name));
                }
                catch (Exception ex)
                    when (ex
                            is HttpRequestException
                                or InvalidDataException
                                or IOException
                                or TaskCanceledException
                                or UnauthorizedAccessException
                                or UriFormatException
                    )
                {
                    skipped.Add(L.T("ui.rebuild.downloadFailed", m.Name, L.Owned(ex.Message)));
                }
            }
        }
        finally
        {
            SetBusy(false);
        }

        _rebuildTotal = _rebuild.Count;
        if (skipped.Count > 0)
            MessageBox.Show(
                this,
                L.T(
                    "ui.rebuild.failed",
                    (_rebuildTotal > 0 ? L.T("ui.rebuild.modsDownloaded", _rebuildTotal) : ""),
                    Environment.NewLine,
                    Environment.NewLine,
                    L.Join(Environment.NewLine, skipped.Select(s => "•  " + s))
                ),
                L.T("ui.common.buildFromList"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        if (_rebuildTotal == 0)
            return;
        InstallTab.IsChecked = true;
        await ContinueRebuildAsync();
    }

    /// <summary>Opens the next mod from the queue (after Apply or ✕ on the package card).</summary>
    private async Task ContinueRebuildAsync()
    {
        if (_rebuild.Count == 0)
        {
            if (_rebuildTotal > 0)
            {
                StatusText.Text = L.T("ui.rebuild.buildFromListCompleteQueue", _rebuildTotal);
                _rebuildTotal = 0;
                _ = MyModsView.ScanAsync(Current, force: true);
            }
            return;
        }
        var (file, url, name) = _rebuild.Dequeue();
        _packageSource = (file, url, name);
        await AnalyzeAsync(file);
        StatusText.Text = L.T(
            "ui.rebuild.buildingFromListModCheck",
            _rebuildTotal - _rebuild.Count,
            _rebuildTotal,
            name
        );
    }
}
