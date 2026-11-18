using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using SharpCompress.Readers;

namespace GTAModManager.Core.NaturalVision;

public sealed record NveComponent(
    string Id,
    string Source,
    string Title,
    string Summary,
    string Description,
    string Group,
    int Order,
    bool Core,
    long Size
);

public sealed record NveCatalog(
    string Source,
    string Stamp,
    GameEdition Edition,
    IReadOnlyList<NveComponent> Components
)
{
    public string Product { get; init; } = "nve";
    public bool IsLcpp => Product == "lcpp";
    public bool IsLcppHd => IsLcpp && Components.Any(c => c.Core && NveBundle.IsLcppHdPart(c.Source));
    public string Title =>
        IsLcpp
            ? IsLcppHd
                ? NveBundle.LcppHdTitle
                : NveBundle.LcppTitle
            : Edition == GameEdition.Enhanced
                ? "NaturalVision Enhanced"
                : "NaturalVision Evolved";

    public IReadOnlyList<string> Validate(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();
        if (selected.Any(id => !Components.Any(c => c.Id == id)))
            errors.Add(L.T("core.nve.listContainsUnknownComponent"));
        if (Components.Where(c => c.Core).Any(c => !selected.Contains(c.Id)))
            errors.Add(L.T("core.nve.allMainPartsPackageRequired"));
        var items = Components.Where(c => selected.Contains(c.Id)).ToArray();
        foreach (
            var group in items.Where(c => !c.Core && c.Group != "").GroupBy(c => c.Group).Where(g => g.Count() > 1)
        )
            errors.Add(L.T("core.nve.chooseOneVariant", group.First().Group));
        bool Has(string s) => items.Any(c => c.Source.Contains(s, StringComparison.OrdinalIgnoreCase));
        if (Has("Darker Nights") && Has("2020 Weather"))
            errors.Add(L.T("core.nve.darkerNightsNve2020Weather"));
        if (
            items.Any(c =>
                c.Source.Contains("3. Brighter Emergency", StringComparison.OrdinalIgnoreCase)
                && c.Source.Contains("+ Performance", StringComparison.OrdinalIgnoreCase)
            ) && items.Any(c => c.Source.Contains(@"2. Performance Boost\", StringComparison.OrdinalIgnoreCase))
        )
            errors.Add(L.T("core.nve.brightEmergencyLightsVariantAlready"));
        return errors;
    }
}

public sealed partial class NveBundle : IDisposable
{
    readonly ModArchive? archive;
    readonly string? directory;
    readonly Dictionary<string, string> local = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> Files => archive?.Files ?? local.Keys.ToArray();
    public string Source { get; }

    public NveBundle(string source)
    {
        Source = Path.GetFullPath(source);
        if (Directory.Exists(Source))
        {
            directory = Source;
            foreach (
                var f in Directory.EnumerateFiles(
                    Source,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    }
                )
            )
            {
                string relative = Path.GetRelativePath(Source, f);
                if (ModArchive.NormalizeKey(relative) is { } key)
                    local[key] = f;
            }
        }
        else
            archive = ModArchive.Open(Source);
    }

    public static bool IsCandidate(string source)
    {
        if (
            !Directory.Exists(source) && Path.GetExtension(source).ToLowerInvariant() is not (".zip" or ".rar" or ".7z")
        )
            return false;
        using var bundle = new NveBundle(source);
        return bundle.Files.Any(f => LcppPart(f) > 0)
            || bundle.EnhancedPrefix() is not null
            || bundle.Files.Count(f => Part(f) > 0) == 3;
    }

    public static int Part(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (!name.EndsWith(".oiv") || !name.Contains("naturalvision") || name.Contains("uninstall"))
            return 0;
        if (name.Contains("part one"))
            return 1;
        if (name.Contains("part two"))
            return 2;
        if (name.Contains("part three"))
            return 3;
        return 0;
    }

    public long Size(string path) => archive?.SourceSize(path) ?? new FileInfo(local[path]).Length;

    public Stream Open(string path) => archive?.OpenSource(path) ?? File.OpenRead(local[path]);

    public string Stamp()
    {
        if (directory is null)
        {
            var info = new FileInfo(Source);
            return info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        }
        string data = string.Join(
            "\n",
            local
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Key + ":" + new FileInfo(p.Value).Length + ":" + File.GetLastWriteTimeUtc(p.Value).Ticks)
        );
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }

    public string? EnhancedPrefix()
    {
        return Files
            .Where(f => f.EndsWith(@"\onigiri.asi", StringComparison.OrdinalIgnoreCase))
            .Select(f => f[..^"onigiri.asi".Length])
            .FirstOrDefault(prefix =>
                Files.Contains(prefix + "NVE.asi", StringComparer.OrdinalIgnoreCase)
                && Files.Any(f =>
                    f.StartsWith(prefix + @"onigiri\dlcpacks\", StringComparison.OrdinalIgnoreCase)
                    && f.EndsWith("dlc.rpf", StringComparison.OrdinalIgnoreCase)
                )
            );
    }

    public static bool EnhancedShader(string relative) =>
        relative.Equals("ReShade.asi", StringComparison.OrdinalIgnoreCase)
        || relative.Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("renodx-", StringComparison.OrdinalIgnoreCase)
            && relative.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(@"reshade-shaders\", StringComparison.OrdinalIgnoreCase);

    public NveCatalog Analyze()
    {
        if (Files.Any(f => LcppPart(f) > 0))
            return AnalyzeLcpp();
        if (EnhancedPrefix() is { } enhanced)
        {
            var shaderFiles = Files
                .Where(f =>
                    f.StartsWith(enhanced, StringComparison.OrdinalIgnoreCase) && EnhancedShader(f[enhanced.Length..])
                )
                .ToArray();
            var coreFiles = Files
                .Where(f =>
                    f.StartsWith(enhanced, StringComparison.OrdinalIgnoreCase) && !EnhancedShader(f[enhanced.Length..])
                )
                .ToArray();
            var components = new List<NveComponent>
            {
                new(
                    "enhanced-main",
                    enhanced,
                    L.T("core.nve.naturalvisionEnhancedMainPackage"),
                    L.T("core.nve.graphicsGtaVEnhancedWeather"),
                    L.T("core.nve.authorSInstructionsRequireEnhanced"),
                    "Основной пакет",
                    1,
                    true,
                    coreFiles.Sum(Size)
                ),
            };
            if (shaderFiles.Length > 0)
                components.Add(
                    new(
                        "reshade",
                        enhanced,
                        L.T("core.nve.reshadeRenodx"),
                        L.T("core.nve.screenEffectsHdrRenodxIncluded"),
                        L.T("core.nve.reshadeRenodxFilesFromMain"),
                        "ReShade",
                        1000,
                        false,
                        shaderFiles.Sum(Size)
                    )
                );
            return new(Source, Stamp(), GameEdition.Enhanced, components);
        }
        var paths = Files
            .Where(f =>
                Part(f) > 0
                || Path.GetExtension(f).Equals(".oiv", StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(f).StartsWith("INSTALL", StringComparison.OrdinalIgnoreCase)
                    && f.Split('\\').Any(p => p.Equals("Optional Add-Ons", StringComparison.OrdinalIgnoreCase))
            )
            .ToArray();
        var result = new List<NveComponent>();
        foreach (var path in paths)
        {
            int part = Part(path);
            string xml;
            using (var stream = Open(path))
                xml = Metadata(stream);
            var doc = InstallDocuments.Parse(xml);
            if (doc.Root?.Name != "package" || (string?)doc.Root.Attribute("target") is not (null or "" or "Five"))
                throw new InvalidDataException(L.T("core.nve.invalidPackageFormat", path));
            var meta = doc.Root.Element("metadata");
            string title = meta?.Element("name")?.Value.Trim() ?? Path.GetFileNameWithoutExtension(path);
            string description = DocumentRequirements
                .PlainDocumentation(meta?.Element("description")?.Value ?? "")
                .Trim();
            if (part > 0 && !title.Contains("NaturalVision Evolved", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("core.nve.differentNaturalvisionProductRequiresSeparate"));
            string[] pieces = path.Split('\\');
            int groupIndex = Array.FindIndex(pieces, p =>
                p.Equals("Optional Add-Ons", StringComparison.OrdinalIgnoreCase)
            );
            string group =
                groupIndex >= 0 && groupIndex + 1 < pieces.Length
                    ? Regex.Replace(pieces[groupIndex + 1], @"^\d+\.\s*", "")
                    : "";
            int order = part > 0 ? part : 100 + (groupIndex >= 0 ? Number(pieces[groupIndex + 1]) : 99);
            if (group == "NaturalVision Sounds Pack")
                title += path.Contains("Vanilla Sirens", StringComparison.OrdinalIgnoreCase)
                    ? L.T("core.nve.defaultSirens")
                    : L.T("core.nve.newSirens");
            if (new[] { "Spring Season Pack", "Autumn Season Pack", "Christmas Content Pack" }.Contains(group))
                group = "Сезонный набор";
            string id =
                part > 0
                    ? "core-" + part
                    : "option-"
                        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];
            result.Add(
                new(id, path, title, Summary(path, description), description, group, order, part > 0, Size(path))
            );
        }
        if (
            result.Count(c => c.Core) != 3
            || Enumerable.Range(1, 3).Any(i => result.Count(c => c.Id == "core-" + i) != 1)
        )
            throw new InvalidDataException(L.T("core.nve.threeMainPartsNaturalvisionEvolved"));
        var reshade = Files
            .Where(f => f.StartsWith(@"ReShade Shaders\", StringComparison.OrdinalIgnoreCase) && !f.EndsWith('\\'))
            .ToArray();
        if (reshade.Length > 0)
            result.Add(
                new(
                    "reshade",
                    "ReShade Shaders",
                    L.T("core.nve.reshadeNveEffects"),
                    L.T("core.nve.nveShadersPresetCloudsColor"),
                    L.T("core.nve.filesFromSeparateReshadeShaders"),
                    "ReShade",
                    1000,
                    false,
                    reshade.Sum(Size)
                )
            );
        return new(
            Source,
            Stamp(),
            GameEdition.Legacy,
            result.OrderBy(c => c.Order).ThenBy(c => c.Source, StringComparer.OrdinalIgnoreCase).ToArray()
        );
    }

    static int Number(string text) => int.TryParse(Regex.Match(text, @"^\d+").Value, out var n) ? n : 99;

    static string Metadata(Stream stream)
    {
        // Streaming is important: the outer bundle is 16 GB and main nested OIV files are about 4 GB each.
        using var limited = new ReadBudget(stream, 256L << 20);
        using var reader = ReaderFactory.OpenReader(limited, new ReaderOptions { LeaveStreamOpen = true });
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory)
                continue;
            if (reader.Entry.Key?.Replace('\\', '/').Equals("assembly.xml", StringComparison.OrdinalIgnoreCase) != true)
                continue;
            if (reader.Entry.Size > 8L << 20)
                throw new InvalidDataException(L.T("core.nve.oivDescriptionExceeds8Mb"));
            using var entry = reader.OpenEntryStream();
            using var text = new StreamReader(new ReadBudget(entry, 8L << 20), Encoding.UTF8, true);
            return text.ReadToEnd();
        }
        throw new InvalidDataException(L.T("core.nve.oivHasNoAssemblyXml"));
    }

    public byte[]? ReadIcon(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.GetExtension(path).Equals(".oiv", StringComparison.OrdinalIgnoreCase))
            return null;
        const long maxIcon = 4L << 20;
        byte[] ReadImage(Stream stream, long length)
        {
            if (length > maxIcon)
                throw new InvalidDataException(L.T("core.nve.oivPreviewExceeds4Mb"));
            using var limited = new ReadBudget(stream, maxIcon, cancellationToken);
            using var output = new MemoryStream();
            limited.CopyTo(output);
            return output.ToArray();
        }
        if (directory is not null)
        {
            using var zip = ZipFile.OpenRead(local[path]);
            var icon = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("icon.png", StringComparison.OrdinalIgnoreCase)
            );
            return icon is null ? null : ReadImage(icon.Open(), icon.Length);
        }
        using var input = new ReadBudget(Open(path), ModArchive.MaxEntry, cancellationToken);
        using var reader = ReaderFactory.OpenReader(input, new ReaderOptions { LeaveStreamOpen = true });
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                !reader.Entry.IsDirectory
                && reader.Entry.Key?.Equals("icon.png", StringComparison.OrdinalIgnoreCase) == true
            )
                return ReadImage(reader.OpenEntryStream(), reader.Entry.Size);
        }
        return null;
    }

    public void Copy(string entry, string target)
    {
        long expected = Size(entry);
        if (expected > 8L << 30)
            throw new InvalidDataException(L.T("core.nve.componentExceeds8GbLimit"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(target))!).AvailableFreeSpace < expected + (128L << 20))
            throw new IOException(L.T("core.nve.notEnoughSpacePrepareNve"));
        using var input = new ReadBudget(Open(entry), expected);
        using var output = File.Create(target);
        input.CopyTo(output);
        if (output.Length != expected)
            throw new InvalidDataException(L.T("core.nve.nveComponentDamaged"));
    }

    public void Dispose() => archive?.Dispose();

    sealed class ReadBudget(Stream inner, long limit, CancellationToken cancellationToken = default) : Stream
    {
        long read;

        public override int Read(byte[] b, int o, int n)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = inner.Read(b, o, n);
            read += count;
            if (read > limit)
                throw new InvalidDataException(L.T("core.nve.oivReadLimitExceededArchive"));
            return count;
        }

        public override int Read(Span<byte> b)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = inner.Read(b);
            read += count;
            if (read > limit)
                throw new InvalidDataException(L.T("core.nve.oivReadLimitExceeded"));
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => read;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();

        public override void SetLength(long v) => throw new NotSupportedException();

        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }

    static string Summary(string path, string original)
    {
        (string key, string text)[] known =
        [
            ("PART ONE", L.T("core.nve.mainPart1WeatherLighting")),
            ("PART TWO", L.T("core.nve.mainPart2RequiredNve")),
            ("PART THREE", L.T("core.nve.mainPart3CompletesMain")),
            ("+ Performance Boost", L.T("core.nve.brighterEmergencyLightsLighterEffects")),
            ("Brighter Emergency", L.T("core.nve.brighterEmergencyVehicleLightsAlso")),
            ("Performance Boost", L.T("core.nve.improvesFpsAtCostSome")),
            ("Parallax Roads", L.T("core.nve.moreDetailedRoadsSidewalksDepth")),
            ("Vanilla Sirens", L.T("core.nve.newWeaponVehicleSoundsRetaining")),
            ("Sounds Pack", L.T("core.nve.realisticWeaponVehicleSirenSounds")),
            ("DLSS", L.T("core.nve.dlssDlaaFsrImageQuality")),
            ("Extra Vegetation", L.T("core.nve.additionalBushesFlowersHedgesIncreases")),
            ("Volumetric Headlights", L.T("core.nve.volumetricHeadlightsSettingsMenuCtrl")),
            ("Hide Weapon", L.T("core.nve.removesDefaultCrosshairDamageIndicator")),
            ("NaturalVision Cam", L.T("core.nve.customizableVehicleCameraMenuCtrl")),
            ("Green Phosphor", L.T("core.nve.greenNightVisionEffect")),
            ("White Phosphor", L.T("core.nve.whiteNightVisionEffect")),
            ("White Street", L.T("core.nve.whiteLedStreetLightingInstead")),
            ("Spring Season", L.T("core.nve.springVegetationStyling")),
            ("Autumn Season", L.T("core.nve.autumnVegetationStyling")),
            ("Halloween", L.T("core.nve.halloweenThemedObjectsWeather")),
            ("Christmas", L.T("core.nve.winterStylingThemedObjects")),
            ("Snowflakes", L.T("core.nve.screenSnowflakeEffectSnowyWeather")),
            ("Default Trash", L.T("core.nve.restoresDefaultStreetLitterDensity")),
            ("More Trash", L.T("core.nve.increasesAmountLitterStreets")),
            ("Remove All Trash", L.T("core.nve.removesMostLitterUsefulScreenshots")),
            ("Darker Nights", L.T("core.nve.darkerNightsIncompatibleNve2020")),
            ("2020 Weather", L.T("core.nve.warmColorsVignetteStyleNve")),
            ("Gameconfig", L.T("core.nve.gameconfigVehiclesMapsReplacesCurrent")),
            ("Older Game", L.T("core.nve.compatibilityPatchOlderGameBuilds")),
            ("LSPDFR", L.T("core.nve.disablesSomeWeaponTexturesLighting")),
        ];
        return known.FirstOrDefault(k => path.Contains(k.key, StringComparison.OrdinalIgnoreCase)).text
            ?? (original.Length > 170 ? original[..170] + "…" : original);
    }
}
