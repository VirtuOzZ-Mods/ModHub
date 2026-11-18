using System.Text;
using GTAModManager.Core.Peds;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.Tests;

/// <summary>Characters brought back from a saved Ped Selector pack (pedselector\dlc.rpf).</summary>
public sealed class PedSelectorImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-pedimport-" + Guid.NewGuid().ToString("N"));

    public PedSelectorImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private static RpfBinarySource Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>A backup folder: Backup\pedselector\dlc.rpf with one normal and one streamed character.</summary>
    private string Backup()
    {
        var peds = new MemoryStream();
        new Rpf7Builder()
            .AddFile("anna.yft", Bytes("model"))
            .AddFile("anna.ymt", Bytes("meta"))
            .AddFile("anna.ydd", Bytes("drawables"))
            .AddFile("anna.ytd", Bytes("textures"))
            .AddFile("bob.yft", Bytes("model"))
            .AddFile("bob.ymt", Bytes("meta"))
            .AddFile(@"bob\head_000_r.ydd", Bytes("head"))
            .AddFile(@"bob\head_diff_000_a_whi.ytd", Bytes("head texture"))
            .Write(peds);
        var folder = Path.Combine(_root, "Backup", PedInstaller.PackName);
        Directory.CreateDirectory(folder);
        using var fs = File.Create(Path.Combine(folder, "dlc.rpf"));
        new Rpf7Builder()
            .AddFile("setup2.xml", Bytes("<SSetupData/>"))
            .AddFile(
                @"common\data\peds.meta",
                Bytes(
                    "<CPedModelInfo__InitDataList><InitDatas><Item><Name>anna</Name><Pedtype>CIVFEMALE</Pedtype></Item></InitDatas></CPedModelInfo__InitDataList>"
                )
            )
            .AddFile(PedSelectorImport.PedsArchive, new RpfBinarySource(peds.ToArray()))
            .Write(fs);
        return Path.Combine(_root, "Backup");
    }

    [Fact]
    public void Pack_is_found_from_the_backup_folder_the_pack_folder_or_the_file()
    {
        var backup = Backup();
        var pack = Path.Combine(backup, PedInstaller.PackName, "dlc.rpf");
        Assert.Equal(pack, PedSelectorImport.FindPack(backup));
        Assert.Equal(pack, PedSelectorImport.FindPack(Path.GetDirectoryName(pack)!));
        Assert.Equal(pack, PedSelectorImport.FindPack(pack));
        Assert.Throws<ArgumentException>(() => PedSelectorImport.FindPack(_root));
    }

    [Fact]
    public void Characters_are_unpacked_with_layout_detected_and_gender_left_to_choose()
    {
        var folder = PedSelectorImport.Extract(null, Backup(), Path.Combine(_root, "import"));
        Assert.Equal(PedInstaller.PackName, Path.GetFileName(folder));
        Assert.Equal("drawables", File.ReadAllText(Path.Combine(folder, "anna.ydd")));

        var analysis = PedArchiveAnalyzer.Analyze(folder);
        var anna = analysis.Candidates.Single(c => c.SpawnName == "anna");
        var bob = analysis.Candidates.Single(c => c.SpawnName == "bob");
        Assert.Equal(PedLayout.Normal, anna.Layout);
        Assert.Equal(PedLayout.Streamed, bob.Layout);
        // The pack's registrations are not taken over: the gender is chosen again for every character.
        Assert.Null(anna.RegistrationXml);
        Assert.Null(bob.RegistrationXml);
        Assert.Contains(bob.Resources, r => r.TargetPath == @"bob\head_000_r.ydd");
    }

    [Fact]
    public void A_pack_without_characters_is_refused()
    {
        var folder = Path.Combine(_root, "Empty", PedInstaller.PackName);
        Directory.CreateDirectory(folder);
        using (var fs = File.Create(Path.Combine(folder, "dlc.rpf")))
            new Rpf7Builder().AddFile("setup2.xml", Bytes("<SSetupData/>")).Write(fs);
        Assert.Throws<ArgumentException>(() =>
            PedSelectorImport.Extract(null, Path.Combine(_root, "Empty"), Path.Combine(_root, "import"))
        );
    }
}
