using GTAModManager.Core.Localization;
using GTAModManager.Core.Oiv;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace GTAModManager.Core.Mods;

/// <summary>File source for the installer: an OIV package, a mod archive or local files.</summary>
public interface IModSource
{
    /// <summary>Opens a source file; the path is as in install commands.</summary>
    Stream OpenSource(string source);

    /// <summary>File size (throws OivFormatException if the file is missing).</summary>
    long SourceSize(string source);
}

/// <summary>Local files (the "add files to a DLC pack" command): source is a full path.</summary>
public sealed class LocalFileSource : IModSource
{
    public static readonly LocalFileSource Instance = new();

    public Stream OpenSource(string source) => File.OpenRead(source);

    public long SourceSize(string source) =>
        File.Exists(source)
            ? new FileInfo(source).Length
            : throw new OivFormatException(L.T("core.common.fileMissing", source));
}

/// <summary>
/// A ZIP / RAR / 7Z mod archive or an unpacked mod folder (read-only). Inner paths are normalized to "folder\file.ext".
/// Solid archives (solid RAR/7z) are extracted once to a temp folder: random access into them is very slow.
/// Safeguards: an extraction size limit, encrypted archives are refused, paths with ".." and absolute paths are dropped.
/// </summary>
public sealed partial class ModArchive : IModSource, IDisposable
{
    public const long MaxTotalUnpacked = 64L << 30; // 64 GB: protection against archive bombs
    public const long MaxEntry = 8L << 30;

    /// <summary>Most files a dropped mod folder may contain (a whole drive dropped by mistake is refused).</summary>
    public const int MaxFolderFiles = 20000;

    private readonly IArchive? _archive;
    private readonly Dictionary<string, IArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    // Folder mode: inner path → full path on disk.
    private readonly Dictionary<string, string> _folder = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _extractDir;

    public string FilePath { get; }

    /// <summary>The source is an unpacked folder rather than an archive file.</summary>
    public bool IsFolder { get; }

    /// <summary>Mod name from the source: the folder name or the archive name without its extension.</summary>
    public string Title => IsFolder ? Path.GetFileName(FilePath) : Path.GetFileNameWithoutExtension(FilePath);

    public IReadOnlyCollection<string> Files => _entries.Keys.Concat(_folder.Keys).Concat(_virtual.Keys).ToArray();
    public List<string> Skipped { get; } = [];

    private ModArchive(string path, IArchive? archive, string? extractDir, bool folder = false)
    {
        FilePath = path;
        _archive = archive;
        _extractDir = extractDir;
        IsFolder = folder;
    }

    /// <summary>Opens a mod archive, or a mod folder when <paramref name="path"/> is a directory.</summary>
    public static ModArchive Open(string path) =>
        Directory.Exists(path) ? OpenFolder(path) : OpenBounded(path, MaxTotalUnpacked);

