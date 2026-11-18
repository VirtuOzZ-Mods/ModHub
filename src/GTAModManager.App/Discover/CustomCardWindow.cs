using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Discover;

/// <summary>
/// Card editor: a GTA5-Mods link becomes a card. The page gives the title, the author with the avatar and
/// author links, and the gallery; the user flips through the screenshots and picks one cover, then checks the
/// game editions and, for Legacy, the ScriptHookVDotNet builds. Previews stay in memory: only the picked cover
/// is stored (by the metadata cache once the card is saved).
/// </summary>
internal sealed class CustomCardWindow : Window
{
    private readonly ModMetadataProvider _provider;
    private readonly CustomCardCollection _collection;
    private readonly CustomCard? _existing;
    private readonly Dictionary<string, ImageSource?> _previews = [];
    private CancellationTokenSource? _cts;
    private Gta5ModsPage? _page;
    private string? _pageUrl;
    private int _index;

    private readonly TextBox _url = new()
    {
        Margin = new Thickness(0, 0, 8, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly Button _load;
    private readonly TextBlock _status = Muted("");
    private readonly StackPanel _details = new()
    {
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 14, 0, 0),
    };
    private readonly Border _preview = new()
    {
        Height = 270,
        CornerRadius = new CornerRadius(12),
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x21)),
        ClipToBounds = true,
    };
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _previewNote = Muted("");
    private readonly TextBlock _counter = Muted("");
    private readonly Button _prev;
    private readonly Button _next;
    private readonly TextBox _title = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly Ellipse _avatar = new()
    {
        Width = 22,
        Height = 22,
        Margin = new Thickness(0, 0, 8, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _author = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _links = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _legacy = new() { Content = "Legacy", Margin = new Thickness(0, 4, 18, 4) };
    private readonly CheckBox _enhanced = new() { Content = "Enhanced", Margin = new Thickness(0, 4, 18, 4) };
    private readonly CheckBox _nightly = new() { Margin = new Thickness(0, 4, 18, 4) };
    private readonly CheckBox _chiheb = new() { Margin = new Thickness(0, 4, 18, 4) };
    private readonly CheckBox _editionUnsure = new() { Margin = new Thickness(0, 4, 18, 4) };
    private readonly CheckBox _shvdnUnsure = new() { Margin = new Thickness(0, 4, 18, 4) };
    private readonly StackPanel _shvdn = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _save;

    /// <summary>The saved card (on DialogResult true).</summary>
    public CustomCard? Result { get; private set; }

    /// <summary>Metadata read from the page, stored in the cache with the card.</summary>
    public ModMetadata? Page => _page?.Data;

    public CustomCardWindow(
        Window? owner,
        ModMetadataProvider provider,
        CustomCardCollection collection,
        CustomCard? existing
    )
    {
        _provider = provider;
        _collection = collection;
        _existing = existing;
        if (owner is not null)
            Owner = owner;
        Title = existing is null ? L.T("ui.discover.addModFromGta5Mods") : L.T("ui.discover.editCard");
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Bg");
        Foreground = Brushes.White;

        var root = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // Window chrome takes about 40 px on top of the content.
            MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 50),
        };

        root.Children.Add(
            Header(
                collection == CustomCardCollection.User
                    ? L.T("ui.discover.modPageLink")
                    : L.T("ui.discover.modPageLinkRecommended")
            )
        );
        var urlRow = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
        _load = new Button
        {
            Content = L.T("ui.discover.loadPage"),
            Style = (Style)Application.Current.FindResource("Ghost"),
            Padding = new Thickness(14, 6, 14, 6),
        };
        _load.Click += (_, _) => _ = LoadAsync();
        DockPanel.SetDock(_load, Dock.Right);
        urlRow.Children.Add(_load);
        urlRow.Children.Add(_url);
        root.Children.Add(urlRow);
        _url.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = LoadAsync();
            }
        };
        _status.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(_status);
        root.Children.Add(_details);

        // Cover: flip through the gallery, the shown image becomes the cover.
        _details.Children.Add(Header(L.T("ui.discover.cover")));
        var previewGrid = new Grid();
        previewGrid.Children.Add(_image);
        _previewNote.HorizontalAlignment = HorizontalAlignment.Center;
        _previewNote.VerticalAlignment = VerticalAlignment.Center;
        _previewNote.TextWrapping = TextWrapping.Wrap;
        previewGrid.Children.Add(_previewNote);
        _preview.Child = previewGrid;
        _preview.Margin = new Thickness(0, 4, 0, 6);
        _details.Children.Add(_preview);
        var nav = new DockPanel();
        _prev = NavButton("", -1);
        _next = NavButton("", 1);
        DockPanel.SetDock(_prev, Dock.Left);
        DockPanel.SetDock(_next, Dock.Right);
        nav.Children.Add(_prev);
        nav.Children.Add(_next);
        _counter.HorizontalAlignment = HorizontalAlignment.Center;
        _counter.VerticalAlignment = VerticalAlignment.Center;
        nav.Children.Add(_counter);
        _details.Children.Add(nav);
        PreviewKeyDown += (_, e) =>
        {
            if (_url.IsKeyboardFocusWithin || _title.IsKeyboardFocusWithin)
                return;
            if (e.Key == Key.Left)
                Flip(-1);
            else if (e.Key == Key.Right)
                Flip(1);
        };

        _details.Children.Add(Header(L.T("ui.discover.cardTitle"), 14));
        _details.Children.Add(_title);

        _details.Children.Add(Header(L.T("ui.discover.cardAuthor"), 4));
        var authorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        authorRow.Children.Add(_avatar);
        authorRow.Children.Add(_author);
        _details.Children.Add(authorRow);
        _details.Children.Add(_links);

        _details.Children.Add(Header(L.T("ui.discover.gameVersion"), 14));
        var editions = new WrapPanel();
        editions.Children.Add(_legacy);
        editions.Children.Add(_enhanced);
        editions.Children.Add(_editionUnsure);
        _details.Children.Add(editions);

        _nightly.Content = L.T("ui.base.nightly");
        _chiheb.Content = L.T("ui.base.legacyEnhancedChihebBacha");
        _shvdn.Children.Add(Header(L.T("ui.discover.shvdnForLegacy"), 6));
        var builds = new WrapPanel();
        builds.Children.Add(_nightly);
        builds.Children.Add(_chiheb);
        builds.Children.Add(_shvdnUnsure);
        _shvdn.Children.Add(builds);
        _shvdn.Children.Add(Muted(L.T("ui.discover.shvdnForLegacyHint")));
        _details.Children.Add(_shvdn);
        _editionUnsure.Content = L.T("ui.discover.notSure");
        _editionUnsure.ToolTip = L.T("ui.discover.notSureEditionTip");
        _shvdnUnsure.Content = L.T("ui.discover.notSure");
        _shvdnUnsure.ToolTip = L.T("ui.discover.notSureShvdnTip");
        // "Not sure" and the specific options of the same group exclude each other.
        Exclusive(_editionUnsure, _legacy, _enhanced);
        Exclusive(_shvdnUnsure, _nightly, _chiheb);
        foreach (var box in new[] { _legacy, _editionUnsure })
        {
            box.Checked += (_, _) => UpdateShvdn();
            box.Unchecked += (_, _) => UpdateShvdn();
        }

        _save = new Button
        {
            Content = L.T("ui.common.save"),
            Style = (Style)Application.Current.FindResource("Primary"),
            Margin = new Thickness(0, 18, 8, 0),
            IsEnabled = false,
        };
        _save.Click += (_, _) => Save();
        var cancel = new Button
        {
            Content = L.T("ui.common.cancel"),
            Style = (Style)Application.Current.FindResource("Ghost"),
            Margin = new Thickness(0, 18, 0, 0),
            IsCancel = true,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(_save);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        if (existing is not null)
        {
            _url.Text = existing.Url;
            _legacy.IsChecked = existing.Legacy;
            _enhanced.IsChecked = existing.Enhanced;
            _nightly.IsChecked = existing.ShvdnNightly;
            _chiheb.IsChecked = existing.ShvdnLegacyEnhanced;
            _editionUnsure.IsChecked = existing.EditionUnsure;
            _shvdnUnsure.IsChecked = existing.ShvdnUnsure;
        }
        UpdateShvdn();
        Loaded += (_, _) =>
        {
            if (existing is not null)
                _ = LoadAsync();
            else
                _url.Focus();
        };
        Closed += (_, _) => _cts?.Cancel();
        // The window grows when the page loads: keep it inside the work area instead of below the taskbar.
        SizeChanged += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            if (Top + ActualHeight > area.Bottom)
                Top = Math.Max(area.Top, area.Bottom - ActualHeight);
        };
    }

    /// <summary>The mod may run on Legacy, so its ScriptHookVDotNet build matters.</summary>
    private void UpdateShvdn() => _shvdn.IsEnabled = _legacy.IsChecked == true || _editionUnsure.IsChecked == true;

    private static void Exclusive(CheckBox unsure, params CheckBox[] specific)
    {
        unsure.Checked += (_, _) =>
        {
            foreach (var box in specific)
                box.IsChecked = false;
        };
        foreach (var box in specific)
            box.Checked += (_, _) => unsure.IsChecked = false;
    }

    private async Task LoadAsync()
    {
        if (CustomCards.Normalize(_url.Text) is not { } url)
        {
            _status.Text = L.T("ui.discover.notGta5ModsModLink");
            return;
        }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _load.IsEnabled = false;
        _save.IsEnabled = false;
        _status.Text = L.T("ui.inventory.loadingPage");
        try
        {
            var page = await Task.Run(() => _provider.LoadPageAsync(url, cts.Token), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            bool samePage = _existing is not null && _existing.Url == url;
            bool reload = _pageUrl == url;
            _page = page;
            _pageUrl = url;
            _url.Text = url;
            _previews.Clear();
            var images = page.Images.ToList();
            // Keep the cover picked earlier selected, even if the gallery no longer lists it first.
            var current = samePage && !reload ? _existing!.CoverUrl : null;
            if (current is not null && !images.Contains(current))
                images.Insert(0, current);
            _page = page with { Images = images };
            _index = current is null ? 0 : Math.Max(0, images.IndexOf(current));
            if (!reload)
                _title.Text = samePage ? _existing!.Title : page.Data.Title ?? CustomCards.Slug(url) ?? "";
            ShowAuthor(page.Data);
            if (!samePage && !reload && page.Data.Compat is { } compat)
            {
                // Pre-check what the author declares on the page; the user confirms or corrects it.
                _editionUnsure.IsChecked = false;
                _legacy.IsChecked = compat.Legacy == true;
                _enhanced.IsChecked = compat.Enhanced == true;
            }
            _details.Visibility = Visibility.Visible;
            _save.IsEnabled = true;
            _status.Text = images.Count == 0 ? L.T("ui.discover.pageHasNoImages") : L.T("ui.discover.pickCoverHint");
            await ShowPreviewAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
            when (ex is HttpRequestException or InvalidDataException or IOException or TaskCanceledException)
        {
            _status.Text = L.T(
                "ui.discover.couldNotLoadPage",
                ex is TaskCanceledException ? L.T("core.discover.responseTimedOut") : L.Owned(ex.Message)
            );
            _save.IsEnabled = _page is not null;
        }
        finally
        {
            if (_cts == cts)
                _load.IsEnabled = true;
        }
    }

    private void ShowAuthor(ModMetadata data)
    {
        _author.Text = data.Author ?? L.T("ui.discover.authorNoData");
        _avatar.Visibility = Visibility.Collapsed;
        if (data.AuthorAvatarUrl is { } avatarUrl)
            _ = LoadAvatarAsync(avatarUrl);
        _links.Children.Clear();
        foreach (var link in data.Links.Select(AuthorLinks.View))
        {
            var pill = new StackPanel { Orientation = Orientation.Horizontal };
            pill.Children.Add(
                new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(4),
                    Background = link.Brush,
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
            pill.Children.Add(
                new TextBlock
                {
                    Text = link.Label,
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
            var button = new Button
            {
                Content = pill,
                Style = (Style)Application.Current.FindResource("LinkButton"),
                ToolTip = link.Url,
                Margin = new Thickness(0, 0, 6, 6),
            };
            var url = link.Url;
            button.Click += (_, _) =>
            {
                if (LinkSafety.IsSafeWebUrl(url, out var u) && u is not null)
                    BrowserView.OpenExternal(u);
            };
            _links.Children.Add(button);
        }
        if (_links.Children.Count == 0)
            _links.Children.Add(Muted(L.T("ui.discover.noAuthorLinks")));
    }

    private async Task LoadAvatarAsync(string url)
    {
        var image = await PreviewAsync(url, 64);
        if (image is null || _page?.Data.AuthorAvatarUrl != url)
            return;
        _avatar.Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        _avatar.Visibility = Visibility.Visible;
    }

    private Button NavButton(string glyph, int step)
    {
        var b = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
            },
            Style = (Style)Application.Current.FindResource("Ghost"),
            Padding = new Thickness(14, 6, 14, 6),
            ToolTip = step < 0 ? L.T("ui.discover.previousImage") : L.T("ui.discover.nextImage"),
        };
        b.Click += (_, _) => Flip(step);
        return b;
    }

    private void Flip(int step)
    {
        if (_page is not { Images.Count: > 1 } page)
            return;
        _index = (_index + step + page.Images.Count) % page.Images.Count;
        _ = ShowPreviewAsync();
    }

    private async Task ShowPreviewAsync()
    {
        var images = _page?.Images ?? [];
        _prev.IsEnabled = _next.IsEnabled = images.Count > 1;
        _counter.Text = images.Count == 0 ? "" : L.T("ui.discover.imageCounter", _index + 1, images.Count);
        if (images.Count == 0)
        {
            _image.Source = null;
            _previewNote.Text = L.T("ui.discover.pageHasNoImages");
            return;
        }
        var url = images[_index];
        if (!_previews.ContainsKey(url))
        {
            _image.Source = null;
            _previewNote.Text = L.T("ui.discover.loadingImage");
        }
        var image = await PreviewAsync(url, 960);
        if (_page is null || _page.Images.Count <= _index || _page.Images[_index] != url)
            return; // flipped further while loading
        _image.Source = image;
        _previewNote.Text = image is null ? L.T("ui.discover.imageUnavailable") : "";
    }

    /// <summary>Downloads and decodes an image of the page once per window; nothing is written to disk.</summary>
    private async Task<ImageSource?> PreviewAsync(string url, int width)
    {
        if (_previews.TryGetValue(url, out var cached))
            return cached;
        ImageSource? image = null;
        try
        {
            var bytes = await Task.Run(() => _provider.DownloadPreviewAsync(url, _cts?.Token ?? default));
            image = bytes is null ? null : await Task.Run(() => Decode(bytes, width));
        }
        catch (Exception ex)
            when (ex is HttpRequestException or InvalidDataException or IOException or OperationCanceledException) { }
        _previews[url] = image;
        return image;
    }

    private static ImageSource? Decode(byte[] bytes, int width)
    {
        try
        {
            var bi = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelWidth = width;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex)
            when (ex
                    is NotSupportedException
                        or IOException
                        or FileFormatException
                        or InvalidOperationException
                        or ArgumentException
            )
        {
            return null;
        }
    }

    private void Save()
    {
        if (_page is null || _pageUrl is null)
            return;
        if (_title.Text.Trim().Length == 0)
        {
            _status.Text = L.T("ui.inventory.enterModName");
            return;
        }
        if (_legacy.IsChecked != true && _enhanced.IsChecked != true && _editionUnsure.IsChecked != true)
        {
            _status.Text = L.T("ui.discover.pickGameVersion");
            return;
        }
        bool legacy = _legacy.IsChecked == true;
        Result = new CustomCard
        {
            Collection = _collection,
            Url = _pageUrl,
            Title = _title.Text.Trim(),
            Author = _page.Data.Author,
            CoverUrl = _page.Images.Count > 0 ? _page.Images[_index] : null,
            Legacy = legacy,
            Enhanced = _enhanced.IsChecked == true,
            EditionUnsure = _editionUnsure.IsChecked == true,
            ShvdnNightly = _nightly.IsChecked == true,
            ShvdnLegacyEnhanced = _chiheb.IsChecked == true,
            ShvdnUnsure = _shvdnUnsure.IsChecked == true,
        }.Consistent();
        DialogResult = true;
    }

    private static TextBlock Header(string text, double top = 0) =>
        new()
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, top, 0, 0),
        };

    private static TextBlock Muted(string text) =>
        new()
        {
            Text = text,
            Foreground = (Brush)Application.Current.FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
}
