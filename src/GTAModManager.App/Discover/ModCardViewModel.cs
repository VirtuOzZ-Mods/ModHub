using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Discover;

public sealed record LinkView(string Label, string Glyph, Brush Brush, string Url, string Tip);

public enum CoverState
{
    Loading,
    Ready,
    Missing,
}

/// <summary>Mod card: the static catalog entry plus dynamic data from the cache.</summary>
public sealed class ModCardViewModel : INotifyPropertyChanged
{
    private const double StarsFullWidth = 72;

    public CatalogEntry Entry { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public ModCardViewModel(CatalogEntry entry) => Entry = entry;

    /// <summary>A card the user built in the editor (My GTA5-Mods cards or Recommended drafts).</summary>
    public ModCardViewModel(CustomCard custom)
        : this(custom.ToCatalogEntry()) => Custom = custom;

    public CustomCard? Custom { get; }

    /// <summary>"Edit" for the user's own cards, shown while the list is being edited.</summary>
    public Visibility EditVisibility => Custom is not null && _canDelete ? Visibility.Visible : Visibility.Collapsed;

    private bool _canReorder;

    /// <summary>The arrows that move the card in a tab with a hand-set order (shown in the author's order only).</summary>
    public bool CanReorder
    {
        get => _canReorder;
        set
        {
            if (_canReorder == value)
                return;
            _canReorder = value;
            Raise();
            Raise(nameof(ReorderVisibility));
            Raise(nameof(ToolsVisibility));
        }
    }
    public Visibility ReorderVisibility => _canReorder ? Visibility.Visible : Visibility.Collapsed;

    private int _position;

    /// <summary>Place of the card in the tab's hand-set order (1-based), shown between the arrows.</summary>
    public int Position
    {
        get => _position;
        set
        {
            if (_position == value)
                return;
            _position = value;
            Raise();
        }
    }

    private bool _canDelete;

    /// <summary>The card sits in an editable tab and can be removed from it (built-in cards are hidden, custom ones deleted).</summary>
    public bool CanDelete
    {
        get => _canDelete;
        set
        {
            if (_canDelete == value)
                return;
            _canDelete = value;
            Raise();
            Raise(nameof(DeleteVisibility));
            Raise(nameof(EditVisibility));
            Raise(nameof(ToolsVisibility));
        }
    }
    public Visibility DeleteVisibility => _canDelete ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The editing row at the bottom of the card (order, edit, delete), shown only while editing the list.</summary>
    public Visibility ToolsVisibility => _canReorder || _canDelete ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The game version is not confirmed: the card offers both editions, installing warns first.</summary>
    public Visibility UnsureVisibility => Entry.EditionUnsure ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>ScriptHookVDotNet builds the mod needs on Legacy, as chosen in the editor or the catalog.</summary>
    public string ShvdnText =>
        L.T(
            "ui.discover.shvdnBuilds",
            string.Join(
                " · ",
                new[]
                {
                    Entry.ShvdnNightly ? L.T("ui.base.nightly") : null,
                    Entry.ShvdnLegacyEnhanced ? L.T("ui.base.legacyEnhancedChihebBacha") : null,
                    Entry.ShvdnUnsure ? L.T("ui.discover.notSureLower") : null,
                }.OfType<string>()
            )
        );
    public Visibility ShvdnVisibility =>
        Entry.ShvdnNightly || Entry.ShvdnLegacyEnhanced || Entry.ShvdnUnsure
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Whether the card belongs in an edition filter: "not sure" cards show under both editions.</summary>
    public bool MayRunOn(bool enhanced) => Entry.EditionUnsure || Compat.Supports(enhanced);

    /// <summary>
    /// Warning before installing a card whose game version (or, on Legacy, ScriptHookVDotNet build) is
    /// marked "not sure": installing is allowed, but the game may stop launching. null if confirmed.
    /// </summary>
    public string? UnsureWarning(bool enhanced) =>
        Entry.EditionUnsure ? L.T("ui.discover.unsureEditionWarning", Title, enhanced ? "Enhanced" : "Legacy")
        : !enhanced && Entry.ShvdnUnsure ? L.T("ui.discover.unsureShvdnWarning", Title)
        : null;

    public CachedMetadata? Cached { get; private set; }
    private ImageSource? _cover;
    private CoverState _coverState = CoverState.Loading;
    private bool _refreshing;

    public Stretch CoverStretch => Entry.Install == "script-library" ? Stretch.Uniform : Stretch.UniformToFill;
    public ImageSource? Cover
    {
        get => _cover;
        private set
        {
            _cover = value;
            Raise();
        }
    }
    private ImageSource? _avatar;
    private string? _avatarPath;

    /// <summary>Uploader avatar from the mod page (GTA5-Mods); hidden until it is cached.</summary>
    public ImageSource? Avatar
    {
        get => _avatar;
        private set
        {
            _avatar = value;
            Raise();
            Raise(nameof(AvatarVisibility));
        }
    }
    public Visibility AvatarVisibility => _avatar is null ? Visibility.Collapsed : Visibility.Visible;

    public CoverState CoverState
    {
        get => _coverState;
        private set
        {
            _coverState = value;
            Raise();
            Raise(nameof(SkeletonVisibility));
            Raise(nameof(PlaceholderVisibility));
        }
    }
    public bool Refreshing
    {
        get => _refreshing;
        set
        {
            _refreshing = value;
            Raise();
            Raise(nameof(SkeletonVisibility));
            Raise(nameof(StatsSkeletonVisibility));
            Raise(nameof(StatsVisibility));
        }
    }

    public Visibility SkeletonVisibility =>
        CoverState == CoverState.Loading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlaceholderVisibility =>
        CoverState == CoverState.Missing ? Visibility.Visible : Visibility.Collapsed;

    private ModMetadata? Data => Cached?.Data;

    public string Title => Data?.Title ?? Entry.Title;
    public string Initials =>
        string.Concat(
            Title
                .Split(' ', '-', '.', '&')
                .Where(w => w.Length > 0 && char.IsLetterOrDigit(w[0]))
                .Take(2)
                .Select(w => char.ToUpperInvariant(w[0]))
        );
    public string AuthorText => (Data?.Author ?? Entry.Author) is { } a ? a : L.T("ui.discover.authorNoData");
    public string VersionText =>
        Entry.Install == "script-library" ? GTAModManager.Core.Installation.ScriptLibraries.Get(Entry.Id).Tag
        : Data?.Version is { } v ? (char.IsDigit(v[0]) ? "v" + v : v)
        : "";
    public Visibility VersionVisibility =>
        Entry.Install != "script-library" && Data?.Version is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Compatibility: what the catalog confirms takes precedence over what is inferred from the page.</summary>
    public CompatInfo Compat
    {
        get
        {
            var c = Entry.Compat;
            if (IsReviewedRecommendation)
                return c ?? new CompatInfo();
            var d = Data?.Compat;
            return new CompatInfo
            {
                Legacy = c?.Legacy ?? d?.Legacy,
                Enhanced = c?.Enhanced ?? d?.Enhanced,
                Note = c?.Note,
            };
        }
    }
    public Visibility LegacyVisibility => Compat.Legacy == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EnhancedVisibility => Compat.Enhanced == true ? Visibility.Visible : Visibility.Collapsed;
    public string? Note => Compat.Note is { } note ? GTAModManager.Core.Localization.L.Display(note) : null;
    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
    public string? Kind => Entry.Kind is { } kind ? GTAModManager.Core.Localization.L.Display(kind) : null;
    public Visibility KindVisibility => string.IsNullOrEmpty(Entry.Kind) ? Visibility.Collapsed : Visibility.Visible;

    // Stats: only what the site provides; missing values show as "no data", not zero.
    public bool HasStatsSource => Entry.Source == MetaSource.Gta5Mods;
    public Visibility StatsVisibility =>
        HasStatsSource && Cached?.Data.Title is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StatsSkeletonVisibility =>
        HasStatsSource && Cached?.Data.Title is null && Refreshing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoStatsVisibility =>
        StatsVisibility == Visibility.Collapsed && StatsSkeletonVisibility == Visibility.Collapsed
            ? Visibility.Visible
            : Visibility.Collapsed;
    public string NoStatsText =>
        HasStatsSource ? L.T("ui.discover.statisticsNoData")
        : Entry.Source == MetaSource.GitHub ? L.T("ui.discover.githubNoRatingDownloadCount")
        : L.T("ui.discover.officialWebsiteNoRatingDownload");

    public string RatingText => Data?.Rating is { } r ? L.T("format.rating", r) : L.T("ui.discover.noData");
    public string VotesText => Data?.Votes is { } v ? L.T("format.parentheses", Count(v)) : "";
    public double StarsWidth => Data?.Rating is { } r ? Math.Clamp(r / 5.0, 0, 1) * StarsFullWidth : 0;
    public string LikesText => Data?.Likes is { } l ? Count(l) : L.T("ui.discover.noData");
    public string DownloadsText => Data?.Downloads is { } d ? Count(d) : L.T("ui.discover.noData");
    public string CommentsText => Data?.Comments is { } c ? Count(c) : L.T("ui.discover.noData");
    public string StatsTip =>
        L.T(
            "ui.discover.ratingLikesDownloadsComments",
            RatingText,
            VotesText,
            LikesText,
            (Data?.Downloads is { } d ? L.T("format.integer", d) : L.T("ui.discover.noData")),
            CommentsText
        );

    public string UpdatedText =>
        Data?.Updated is { } u ? L.T("ui.discover.updated", L.T("format.date.readable", u.ToLocalTime())) : "";
    public List<LinkView> Links => (Data?.Links ?? []).Select(ToLink).ToList();
    public Visibility LinksVisibility => Links.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    // Card buttons: mod page and install.
    public string PageButtonText =>
        Entry.Install == "naturalvision"
            ? L.T("ui.common.visitWebsite")
            : Entry.Source switch
            {
                MetaSource.Gta5Mods => L.T("ui.common.openOn"),
                MetaSource.GitHub => L.T("ui.discover.openGithub"),
                _ => L.T("ui.discover.openWebsite"),
            };
    public Visibility Gta5ModsPlateVisibility =>
        Entry.Install != "naturalvision" && Entry.Source == MetaSource.Gta5Mods
            ? Visibility.Visible
            : Visibility.Collapsed;
    private bool _librarySupported;

    public void SetGame(GTAModManager.Core.Game.GameInstall? game)
    {
        _librarySupported =
            game is not null
            && (
                GTAModManager.Core.Installation.ScriptLibraries.Catalog.Any(c =>
                    c.Id == Entry.Id && c.Supports(game.Edition)
                )
                || GTAModManager.Core.Installation.RecommendedMods.Find(Entry.Id)?.Supports(game.Edition) == true
            );
        Raise(nameof(InstallVisibility));
        Raise(nameof(InstallEnabled));
    }

    private bool IsReviewedRecommendation => GTAModManager.Core.Installation.RecommendedMods.Find(Entry.Id) is not null;
    public Visibility InstallVisibility =>
        IsReviewedRecommendation
            ? (Entry.Install == "download" && _librarySupported ? Visibility.Visible : Visibility.Collapsed)
        : Entry.Install is "base" or "download" || Entry.Install == "script-library" && _librarySupported
            ? Visibility.Visible
        : Visibility.Collapsed;
    public string InstallTip =>
        Entry.Install == "base"
            ? L.T("ui.discover.includedModHubBasePackage")
            : L.T("ui.discover.downloadFromGta5ModsOpen");

    private string? _installState;

    /// <summary>Download in progress: text shown instead of Install ("Downloading 42%"); null means the button is available.</summary>
    public string? InstallState
    {
        get => _installState;
        set
        {
            _installState = value;
            Raise();
            Raise(nameof(InstallText));
            Raise(nameof(InstallEnabled));
        }
    }
    public string InstallText =>
        _installState
        ?? _status switch
        {
            CatalogInstallStatus.UpdateAvailable => L.T("ui.common.refresh"),
            CatalogInstallStatus.Installed => L.T("ui.discover.reinstall"),
            _ => L.T("ui.common.install"),
        };

    private CatalogInstallStatus _status;

    /// <summary>Whether the mod is installed in the selected game (via Recommendations or detected) and whether an update exists.</summary>
    public CatalogInstallStatus Status
    {
        get => _status;
        set
        {
            if (_status == value)
                return;
            _status = value;
            Raise();
            Raise(nameof(InstallText));
            Raise(nameof(StatusText));
            Raise(nameof(StatusVisibility));
            Raise(nameof(StatusBrush));
        }
    }
    public string StatusText =>
        _status == CatalogInstallStatus.UpdateAvailable
            ? L.T("ui.discover.updateAvailable")
            : L.T("ui.common.installed");
    public Visibility StatusVisibility =>
        _status == CatalogInstallStatus.NotInstalled ? Visibility.Collapsed : Visibility.Visible;
    public Brush StatusBrush => _status == CatalogInstallStatus.UpdateAvailable ? UpdateBrush : InstalledBrush;
    private static readonly Brush UpdateBrush = Frozen(Color.FromRgb(0xE8, 0x8A, 0x1A));
    private static readonly Brush InstalledBrush = Frozen(Color.FromRgb(0x2F, 0xAE, 0x3A));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public bool InstallEnabled =>
        _installState is null && (Entry.Install != "script-library" && !IsReviewedRecommendation || _librarySupported);

    /// <summary>Warning when the declared compatibility does not include the selected game edition; null if fine or unknown.</summary>
    public string? EditionWarning(bool enhanced)
    {
        var c = Compat;
        bool? mine = enhanced ? c.Enhanced : c.Legacy,
            other = enhanced ? c.Legacy : c.Enhanced;
        string me = enhanced ? "Enhanced" : "Legacy",
            them = enhanced ? "Legacy" : "Enhanced";
        if (mine == false)
            return L.T("ui.discover.authorStatesDoesNotWork", Title, me);
        if (mine != true && other == true)
            return L.T("ui.discover.listedGtaVOnlyHave", Title, them, me);
        return null;
    }

    // Sorting.
    public long SortDownloads => Data?.Downloads ?? -1;
    public double SortRating => Data?.Rating ?? -1;
    public DateTimeOffset SortUpdated => Data?.Updated ?? DateTimeOffset.MinValue;

    public void Apply(CachedMetadata? cached, ModMetadataProvider provider)
    {
        var oldImage = Cached?.ImageFile;
        Cached = cached;
        Raise(string.Empty); // all properties
        if (provider.AvatarPath(cached) is var avatarPath && avatarPath != _avatarPath)
        {
            _avatarPath = avatarPath;
            LoadAvatarAsync(avatarPath);
        }
        var path = provider.ImagePath(cached);
        if (path is null && Entry.Cover is { } bundled && BundledCover(bundled) is { } builtIn)
        {
            // The source provided no image (official site, GitHub): use the bundled cover.
            Cover = builtIn;
            CoverState = CoverState.Ready;
            return;
        }
        if (path is null)
        {
            Cover = null;
            // While there is no data and loading is in progress, show a skeleton; otherwise a neat placeholder.
            CoverState =
                Refreshing && cached?.Data.Title is null && Entry.Source == MetaSource.Gta5Mods
                    ? CoverState.Loading
                    : CoverState.Missing;
            return;
        }
        if (Cover is not null && oldImage == cached?.ImageFile)
            return;
        LoadCoverAsync(path);
    }

    private async void LoadAvatarAsync(string? path)
    {
        var img = path is null ? null : await Task.Run(() => Decode(path, 64));
        if (path == _avatarPath)
            Avatar = img;
    }

    private async void LoadCoverAsync(string path)
    {
        var img = await Task.Run(() => Decode(path));
        Cover = img;
        CoverState = img is null ? CoverState.Missing : CoverState.Ready;
    }

    private static readonly Dictionary<string, ImageSource?> Bundled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bundled cover from the app resources (Discover\Assets\covers\…), cached.</summary>
    private static ImageSource? BundledCover(string file)
    {
        if (Bundled.TryGetValue(file, out var cached))
            return cached;
        ImageSource? img = null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri("pack://application:,,,/Discover/Assets/covers/" + file, UriKind.Absolute);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 720;
            bi.EndInit();
            bi.Freeze();
            img = bi;
        }
        catch (Exception ex)
            when (ex is IOException or NotSupportedException or UriFormatException or InvalidOperationException) { }
        Bundled[file] = img;
        return img;
    }

    /// <summary>Decodes in the background; a format WPF cannot read (for example WebP without a codec) gets a placeholder.</summary>
    private static ImageSource? Decode(string path, int width = 720)
    {
        try
        {
            var bi = new BitmapImage();
            using var fs = File.OpenRead(path);
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelWidth = width;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex)
            when (ex
                    is NotSupportedException
                        or IOException
                        or FileFormatException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                        or ArgumentException
            )
        {
            return null;
        }
    }

    private static string Count(long n) =>
        n switch
        {
            >= 1_000_000 => L.T(
                "ui.discover.million",
                L.T(n >= 10_000_000 ? "format.number.one" : "format.number.two", n / 1_000_000d)
            ),
            >= 10_000 => L.T("ui.discover.thousandsShort", L.T("format.number.one", n / 1000d)),
            _ => L.T("format.integer", n),
        };

    private static LinkView ToLink(AuthorLink l) => AuthorLinks.View(l);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
