using GTAModManager.Core.Discover;
using Xunit;

namespace GTAModManager.Core.Tests;

public class Gta5ModsDownloadsTests
{
    private static readonly Uri Page = new("https://www.gta5-mods.com/scripts/n-o-s-nitro-overdrive-system");

    private const string PageHtml = """
        <a href="/scripts/other-mod/download/1" class="btn btn-download">x</a>
        <a href="/scripts/n-o-s-nitro-overdrive-system/download/204263" class="btn btn-primary btn-download" >Download</a>
        <div class="well pull-left file-version-container "> <div class="pull-left"> <i class="fa fa-file"></i>&nbsp;1.9.5 [Legacy ONLY] <span>(current)</span>
        <a target="_blank" href="/scripts/n-o-s-nitro-overdrive-system/download/203777"><i class="fa fa-download download-version"></i></a>
        """;

    [Fact]
    public void Main_download_button_of_this_mod_is_found()
    {
        Assert.Equal(
            "https://www.gta5-mods.com/scripts/n-o-s-nitro-overdrive-system/download/204263",
            Gta5ModsDownloads.FindDownloadPage(PageHtml, Page)?.AbsoluteUri
        );
        Assert.Equal("1.9.5 [Legacy ONLY]", Gta5ModsDownloads.FindCurrentVersion(PageHtml));
    }

    [Fact]
    public void File_link_only_from_files_host_and_mod_archives()
    {
        var ok = Gta5ModsDownloads.FindFile(
            """<a class="btn btn-primary btn-download" href="https://files.gta5-mods.com/uploads/simple-trainer-for-gtav/0cf657-TrainerV.rar">"""
        );
        Assert.NotNull(ok);
        Assert.Equal("TrainerV.rar", Gta5ModsDownloads.FileName(ok!));
        Assert.Equal(
            "NOS 1.9.5 Legacy.zip",
            Gta5ModsDownloads.FileName(new Uri("https://files.gta5-mods.com/uploads/x/c5d897-NOS%201.9.5%20Legacy.zip"))
        );
        Assert.Null(
            Gta5ModsDownloads.FindFile("""<a class="btn-download" href="https://evil.example.com/uploads/a.zip">""")
        );
        Assert.Null(
            Gta5ModsDownloads.FindFile(
                """<a class="btn-download" href="https://files.gta5-mods.com/uploads/a/b.exe">"""
            )
        );
    }

    [Theory]
    [InlineData("1.9.5 [Legacy ONLY]", "Legacy")]
    [InlineData("2.0 Enhanced only", "Enhanced")]
    [InlineData("Only for Enhanced", "Enhanced")]
    [InlineData("1.9.2 [FIX V2]", null)]
    [InlineData("Legacy + Enhanced", null)]
    [InlineData(null, null)]
    public void Edition_restriction_is_read_from_version_name(string? version, string? expected) =>
        Assert.Equal(expected, Gta5ModsDownloads.EditionOnly(version));

    [Fact]
    public void Catalog_install_modes()
    {
        var c = DiscoverCatalog.LoadBuiltIn();
        Assert.Equal("base", c.Entries.Single(e => e.Id == "scripthookv").Install);
        Assert.Equal("base", c.Entries.Single(e => e.Id == "shvdn-enhanced").Install);
        Assert.Equal("download", c.Entries.Single(e => e.Id == "simple-trainer").Install);
        Assert.Equal("download", c.Entries.Single(e => e.Id == "nos").Install);
    }
}
