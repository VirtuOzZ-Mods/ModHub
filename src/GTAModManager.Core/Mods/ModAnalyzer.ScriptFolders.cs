using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using GTAModManager.Core.Game;
using GTAModManager.Core.Oiv;

namespace GTAModManager.Core.Mods;

/// <summary>A folder of the archive that is installed as a whole into the game's scripts\&lt;Name&gt;\.</summary>
public sealed record ScriptFolder(string Dir, string Name, IReadOnlyList<string> Files)
{
    public string Target(string file) =>
        @"scripts\" + Name + "\\" + (Dir.Length == 0 ? file : file[(Dir.Length + 1)..]);
}

public static partial class ModAnalyzer
{
    private const long MaxScriptAssembly = 32L << 20;

    /// <summary>
    /// ScriptHookVDotNet mods that live in their own folder under scripts\ (Autosport Racing System:
    /// scripts\AutosportRacingSystem\ARS.dll with Tracks\, Settings\… beside it). The folder is recognized when it
    /// directly holds a SHVDN assembly and either carries its own resources in subfolders or the README tells to put
    /// that folder into scripts. Its name and the whole inner structure are kept. Files already under a scripts\
    /// segment of the archive are handled by the plain scripts rule, so the path is never doubled.
    /// A folder that holds Name.dll next to a Name\ subfolder is a download wrapper, not a script folder: SHVDN
    /// scripts conventionally keep their data in a sibling folder named after the assembly, directly in scripts\.
    /// </summary>
    public static List<ScriptFolder> FindScriptFolders(
        ModArchive archive,
        IReadOnlyCollection<string> files,
        GameEdition edition
    )
    {
        var pool = files.Where(f => ScriptsIndex(f) < 0).ToList();
        var assemblies = PickEdition(
                pool.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList(),
                edition
            )
            .Where(f => IsShvdnScript(archive, f))
            .ToList();
        if (assemblies.Count == 0)
            return [];
        string? readme = null;
        var result = new List<ScriptFolder>();
        foreach (
            var dir in assemblies
                .Select(f => Path.GetDirectoryName(f) ?? "")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d.Length == 0 ? 0 : d.Count(c => c == '\\') + 1)
        )
        {
            // The outermost script folder takes everything inside it (bundled libraries in a subfolder included).
            if (result.Any(r => Inside(dir, r.Dir)))
                continue;
            // Files at the archive root have no folder name to keep (a dropped folder already starts with its own name).
            if (dir.Length == 0)
                continue;
            var name = Path.GetFileName(dir);
            if (!PlausibleScriptFolderName(name))
                continue;
            var subtree = pool.Where(f => Inside(f, dir)).ToList();
            // Mixed mods (an ASI or a DLC pack inside) are left to their own rules.
            if (
                subtree.Any(f =>
                    Path.GetExtension(f).ToLowerInvariant() is ".asi" or ".oiv"
                    || Path.GetFileName(f).Equals("dlc.rpf", StringComparison.OrdinalIgnoreCase)
                )
            )
                continue;
            var direct = assemblies
                .Where(f => string.Equals(Path.GetDirectoryName(f) ?? "", dir, StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var subfolders = subtree
                .Select(f => Relative(f, dir).Split('\\'))
                .Where(p => p.Length > 1)
                .Select(p => p[0])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (subfolders.Overlaps(direct))
                continue;
            readme ??= ReadDocs(archive, files);
            if (subfolders.Count == 0 && !ReadmeMentionsScriptsFolder(readme, name))
                continue;
            var install = subtree.Where(f => !NeverInstall.Contains(Path.GetExtension(f))).ToList();
            result.Add(new ScriptFolder(dir, name, install));
        }
        return result;
    }

    private static bool Inside(string path, string dir) =>
        dir.Length == 0 || path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);

    private static string Relative(string path, string dir) => dir.Length == 0 ? path : path[(dir.Length + 1)..];

    private static bool PlausibleScriptFolderName(string name) =>
        name.Trim().Length > 0
        && name == name.Trim()
        && !name.EndsWith('.')
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Equals("scripts", StringComparison.OrdinalIgnoreCase)
        && !GenericFolders.Contains(name)
        && !EnhancedWords.Concat(LegacyWords).Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The README names the folder close to the word "scripts" ("extract the X folder into GTA V\Scripts\").</summary>
    private static bool ReadmeMentionsScriptsFolder(string text, string name)
    {
        for (int i = text.IndexOf(name, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(name, i + name.Length, StringComparison.OrdinalIgnoreCase))
        {
            int from = Math.Max(0, i - 160);
            int to = Math.Min(text.Length, i + name.Length + 160);
            var near = text[from..to];
            bool folderWord =
                near.Contains("folder", StringComparison.OrdinalIgnoreCase)
                || near.Contains("папк", StringComparison.OrdinalIgnoreCase);
            if (folderWord && near.Contains("scripts", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ReadDocs(ModArchive archive, IEnumerable<string> files) =>
        string.Join(
            "\n",
            files
                .Where(f =>
                    Path.GetExtension(f).ToLowerInvariant() is ".txt" or ".md" or ".nfo"
                    && (
                        Path.GetFileName(f).Contains("readme", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(f).Contains("install", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(f).Contains("инструкц", StringComparison.OrdinalIgnoreCase)
                    )
                )
                .Take(8)
                .Select(f => ReadSmallText(archive, f))
        );

    /// <summary>A managed assembly that references ScriptHookVDotNet (a script or a UI library for scripts).</summary>
    private static bool IsShvdnScript(ModArchive archive, string path)
    {
        try
        {
            if (archive.SourceSize(path) > MaxScriptAssembly)
                return false;
            using var bytes = new MemoryStream(archive.Read(path), false);
            using var pe = new PEReader(bytes);
            if (!pe.HasMetadata)
                return false;
            var md = pe.GetMetadataReader();
            return md.AssemblyReferences.Any(h =>
                md.GetString(md.GetAssemblyReference(h).Name)
                    .StartsWith("ScriptHookVDotNet", StringComparison.OrdinalIgnoreCase)
            );
        }
        catch (Exception ex)
            when (ex
                    is BadImageFormatException
                        or InvalidOperationException
                        or IOException
                        or InvalidDataException
                        or NotSupportedException
                        or OivFormatException
            )
        {
            return false;
        }
    }
}
