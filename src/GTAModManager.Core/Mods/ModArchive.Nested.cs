using GTAModManager.Core.Localization;
using GTAModManager.Core.Oiv;

namespace GTAModManager.Core.Mods;

public sealed partial class ModArchive
{
    private readonly Dictionary<string, (ModArchive Archive, string Key)> _virtual = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly List<ModArchive> _children = [];
    private string? _nestedDirectory;

    private sealed class ExpansionBudget
    {
        public long Remaining = 4L << 30;
        public int Count;
    }

    public static ModArchive OpenExpanded(string path)
    {
        // An unpacked folder is read as is: archives inside it are not expanded.
        if (Directory.Exists(path))
            return OpenFolder(path);
        var budget = new ExpansionBudget();
        var archive = OpenBounded(path, budget.Remaining);
        try
        {
            budget.Remaining -= archive._entries.Values.Sum(e => Math.Max(0, e.Size));
            archive.Expand(0, budget);
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private void Expand(int depth, ExpansionBudget budget)
    {
        foreach (
            var key in _entries
                .Keys.Where(k =>
                    new[] { ".zip", ".rar", ".7z" }.Contains(Path.GetExtension(k), StringComparer.OrdinalIgnoreCase)
                )
                .ToArray()
        )
        {
            if (depth >= 3 || ++budget.Count > 32)
                throw new OivFormatException(L.T("core.mods.nestedArchiveLimitExceeded3"));
            if (SourceSize(key) > 1L << 30)
                throw new OivFormatException(L.T("core.mods.nestedArchiveExceeds1Gb"));
            _nestedDirectory ??= Path.Combine(Path.GetTempPath(), "ModHub", "nested-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_nestedDirectory);
            var local = Path.Combine(_nestedDirectory, Guid.NewGuid().ToString("N") + Path.GetExtension(key));
            using (var input = OpenSource(key))
            using (var output = File.Create(local))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = input.Read(buffer)) > 0)
                {
                    total += count;
                    if (total > 1L << 30)
                        throw new OivFormatException(L.T("core.mods.nestedArchiveExceedsLimit"));
                    output.Write(buffer, 0, count);
                }
            }
            var child = OpenBounded(local, budget.Remaining);
            _children.Add(child);
            budget.Remaining -= child._entries.Values.Sum(e => Math.Max(0, e.Size));
            child.Expand(depth + 1, budget);
            foreach (var source in child.Files)
            {
                string virtualPath = key + "\\" + source;
                if (_entries.ContainsKey(virtualPath) || !_virtual.TryAdd(virtualPath, (child, source)))
                    throw new OivFormatException(L.T("core.mods.nestedArchivePathsOverlap"));
            }
        }
    }
}
