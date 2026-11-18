using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("GTAModManager.Core.Tests")]

namespace GTAModManager.Core.Peds;

public sealed partial class PedPackageLibrary(GameInstall game)
{
    public const string CacheDirectory = @"GMMAddonPeds\packages";
    private const string ManifestPath = "__gmm_package/manifest.json";
    private const string Owner = "GMM AddonPeds package";
    private const long MaxImage = 20L << 20,
        MaxMedia = 80L << 20,
        MaxManifest = 4L << 20;

    [GeneratedRegex("^gmm-ped-v1-[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool IsValidPackageId(string? id) => id is not null && IdPattern().IsMatch(id);

    internal static string NewId() => "gmm-ped-v1-" + Guid.NewGuid().ToString("N");

    internal static string RelativePath(string id) =>
        IsValidPackageId(id)
            ? CacheDirectory + "\\" + id + ".zip"
            : throw new ArgumentException(L.T("core.peds.invalidPedPackageIdentifier"));

    private sealed class Manifest
    {
        public string Owner { get; set; } = PedPackageLibrary.Owner;
        public int Version { get; set; } = 1;
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string SourceArchive { get; set; } = "";
        public string Readme { get; set; } = "";
        public PedReadmeDetails Details { get; set; } = PedReadmeDetails.Empty;
        public List<string> ImagePaths { get; set; } = [];
    }

    /// <summary>Loads one UI group. Cached media requires no original model archive.</summary>
    public PedPackagePresentation Load(IReadOnlyList<PedEntry> entries)
    {
        using var gate = GameOperationGate.Acquire(game);
        if (game.Edition != GameEdition.Legacy)
            throw new ArgumentException(L.T("core.peds.pedPackagesSupportedGtaV"));
        if (entries.Count == 0)
            throw new ArgumentException(L.T("core.peds.pedPackageGroupEmpty"));
        var first = entries[0];
        if (
            entries.Any(e =>
                e.PackageId != first.PackageId
                || (
                    string.IsNullOrEmpty(first.PackageId)
                    && !e.SourceArchive.Equals(first.SourceArchive, StringComparison.OrdinalIgnoreCase)
                )
            )
        )
            throw new ArgumentException(L.T("core.peds.provideModelsFromSinglePed"));
        string note = "";
        if (IsValidPackageId(first.PackageId))
        {
            string cache = Path.Combine(game.Root, RelativePath(first.PackageId));
            try
            {
                return ReadCache(cache, first.PackageId);
            }
            catch (Exception ex) when (ReadFailure(ex))
            {
                note = L.T("core.peds.savedPackageUnavailable", ex.Message);
            }
        }
        if (!string.IsNullOrWhiteSpace(first.SourceArchive) && File.Exists(first.SourceArchive))
        {
            try
            {
                var analysis = PedArchiveAnalyzer.Analyze(first.SourceArchive);
                return new(
                    Title(analysis),
                    first.SourceArchive,
                    note + analysis.Readme,
                    analysis.Details,
                    analysis.ImagePaths,
                    first.SourceArchive
                );
            }
            catch (Exception ex) when (ReadFailure(ex))
            {
                note += L.T("core.peds.sourceArchiveUnavailable", ex.Message);
            }
        }
        string title = string.IsNullOrWhiteSpace(first.SourceArchive)
            ? first.DisplayName
            : Path.GetFileNameWithoutExtension(first.SourceArchive);
        if (string.IsNullOrWhiteSpace(title))
            title = first.SpawnName;
        return new(
            title,
            first.SourceArchive,
            L.T(
                "core.peds.readmeImagesUnavailableSourceArchive",
                note,
                string.Join(", ", entries.Select(e => e.SpawnName))
            ),
            PedReadmeDetails.Empty,
            [],
            ""
        );
    }

    internal static CacheDraft Stage(GameInstall game, string id, PedArchiveAnalysis analysis, ModArchive source)
    {
        string target = Path.Combine(game.Root, RelativePath(id));
        RejectReparseAncestors(target);
        if (File.Exists(target))
            throw new ArgumentException(L.T("core.peds.savedPedPackageNameAlready"));
        string stage = Path.Combine(game.WorkDir, "prep", "ped-media-" + Guid.NewGuid().ToString("N"));
        RejectReparseAncestors(stage);
        Directory.CreateDirectory(stage);
        var draft = new CacheDraft(stage, Path.Combine(stage, "package.zip"));
        try
        {
            var readme = analysis.Readme[..Math.Min(analysis.Readme.Length, 262144)];
            var manifest = new Manifest
            {
                Id = id,
                Title = Title(analysis),
                SourceArchive = analysis.ArchivePath,
                Readme = readme,
                Details = PedReadmeParser.Parse(readme),
            };
            using (var zip = ZipFile.Open(draft.Path, ZipArchiveMode.Create))
            {
                long total = 0;
                foreach (string image in analysis.ImagePaths.Distinct(StringComparer.OrdinalIgnoreCase).Take(32))
                {
                    string? path = SafeImage(image);
                    if (path is null)
                        continue;
                    long size = source.SourceSize(image);
                    if (size < 0 || size > MaxImage || total + size > MaxMedia)
                        continue;
                    total += size;
                    using var input = source.OpenSource(image);
                    using var output = zip.CreateEntry(path.Replace('\\', '/'), CompressionLevel.Fastest).Open();
                    CopyBounded(input, output, size);
                    manifest.ImagePaths.Add(path);
                }
                using var metadata = zip.CreateEntry(ManifestPath, CompressionLevel.Fastest).Open();
                JsonSerializer.Serialize(metadata, manifest);
            }
            return draft;
        }
        catch
        {
            draft.Dispose();
            throw;
        }
    }

    private static PedPackagePresentation ReadCache(string path, string id)
    {
        RejectReparseAncestors(path);
        if (new FileInfo(path).Length > MaxMedia + MaxManifest + (1L << 20))
            throw new InvalidDataException(L.T("core.peds.pedCacheExceedsAllowedSize"));
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 33)
            throw new InvalidDataException(L.T("core.peds.tooManyFilesPedCache"));
        var entry = zip.GetEntry(ManifestPath)
            ?? throw new InvalidDataException(L.T("core.peds.savedPedPackageMarkerMissing"));
        if (entry.Length > MaxManifest)
            throw new InvalidDataException(L.T("core.peds.pedPackageDescriptionExceedsLimit"));
        using var input = entry.Open();
        using var bytes = new MemoryStream();
        CopyBounded(input, bytes, entry.Length);
        var manifest = JsonSerializer.Deserialize<Manifest>(bytes.ToArray())
            ?? throw new InvalidDataException(L.T("core.peds.pedPackageDescriptionCorrupted"));
        if (
            manifest.Owner != Owner
            || manifest.Version != 1
            || manifest.Id != id
            || manifest.Readme is null
            || manifest.Readme.Length > 262144
            || manifest.ImagePaths is null
            || manifest.ImagePaths.Count > 32
        )
            throw new InvalidDataException(L.T("core.peds.unknownSavedPedPackageFormat"));
        var images = new List<string>();
        long total = 0;
        foreach (string image in manifest.ImagePaths)
        {
            string safe = SafeImage(image) ?? throw new InvalidDataException(L.T("core.peds.unsafeImagePathPedCache"));
            var media = zip.GetEntry(safe.Replace('\\', '/'))
                ?? throw new InvalidDataException(L.T("core.peds.imageMissingFromPedCache"));
            if (media.Length > MaxImage || (total += media.Length) > MaxMedia)
                throw new InvalidDataException(L.T("core.peds.pedCacheImagesExceedLimit"));
            images.Add(safe);
        }
        var details = PedReadmeParser.Parse(manifest.Readme);
        string title = string.IsNullOrWhiteSpace(details.Title) ? manifest.Title : details.Title;
        return new(
            title[..Math.Min(title.Length, 300)],
            manifest.SourceArchive,
            manifest.Readme,
            details,
            images,
            path
        );
    }

    private static string Title(PedArchiveAnalysis analysis) =>
        string.IsNullOrWhiteSpace(analysis.Details.Title)
            ? Path.GetFileNameWithoutExtension(analysis.ArchivePath)
            : analysis.Details.Title[..Math.Min(analysis.Details.Title.Length, 300)];

    private static string? SafeImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith('\\') || Path.IsPathRooted(path))
            return null;
        var safe = ModArchive.NormalizeKey(path);
        return
            safe is not null
            && new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }.Contains(
                Path.GetExtension(safe),
                StringComparer.OrdinalIgnoreCase
            )
            ? safe
            : null;
    }

    private static void CopyBounded(Stream source, Stream target, long expected)
    {
        byte[] buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, expected - copied + 1))) > 0)
        {
            copied += read;
            if (copied > expected)
                throw new InvalidDataException(L.T("core.peds.pedPackageFileSizeChanged"));
            target.Write(buffer, 0, read);
        }
        if (copied != expected)
            throw new InvalidDataException(L.T("core.peds.pedPackageFileTruncated"));
    }

    private static void RejectReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (
                (File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0
            )
                throw new IOException(L.T("core.peds.fileSystemLinksPedPackage", current));
    }

    private static bool ReadFailure(Exception ex) =>
        ex
            is IOException
                or InvalidDataException
                or ArgumentException
                or InvalidOperationException
                or UnauthorizedAccessException
                or JsonException
                or System.Xml.XmlException
                or Rpf.RpfException
                or OivFormatException
                or SharpCompress.Common.ArchiveException
                or SharpCompress.Common.ArchiveOperationException;

    internal sealed class CacheDraft(string directory, string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

public static class PedInstallBatch
{
    public static IReadOnlyList<PedInstallOutcome> Run(
        GameInstall game,
        IReadOnlyList<PedInstallRequest> requests,
        BackupLibrary library
    ) => Run(game, requests, library, (plan, backup) => new OivInstaller(game).Install(plan, backup));

    internal static IReadOnlyList<PedInstallOutcome> Run(
        GameInstall game,
        IReadOnlyList<PedInstallRequest> requests,
        BackupLibrary library,
        Func<InstallPlan, BackupLibrary, TxResult> install
    )
    {
        ArgumentNullException.ThrowIfNull(library);
        using var gate = GameOperationGate.Acquire(game);
        if (game.Edition != GameEdition.Legacy)
            throw new ArgumentException(L.T("core.peds.gmmAddonpedsSupportsGtaV"));
        if (game.IsRunning())
            throw new ArgumentException(L.T("core.peds.gameRunningPedChangesProhibited"));
        if (new ModModeService(game).Inspect().WritesBlocked)
            throw new ArgumentException(L.T("core.peds.enableModsFinishRecoveryFirst"));
        if (requests.Count is < 1 or > 100)
            throw new ArgumentException(L.T("core.peds.pedQueueRequires1100"));
        var outcomes = new List<PedInstallOutcome>();
        bool recoveryRequired = false;
        foreach (var request in requests)
        {
            string archive = request?.ArchivePath ?? "";
            if (recoveryRequired)
            {
                outcomes.Add(new(archive, false, L.T("core.peds.recoveryPreviousOperationRequiredDid")));
                continue;
            }
            try
            {
                // A previous process or a previous batch may have left an unrecovered
                // operation. Never build the next mutation on that partial state.
                if (HasPendingOperation(game))
                {
                    recoveryRequired = true;
                    outcomes.Add(new(archive, false, L.T("core.peds.recoveryPreviousOperationRequiredDid")));
                    continue;
                }
                if (
                    string.IsNullOrWhiteSpace(archive)
                    || archive.Length > 32768
                    || request?.Selections is null
                    || request.Selections.Count is < 1 or > 1024
                    || request.Selections.Any(s => s is null)
                )
                    throw new ArgumentException(L.T("core.peds.pedArchiveRequestDoesNot"));
                var analysis = PedArchiveAnalyzer.Analyze(archive);
                var discovery = new PedDiscoveryService(game).Scan();
                using var plan = new PedInstaller(game).PrepareInstall(analysis, request.Selections, discovery.Entries);
                if (!plan.CanInstall)
                {
                    outcomes.Add(
                        new(
                            archive,
                            false,
                            string.Join("; ", plan.Errors.DefaultIfEmpty(L.T("core.peds.noChangesInstall")))
                        )
                    );
                    continue;
                }
                var result = install(plan, library);
                outcomes.Add(
                    new(
                        archive,
                        result.State == TxState.Completed,
                        result.Message,
                        result.State == TxState.Completed ? result.Id : null
                    )
                );
                recoveryRequired = result.State is TxState.NeedsIntervention or TxState.Staged or TxState.Committing;
            }
            catch (Exception ex)
                when (ex
                        is ArgumentException
                            or IOException
                            or InvalidDataException
                            or InvalidOperationException
                            or UnauthorizedAccessException
                            or JsonException
                            or System.Xml.XmlException
                            or Rpf.RpfException
                            or OivFormatException
                            or SharpCompress.Common.ArchiveException
                            or SharpCompress.Common.ArchiveOperationException
                )
            {
                outcomes.Add(new(archive, false, ex.Message));
            }
        }
        return outcomes;
    }

    private static bool HasPendingOperation(GameInstall game)
    {
        string journals = Path.Combine(game.WorkDir, "journals");
        if (!Directory.Exists(journals))
            return false;
        foreach (string path in Directory.EnumerateFiles(journals, "*.json"))
        {
            try
            {
                // Edition work directories are shared by distinct installations. A read
                // must not prevent a different game's atomic journal replacement.
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                var journal = JsonSerializer.Deserialize<TxJournal>(stream);
                if (
                    journal is not null
                    && journal.TargetRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase)
                    && journal.State is TxState.NeedsIntervention or TxState.Staged or TxState.Committing
                )
                    return true;
            }
            catch (FileNotFoundException) { } // A completed operation's journal was removed concurrently.
        }
        return false;
    }
}
