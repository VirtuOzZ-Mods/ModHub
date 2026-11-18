using GTAModManager.Core.Dlc;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;

namespace GTAModManager.Core.Installation;

/// <summary>What a dropped mod mainly is; also the order of the queue after its prevailing kind.</summary>
public enum BatchKind
{
    Vehicles,
    EngineSounds,
    Map,
    Peds,
    Scripts,
    Other,
}

/// <summary>One dropped mod in the queue.</summary>
public sealed class BatchItem
{
    public required string Path { get; init; }
    public required string Title { get; init; }
    public required BatchKind Kind { get; init; }

    /// <summary>What the analysis found ("Add-on vehicle", "Scripts + ASI plugin").</summary>
    public string Summary { get; init; } = "";

    /// <summary>gmm verb that installs it: "mod" for archives and folders, "oiv" for OIV packages.</summary>
    public string Verb { get; init; } = "mod";

    /// <summary>Why it cannot be installed unattended (a variant to choose, an error); null when it can.</summary>
    public string? Problem { get; init; }

    /// <summary>Not of the prevailing kind: placed after the prevailing mods.</summary>
    public bool Moved { get; set; }
}

public sealed record BatchPlan(BatchKind Main, IReadOnlyList<BatchItem> Items)
{
    public int MainCount => Items.Count(i => i.Kind == Main);
}

/// <summary>
/// Several mods dropped at once: each is analyzed the usual way, the kind most of them share (say, add-on cars)
/// goes first, and the rest follow in a fixed order (cars, engine sounds, maps, characters, scripts, other), so an
/// engine sound pack picked by mistake among cars lands after them. Nothing is installed here.
/// </summary>
public static class DropBatch
{
    public static BatchPlan Plan(IReadOnlyList<string> paths, GameInstall game, Action<int, string>? progress = null)
    {
        var items = new List<BatchItem>();
        for (int i = 0; i < paths.Count; i++)
        {
            progress?.Invoke(i, paths[i]);
            items.Add(Analyze(paths[i], game));
        }
        return Order(items);
    }

    /// <summary>Puts the prevailing kind first (ties: the earlier kind), keeping the drop order inside each kind.</summary>
    public static BatchPlan Order(IReadOnlyList<BatchItem> items)
    {
        var main =
            items.Count == 0
                ? BatchKind.Other
                : items.GroupBy(i => i.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        var ordered = items.OrderBy(i => i.Kind == main ? -1 : (int)i.Kind).ToList();
        foreach (var item in ordered)
            item.Moved = item.Kind != main;
        return new(main, ordered);
    }

    public static BatchItem Analyze(string path, GameInstall game)
    {
        string title = Directory.Exists(path)
            ? System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))
            : System.IO.Path.GetFileNameWithoutExtension(path);
        if (System.IO.Path.GetExtension(path).Equals(".oiv", StringComparison.OrdinalIgnoreCase))
            return new()
            {
                Path = path,
                Title = title,
                Kind = BatchKind.Other,
                Verb = "oiv",
                Summary = L.T("core.batch.oivPackage"),
            };
        try
        {
            using var archive = ModArchive.Open(path);
            var a = ModAnalyzer.Analyze(archive, game);
            string? problem =
                a.PendingChoices.Count > 0 || a.NeedsGender ? a.Errors.FirstOrDefault() ?? L.T("core.batch.needsChoice")
                : a.NestedOiv.Count > 1 ? L.T("core.batch.severalOiv")
                : a.Errors.FirstOrDefault();
            return new()
            {
                Path = path,
                Title = a.Title,
                Kind = KindOf(a),
                Summary = a.Summary,
                Problem = problem,
            };
        }
        catch (Exception ex)
            when (ex is OivFormatException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new()
            {
                Path = path,
                Title = title,
                Kind = BatchKind.Other,
                Problem = ex.Message,
            };
        }
    }

    /// <summary>The kind of a mod by its analysis: its add-on packs first, then scripts and ASI plugins.</summary>
    public static BatchKind KindOf(ModAnalysis a)
    {
        var packs = a.DlcPacks.Select(p => p.Kind).ToList();
        if (packs.Contains(DlcPackKind.Vehicles))
            return BatchKind.Vehicles;
        if (packs.Contains(DlcPackKind.EngineSounds))
            return BatchKind.EngineSounds;
        if (packs.Contains(DlcPackKind.Map))
            return BatchKind.Map;
        if (packs.Contains(DlcPackKind.Peds))
            return BatchKind.Peds;
        if (
            a.Parts.Any(p =>
                p.Target.StartsWith(@"scripts\", StringComparison.OrdinalIgnoreCase)
                || p.Target.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)
            )
        )
            return BatchKind.Scripts;
        return BatchKind.Other;
    }

    /// <summary>"Add-on cars", "Engine sounds"… for the queue header and notes.</summary>
    public static string Label(BatchKind kind) =>
        L.T(
            kind switch
            {
                BatchKind.Vehicles => "core.batch.kindVehicles",
                BatchKind.EngineSounds => "core.batch.kindEngineSounds",
                BatchKind.Map => "core.batch.kindMaps",
                BatchKind.Peds => "core.batch.kindPeds",
                BatchKind.Scripts => "core.batch.kindScripts",
                _ => "core.batch.kindOther",
            }
        );
}
