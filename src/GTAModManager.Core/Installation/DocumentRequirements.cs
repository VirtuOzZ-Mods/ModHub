using System.Text.RegularExpressions;
using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Installation;

public sealed record DependencyNeed(string Id, string Title, bool Required, bool Present, string Evidence, string Url);

public static class DocumentRequirements
{
    static readonly (string Id, string Title, string Pattern, string File, string Legacy, string Enhanced)[] Rules =
    [
        (
            "shv",
            "Script Hook V",
            @"script\s*hook\s*v(?!\s*(?:dot|\.))|scripthookv(?!dot)",
            "ScriptHookV.dll",
            "https://www.dev-c.com/gtav/scripthookv/",
            "https://www.dev-c.com/gtav/scripthookv/"
        ),
        (
            "shvdn",
            "ScriptHookVDotNet",
            @"scripthookv?dotnet|script\s*hook\s*v?\s*\.net|shvdn",
            "ScriptHookVDotNet.asi",
            "https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases",
            "https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced/releases"
        ),
        (
            "heap",
            "Heap Adjuster",
            @"heap[\s_-]*adjuster",
            "HeapAdjuster.asi",
            "https://www.gta5-mods.com/tools/heapadjuster",
            "https://www.gta5-mods.com/tools/heapadjuster-enhanced"
        ),
        (
            "packfile",
            "Packfile Limit Adjuster",
            @"pack[\s_-]*file[\s_-]*limit|packfilelimit",
            "PackfileLimitAdjuster.asi",
            "https://www.gta5-mods.com/tools/packfile-limit-adjuster",
            "https://www.gta5-mods.com/scripts/packfile-limit-adjuster-enhanced"
        ),
        (
            "gameconfig",
            "GameConfig",
            @"game\s*config",
            "mods/update/update.rpf",
            "https://www.gta5-mods.com/misc/gta-5-gameconfig-300-cars",
            "https://www.gta5-mods.com/misc/gta-5-gameconfig-300-cars"
        ),
    ];

    public static IReadOnlyList<DependencyNeed> Inspect(
        GameInstall game,
        string documentation,
        IEnumerable<string> targets
    )
    {
        var result = new List<DependencyNeed>();
        var paths = targets.ToArray();
        var status = ModdingBase.Inspect(game);
        bool managedScript = paths.Any(p =>
            p.StartsWith("scripts\\", StringComparison.OrdinalIgnoreCase)
            && p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        );
        var lines = documentation.Replace("\r", "").Split('\n');
        foreach (var rule in Rules)
        {
            var evidence = lines.FirstOrDefault(line =>
                Regex.IsMatch(line, rule.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
            );
            bool byType = (rule.Id is "shv" or "shvdn") && managedScript;
            if (
                rule.Id == "gameconfig"
                && paths.Any(p => p.EndsWith("gameconfig.xml", StringComparison.OrdinalIgnoreCase))
            )
                continue;
            if (evidence is null && !byType)
                continue;
            bool optional =
                evidence is not null
                && Regex.IsMatch(
                    evidence,
                    @"optional|recommend|рекоменд|необяз|not\s+(?:needed|required)|no\s+need|не\s+(?:нуж|требу)",
                    RegexOptions.IgnoreCase
                );
            bool present =
                rule.Id == "shv" ? status.ScriptHookV
                : rule.Id == "shvdn" ? status.Shvdn
                : rule.Id is "heap" or "packfile" ? AdjusterComponents.Present(game, rule.Id)
                : rule.Id == "gameconfig" ? File.Exists(Path.Combine(game.Root, @"mods\update\update.rpf"))
                : File.Exists(Path.Combine(game.Root, rule.File));
            result.Add(
                new(
                    rule.Id,
                    rule.Title,
                    !optional,
                    present,
                    byType && evidence is null ? L.T("core.installation.netScriptDetectedScripts") : evidence ?? "",
                    game.Edition == GameEdition.Legacy ? rule.Legacy : rule.Enhanced
                )
            );
        }
        bool asi = paths.Any(p => p.EndsWith(".asi", StringComparison.OrdinalIgnoreCase));
        bool mods = paths.Any(p => GameInstall.IsGameDataPath(p) || GameInstall.IsInMods(p));
        if (asi || mods || managedScript)
            result.Add(
                new(
                    "asi",
                    "ASI Loader",
                    true,
                    status.AsiLoader,
                    L.T("core.installation.requiredLoadAsiPlugins"),
                    "https://www.dev-c.com/gtav/scripthookv/"
                )
            );
        if (mods)
            result.Add(
                new(
                    "mods",
                    L.T("core.installation.modsFolderLoader"),
                    true,
                    status.ModsLoader,
                    L.T("core.installation.packageModifiesModdedRpfArchives"),
                    "https://openiv.com/"
                )
            );
        return result;
    }

    public static string PlainDocumentation(string text) =>
        System.Net.WebUtility.HtmlDecode(
            Regex.Replace(
                Regex.Replace(
                    text,
                    @"<script\b[^>]*>[\s\S]*?</script>|<style\b[^>]*>[\s\S]*?</style>",
                    "",
                    RegexOptions.IgnoreCase
                ),
                "<[^>]+>",
                " "
            )
        );
}
