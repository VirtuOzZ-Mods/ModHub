using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Peds;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.Tests;

public sealed class PedDiscoveryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "gmm-ped-discovery-" + Guid.NewGuid().ToString("N"));

    public PedDiscoveryTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "GTA5.exe"), "stub");
    }

    public void Dispose() => Directory.Delete(root, true);

    static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Empty_owned_ped_pack_is_valid_after_removing_all_models()
    {
        Dlc("gmm_peds", "test:/common/data/peds.meta");
        var path = Path.Combine(root, "mods/update/x64/dlcpacks/gmm_peds/dlc.rpf");
        byte[] bytes;
        using (var pack = ArchiveSession.OpenFile(path, "fixture"))
        {
            pack.Put(
                "common/data/peds.meta",
                Bytes("<CPedModelInfo__InitDataList><InitDatas/></CPedModelInfo__InitDataList>")
            );
            using var result = new MemoryStream();
            pack.WriteTo(result);
            bytes = result.ToArray();
        }
        File.WriteAllBytes(path, bytes);
        var scan = new PedDiscoveryService(GameInstall.Open(root)).Scan();
        Assert.Empty(scan.Entries);
        Assert.Empty(scan.Warnings);
    }

    void EncryptedDlc(string name)
    {
        var dir = Path.Combine(root, "update", "x64", "dlcpacks", name);
        Directory.CreateDirectory(dir);
        using var stream = new MemoryStream();
        new Rpf7Builder().AddFile("setup2.xml", new RpfBinarySource(Bytes("<SSetupData/>"))).Write(stream);
        var bytes = stream.ToArray();
        BitConverter.GetBytes((uint)RpfEncryption.Ng).CopyTo(bytes, 12);
        File.WriteAllBytes(Path.Combine(dir, "dlc.rpf"), bytes);
    }

    [Fact]
    public void Unrelated_encrypted_dlc_is_diagnostic_not_a_ped_problem()
    {
        EncryptedDlc("mp2023_01");
        var scan = new PedDiscoveryService(GameInstall.Open(root)).Scan();
        Assert.Empty(scan.Entries);
        Assert.Empty(scan.Warnings);
        Assert.Contains(scan.Diagnostics, d => d.Contains("mp2023_01"));
    }

    [Fact]
    public void Empty_installation_does_not_show_loading_warnings_without_any_peds()
    {
        var scan = new PedDiscoveryService(GameInstall.Open(root)).Scan();
        Assert.Empty(scan.Entries);
        Assert.Empty(scan.Warnings);
    }

    [Fact]
    public void Unreadable_known_ped_pack_keeps_its_actionable_warning()
    {
        EncryptedDlc("pedselector");
        var scan = new PedDiscoveryService(GameInstall.Open(root)).Scan();
        Assert.Contains(scan.Warnings, w => w.Contains("pedselector"));
    }

    [Fact]
    public void Custom_named_ped_pack_still_reports_broken_registration()
    {
        Dlc("my_custom_characters", "user:/UserMods/../escape.xml", true);
        var scan = new PedDiscoveryService(GameInstall.Open(root)).Scan();
        Assert.Contains(scan.Warnings, w => w.Contains("регистрацию"));
    }

    const string Meta =
        "<CPedModelInfo__InitDataList><InitDatas><Item custom='keep'><Name>hero</Name><Pedtype>COP</Pedtype><IsStreamedGfx value='true'/><Unknown extra='yes'>preserve</Unknown></Item></InitDatas></CPedModelInfo__InitDataList>";

    void Dlc(string folder, string reference, bool external = false, string streamPath = "peds.rpf")
    {
        var dir = Path.Combine(root, "mods", "update", "x64", "dlcpacks", folder);
        Directory.CreateDirectory(dir);
        using var inner = new MemoryStream();
        new Rpf7Builder()
            .AddFile("hero.yft", new RpfBinarySource([1]))
            .AddFile("hero.ymt", new RpfBinarySource([1]))
            .AddFile(@"hero\head_000_u.ydd", new RpfBinarySource([1]))
            .Write(inner);
        var content =
            $"<CDataFileMgr__ContentsOfDataFileXml><dataFiles><Item><filename>{reference}</filename><fileType>PED_METADATA_FILE</fileType></Item><Item><filename>test:/{streamPath}</filename><fileType>PEDSTREAM_FILE</fileType></Item></dataFiles></CDataFileMgr__ContentsOfDataFileXml>";
        var b = new Rpf7Builder()
            .AddFile(
                "setup2.xml",
                new RpfBinarySource(
                    Bytes("<SSetupData><deviceName>test</deviceName><datFile>appdata.xml</datFile></SSetupData>")
                )
            )
            .AddFile("appdata.xml", new RpfBinarySource(Bytes(content)))
            .AddFile(streamPath.Replace("%PLATFORM%", "x64"), new RpfBinarySource(inner.ToArray()));
        if (!external)
            b.AddFile(@"common\data\peds.meta", new RpfBinarySource(Bytes(Meta)));
        using var f = File.Create(Path.Combine(dir, "dlc.rpf"));
        b.Write(f);
    }

    [Fact]
    public void Modern_user_metadata_and_classic_internal_metadata_preserve_full_items_and_resources()
    {
        var user = Path.Combine(root, "UserMods");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "ap_m.xml"), Meta);
        Dlc("pedselector", "user:/UserMods/ap_m.xml", true);
        Dlc("addonpeds", "test:/common/data/peds.meta");
        var result = new PedDiscoveryService(GameInstall.Open(root), user).Scan();
        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, e =>
        {
            Assert.False(e.Managed);
            Assert.Equal("hero", e.SpawnName);
            Assert.Equal("COP", e.PedType);
            Assert.Equal(PedLayout.Streamed, e.Layout);
            Assert.Contains("Unknown", e.RegistrationXml);
            Assert.Contains("custom=\"keep\"", e.RegistrationXml);
            Assert.Contains(e.ResourcePaths, p =>
                p.EndsWith(@"hero\head_000_u.ydd", StringComparison.OrdinalIgnoreCase)
            );
        });
    }

    [Fact]
    public void Unsafe_reference_is_warned_and_not_read()
    {
        Dlc("bad", "user:/UserMods/../escape.xml", true);
        var result = new PedDiscoveryService(GameInstall.Open(root), Path.Combine(root, "UserMods")).Scan();
        Assert.Empty(result.Entries);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Enhanced_is_rejected()
    {
        File.WriteAllText(Path.Combine(root, "GTA5_Enhanced.exe"), "stub");
        Assert.Throws<NotSupportedException>(() => new PedDiscoveryService(GameInstall.Open(root)).Scan());
    }

    [Fact]
    public void Platform_placeholder_resolves_legacy_x64_stream_resources()
    {
        Dlc("classic", "test:/common/data/peds.meta", false, "%PLATFORM%/models/cdimages/peds.rpf");
        var result = new PedDiscoveryService(GameInstall.Open(root)).Scan();
        Assert.NotEmpty(Assert.Single(result.Entries).ResourcePaths);
    }

    [Fact]
    public void Empty_mods_directory_does_not_hide_original_dlc_archive()
    {
        Dlc("fallback", "test:/common/data/peds.meta");
        var mods = Path.Combine(root, "mods", "update", "x64", "dlcpacks", "fallback");
        var original = Path.Combine(root, "update", "x64", "dlcpacks", "fallback");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        Directory.Move(mods, original);
        Directory.CreateDirectory(mods);
        var result = new PedDiscoveryService(GameInstall.Open(root)).Scan();
        Assert.Equal(Path.Combine(original, "dlc.rpf"), Assert.Single(result.Entries).SourceDlc);
    }
}
