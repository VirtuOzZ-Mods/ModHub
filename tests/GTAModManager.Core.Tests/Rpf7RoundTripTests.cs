using System.Buffers.Binary;
using System.Text;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.Tests;

public class Rpf7RoundTripTests
{
    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] FakeResource(int payload, uint version = 2)
    {
        var b = new byte[16 + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Rpf7Format.ResourceMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), version);
        new Random(payload).NextBytes(b.AsSpan(16));
        return b;
    }

    private static Rpf7Archive Build(Rpf7Builder b, out MemoryStream ms)
    {
        ms = new MemoryStream();
        b.Write(ms);
        ms.Position = 0;
        return Rpf7Archive.Open(ms);
    }

    [Fact]
    public void Synthetic_dlc_roundtrip_preserves_tree_and_content()
    {
        var res = FakeResource(3000);
        var builder = new Rpf7Builder()
            .AddFile("content.xml", new RpfBinarySource(Text("<CDataFileMgr__ContentsOfDataFileXml/>")))
            .AddFile("setup2.xml", new RpfBinarySource(Text("<SSetupData/>"), Compress: true))
            .AddFile(@"common\data\vehicles.meta", new RpfBinarySource(Text(new string('x', 10_000)), Compress: true))
            .AddFile(@"x64\vehicles.rpf\car.yft", new RpfResourceSource(res, (uint)res.Length, 0x10, 0x20))
            .AddDirectory(@"empty\dir");

        using var a = Build(builder, out _);

        Assert.Equal(RpfEncryption.Open, a.Header.Encryption);
        Assert.Equal(Text("<SSetupData/>"), a.ReadContent((RpfBinaryFile)a.Find("setup2.xml")!));
        var meta = (RpfBinaryFile)a.Find(@"common\data\vehicles.meta")!;
        Assert.NotEqual(0u, meta.CompressedSize);
        Assert.Equal(10_000, a.ReadContent(meta).Length);
        var r = (RpfResourceFile)a.Find(@"x64\vehicles.rpf\car.yft")!;
        Assert.Equal(res, a.ReadRaw(r));
        Assert.Equal(0x10u, r.SystemFlags);
        Assert.IsType<RpfDirectory>(a.Find(@"empty\dir"));
    }

    [Fact]
    public void Rebuild_from_archive_is_byte_identical_on_second_pass()
    {
        var builder = new Rpf7Builder()
            .AddFile("b.txt", new RpfBinarySource(Text("bbb")))
            .AddFile("A.txt", new RpfBinarySource(Text("aaa"), Compress: true))
            .AddFile(@"d\r.ydr", new RpfResourceSource(FakeResource(700), 716, 1, 2));
        using var first = Build(builder, out var ms1);
        using var second = Build(Rpf7Builder.FromArchive(first), out var ms2);
        Assert.Equal(ms1.ToArray(), ms2.ToArray());
    }

    [Fact]
    public void Children_are_sorted_and_contiguous()
    {
        var builder = new Rpf7Builder()
            .AddFile("zeta.txt", new RpfBinarySource([1]))
            .AddFile("Alpha.txt", new RpfBinarySource([2]))
            .AddFile(@"mid\x.txt", new RpfBinarySource([3]));
        using var a = Build(builder, out _);
        Assert.Equal(["Alpha.txt", "mid", "zeta.txt"], a.Root.Children.Select(c => c.Name));
    }

    [Fact]
    public void Nested_uncompressed_rpf_can_be_opened()
    {
        var inner = new MemoryStream();
        new Rpf7Builder().AddFile("inner.txt", new RpfBinarySource(Text("hello"))).Write(inner);
        var outer = new Rpf7Builder().AddFile(@"dlcpacks\test\dlc.rpf", new RpfBinarySource(inner.ToArray()));

        using var a = Build(outer, out _);
        using var nested = a.OpenNested((RpfBinaryFile)a.Find(@"dlcpacks\test\dlc.rpf")!);
        Assert.Equal(Text("hello"), nested.ReadContent((RpfBinaryFile)nested.Find("inner.txt")!));
    }

    [Theory]
    [InlineData(0x0FFFFFF9u, RpfEncryption.Aes)]
    [InlineData(0x0FEFFFFFu, RpfEncryption.Ng)]
    public void Encrypted_toc_is_reported_not_parsed(uint raw, RpfEncryption expected)
    {
        var bytes = new byte[512];
        new RpfHeader(Rpf7Format.Magic, 1, 16, raw).Write(bytes);
        var ex = Assert.Throws<RpfEncryptedException>(() => Rpf7Archive.Open(new MemoryStream(bytes)));
        Assert.Equal(expected, ex.Encryption);
    }

    [Fact]
    public void Corrupted_directory_range_is_rejected()
    {
        var ms = new MemoryStream();
        new Rpf7Builder().AddFile("a.txt", new RpfBinarySource([1])).Write(ms);
        var bytes = ms.ToArray();
        // Root directory: EntriesCount = 999.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16 + 12), 999);
        Assert.Throws<RpfException>(() => Rpf7Archive.Open(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData(@"..\evil.txt")]
    [InlineData(@"a\..\..\evil.txt")]
    public void Path_traversal_is_rejected(string path)
    {
        Assert.Throws<ArgumentException>(() => new Rpf7Builder().AddFile(path, new RpfBinarySource([1])));
    }

    [Fact]
    public void Encrypted_binary_is_copied_verbatim_but_cannot_be_renamed()
    {
        var blob = new byte[] { 9, 8, 7, 6 };
        var src = new RpfRawBinarySource(blob, 0, 4, IsEncrypted: 1, OriginalName: "a.meta");

        using var a = Build(new Rpf7Builder().AddFile(@"x\a.meta", src), out _);
        var f = (RpfBinaryFile)a.Find(@"x\a.meta")!;
        Assert.Equal(1u, f.IsEncrypted);
        Assert.Equal(blob, a.ReadRaw(f));
        Assert.Throws<RpfException>(() => a.ReadContent(f));

        var renamed = new Rpf7Builder().AddFile("b.meta", src);
        Assert.Throws<RpfException>(() => renamed.Write(new MemoryStream()));
    }

    [Fact]
    public void Duplicate_names_are_stored_once_in_name_table()
    {
        var b = new Rpf7Builder()
            .AddFile(@"common\data\a.meta", new RpfBinarySource([1]))
            .AddFile(@"x64\data\b.meta", new RpfBinarySource([2]));
        var ms = new MemoryStream();
        b.Write(ms);
        var bytes = ms.ToArray();
        var h = RpfHeader.Parse(bytes);
        var names = Encoding.ASCII.GetString(bytes, 16 + (int)h.EntryCount * 16, (int)h.NamesLength);
        Assert.Equal(1, names.Split('\0').Count(n => n == "data"));
    }

    [Fact]
    public void Resource_without_rsc7_magic_is_rejected()
    {
        var b = new Rpf7Builder().AddFile("bad.ydr", new RpfResourceSource(new byte[32], 32, 0, 0));
        Assert.Throws<RpfException>(() => b.Write(new MemoryStream()));
    }
}
