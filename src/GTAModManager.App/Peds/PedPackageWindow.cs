using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Peds;

namespace GTAModManager.App.Peds;

/// <summary>Standalone package card: variant selection and a view of the saved materials.</summary>
public sealed class PedPackageWindow : Window
{
    private sealed record GenderChoice(string Value, string Label);

    private static readonly Brush BackgroundBrush = Brush("#101010");
    private static readonly Brush CardBrush = Brush("#1B1B1B");
    private static readonly Brush MutedBrush = Brush("#AAAAAA");
    private static readonly Brush AccentBrush = Brush("#FF9900");
    private readonly List<VariantState> _variants = [];
    private readonly Dictionary<string, PhotoState> _photos = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel _body = new() { Margin = new Thickness(24, 20, 24, 24) };
    private readonly TextBlock _status = Label("");
    private readonly TextBlock _error = Label("");
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _mediaArchive;
    private IReadOnlyList<PedSelection>? _result;
    private bool _photosLoading;
    private bool _photosLoaded;

    public static async Task<IReadOnlyList<PedSelection>?> ConfigureAsync(
        Window owner,
        PedArchiveAnalysis analysis,
        IReadOnlyList<PedSelection>? initial = null
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(analysis);
        return await owner
            .Dispatcher.InvokeAsync(() =>
            {
                var dialog = new PedPackageWindow(owner, analysis, initial);
                dialog.ShowDialog();
                return dialog._result;
            })
            .Task;
    }

    public static async Task ShowInstalledAsync(
        Window owner,
        PedPackagePresentation presentation,
        IReadOnlyList<PedEntry> installed
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(installed);
        await owner
            .Dispatcher.InvokeAsync(() =>
            {
                new PedPackageWindow(owner, presentation, installed).ShowDialog();
            })
            .Task;
    }

