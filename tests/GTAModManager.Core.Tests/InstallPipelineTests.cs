using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Mods;

namespace GTAModManager.Core.Tests;

public sealed class InstallPipelineTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "gmm-pipeline-" + Guid.NewGuid().ToString("N"));

    public InstallPipelineTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "GTA5.exe"), "stub");
    }

    public void Dispose() => Directory.Delete(root, true);

    string Zip(Dictionary<string, byte[]> files)
    {
        string p = Path.Combine(root, Guid.NewGuid() + ".zip");
        using var z = ZipFile.Open(p, ZipArchiveMode.Create);
        foreach (var f in files)
        {
            using var s = z.CreateEntry(f.Key).Open();
            s.Write(f.Value);
        }
        return p;
    }

    [Fact]
    public void Deep_asi_inside_scripts_keeps_its_config_at_game_root()
    {
        using var a = ModArchive.Open(
            Zip(
                new()
                {
                    ["release/scripts/deep/Nitro.asi"] = [77, 90],
                    ["release/scripts/deep/Nitro.INI"] = Encoding.UTF8.GetBytes("[Menu]"),
                }
            )
        );
        var result = ModAnalyzer.Analyze(a, GameInstall.Open(root));
        Assert.Contains(result.Parts, p => p.Source.EndsWith("Nitro.asi") && p.Target == "Nitro.asi");
        Assert.Contains(result.Parts, p => p.Source.EndsWith("Nitro.INI") && p.Target == "Nitro.INI");
    }

    [Fact]
    public void Backup_plugin_is_not_installed_alongside_current_plugin()
    {
        using var a = ModArchive.Open(Zip(new() { ["release/main.asi"] = [77, 90], ["backup/main.asi"] = [77, 90] }));
        var result = ModAnalyzer.Analyze(a, GameInstall.Open(root));
        Assert.Single(result.Parts, p => p.Target == "main.asi");
    }
}
