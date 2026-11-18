using System.Windows;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

/// <summary>
/// Support the Author card: the mod was downloaded from GTA5-Mods through Mod Hub and has been tried in game, so we suggest opening
/// its page to like, rate or comment. One card at a time, and only once per mod.
/// </summary>
public partial class MainWindow
{
    private readonly AuthorThanks _thanks = new();
    private readonly HashSet<string> _thanksSnoozed = new(StringComparer.OrdinalIgnoreCase);
    private ThanksEntry? _thanksShown;
    private bool _thanksChecking;

    private async Task RefreshThanksAsync()
    {
        if (ThanksCard is null || _thanksChecking)
            return;
        if (!AuthorThanks.Enabled)
        {
            HideThanks();
            return;
        }
        var game = Current;
        if (game is null || !IsReady(game))
        {
            HideThanks();
            return;
        }
        _thanksChecking = true;
        try
        {
            var entry = await Task.Run(() =>
                game.IsRunning() ? null
                : _thanks.Next(game, GameReadiness.LastLaunchUtc(game.Root)) is { } e && !_thanksSnoozed.Contains(e.Url)
                    ? e
                : null
            );
            if (Current?.Root.Equals(game.Root, StringComparison.OrdinalIgnoreCase) != true)
                return;
            if (entry is null)
            {
                HideThanks();
                return;
            }
            _thanksShown = entry;
            ThanksTitle.Text = L.T("ui.thanks.howDoLike", entry.Title);
            ThanksCard.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            HideThanks();
        }
        finally
        {
            _thanksChecking = false;
        }
    }

    private void HideThanks()
    {
        _thanksShown = null;
        if (ThanksCard is not null)
            ThanksCard.Visibility = Visibility.Collapsed;
    }

    private void OnThanksOpen(object sender, RoutedEventArgs e)
    {
        if (_thanksShown is not { } entry)
            return;
        // Use the regular browser: the user is most likely already signed in to GTA5-Mods there. The page is the one the mod was downloaded from.
        if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) && AuthorThanks.IsGta5Mods(entry.Url))
            Discover.BrowserView.OpenExternal(uri);
        _thanks.Finish(entry.Url);
        HideThanks();
        _ = RefreshThanksAsync();
    }

    private void OnThanksLater(object sender, RoutedEventArgs e)
    {
        if (_thanksShown is { } entry)
            _thanksSnoozed.Add(entry.Url);
        HideThanks();
    }

    private void OnThanksNever(object sender, RoutedEventArgs e)
    {
        if (_thanksShown is { } entry)
            _thanks.Finish(entry.Url);
        HideThanks();
        _ = RefreshThanksAsync();
    }
}