    private PedPackageWindow(Window owner, PedArchiveAnalysis analysis, IReadOnlyList<PedSelection>? initial)
        : this(owner, analysis.ArchivePath)
    {
        Title = L.T("ui.peds.characterVariantsModhub");
        var title = string.IsNullOrWhiteSpace(analysis.Details.Title)
            ? GTAModManager.Core.Mods.ModNames.Pretty(Path.GetFileNameWithoutExtension(analysis.ArchivePath))
            : analysis.Details.Title;
        AddHeader(title, analysis.ArchivePath, analysis.Details);
        var duplicateNames = analysis
            .Candidates.GroupBy(c => c.SpawnName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        PreparePhotos(analysis.ImagePaths.Concat(analysis.Candidates.Select(c => c.PreviewPath).OfType<string>()));
        _body.Children.Add(Label(L.T("ui.peds.selectVariantsInstallationQueueNormal"), muted: true));
        foreach (var candidate in analysis.Candidates)
        {
            var saved = initial?.FirstOrDefault(s => s.CandidateId == candidate.Id);
            var state = new VariantState(
                candidate,
                saved,
                initial is null && !duplicateNames.Contains(candidate.SpawnName),
                duplicateNames.Contains(candidate.SpawnName)
            );
            _variants.Add(state);
            state.PropertyChanged += (_, _) => UpdateSelectionSummary();
            _body.Children.Add(VariantCard(state));
        }
        if (_variants.Count == 0)
            _body.Children.Add(Label(L.T("ui.peds.noSuitableVariantsFoundArchive")));
        AddInformation(analysis.Readme, analysis.Details, analysis.Warnings);
        AddGallery();
        BuildShell(configure: true);
        UpdateSelectionSummary();
    }

    private PedPackageWindow(Window owner, PedPackagePresentation presentation, IReadOnlyList<PedEntry> installed)
        : this(owner, presentation.MediaArchivePath)
    {
        Title = L.T("ui.peds.characterDetailsModhub");
        AddHeader(presentation.Title, presentation.SourceArchive, presentation.Details);
        PreparePhotos(presentation.ImagePaths.Concat(installed.Select(e => e.PreviewPath).OfType<string>()));
        foreach (var entry in installed)
        {
            var card = new StackPanel();
            card.Children.Add(
                Label(string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.SpawnName : entry.DisplayName, 18)
            );
            card.Children.Add(
                Label(
                    entry.SpawnName + " · " + entry.Layout + (entry.PedType.Length == 0 ? "" : " · " + entry.PedType),
                    muted: true
                )
            );
            if (entry.PreviewPath is not null && _photos.TryGetValue(entry.PreviewPath, out var photo))
                card.Children.Add(PhotoButton(photo, 260, 170));
            if (!string.IsNullOrWhiteSpace(entry.RegistrationXml))
                card.Children.Add(
                    Disclosure(L.T("ui.peds.allRegistrationSettings"), ReadOnlyText(entry.RegistrationXml, 240))
                );
            _body.Children.Add(Card(card));
        }
        if (installed.Count == 0)
            _body.Children.Add(Label(L.T("ui.peds.packHasNoInstalledModels"), muted: true));
        AddInformation(presentation.Readme, presentation.Details, []);
        AddGallery();
        BuildShell(configure: false);
    }

    private PedPackageWindow(Window owner, string mediaArchive)
    {
        Owner = owner;
        _mediaArchive = mediaArchive;
        Width = 1000;
        Height = 760;
        MinWidth = 700;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = BackgroundBrush;
        Foreground = Brushes.White;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        _error.Foreground = Brush("#FF8585");
        _error.Visibility = Visibility.Collapsed;
        _status.Foreground = MutedBrush;
        Closed += (_, _) => _lifetime.Cancel();
        Loaded += async (_, _) => await LoadPhotosAsync();
    }

    private void BuildShell(bool configure)
    {
        var root = new DockPanel();
        var footer = new Border
        {
            Background = Brush("#161616"),
            Padding = new Thickness(24, 14, 24, 14),
            BorderBrush = Brush("#303030"),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        var footerStack = new StackPanel();
        footerStack.Children.Add(
            new ScrollViewer
            {
                Content = _error,
                MaxHeight = 140,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            }
        );
        footerStack.Children.Add(_status);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var close = Button(configure ? L.T("ui.common.cancel") : L.T("ui.common.close"));
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        if (configure)
        {
            var confirm = Button(L.T("ui.peds.addSelectedQueue"), primary: true);
            confirm.Margin = new Thickness(10, 0, 0, 0);
            confirm.Click += (_, _) => Confirm();
            actions.Children.Add(confirm);
        }
        footerStack.Children.Add(actions);
        footer.Child = footerStack;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(
            new ScrollViewer
            {
                Content = _body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            }
        );
        Content = root;
    }

    private void AddHeader(string title, string source, PedReadmeDetails details)
    {
        var heading = Label(string.IsNullOrWhiteSpace(title) ? L.T("ui.peds.characterPack") : title, 25);
        heading.FontWeight = FontWeights.SemiBold;
        heading.MaxHeight = 100;
        heading.ToolTip = title;
        _body.Children.Add(heading);
        _body.Children.Add(Label(Path.GetFileName(source), muted: true));
        if (!string.IsNullOrWhiteSpace(details.Authors))
        {
            var authors = details.Authors.Length <= 180 ? details.Authors : details.Authors[..180] + "…";
            var line = Label(L.T("ui.common.author", authors));
            line.Foreground = AccentBrush;
            _body.Children.Add(line);
        }
    }

    private FrameworkElement VariantCard(VariantState state)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var content = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var check = new CheckBox
        {
            Content = state.Candidate.VariantName,
            Foreground = Brushes.White,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
            IsEnabled = state.CanInstall,
        };
        check.SetBinding(
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            Bind(state, nameof(state.Selected))
        );
        content.Children.Add(check);
        var type = Label(L.T("ui.peds.automatic", state.Candidate.SpawnName, state.Candidate.Layout), 13);
        type.Foreground = AccentBrush;
        content.Children.Add(type);
        content.Children.Add(Label(L.T("ui.peds.menuName"), muted: true));
        var display = new TextBox
        {
            MaxLength = 120,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 8),
        };
        display.SetBinding(TextBox.TextProperty, Bind(state, nameof(state.DisplayName)));
        content.Children.Add(display);
        if (state.CanChooseGender)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Label(L.T("ui.peds.gender")));
            var gender = new ComboBox
            {
                ItemsSource = new[]
                {
                    new GenderChoice("Мужской", L.T("ui.common.male")),
                    new GenderChoice("Женский", L.T("ui.common.female")),
                },
                DisplayMemberPath = nameof(GenderChoice.Label),
                SelectedValuePath = nameof(GenderChoice.Value),
                Width = 150,
                Margin = new Thickness(8, 0, 0, 0),
            };
            gender.SetBinding(
                System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
                Bind(state, nameof(state.Gender))
            );
            row.Children.Add(gender);
            content.Children.Add(row);
        }
        else
            content.Children.Add(Label(L.T("ui.peds.characterTypeFromRegistration", state.PedType), muted: true));
        content.Children.Add(Label(state.Note, muted: true));
        if (state.Candidate.RegistrationXml is not null)
            content.Children.Add(
                Disclosure(
                    L.T("ui.peds.originalRegistrationParametersUnchanged"),
                    ReadOnlyText(state.Candidate.RegistrationXml, 180)
                )
            );
        grid.Children.Add(content);
        if (state.Candidate.PreviewPath is not null && _photos.TryGetValue(state.Candidate.PreviewPath, out var photo))
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            var preview = PhotoButton(photo, 190, 170);
            Grid.SetColumn(preview, 1);
            grid.Children.Add(preview);
        }
        return Card(grid);
    }

    private void AddInformation(string readme, PedReadmeDetails details, IReadOnlyList<string> warnings)
    {
        var panel = new StackPanel();
        if (!string.IsNullOrWhiteSpace(details.Authors))
            panel.Children.Add(Disclosure(L.T("ui.peds.credits"), ReadOnlyText(details.Authors, 200)));
        if (!string.IsNullOrWhiteSpace(details.Credits))
            panel.Children.Add(Disclosure(L.T("ui.peds.creditsResourcesUsed"), ReadOnlyText(details.Credits, 220)));
        if (!string.IsNullOrWhiteSpace(details.Installation))
            panel.Children.Add(Disclosure(L.T("ui.peds.authorSInstructions"), ReadOnlyText(details.Installation, 240)));
        if (details.Links.Count > 0)
        {
            var links = new StackPanel();
            foreach (var link in details.Links)
            {
                var control = Button(
                    L.T(
                        "ui.peds.text",
                        link.Label,
                        link.Value,
                        (link.Inferred ? L.T("ui.peds.linkDetectedAutomatically") : "")
                    )
                );
                control.HorizontalContentAlignment = HorizontalAlignment.Left;
                control.MaxWidth = 860;
                control.ToolTip = link.Url ?? link.Value;
                var safe = SafeLink(link.Url);
                control.IsEnabled = safe is not null;
                control.Click += (_, _) => OpenLink(safe);
                links.Children.Add(control);
            }
            panel.Children.Add(Disclosure(L.T("ui.peds.authorLinks"), links));
        }
        var full = L.Join("", L.Join("\n", warnings.Select(L.Owned)), (warnings.Count > 0 ? "\n\n" : ""), readme);
        panel.Children.Add(
            Disclosure(
                L.T("ui.peds.fullReadmeWarnings"),
                ReadOnlyText(string.IsNullOrWhiteSpace(full) ? L.T("ui.peds.noReadmeIncluded") : full, 300)
            )
        );
        _body.Children.Add(Card(panel));
    }

    private void PreparePhotos(IEnumerable<string> paths)
    {
        foreach (
            var path in paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(32)
        )
            _photos[path] = new PhotoState(path);
    }

    private void AddGallery()
    {
        if (_photos.Count == 0)
            return;
        var gallery = new WrapPanel();
        foreach (var photo in _photos.Values)
            gallery.Children.Add(PhotoButton(photo, 260, 180));
        var section = Disclosure(L.T("ui.peds.allPackageImages", _photos.Count), gallery);
        section.Expanded += async (_, _) => await LoadPhotosAsync();
        _body.Children.Add(section);
    }

    private Button PhotoButton(PhotoState photo, double width, double height)
    {
        var panel = new StackPanel();
        var image = new Image
        {
            Width = width,
            Height = height,
            Stretch = Stretch.Uniform,
        };
        image.SetBinding(Image.SourceProperty, new Binding(nameof(photo.Image)) { Source = photo });
        panel.Children.Add(image);
        panel.Children.Add(Label(Path.GetFileNameWithoutExtension(photo.Path), 12));
        var error = Label("", 11, muted: true);
        error.MaxWidth = width;
        error.SetBinding(TextBlock.TextProperty, new Binding(nameof(photo.Error)) { Source = photo });
        panel.Children.Add(error);
        var button = Button("");
        button.Content = panel;
        button.Padding = new Thickness(8);
        button.Margin = new Thickness(0, 8, 10, 8);
        button.Click += async (_, _) => await ShowPhotoAsync(photo);
        return button;
    }

    private async Task LoadPhotosAsync()
    {
        if (_photosLoading || _photosLoaded || _photos.Count == 0 || _lifetime.IsCancellationRequested)
            return;
        _photosLoading = true;
        _status.Text = L.T("ui.peds.loadingImages");
        var token = _lifetime.Token;
        try
        {
            await Task.Run(
                async () =>
                {
                    using var archive = ModArchive.Open(_mediaArchive);
                    long total = 0;
                    foreach (var photo in _photos.Values)
                    {
                        token.ThrowIfCancellationRequested();
                        BitmapSource? bitmap = null;
                        byte[]? bytes = null;
                        string error = "";
                        try
                        {
                            long size = archive.SourceSize(photo.Path);
                            if (size <= 0 || size > 20L * 1024 * 1024)
                                throw new NotSupportedException(L.T("ui.peds.fileExceeds20MbLimit"));
                            if (total + size > 80L * 1024 * 1024)
                                throw new NotSupportedException(L.T("ui.peds.totalImageLimit80Mb"));
                            bytes = ReadImageBytes(
                                archive,
                                photo.Path,
                                Math.Min(20L * 1024 * 1024, 80L * 1024 * 1024 - total)
                            );
                            total += bytes.Length;
                            bitmap = Decode(bytes, 800);
                        }
                        catch (Exception ex) when (IsImageFailure(ex))
                        {
                            error = L.T("ui.peds.previewUnavailable", L.Owned(ex.Message));
                            bytes = null;
                        }
                        token.ThrowIfCancellationRequested();
                        await Dispatcher.InvokeAsync(() => photo.SetImage(bitmap, bytes, error));
                    }
                },
                token
            );
            _photosLoaded = true;
            _status.Text =
                _variants.Count > 0
                    ? SelectionSummary()
                    : L.T("ui.peds.imagesAvailable", _photos.Values.Count(p => p.Image is not null), _photos.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsImageFailure(ex))
        {
            _status.Text = L.T("ui.peds.archiveAssetsUnavailableModelDetails", L.Owned(ex.Message));
            foreach (var photo in _photos.Values)
                photo.SetImage(null, null, L.T("ui.peds.sourceArchiveImageCacheUnavailable"));
        }
        finally
        {
            _photosLoading = false;
        }
    }

    private async Task ShowPhotoAsync(PhotoState photo)
    {
        var bytes = photo.Bytes;
        if (bytes is null)
        {
            _status.Text = photo.Error.Length > 0 ? photo.Error : L.T("ui.peds.imageStillLoading");
            return;
        }
        try
        {
            var image = await Task.Run(() => Decode(bytes, 1000));
            if (_lifetime.IsCancellationRequested)
                return;
            new Window
            {
                Owner = this,
                Title = Path.GetFileName(photo.Path),
                Width = 900,
                Height = 700,
                MinWidth = 400,
                MinHeight = 350,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.Black,
                Content = new Image
                {
                    Source = image,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(12),
                },
            }.ShowDialog();
        }
        catch (Exception ex) when (IsImageFailure(ex))
        {
            _status.Text = L.T("ui.peds.couldNotOpenImage", L.Owned(ex.Message));
        }
    }

    private static byte[] ReadImageBytes(ModArchive archive, string path, long limit)
    {
        using var source = archive.OpenSource(path);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - output.Length + 1))) > 0)
        {
            if (output.Length + count > limit)
                throw new NotSupportedException(L.T("ui.peds.imageExceedsPreviewSizeLimit"));
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static BitmapSource Decode(byte[] bytes, int edge)
    {
        using var input = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
            throw new NotSupportedException(L.T("ui.peds.fileContainsNoImage"));
        var frame = decoder.Frames[0];
        int width = frame.PixelWidth,
            height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 64_000_000)
            throw new NotSupportedException(L.T("ui.peds.imageExceeds64Megapixels"));
        input.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height)
            bitmap.DecodePixelWidth = Math.Min(width, edge);
        else
            bitmap.DecodePixelHeight = Math.Min(height, edge);
        bitmap.StreamSource = input;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void Confirm()
    {
        var selected = _variants.Where(v => v.Selected).ToArray();
        var errors = new List<string>();
        if (selected.Length == 0)
            errors.Add(L.T("ui.peds.selectAtLeastOneVariant"));
        if (selected.Any(v => !v.CanInstall))
            errors.Add(L.T("ui.peds.ambiguousDamagedVariantsCannotAdded"));
        foreach (
            var group in selected.GroupBy(v => PedMetadata.NameHash(v.Candidate.SpawnName)).Where(g => g.Count() > 1)
        )
            errors.Add(
                L.T(
                    "ui.peds.duplicateNamesGameHashesKeep",
                    L.Join(", ", group.Select(v => v.Candidate.SpawnName + " — " + v.Candidate.VariantName))
                )
            );
        if (errors.Count > 0)
        {
            _error.Text = L.Join("\n", errors);
            _error.Visibility = Visibility.Visible;
            return;
        }
        _result = selected
            .Select(v => new PedSelection(
                v.Candidate.Id,
                string.IsNullOrWhiteSpace(v.DisplayName) ? v.Candidate.SpawnName : v.DisplayName.Trim(),
                v.Gender == "Женский" ? PedGender.Female : PedGender.Male,
                v.Candidate.Layout
            ))
            .ToArray();
        DialogResult = true;
    }

    private void UpdateSelectionSummary()
    {
        if (!_photosLoading)
            _status.Text = SelectionSummary();
        _error.Visibility = Visibility.Collapsed;
    }

    private string SelectionSummary() =>
        L.T("ui.peds.variantsSelectedIdenticalTechnicalNames", _variants.Count(v => v.Selected), _variants.Count);

    private void OpenLink(Uri? uri)
    {
        if (uri is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _status.Text = L.T("ui.peds.couldNotOpenLink", L.Owned(ex.Message));
        }
    }

    private static Uri? SafeLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
            ? uri
            : null;

    private static bool IsImageFailure(Exception ex) =>
        ex
            is IOException
                or InvalidDataException
                or NotSupportedException
                or ArgumentException
                or InvalidOperationException
                or System.Runtime.InteropServices.COMException
                or System.IO.FileFormatException
                or UnauthorizedAccessException
                or GTAModManager.Core.Oiv.OivFormatException
                or SharpCompress.Common.ArchiveException
                or SharpCompress.Common.ArchiveOperationException;

    private static Binding Bind(object source, string property) =>
        new(property)
        {
            Source = source,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        };

    private static SolidColorBrush Brush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private static TextBlock Label(string value, double size = 13, bool muted = false) =>
        new()
        {
            Text = value,
            FontSize = size,
            Foreground = muted ? MutedBrush : Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 7),
        };

    private static Border Card(UIElement child) =>
        new()
        {
            Child = child,
            Background = CardBrush,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 12, 0, 0),
        };

    private static TextBox ReadOnlyText(string value, double height) =>
        new()
        {
            Text = value,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = height,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = BackgroundBrush,
            Foreground = Brushes.White,
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 8),
        };

    private static Expander Disclosure(string title, UIElement child) =>
        new()
        {
            Header = title,
            Content = child,
            IsExpanded = false,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 10, 0, 4),
        };

    private Button Button(string title, bool primary = false)
    {
        var button = new Button
        {
            Content = title,
            Padding = new Thickness(16, 9, 16, 9),
            Foreground = primary ? Brushes.Black : Brushes.White,
            Background = primary ? AccentBrush : CardBrush,
            Margin = new Thickness(0, 0, 0, 6),
        };
        if (TryFindResource(primary ? "Primary" : "Ghost") is Style style)
            button.Style = style;
        return button;
    }

    private sealed class VariantState : INotifyPropertyChanged
    {
        private bool _selected;
        private string _displayName;
        private string _gender;
        public PedCandidate Candidate { get; }
        public bool CanInstall { get; }
        public bool CanChooseGender => Candidate.RegistrationXml is null;
        public string PedType { get; } = L.T("ui.peds.notSpecified");
        public string Note { get; }
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                    return;
                _selected = value;
                Changed();
            }
        }
        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName == value)
                    return;
                _displayName = value;
                Changed();
            }
        }
        public string Gender
        {
            get => _gender;
            set
            {
                if (_gender == value)
                    return;
                _gender = value;
                Changed();
            }
        }

        public VariantState(PedCandidate candidate, PedSelection? initial, bool selectByDefault, bool duplicate)
        {
            Candidate = candidate;
            _displayName = initial?.DisplayName ?? candidate.SpawnName;
            _gender = initial?.Gender == PedGender.Female ? "Женский" : "Мужской";
            var problems = new List<string>();
            bool valid = true;
            try
            {
                PedMetadata.ValidateSpawnName(candidate.SpawnName);
            }
            catch (ArgumentException ex)
            {
                valid = false;
                problems.Add(L.Owned(ex.Message));
            }
            if (candidate.RegistrationXml is not null)
            {
                try
                {
                    var item = PedMetadata.ParseItem(candidate.RegistrationXml);
                    PedType = item.Element("Pedtype")?.Value ?? L.T("ui.peds.notSpecified");
                    _gender = PedType == "CIVFEMALE" ? "Женский" : "Мужской";
                }
                catch (Exception ex) when (ex is ArgumentException or System.Xml.XmlException)
                {
                    valid = false;
                    problems.Add(L.T("ui.peds.registrationNeedsChecking", L.Owned(ex.Message)));
                }
            }
            CanInstall = valid && !candidate.LayoutAmbiguous;
            _selected = CanInstall && (initial is not null || selectByDefault);
            if (candidate.LayoutAmbiguous)
                problems.Add(L.T("ui.peds.modelTypeAmbiguousVariantCannot"));
            if (duplicate)
                problems.Add(L.T("ui.peds.archiveContainsSeveralVariantsName"));
            problems.Add(
                CanChooseGender
                    ? L.T("ui.peds.noExistingRegistrationFoundMale")
                    : L.T("ui.peds.allOriginalRegistrationParametersWill")
            );
            problems.AddRange(candidate.Warnings);
            Note = L.Join(" ", problems);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }

    private sealed class PhotoState(string path) : INotifyPropertyChanged
    {
        public string Path { get; } = path;
        public BitmapSource? Image { get; private set; }
        public byte[]? Bytes { get; private set; }
        public string Error { get; private set; } = L.T("ui.peds.loading");

        public void SetImage(BitmapSource? image, byte[]? bytes, string error)
        {
            Image = image;
            Bytes = bytes;
            Error = error;
            PropertyChanged?.Invoke(this, new(nameof(Image)));
            PropertyChanged?.Invoke(this, new(nameof(Error)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
