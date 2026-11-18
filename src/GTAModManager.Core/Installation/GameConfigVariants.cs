using System.Text.RegularExpressions;
using GTAModManager.Core.Mods;

namespace GTAModManager.Core.Installation;

public sealed record GameConfigVariant(
    string Source,
    string Folder,
    string Preset,
    string Traffic,
    string? EditionHint,
    string? BuildHint
)
{
    public string PresetLabel =>
        Source
            .Split('\\')
            .Any(p =>
                p.Contains("less mods", StringComparison.OrdinalIgnoreCase)
                || p.Contains("more mods", StringComparison.OrdinalIgnoreCase)
            )
            ? Preset
            : Localization.L.T("core.installation.default2");
    public string TrafficLabel => Source.Contains('\\') ? Traffic : Localization.L.T("core.installation.default");
}

public static class GameConfigVariants
{
    public static IReadOnlyList<GameConfigVariant> Find(ModArchive archive)
    {
        var result = new List<GameConfigVariant>();
        foreach (
            var source in archive.Files.Where(f =>
                Path.GetFileName(f).Equals("gameconfig.xml", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            var parts = source.Split('\\');
            if (
                parts.Any(p =>
                    p.Equals("backup", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("original", StringComparison.OrdinalIgnoreCase)
                )
            )
                continue;
            string traffic = parts.Length > 1 ? parts[^2] : "Стандартный";
            int preset = Array.FindIndex(parts, p =>
                p.Contains("less mods", StringComparison.OrdinalIgnoreCase)
                || p.Contains("more mods", StringComparison.OrdinalIgnoreCase)
            );
            string folder =
                preset > 0
                    ? string.Join(" / ", parts[..preset])
                    : string.Join(" / ", parts[..Math.Max(0, parts.Length - 2)]);
            string profile = preset >= 0 ? parts[preset] : "По умолчанию";
            string? edition =
                source.Contains("enhanced", StringComparison.OrdinalIgnoreCase) ? "Enhanced"
                : source.Contains("legacy", StringComparison.OrdinalIgnoreCase) ? "Legacy"
                : null;
            var build = Regex.Match(folder, @"(?<!\d)1\.0\.\d+(?:\.\d+)?");
            result.Add(new(source, folder, profile, traffic, edition, build.Success ? build.Value : null));
        }
        return result;
    }
}
