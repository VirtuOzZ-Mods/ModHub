using GTAModManager.Core.Dlc;

namespace GTAModManager.Core.Tests;

public class DlcListTests
{
    private const string Sample =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n"
        + "<SMandatoryPacksData>\r\n"
        + "\t<Paths>\r\n"
        + "\t\t<Item>platform:/dlcPacks/mpBeach/</Item>\r\n"
        + "\t\t<Item>dlcpacks:/mpluxe/</Item>\r\n"
        + "\t\t<Item>dlcpacks:\\NVE_gas\\</Item>\r\n"
        + "\t</Paths>\r\n"
        + "</SMandatoryPacksData>\r\n";

    [Fact]
    public void Add_appends_once_and_preserves_existing_text() // T04
    {
        var (once, changed1) = DlcList.Add(Sample, "skyline34");
        var (twice, changed2) = DlcList.Add(once, "skyline34");

        Assert.True(changed1);
        Assert.False(changed2);
        Assert.Equal(once, twice);
        Assert.StartsWith(Sample[..Sample.IndexOf("\t</Paths>")], once);
        Assert.Contains("\t\t<Item>dlcpacks:/skyline34/</Item>\r\n\t</Paths>", once);
    }

    [Theory]
    [InlineData("MPLUXE")]
    [InlineData("nve_gas")]
    public void Existing_pack_in_other_case_or_slash_style_is_not_duplicated(string pack)
    {
        Assert.False(DlcList.Add(Sample, pack).changed);
    }

    [Fact]
    public void Remove_deletes_only_matching_line()
    {
        var (added, _) = DlcList.Add(Sample, "skyline34");
        var (removed, changed) = DlcList.Remove(added, "skyline34");
        Assert.True(changed);
        Assert.Equal(Sample, removed);
    }

    [Fact]
    public void Real_world_pack_name_with_dots_and_spaces_is_supported()
    {
        var (xml, changed) = DlcList.Add(Sample, "N.O.S - Nitro Overdrive System");
        Assert.True(changed);
        Assert.Contains("N.O.S - Nitro Overdrive System", DlcList.Packs(xml));
        Assert.Equal(Sample, DlcList.Remove(xml, "N.O.S - Nitro Overdrive System").xml);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData(" lead")]
    [InlineData("")]
    public void Invalid_pack_names_are_rejected(string pack)
    {
        Assert.Throws<ArgumentException>(() => DlcList.Add(Sample, pack));
    }

    [Fact]
    public void Broken_xml_is_rejected()
    {
        Assert.ThrowsAny<System.Xml.XmlException>(() => DlcList.Add("<SMandatoryPacksData><Paths>", "x"));
    }
}
