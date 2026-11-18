using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

/// <summary>End-to-end OIV installer tests on a synthetic game in a temp folder.</summary>
public sealed class OivInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-oiv-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "GTA V Enhanced");
    private BackupLibrary? _lib;
    private BackupLibrary Lib => _lib ??= BackupLibrary.Create(Path.Combine(_root, "backups"), 1L << 30);

    private const string DlcList =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SMandatoryPacksData>\r\n\t<Paths>\r\n\t\t<Item>dlcpacks:/mpluxe/</Item>\r\n\t\t<Item>dlcpacks:/oldmod/</Item>\r\n\t</Paths>\r\n</SMandatoryPacksData>\r\n";
    private const string GameConfig =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<fwAllConfigs><ConfigArray><Item><Config type=\"CGameConfig\"><PoolSizes><Entries>\r\n\t<Item><PoolName>Other</PoolName><PoolSize value=\"10\" /></Item>\r\n</Entries></PoolSizes></Config></Item></ConfigArray></fwAllConfigs>\r\n";

    public OivInstallerTests()
    {
        Directory.CreateDirectory(Game);
        File.WriteAllText(Path.Combine(Game, "GTA5_Enhanced.exe"), "stub");
        File.WriteAllText(Path.Combine(Game, "OpenRPF.asi"), "MZ stub loader");
        Directory.CreateDirectory(Path.Combine(Game, "mods", "update"));
        using (var fs = File.Create(Path.Combine(Game, "mods", "update", "update.rpf")))
            new Rpf7Builder()
                .AddFile(
                    @"common\data\dlclist.xml",
                    new RpfBinarySource(Encoding.UTF8.GetBytes(DlcList), Compress: true)
                )
                .AddFile(
                    @"common\data\gameconfig.xml",
                    new RpfBinarySource(Encoding.UTF8.GetBytes(GameConfig), Compress: true)
                )
                .AddFile(@"common\data\remove_me.txt", new RpfBinarySource([1, 2, 3]))
                .Write(fs);
        // An original game archive with an encrypted (NG) table of contents.
        Directory.CreateDirectory(Path.Combine(Game, "update", "x64", "dlcpacks", "patchday8ng"));
        var ng = new byte[512];
        new RpfHeader(Rpf7Format.Magic, 1, 16, (uint)RpfEncryption.Ng).Write(ng);
        File.WriteAllBytes(Path.Combine(Game, "update", "x64", "dlcpacks", "patchday8ng", "dlc.rpf"), ng);
        File.WriteAllText(Path.Combine(Game, "old.asi"), "old asi");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
        // WorkDir is shared; fixture cleanup must not delete other transactions.
    }

    private string MakeOiv(string content, Dictionary<string, byte[]> files)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".oiv");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("assembly.xml").Open()))
            w.Write(
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?><package version=\"2.2\" target=\"Five\"><metadata><name>Test</name></metadata><content>{content}</content></package>"
            );
        foreach (var (name, data) in files)
            using (var s = zip.CreateEntry("content/" + name).Open())
                s.Write(data);
        return path;
    }

    private static byte[] DlcRpf()
    {
        var ms = new MemoryStream();
        new Rpf7Builder().AddFile("setup2.xml", new RpfBinarySource(Encoding.UTF8.GetBytes("<SSetupData/>"))).Write(ms);
        return ms.ToArray();
    }

    private const string FullPackage = """
        <delete>old.asi</delete>
        <add source="my.asi">my.asi</add>
        <add source="dlc.rpf">update\x64\dlcpacks\mycar\dlc.rpf</add>
        <archive path="update\update.rpf" createIfNotExist="True" type="RPF7">
          <text path="common\data\dlclist.xml" createIfNotExist="False">
            <insert where="Before" line="*&lt;/Paths&gt;*" condition="Mask">		&lt;Item&gt;dlcpacks:/mycar/&lt;/Item&gt;</insert>
          </text>
          <xml path="\common\data\gameconfig.xml">
            <add append="Last" xpath="//PoolSizes/Entries"><Item><PoolName>PtFxAssetStore</PoolName><PoolSize value="800" /></Item></add>
          </xml>
          <xml path="common\data\dlclist.xml"><remove xpath="/SMandatoryPacksData/Paths/Item[.='dlcpacks:/oldmod/']" /></xml>
          <delete>common\data\remove_me.txt</delete>
          <archive path="x64\new_nested.rpf" createIfNotExist="True" type="RPF7">
            <add source="readme.txt">readme.txt</add>
          </archive>
        </archive>
        """;

    private Dictionary<string, byte[]> FullFiles() =>
        new()
        {
            ["my.asi"] = Encoding.UTF8.GetBytes("new asi"),
            ["dlc.rpf"] = DlcRpf(),
            ["readme.txt"] = Encoding.UTF8.GetBytes("hello"),
        };

    private string ReadInUpdate(string inner)
    {
        using var a = Rpf7Archive.OpenRead(Path.Combine(Game, "mods", "update", "update.rpf"));
        return Encoding.UTF8.GetString(a.ReadContent((RpfBinaryFile)a.Find(inner)!));
    }

    [Fact]
    public void Full_package_installs_with_redirects_and_edits()
    {
        var g = OpenGame();
        using var pkg = OivPackage.Open(MakeOiv(FullPackage, FullFiles()));
        using var plan = new OivInstaller(g).Prepare(pkg);
        Assert.Empty(plan.Errors);

        var r = new OivInstaller(g).Install(plan, Lib);
        Assert.Equal(TxState.Completed, r.State);

        Assert.False(File.Exists(Path.Combine(Game, "old.asi")));
        Assert.Equal("new asi", File.ReadAllText(Path.Combine(Game, "my.asi")));
        Assert.True(File.Exists(Path.Combine(Game, "mods", "update", "x64", "dlcpacks", "mycar", "dlc.rpf")));
        Assert.False(File.Exists(Path.Combine(Game, "update", "x64", "dlcpacks", "mycar", "dlc.rpf")));

        var dlclist = ReadInUpdate(@"common\data\dlclist.xml");
        Assert.Contains("\t\t<Item>dlcpacks:/mycar/</Item>\r\n\t</Paths>", dlclist);
        Assert.DoesNotContain("oldmod", dlclist);
        Assert.Contains("PtFxAssetStore", ReadInUpdate(@"common\data\gameconfig.xml"));

        using var a = Rpf7Archive.OpenRead(Path.Combine(Game, "mods", "update", "update.rpf"));
        Assert.Null(a.Find(@"common\data\remove_me.txt"));
        using var nested = a.OpenNested((RpfBinaryFile)a.Find(@"x64\new_nested.rpf")!);
        Assert.Equal("hello", Encoding.UTF8.GetString(nested.ReadContent((RpfBinaryFile)nested.Find("readme.txt")!)));
    }

    [Fact]
    public void Reinstall_is_idempotent_and_undo_restores_everything()
    {
        var g = OpenGame();
        var before = Snapshot();
        var oiv = MakeOiv(FullPackage, FullFiles());

        string id;
        using (var pkg = OivPackage.Open(oiv))
        using (var plan = new OivInstaller(g).Prepare(pkg))
            id = new OivInstaller(g).Install(plan, Lib).Id;

        using (var pkg = OivPackage.Open(oiv))
        using (var plan = new OivInstaller(g).Prepare(pkg))
        {
            Assert.Empty(plan.Errors);
            Assert.Empty(plan.Changes); // everything already installed: no duplicates in dlclist and no extra records
        }

        Assert.Equal(TxState.Undone, new OivInstaller(g).Undo(id, Lib).State);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Preview_lists_changes_without_writing_and_cannot_be_installed()
    {
        var g = OpenGame();
        var before = Snapshot();
        using var pkg = OivPackage.Open(MakeOiv(FullPackage, FullFiles()));
        using var plan = new OivInstaller(g).Prepare(pkg, preview: true);

        Assert.Empty(plan.Errors);
        Assert.False(plan.CanInstall);
        var targets = plan.Changes.Select(c => c.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("my.asi", targets);
        Assert.Contains("old.asi", targets);
        Assert.Contains(@"mods\update\update.rpf", targets);
        Assert.Contains(@"mods\update\x64\dlcpacks\mycar\dlc.rpf", targets);
        Assert.Equal(before, Snapshot());
    }

    private Dictionary<string, string> UpdateRpfContents()
    {
        using var a = Rpf7Archive.OpenRead(Path.Combine(Game, "mods", "update", "update.rpf"));
        return a.Files.OfType<RpfBinaryFile>().ToDictionary(f => f.Path, f => Convert.ToHexString(a.ReadContent(f)));
    }

    private string Install(string content, Dictionary<string, byte[]> files, BackupLibrary? lib)
    {
        var g = OpenGame();
        using var pkg = OivPackage.Open(MakeOiv(content, files));
        using var plan = new OivInstaller(g).Prepare(pkg);
        Assert.Empty(plan.Errors);
        var r = new OivInstaller(g).Install(plan, lib);
        Assert.Equal(TxState.Completed, r.State);
        return r.Id;
    }

    [Fact]
    public void Backup_keeps_only_changed_files_not_whole_archive()
    {
        var updateSize = new FileInfo(Path.Combine(Game, "mods", "update", "update.rpf")).Length;
        var before = UpdateRpfContents();
        var id = Install(FullPackage, FullFiles(), Lib);

        var rec = Lib.LoadRecord("Enhanced", id)!;
        var arc = rec.Archives.Single(a => a.Rel == @"mods\update\update.rpf");
        Assert.Contains(arc.Entries, e => e.Path == @"common\data\dlclist.xml" && e.Kind == InnerKind.Replaced);
        Assert.Contains(arc.Entries, e => e.Path == @"common\data\remove_me.txt" && e.Kind == InnerKind.Deleted);
        Assert.Contains(arc.Entries, e => e.Path == @"x64\new_nested.rpf" && e.Kind == InnerKind.Added);
        // The backup holds only the old dlclist.xml, gameconfig.xml, remove_me.txt and old.asi, not the whole update.rpf.
        Assert.True(Lib.UsedBytes() < updateSize, $"копия {Lib.UsedBytes()} байт >= архива {updateSize}");
        // The transaction's temporary full copies are deleted.
        Assert.False(Directory.Exists(Path.Combine(GameInstall.Open(Game).WorkDir, "tx", id)));

        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(id, Lib).State);
        Assert.Equal(before, UpdateRpfContents());
        Assert.Equal("old asi", File.ReadAllText(Path.Combine(Game, "old.asi")));
        Assert.False(File.Exists(Path.Combine(Game, "my.asi")));
        Assert.False(File.Exists(Path.Combine(Game, "mods", "update", "x64", "dlcpacks", "mycar", "dlc.rpf")));
        Assert.Equal(0, Lib.UsedBytes()); // after undo the copies are no longer needed
    }

    [Fact]
    public void Undo_in_any_order_touches_only_own_files_in_shared_archive()
    {
        var before = UpdateRpfContents();
        var a = Install(
            """<archive path="update\update.rpf" createIfNotExist="False" type="RPF7"><add source="a.txt">mods_a\a.txt</add><delete>common\data\remove_me.txt</delete></archive>""",
            new() { ["a.txt"] = "AAA"u8.ToArray() },
            Lib
        );
        var b = Install(
            """<archive path="update\update.rpf" createIfNotExist="False" type="RPF7"><add source="b.txt">mods_b\b.txt</add></archive>""",
            new() { ["b.txt"] = "BBB"u8.ToArray() },
            Lib
        );

        // Undo the FIRST install; the second one stays.
        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(a, Lib).State);
        var mid = UpdateRpfContents();
        Assert.False(mid.ContainsKey(@"mods_a\a.txt"));
        Assert.True(mid.ContainsKey(@"common\data\remove_me.txt"));
        Assert.Equal(Convert.ToHexString("BBB"u8.ToArray()), mid[@"mods_b\b.txt"]);

        Assert.Equal(TxState.Undone, new OivInstaller(OpenGame()).Undo(b, Lib).State);
        Assert.Equal(before, UpdateRpfContents());
    }

    [Fact]
    public void Undo_refuses_when_file_was_changed_by_later_install()
    {
        var a = Install(
            """<archive path="update\update.rpf" createIfNotExist="False" type="RPF7"><add source="x.txt">shared\x.txt</add></archive>""",
            new() { ["x.txt"] = "first"u8.ToArray() },
            Lib
        );
        Install(
            """<archive path="update\update.rpf" createIfNotExist="False" type="RPF7"><add source="x.txt">shared\x.txt</add></archive>""",
            new() { ["x.txt"] = "second"u8.ToArray() },
            Lib
        );

        var before = UpdateRpfContents();
        var r = new OivInstaller(OpenGame()).Undo(a, Lib);
        Assert.Equal(TxState.Rejected, r.State);
        Assert.Contains("изменён после установки", r.Message);
        Assert.Equal(before, UpdateRpfContents());
    }

    [Fact]
    public void Vanilla_and_mod_files_are_backed_up_separately()
    {
        // The game's original update.rpf equals the current copy in mods\ (as right after copying the original).
        Directory.CreateDirectory(Path.Combine(Game, "update"));
        File.Copy(Path.Combine(Game, "mods", "update", "update.rpf"), Path.Combine(Game, "update", "update.rpf"));

        const string edit =
            """<archive path="update\update.rpf" createIfNotExist="False" type="RPF7"><add source="d.xml">common\data\dlclist.xml</add></archive>""";
        var first = Install(
            edit,
            new() { ["d.xml"] = "<SMandatoryPacksData><Paths/></SMandatoryPacksData>"u8.ToArray() },
            Lib
        );
        var second = Install(
            edit,
            new()
            {
                ["d.xml"] = "<SMandatoryPacksData><Paths><Item>x</Item></Paths></SMandatoryPacksData>"u8.ToArray(),
            },
            Lib
        );

        // The first install replaced the original dlclist.xml, so its backup is vanilla.
        Assert.Equal(BackupOrigin.Vanilla, Lib.LoadRecord("Enhanced", first)!.Archives[0].Entries.Single().Old!.Origin);
        // The second replaced the first mod's version, so that is a mod file.
        Assert.Equal(BackupOrigin.Mods, Lib.LoadRecord("Enhanced", second)!.Archives[0].Entries.Single().Old!.Origin);
        Assert.True(Lib.UsedBytes(BackupOrigin.Vanilla) > 0);
        Assert.True(Lib.UsedBytes(BackupOrigin.Mods) > 0);
        Assert.True(File.Exists(Path.Combine(Lib.Root, BackupLibrary.VanillaPackName)));
        Assert.True(File.Exists(Path.Combine(Lib.Root, BackupLibrary.ModsPackName)));
    }

    [Fact]
    public void Quota_exceeded_blocks_install_without_changes()
    {
        var tiny = BackupLibrary.Create(Path.Combine(_root, "tiny"), 4);
        var g = OpenGame();
        var before = Snapshot();
        using var pkg = OivPackage.Open(MakeOiv(FullPackage, FullFiles()));
        using var plan = new OivInstaller(g).Prepare(pkg);
        var r = new OivInstaller(g).Install(plan, tiny);
        Assert.Equal(TxState.Rejected, r.State);
        Assert.Contains("лимит", r.Message);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Without_backups_install_works_and_leaves_no_copies()
    {
        var id = Install(FullPackage, FullFiles(), lib: null);
        var g = OpenGame();
        Assert.False(Directory.Exists(Path.Combine(g.WorkDir, "tx", id)));
        Assert.Equal(TxState.Rejected, new OivInstaller(g).Undo(id, null).State);
    }

    [Theory]
    [InlineData("50GB", 50L << 30)]
    [InlineData("100 мб", 100L << 20)]
    [InlineData("25", 25L << 30)]
    [InlineData("1,5 ГБ", 3L << 29)]
    public void Quota_text_is_parsed(string text, long expected) =>
        Assert.Equal(expected, BackupLibrary.ParseSize(text));

    [Fact]
    public void Missing_mod_loader_blocks_mods_writes_but_not_loose_files()
    {
        File.Delete(Path.Combine(Game, "OpenRPF.asi"));
        var g = OpenGame();
        Assert.False(g.CheckModLoader().Installed);

        using (var pkg = OivPackage.Open(MakeOiv(FullPackage, FullFiles())))
        using (var plan = new OivInstaller(g).Prepare(pkg))
            Assert.Contains(plan.Errors, e => e.Contains("OpenRPF.asi"));

        using (var pkg = OivPackage.Open(MakeOiv("<add source=\"my.asi\">my.asi</add>", new() { ["my.asi"] = [1] })))
        using (var plan = new OivInstaller(g).Prepare(pkg))
            Assert.Empty(plan.Errors);
    }

    [Fact]
    public void Protected_game_file_is_refused()
    {
        var g = OpenGame();
        using var pkg = OivPackage.Open(MakeOiv("<add source=\"x\">GTA5_Enhanced.exe</add>", new() { ["x"] = [1] }));
        using var plan = new OivInstaller(g).Prepare(pkg);
        Assert.False(plan.CanInstall);
        Assert.Contains(plan.Errors, e => e.Contains("файл игры"));
    }

    [Fact]
    public void Encrypted_original_archive_requires_keys()
    {
        var g = OpenGame();
        using var pkg = OivPackage.Open(
            MakeOiv(
                "<archive path=\"update\\x64\\dlcpacks\\patchday8ng\\dlc.rpf\" createIfNotExist=\"False\" type=\"RPF7\"><delete>a.txt</delete></archive>",
                new()
            )
        );
        using var plan = new OivInstaller(g).Prepare(pkg);
        Assert.False(plan.CanInstall);
        Assert.Contains(plan.Errors, e => e.Contains("ключи"));
    }

    [Fact]
    public void Deleting_original_game_archive_is_never_planned()
    {
        var g = OpenGame();
        using var pkg = OivPackage.Open(MakeOiv("<delete>update\\x64\\dlcpacks\\patchday8ng</delete>", new()));
        using var plan = new OivInstaller(g).Prepare(pkg);
        Assert.Empty(plan.Changes);
        Assert.Contains(plan.Log, l => l.Contains("не удаляю"));
    }

    [Fact]
    public void Zip_slip_source_is_rejected()
    {
        var g = OpenGame();
        using var pkg = OivPackage.Open(MakeOiv("<add source=\"..\\..\\evil\">a.asi</add>", new()));
        using var plan = new OivInstaller(g).Prepare(pkg);
        Assert.False(plan.CanInstall);
    }

    private GameInstall OpenGame()
    {
        var g = GameInstall.Open(Game);
        g.RunningCheckOverride = () => false;
        return g;
    }

    private Dictionary<string, string> Snapshot() =>
        Directory
            .EnumerateFiles(Game, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Game, f), FileTransactionEngine.Hash);
}