    /// <summary>
    /// An unpacked mod folder. Inner paths start with the folder's own name ("stew_seraphxs\dlc.rpf"), as if the
    /// folder had been packed into an archive, so a pack folder keeps its name. Links and junk files are skipped.
    /// </summary>
    public static ModArchive OpenFolder(string path)
    {
        var root = Path.GetFullPath(path).TrimEnd('\\', '/');
        var name = Path.GetFileName(root);
        if (name.Length == 0)
            throw new OivFormatException(L.T("core.mods.folderTooLarge", root));
        var result = new ModArchive(root, null, null, folder: true);
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    result.Skipped.Add(entry.FullName);
                    continue;
                }
                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                    continue;
                }
                var key = NormalizeKey(name + "\\" + Path.GetRelativePath(root, entry.FullName));
                var size = ((FileInfo)entry).Length;
                if (key is null || size > MaxEntry)
                {
                    result.Skipped.Add(entry.FullName);
                    continue;
                }
                if (IsJunk(key))
                    continue;
                total += size;
                if (result._folder.Count >= MaxFolderFiles || total > MaxTotalUnpacked)
                    throw new OivFormatException(L.T("core.mods.folderTooLarge", root));
                result._folder[key] = entry.FullName;
            }
        }
        return result;
    }

    private static ModArchive OpenBounded(string path, long limit)
    {
        IArchive archive;
        try
        {
            archive = ArchiveFactory.OpenArchive(path, new ReaderOptions());
        }
        catch (Exception ex)
            when (ex
                    is InvalidOperationException
                        or ArgumentException
                        or NotSupportedException
                        or IOException
                        or SharpCompress.Common.ArchiveException
            )
        {
            throw new OivFormatException(L.T("core.mods.couldNotOpenArchive", ex.Message));
        }
        ModArchive? opened = null;
        try
        {
            if (archive.IsEncrypted || archive.Entries.Any(e => e.IsEncrypted))
                throw new OivFormatException(L.T("core.mods.archivePasswordProtectedTheseArchives"));
            long total = archive.Entries.Where(e => !e.IsDirectory).Sum(e => Math.Max(0, e.Size));
            if (total > limit)
                throw new OivFormatException(L.T("core.mods.archiveExpandsGbAboveAllowed", total >> 30));

            string? extractDir = null;
            if (archive.IsSolid)
            {
                extractDir = Path.Combine(
                    Path.GetTempPath(),
                    "ModHub",
                    "extract-" + Guid.NewGuid().ToString("N")[..10]
                );
                if (
                    new DriveInfo(Path.GetPathRoot(Path.GetFullPath(extractDir))!).AvailableFreeSpace
                    < total + (128L << 20)
                )
                    throw new IOException(
                        L.T(
                            "lcpp.space",
                            Path.GetPathRoot(extractDir),
                            Library.BackupLibrary.FormatSize(total + (128L << 20))
                        )
                    );
                Directory.CreateDirectory(extractDir);
            }
            var result = opened = new ModArchive(path, archive, extractDir);
            foreach (var e in archive.Entries.Where(e => !e.IsDirectory))
            {
                var key = NormalizeKey(e.Key);
                if (key is null || e.Size > MaxEntry)
                {
                    result.Skipped.Add(e.Key ?? "?");
                    continue;
                }
                if (IsJunk(key))
                    continue;
                result._entries[key] = e;
            }
            if (extractDir is not null)
                result.ExtractSolid();
            return result;
        }
        catch
        {
            if (opened is not null)
                opened.Dispose();
            else
                archive.Dispose();
            throw;
        }
    }

    /// <summary>"a/b\\c.txt" → "a\b\c.txt"; null if the path is unsafe (absolute or containing "..").</summary>
    public static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var parts = key.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.Contains(':')))
            return null;
        return string.Join('\\', parts);
    }

    private static bool IsJunk(string key) =>
        key.StartsWith("__MACOSX\\", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith(".DS_Store", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("Thumbs.db", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase);

    private void ExtractSolid()
    {
        using var reader = _archive!.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory)
                continue;
            var key = NormalizeKey(reader.Entry.Key);
            if (key is null || !_entries.ContainsKey(key))
                continue;
            var target = Path.Combine(_extractDir!, key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var fs = File.Create(target);
            reader.WriteEntryTo(fs);
        }
    }

    public Stream OpenSource(string source)
    {
        var key = NormalizeKey(source) ?? throw new OivFormatException(L.T("core.mods.invalidPath", source));
        if (_virtual.TryGetValue(key, out var nested))
            return nested.Archive.OpenSource(nested.Key);
        if (_folder.TryGetValue(key, out var full))
            return new FileStream(
                full,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1 << 16,
                FileOptions.RandomAccess
            );
        if (!_entries.TryGetValue(key, out var e))
            throw new OivFormatException(L.T("core.mods.archiveHasNoFile", source));
        return _extractDir is not null ? File.OpenRead(Path.Combine(_extractDir, key)) : e.OpenEntryStream();
    }

    public long SourceSize(string source)
    {
        var key = NormalizeKey(source) ?? throw new OivFormatException(L.T("core.mods.invalidPath", source));
        if (_virtual.TryGetValue(key, out var nested))
            return nested.Archive.SourceSize(nested.Key);
        if (_folder.TryGetValue(key, out var full))
            return File.Exists(full)
                ? new FileInfo(full).Length
                : throw new OivFormatException(L.T("core.mods.archiveHasNoFile", source));
        return _entries.TryGetValue(key, out var e)
            ? e.Size
            : throw new OivFormatException(L.T("core.mods.archiveHasNoFile", source));
    }

    public byte[] Read(string source)
    {
        if (SourceSize(source) > int.MaxValue)
            throw new OivFormatException(L.T("core.mods.fileTooLargeReadInto", source));
        using var s = OpenSource(source);
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        foreach (var child in _children)
            child.Dispose();
        _archive?.Dispose();
        if (_nestedDirectory is not null)
        {
            try
            {
                Directory.Delete(_nestedDirectory, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (_extractDir is not null)
            try
            {
                Directory.Delete(_extractDir, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
    }
}
