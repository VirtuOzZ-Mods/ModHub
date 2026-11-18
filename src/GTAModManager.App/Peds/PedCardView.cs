using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Peds;

namespace GTAModManager.App.Peds;

/// <summary>
/// Card of one detected character: photo, name, author, gender and a check box, all editable on the card itself
/// without a separate window. Gender is selectable only if the model has no registration of its own (otherwise it comes from the mod).
/// </summary>
public sealed class PedCardView : INotifyPropertyChanged
{
    private static readonly Brush SelectedBorder = Frozen(0xFF, 0x99, 0x00),
        IdleBorder = Frozen(0x2A, 0x2A, 0x2A);

    public PedQueueItem Item { get; }
    public PedCandidate Candidate { get; }
    public string Id => Item.ArchivePath + "|" + Candidate.Id;
    public string Title { get; }
    public string Author { get; }
    public string SpawnText =>
        $"{Candidate.SpawnName} · {(Candidate.Layout == PedLayout.Streamed ? "Streamed" : "Normal")}";
    public bool CanInstall { get; }
    public bool CanChooseGender => Candidate.RegistrationXml is null;
    public string FixedGender { get; } = "";
    public string Note { get; }
    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GenderChoiceVisibility =>
        CanChooseGender && CanInstall ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FixedGenderVisibility =>
        !CanChooseGender && CanInstall ? Visibility.Visible : Visibility.Collapsed;
    public string Initials =>
        string.Concat(
            Title.Split(' ', '_', '-').Where(w => w.Length > 0).Take(2).Select(w => char.ToUpperInvariant(w[0]))
        );

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == (value && CanInstall))
                return;
            _selected = value && CanInstall;
            Changed();
            Changed(nameof(Border));
            SelectionChanged?.Invoke();
        }
    }
    public Brush Border => _selected ? SelectedBorder : IdleBorder;
    public event Action? SelectionChanged;

    private bool _female;

    // Two radio buttons bound to opposite properties: WPF writes false when a button is unchecked; such writes are ignored,
    // otherwise unchecking one button would flip the gender.
    public bool IsFemale
    {
        get => _female;
        set
        {
            if (value)
                SetFemale(true);
        }
    }
    public bool IsMale
    {
        get => !_female;
        set
        {
            if (value)
                SetFemale(false);
        }
    }

    private void SetFemale(bool female)
    {
        if (_female == female)
            return;
        _female = female;
        Changed(nameof(IsFemale));
        Changed(nameof(IsMale));
    }

    private ImageSource? _image;
    public ImageSource? Image
    {
        get => _image;
        private set
        {
            _image = value;
            Changed();
            Changed(nameof(PlaceholderVisibility));
        }
    }
    public Visibility PlaceholderVisibility => _image is null ? Visibility.Visible : Visibility.Collapsed;

    public PedCardView(PedQueueItem item, PedCandidate candidate, bool duplicateName)
    {
        Item = item;
        Candidate = candidate;
        var a = item.Analysis;
        // Name: for a single-character archive, the title from the README; otherwise the model variant.
        // A README title that looks like a file name ("01-Readme.txt") is not usable as a character name.
        var readmeTitle = a.Details.Title?.Trim() ?? "";
        bool usableTitle =
            readmeTitle.Length is > 1 and < 60
            && !System.Text.RegularExpressions.Regex.IsMatch(
                readmeTitle,
                @"(?i)\.(txt|md|rtf|pdf|docx?)$|readme|read me|instal"
            );
        Title =
            a.Candidates.Count == 1 && usableTitle ? readmeTitle
            : !string.IsNullOrWhiteSpace(candidate.VariantName) && !candidate.VariantName.Contains('.')
                ? candidate.VariantName.Trim()
            : candidate.SpawnName;
        Author = string.IsNullOrWhiteSpace(a.Details.Authors)
            ? L.T("ui.peds.authorNotSpecified")
            : L.T("ui.common.author", a.Details.Authors.Trim());

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
                var type = PedMetadata.ParseItem(candidate.RegistrationXml).Element("Pedtype")?.Value ?? "";
                _female = type.Contains("FEMALE", StringComparison.OrdinalIgnoreCase);
                FixedGender = L.T("ui.peds.fromMod", (_female ? L.T("ui.common.female") : L.T("ui.common.male")));
            }
            catch (Exception ex) when (ex is ArgumentException or System.Xml.XmlException)
            {
                valid = false;
                problems.Add(L.T("ui.peds.modelRegistrationCorrupted"));
            }
        }
        if (candidate.LayoutAmbiguous)
        {
            valid = false;
            problems.Add(L.T("ui.peds.ambiguousFileStructureInstallationUnavailable"));
        }
        if (duplicateName)
            problems.Add(L.T("ui.peds.anotherVariantHasSameName"));
        CanInstall = valid;
        _selected = valid && !duplicateName;
        Note = L.Join(" ", problems);
    }

    public PedSelection ToSelection() =>
        new(Candidate.Id, Title, IsFemale ? PedGender.Female : PedGender.Male, Candidate.Layout);

    // ---------- Photos: several images can be browsed with arrows ----------

    private List<string>? _gallery;
    private readonly Dictionary<int, ImageSource?> _loaded = [];
    private int _index;

    /// <summary>Package images: the best one first, then the rest, auxiliary ones (readme, rules…) last.</summary>
    public IReadOnlyList<string> Gallery => _gallery ??= BuildGallery();
    public Visibility ArrowsVisibility => Gallery.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    public string Counter => Gallery.Count > 1 ? $"{_index + 1}/{Gallery.Count}" : "";

    private List<string> BuildGallery()
    {
        var images = Item.Analysis.ImagePaths;
        var first = Candidate.PreviewPath ?? PickImage(images, Candidate.SpawnName);
        var service = new System.Text.RegularExpressions.Regex(
            @"(?i)readme|rule|warn|credit|logo|banner|donat|patreon|discord|license|dont|don't|nude|porn|install|thank|support"
        );
        var rest = images
            .Where(p => p != first)
            .OrderBy(p => service.IsMatch(Path.GetFileName(p)) ? 1 : 0)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
        return (first is null ? rest : rest.Prepend(first)).Distinct().Take(40).ToList();
    }

    public Task NextAsync() => ShowAsync(_index + 1);

    public Task PrevAsync() => ShowAsync(_index - 1);

    private async Task ShowAsync(int index)
    {
        if (Gallery.Count == 0)
            return;
        _index = (index % Gallery.Count + Gallery.Count) % Gallery.Count;
        Changed(nameof(Counter));
        if (!_loaded.TryGetValue(_index, out var cached))
        {
            var want = _index;
            cached = await DecodeAsync(Gallery[want]);
            _loaded[want] = cached;
            if (want != _index)
                return; // the user has already moved on
        }
        Image = cached;
    }

    /// <summary>First (best) photo, when the card appears.</summary>
    public Task LoadImageAsync() => ShowAsync(0);

    private async Task<ImageSource?> DecodeAsync(string path)
    {
        var archive = Item.ArchivePath;
        try
        {
            return await Task.Run(() =>
            {
                using var a = ModArchive.Open(archive);
                using var s = a.OpenSource(path);
                using var ms = new MemoryStream();
                var buffer = new byte[81920];
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    if (ms.Length > 32 << 20)
                        throw new NotSupportedException(L.T("ui.peds.imageTooLarge"));
                }
                ms.Position = 0;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 420;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                return (ImageSource)bi;
            });
        }
        catch (Exception)
        {
            return null; /* no photo: placeholder with initials */
        }
    }

    /// <summary>Package photo: first one named after the model, then any image except auxiliary ones (readme, rules, logos, banners).</summary>
    private static string? PickImage(IReadOnlyList<string> images, string spawn)
    {
        static string Name(string p) => Path.GetFileNameWithoutExtension(p.Replace('\\', '/'));
        var service = new System.Text.RegularExpressions.Regex(
            @"(?i)readme|rule|warn|credit|logo|banner|donat|patreon|discord|license|dont|don't|nude|porn|install|thank|support"
        );
        var preview = new System.Text.RegularExpressions.Regex(@"(?i)preview|cover|main|showcase|thumb|poster");
        static bool Root(string p) => !p.Replace('\\', '/').Trim('/').Contains('/');
        return images.FirstOrDefault(p => Name(p).Contains(spawn, StringComparison.OrdinalIgnoreCase))
            ?? images
                .Where(p => preview.IsMatch(Name(p)) && !service.IsMatch(Name(p)))
                .OrderBy(p => p.Length)
                .FirstOrDefault()
            ?? images.FirstOrDefault(p => Root(p) && !service.IsMatch(Name(p))) // the main image is usually in the archive root
            ?? images.FirstOrDefault(p => !service.IsMatch(Name(p)))
            ?? images.FirstOrDefault();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }
}
