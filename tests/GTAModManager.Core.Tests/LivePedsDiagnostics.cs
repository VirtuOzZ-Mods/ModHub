using GTAModManager.Core.Game;
using GTAModManager.Core.Peds;
using Xunit.Abstractions;

namespace GTAModManager.Core.Tests;

/// <summary>Read-only diagnostics on a real game: only with GMM_LIVE_PEDS=game folder.</summary>
public sealed class LivePedsDiagnostics(ITestOutputHelper output)
{
    /// <summary>Structure of peds.rpf in a DLC: GMM_LIVE_PEDS_RPF=path to dlc.rpf (read-only).</summary>
    [Fact]
    public void Ped_pack_structure()
    {
        var dlc = Environment.GetEnvironmentVariable("GMM_LIVE_PEDS_RPF");
        if (string.IsNullOrEmpty(dlc))
            return;
        using var a = GTAModManager.Core.Rpf.Rpf7Archive.OpenRead(dlc);
        output.WriteLine("dlc root: " + string.Join(", ", a.Files.Select(f => f.Path)));
        var nested = a
            .Files.OfType<GTAModManager.Core.Rpf.RpfBinaryFile>()
            .First(f => f.Path.EndsWith("peds.rpf", StringComparison.OrdinalIgnoreCase));
        using var n = a.OpenNested(nested);
        var paths = n
            .Root.Descendants()
            .Select(e => e.Path + (e is GTAModManager.Core.Rpf.RpfFile ? "" : "\\"))
            .ToList();
        output.WriteLine($"files: {paths.Count}");
        foreach (var g in paths.GroupBy(p => p.Contains('\\') ? p[..p.IndexOf('\\')] + "\\" : "(root)").Take(12))
            output.WriteLine($"{g.Key}: {g.Count()}  e.g. {string.Join(", ", g.Take(4))}");
    }

    [Fact]
    public void Selector_and_ped_plans_on_real_game()
    {
        var root = Environment.GetEnvironmentVariable("GMM_LIVE_PEDS");
        if (string.IsNullOrEmpty(root))
            return;
        var game = GameInstall.Open(root);
        var component = new PedSelectorComponent(game);
        var inspect = component.Inspect();
        output.WriteLine(
            $"selector: own={inspect.InstalledOwn} third={inspect.InstalledThirdParty} missing=[{string.Join("; ", inspect.MissingDependencies)}]"
        );
        var discovery = new PedDiscoveryService(game).Scan();
        output.WriteLine(
            $"discovery: {discovery.Entries.Count} entries; warnings: {string.Join(" | ", discovery.Warnings)}"
        );
        var asi = Path.Combine(Environment.GetEnvironmentVariable("GMM_SELECTOR_ASI") ?? "", "");
        using (var plan = component.PrepareInstall(asi, discovery.Entries))
            output.WriteLine(
                $"selector plan: canInstall={plan.CanInstall} errors=[{string.Join(" | ", plan.Errors)}] changes={plan.Changes.Count}"
            );
        foreach (
            var archive in (Environment.GetEnvironmentVariable("GMM_PED_ARCHIVES") ?? "").Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            var analysis = PedArchiveAnalyzer.Analyze(archive);
            output.WriteLine(
                $"archive {Path.GetFileName(archive)}: candidates={analysis.Candidates.Count} warnings=[{string.Join(" | ", analysis.Warnings)}]"
            );
        }
    }
}
